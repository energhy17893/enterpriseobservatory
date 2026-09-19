using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Api.Projections;

/// <summary>
/// Turns the one model into the views the interface needs.
/// </summary>
/// <remarks>
/// <para>
/// The governing rule of ADR-0007: however many screens there are, they are all
/// projections of one model. No view holds its own state, builds its own alert
/// list or computes its own health. The previous product broke this — the alert
/// page read lifecycle instances while vendor pages read the raw per-cycle
/// snapshot, so the same alert could be acknowledged on one screen and open on
/// another.
/// </para>
/// <para>
/// Read-only and stateless. It is given the stores and asked a question; it
/// keeps nothing between calls.
/// </para>
/// </remarks>
public sealed class ReadModel(
    IEntityGraphStore graphs,
    IAlertStateStore alerts,
    ICollectorHealthStore collectors,
    IObservationStore observations,
    IClock clock)
{
    private readonly IEntityGraphStore _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    private readonly IAlertStateStore _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));

    private readonly ICollectorHealthStore _collectors =
        collectors ?? throw new ArgumentNullException(nameof(collectors));

    private readonly IObservationStore _observations =
        observations ?? throw new ArgumentNullException(nameof(observations));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The largest page the API will return.</summary>
    /// <remarks>
    /// Bounded here rather than trusted from the query string. An unbounded
    /// page is a request to serialise the entire estate, which is both a
    /// performance problem and the easiest denial of service in the product.
    /// </remarks>
    public const int MaxLimit = 500;

    // --- overview ---------------------------------------------------------

    public OverviewView Overview()
    {
        var graph = _graphs.Current;
        var visible = Visible();
        var health = _collectors.Current;

        var byHealth = graph.Active
            .GroupBy(e => e.EffectiveHealth)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        // Every state appears, including the ones with no members. A missing
        // key reads as "no answer" in a client; a zero reads as "none", which
        // is what we mean.
        foreach (var state in Enum.GetValues<HealthState>())
        {
            byHealth.TryAdd(state.ToString(), 0);
        }

        return new OverviewView
        {
            GeneratedAtUtc = _clock.UtcNow,
            CriticalAlerts = visible.Count(a => a.Severity == AlertSeverity.Critical),
            WarningAlerts = visible.Count(a => a.Severity == AlertSeverity.Warning),
            UnacknowledgedAlerts = visible.Count(a => a.State == AlertLifecycleState.Open),
            SuppressedAlerts = visible.Count(a => a.SuppressedByWindowId is not null),
            EntitiesByHealth = byHealth,
            VanishedEntities = graph.Vanished.Count(),
            FailingCollectors = health.Count(c => c.Health != HealthState.Healthy),
            OldestSuccessfulReadUtc = health.Count == 0
                ? null
                : health.Min(c => c.LastSuccessUtc),
        };
    }

    // --- alerts -----------------------------------------------------------

    /// <summary>
    /// The inbox: every visible alert, worst first.
    /// </summary>
    /// <remarks>
    /// This is the flat list ADR-0007 §5.1 guarantees is always one click away.
    /// Event grouping is a view over it, never a replacement for it: no alert
    /// may live only inside a group.
    /// </remarks>
    public Page<AlertView> Alerts(
        AlertSeverity? severity = null,
        AlertLifecycleState? state = null,
        string? category = null,
        string? source = null,
        string? search = null,
        int offset = 0,
        int limit = 50)
    {
        var graph = _graphs.Current;

        var matching = Visible()
            .Where(a => severity is null || a.Severity == severity)
            .Where(a => state is null || a.State == state)
            .Where(a => category is null ||
                string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(a => source is null ||
                string.Equals(a.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(a => Matches(a, search))
            // Worst first, then most recent. An operator reads from the top and
            // must find the thing that matters there.
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.LastSeenUtc)
            .ToList();

        return Paged(matching, offset, limit, a => ToView(a, graph));
    }

    // --- entities ---------------------------------------------------------

    /// <summary>The entity explorer (tier 1 of ADR-0007).</summary>
    public Page<EntityView> Entities(
        EntityKind? kind = null,
        HealthState? health = null,
        string? source = null,
        string? search = null,
        bool includeVanished = false,
        int offset = 0,
        int limit = 50)
    {
        var graph = _graphs.Current;
        var counts = AlertCountsByEntity();

        var matching = graph.Entities.Values
            .Where(e => includeVanished || e.ObservationState != ObservationState.Vanished)
            .Where(e => kind is null || e.Kind == kind)
            .Where(e => health is null || e.EffectiveHealth == health)
            .Where(e => source is null || string.Equals(e.SourceInstanceId, source, StringComparison.Ordinal))
            .Where(e => search is null ||
                e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            // Worst first: the explorer is also a triage surface.
            .OrderByDescending(e => e.EffectiveHealth)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Paged(matching, offset, limit, e => ToView(e, counts));
    }

    /// <summary>One entity with its edges and its alerts (tier 2 of ADR-0007).</summary>
    public EntityDetailView? Entity(string id)
    {
        var graph = _graphs.Current;
        var entityId = new EntityId(id);

        if (!graph.Entities.TryGetValue(entityId, out var entity))
        {
            return null;
        }

        var counts = AlertCountsByEntity();

        return new EntityDetailView
        {
            Entity = ToView(entity, counts),
            Marks =
            [
                .. entity.Marks.Select(m => new IdentityMarkView
                {
                    Kind = m.Kind,
                    Value = m.Value,
                    Source = m.Source,
                }),
            ],
            Relationships = RelationshipsOf(graph, entityId),
            Alerts =
            [
                .. Visible()
                    .Where(a => a.Entity == entityId)
                    .OrderByDescending(a => a.Severity)
                    .Select(a => ToView(a, graph)),
            ],
        };
    }

    // --- collectors -------------------------------------------------------

    /// <summary>
    /// The monitoring system observing itself.
    /// </summary>
    /// <remarks>
    /// A first-class screen rather than a log file. "Is the monitoring working"
    /// must be answerable from inside the product; in the previous one it was
    /// answerable only by reading logs on the server. See ADR-0005.
    /// </remarks>
    public IReadOnlyList<CollectorView> Collectors() =>
    [
        .. _collectors.Current
            .OrderBy(c => c.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Role)
            .Select(c => new CollectorView
            {
                InstanceId = c.InstanceId,
                Role = c.Role.ToString(),
                Health = c.Health,
                ConsecutiveFailures = c.ConsecutiveFailures,
                IsBackingOff = c.IsBackingOff,
                LastSuccessUtc = c.LastSuccessUtc,
                LastFailureDetail = c.LastFailureDetail,
            }),
    ];

    // --- events -------------------------------------------------------------

    /// <summary>
    /// The inbox, grouped into what is actually going wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projection over the same instances the flat list shows, computed on
    /// demand rather than stored. Correlation depends on the graph, and the
    /// graph changes; a stored grouping would be a claim about a topology that
    /// no longer exists.
    /// </para>
    /// <para>
    /// Every visible alert appears exactly once, in an event or on its own, and
    /// the total is reported so the arithmetic can be checked. See ADR-0007.
    /// </para>
    /// </remarks>
    public EventBoardView Events(CorrelationPolicy? policy = null)
    {
        var graph = _graphs.Current;
        var visible = Visible();
        var byFingerprint = visible.ToDictionary(a => a.Fingerprint);

        var result = EventCorrelator.Correlate(visible, graph, policy ?? CorrelationPolicy.Default);

        AlertView Look(AlertFingerprint fingerprint) => ToView(byFingerprint[fingerprint], graph);

        return new EventBoardView
        {
            Events =
            [
                .. result.Events.Select(e => new EventView
                {
                    Id = e.Id,
                    Title = e.Title,
                    Severity = e.Severity,
                    RootId = e.Root.Value,
                    RootName = NameOf(graph, e.Root),
                    AlertCount = e.Alerts.Count,
                    EntityCount = e.Entities.Count,
                    FirstSeenUtc = e.FirstSeenUtc,
                    LastSeenUtc = e.LastSeenUtc,
                    Alerts = [.. e.Alerts.Select(Look)],
                    Explanation =
                    [
                        .. e.Explanation.Select(link => new CorrelationLinkView
                        {
                            FromId = link.From.Value,
                            FromName = NameOf(graph, link.From),
                            Kind = link.Kind,
                            ToId = link.To.Value,
                            ToName = NameOf(graph, link.To),
                        }),
                    ],
                }),
            ],
            Suggestions =
            [
                .. result.Suggestions.Select(s => new SuggestionView
                {
                    WithinUtc = s.WithinUtc,
                    WindowSeconds = (int)s.Window.TotalSeconds,
                    Alerts = [.. s.Alerts.Select(Look)],
                }),
            ],
            Ungrouped =
            [
                .. result.Ungrouped
                    .Select(Look)
                    .OrderByDescending(a => a.Severity)
                    .ThenByDescending(a => a.LastSeenUtc),
            ],
            TotalAlerts = visible.Count,
        };
    }

    /// <summary>An entity display name, or its id when it is not in the graph.</summary>
    /// <remarks>
    /// An identifier is a poor thing to show an operator, but it beats an empty
    /// space: it at least says which thing the product means.
    /// </remarks>
    private static string NameOf(EntityGraph graph, EntityId id) =>
        graph.Entities.TryGetValue(id, out var entity) ? entity.DisplayName : id.Value;

    // --- measurements -----------------------------------------------------

    /// <summary>Which counters exist for an entity.</summary>
    /// <remarks>
    /// Read from what has actually been recorded rather than from a fixed menu
    /// per entity type. A menu would be wrong for half of them and would go on
    /// offering a counter the platform stopped providing.
    /// </remarks>
    public IReadOnlyList<SeriesOptionView> SeriesFor(string entityId) =>
    [
        .. _observations.SeriesFor(new EntityId(entityId))
            .Select(k => new SeriesOptionView { Counter = k.Counter, Instance = k.Instance }),
    ];

    /// <summary>One series over a window.</summary>
    public SeriesView Series(
        string entityId,
        string counter,
        string? instance,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int maxPoints)
    {
        var to = toUtc ?? _clock.UtcNow;
        var from = fromUtc ?? to.AddHours(-1);

        var result = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(new EntityId(entityId), counter, instance ?? string.Empty),
            FromUtc = from,
            ToUtc = to,
            MaxPoints = Math.Clamp(maxPoints, 1, 2_000),
        });

        return new SeriesView
        {
            EntityId = entityId,
            Counter = counter,
            Instance = instance ?? string.Empty,
            Exists = result.Exists,
            Resolution = result.Resolution,
            Unit = result.Unit,
            Rollup = result.Rollup,
            Truncated = result.Truncated,
            Points =
            [
                .. result.Points.Select(p => new SeriesPointView
                {
                    AtUtc = p.StartUtc,
                    Min = p.Min,
                    Max = p.Max,
                    Average = p.Average,
                    Last = p.Last,
                    Count = p.Count,
                }),
            ],
        };
    }

    // --- internals --------------------------------------------------------

    private IReadOnlyList<AlertInstance> Visible() => [.. _alerts.All.Where(a => a.IsVisible)];

    private Dictionary<EntityId, int> AlertCountsByEntity() =>
        Visible()
            .Where(a => a.Entity is not null)
            .GroupBy(a => a.Entity!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

    private static bool Matches(AlertInstance alert, string? search) =>
        search is null ||
        alert.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        alert.Description.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static Page<TView> Paged<TSource, TView>(
        IReadOnlyList<TSource> all,
        int offset,
        int limit,
        Func<TSource, TView> project)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var safeOffset = Math.Max(offset, 0);

        return new Page<TView>
        {
            Items = [.. all.Skip(safeOffset).Take(safeLimit).Select(project)],
            Total = all.Count,
            Offset = safeOffset,
            Limit = safeLimit,
        };
    }

    /// <summary>Presents one alert instance, for a command's response.</summary>
    public AlertView Present(AlertInstance alert) => ToView(alert, _graphs.Current);

    private static AlertView ToView(AlertInstance alert, EntityGraph graph)
    {
        string? name = null;

        if (alert.Entity is { } entityId && graph.Entities.TryGetValue(entityId, out var entity))
        {
            name = entity.DisplayName;
        }

        return new AlertView
        {
            Fingerprint = alert.Fingerprint.Value,
            Severity = alert.Severity,
            State = alert.State,
            Title = alert.Title,
            Description = alert.Description,
            Category = alert.Category,
            Source = alert.Source,
            EntityId = alert.Entity?.Value,
            EntityName = name,
            IsDerived = alert.IsDerived,
            SuppressedByWindowId = alert.SuppressedByWindowId,
            FirstSeenUtc = alert.FirstSeenUtc,
            LastSeenUtc = alert.LastSeenUtc,
        };
    }

    private static EntityView ToView(Entity entity, Dictionary<EntityId, int> alertCounts) => new()
    {
        Id = entity.Id.Value,
        Kind = entity.Kind,
        DisplayName = entity.DisplayName,
        Health = entity.EffectiveHealth,
        ObservationState = entity.ObservationState,
        Source = entity.SourceInstanceId,
        LastSeenUtc = entity.LastSeenUtc,
        AlertCount = alertCounts.GetValueOrDefault(entity.Id),
    };

    /// <summary>
    /// Every edge touching one entity, in both directions.
    /// </summary>
    /// <remarks>
    /// A linear scan of the edge list. Fine at the size of one estate's
    /// topology and wrong at ten; it will need an adjacency index before it
    /// becomes a problem. Noted rather than solved, because the shape of the
    /// index depends on which traversals turn out to matter and we have not
    /// measured that yet.
    /// </remarks>
    private static List<RelationshipView> RelationshipsOf(EntityGraph graph, EntityId id)
    {
        var views = new List<RelationshipView>();

        foreach (var edge in graph.Relationships)
        {
            var outgoing = edge.From == id;

            if (!outgoing && edge.To != id)
            {
                continue;
            }

            var otherId = outgoing ? edge.To : edge.From;

            if (!graph.Entities.TryGetValue(otherId, out var other))
            {
                continue;
            }

            views.Add(new RelationshipView
            {
                Kind = edge.Kind,
                IsOutgoing = outgoing,
                OtherId = otherId.Value,
                OtherName = other.DisplayName,
                OtherKind = other.Kind,
                OtherHealth = other.EffectiveHealth,
            });
        }

        return views;
    }
}
