using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Application.Analysis;
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
    ICoverageStore coverage,
    IObservationStore observations,
    MonitoringOptions options,
    IClock clock)
{
    private readonly IEntityGraphStore _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    private readonly IAlertStateStore _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));

    private readonly ICoverageStore _coverage =
        coverage ?? throw new ArgumentNullException(nameof(coverage));

    private readonly ICollectorHealthStore _collectors =
        collectors ?? throw new ArgumentNullException(nameof(collectors));

    private readonly IObservationStore _observations =
        observations ?? throw new ArgumentNullException(nameof(observations));

    /// <summary>
    /// Held for its retention windows, which decide what a window can be
    /// answered from.
    /// </summary>
    /// <remarks>
    /// Chosen here rather than in the store because picking a resolution needs
    /// two things the store deliberately does not hold: the retention policy —
    /// which <c>Compact</c> receives per call, so the store is stateless about
    /// it — and a clock, to know how far back the window reaches. Both are
    /// already here.
    /// </remarks>
    private readonly MonitoringOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

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

    // --- reports ------------------------------------------------------------

    /// <summary>How far back a report looks when no range is given.</summary>
    private static readonly TimeSpan DefaultReportWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// The alert/finding report: open alerts plus whatever resolved within the
    /// chosen window. Serves both the printable page and the CSV export, so
    /// the two can never disagree about what a report contains.
    /// </summary>
    /// <remarks>
    /// Open alerts are included regardless of the range — an alert still
    /// firing belongs on the report no matter when it started, the same way
    /// the inbox works. Only the resolved half is windowed, by when it
    /// actually resolved rather than by <c>LastSeenUtc</c>, which a resolved
    /// alert does not update again.
    /// </remarks>
    public AlertReportView AlertsReport(
        AlertSeverity? severity = null,
        AlertLifecycleState? state = null,
        string? category = null,
        string? source = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        var to = toUtc ?? _clock.UtcNow;
        var from = fromUtc ?? to - DefaultReportWindow;
        var graph = _graphs.Current;

        var rows = ReportCandidates(from, to)
            .Where(a => severity is null || a.Severity == severity)
            .Where(a => state is null || a.State == state)
            .Where(a => category is null ||
                string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(a => source is null ||
                string.Equals(a.Source, source, StringComparison.OrdinalIgnoreCase))
            // Worst first, then most recent -- the same order the inbox uses.
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.LastSeenUtc)
            .Select(a => ToReportRow(a, graph))
            .ToList();

        return new AlertReportView
        {
            GeneratedAtUtc = _clock.UtcNow,
            FromUtc = from,
            ToUtc = to,
            Summary = Summarize(rows),
            Rows = rows,
        };
    }

    /// <summary>Open (in any of its three live states) plus resolved-within-range.</summary>
    private IReadOnlyList<AlertInstance> ReportCandidates(DateTimeOffset from, DateTimeOffset to) =>
        [.. _alerts.All.Where(a => a.IsConfirmed && IsInReport(a, from, to))];

    private static bool IsInReport(AlertInstance alert, DateTimeOffset from, DateTimeOffset to) =>
        alert.State != AlertLifecycleState.Resolved ||
        (ResolvedAtUtc(alert) is { } resolvedAt && resolvedAt >= from && resolvedAt <= to);

    /// <summary>When an instance last moved into <see cref="AlertLifecycleState.Resolved"/>.</summary>
    /// <remarks>
    /// From the transition history rather than <c>LastSeenUtc</c>: a resolved
    /// alert is not observed again, so its last-seen time is when the
    /// condition was last true, not when it stopped being one. A resolved
    /// alert with no such transition (state restored some other way) has no
    /// answer and is excluded rather than guessed at.
    /// </remarks>
    private static DateTimeOffset? ResolvedAtUtc(AlertInstance alert) =>
        alert.History.LastOrDefault(t => t.To == AlertLifecycleState.Resolved)?.AtUtc;

    private static AlertReportRow ToReportRow(AlertInstance alert, EntityGraph graph)
    {
        string? name = null;
        EntityKind? kind = null;

        if (alert.Entity is { } entityId && graph.Entities.TryGetValue(entityId, out var entity))
        {
            name = entity.DisplayName;
            kind = entity.Kind;
        }

        var acknowledged = LastActorTransition(alert, AlertTransitionReason.OperatorAcknowledged);
        var cleared = LastActorTransition(alert, AlertTransitionReason.OperatorCleared);

        return new AlertReportRow
        {
            Severity = alert.Severity,
            Title = alert.Title,
            EntityName = name,
            EntityKind = kind,
            Category = alert.Category,
            Source = alert.Source,
            State = alert.State,
            FirstSeenUtc = alert.FirstSeenUtc,
            LastSeenUtc = alert.LastSeenUtc,
            AcknowledgedBy = acknowledged.By,
            AcknowledgedAtUtc = acknowledged.AtUtc,
            ClearedBy = cleared.By,
            ClearedAtUtc = cleared.AtUtc,
            IsDerived = alert.IsDerived,
        };
    }

    /// <summary>
    /// The last time an operator caused this transition, and who -- <c>null</c>
    /// when it never happened. Only operator-attributed reasons carry an
    /// actor; see <see cref="AlertTransition.Actor"/>.
    /// </summary>
    private static (string? By, DateTimeOffset? AtUtc) LastActorTransition(
        AlertInstance alert, AlertTransitionReason reason)
    {
        var transition = alert.History.LastOrDefault(t => t.Reason == reason);
        return transition is null ? (null, null) : (transition.Actor, transition.AtUtc);
    }

    private static AlertReportSummary Summarize(List<AlertReportRow> rows)
    {
        var bySeverity = rows.GroupBy(r => r.Severity.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var value in Enum.GetValues<AlertSeverity>())
        {
            bySeverity.TryAdd(value.ToString(), 0);
        }

        var byState = rows.GroupBy(r => r.State.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var value in Enum.GetValues<AlertLifecycleState>())
        {
            byState.TryAdd(value.ToString(), 0);
        }

        return new AlertReportSummary
        {
            BySeverity = bySeverity,
            ByState = byState,
            Total = rows.Count,
        };
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
            TimeToFull = entity.Kind == EntityKind.Datastore ? TimeToFull(entityId) : null,
        };
    }

    /// <summary>
    /// The same estimate the fill-date rule makes, from the same history.
    /// </summary>
    /// <remarks>
    /// Computed on request from the stored series rather than remembered from
    /// the last cycle: the rule keeps nothing, and a copy of its answer would
    /// be a second model. The capacity is the latest one recorded, where the
    /// rule has the reading of the cycle it runs in; the two agree whenever
    /// the capacity has not just changed.
    /// </remarks>
    private TimeToFullView TimeToFull(EntityId datastore)
    {
        var now = _clock.UtcNow;
        var retention = _options.Retention;

        if (DatastoreTimeToFull.LatestCapacity(_observations, datastore, now, retention) is not { } capacity)
        {
            return new TimeToFullView
            {
                IsForecast = false,
                PointsUsed = 0,
                Reason = "NoCapacity",
                Summary =
                    $"Cannot estimate a fill date: no capacity reading was recorded in the last " +
                    $"{retention.Raw.TotalDays:0} days.",
            };
        }

        var estimate = DatastoreTimeToFull.Read(
            _observations, datastore, capacity, now, _options.DatastoreTimeToFull, retention);

        var summary = DatastoreTimeToFull.Explain(estimate);
        summary = char.ToUpperInvariant(summary[0]) + summary[1..] + (summary.EndsWith('.') ? "" : ".");

        return estimate switch
        {
            TimeToFullResult.Forecast f => new TimeToFullView
            {
                IsForecast = true,
                FullAtUtc = f.FullAtUtc,
                Days = f.Days,
                GrowthBytesPerDay = f.SlopePerDay,
                WindowFromUtc = f.Window.FromUtc,
                WindowToUtc = f.Window.ToUtc,
                PointsUsed = f.PointsUsed,
                Summary = summary,
            },
            TimeToFullResult.Refusal r => new TimeToFullView
            {
                IsForecast = false,
                GrowthBytesPerDay = r.SlopePerDay,
                WindowFromUtc = r.Window?.FromUtc,
                WindowToUtc = r.Window?.ToUtc,
                PointsUsed = r.PointsUsed,
                Reason = r.Reason.ToString(),
                Summary = summary,
            },
            _ => throw new InvalidOperationException("An estimate is a forecast or a refusal."),
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
    /// <summary>
    /// What each source could read, worst first.
    /// </summary>
    /// <remarks>
    /// Ordered so the blind rows are at the top and the complete ones at the
    /// bottom, because a report of forty rows in which two matter is read by
    /// nobody if the two are in the middle. Complete rows are kept rather than
    /// filtered out: "this was checked and it is fine" is the half that makes
    /// the other half trustworthy, and a screen that only ever shows problems
    /// cannot distinguish a healthy estate from a report that stopped running.
    /// </remarks>
    public IReadOnlyList<CoverageView> Coverage() =>
    [
        .. _coverage.Current
            .OrderBy(c => c.SourceInstanceId, StringComparer.Ordinal)
            .Select(c => new CoverageView
            {
                InstanceId = c.SourceInstanceId,
                MeasuredAtUtc = c.MeasuredAtUtc,
                Properties =
                [
                    .. c.Properties
                        .OrderByDescending(p => p.IsBlind)
                        .ThenBy(p => p.Answered - p.Asked)
                        .ThenBy(p => p.ObjectType, StringComparer.Ordinal)
                        .ThenBy(p => p.Property, StringComparer.Ordinal)
                        .Select(p => new CoveragePropertyView
                        {
                            ObjectType = p.ObjectType,
                            Property = p.Property,
                            Asked = p.Asked,
                            Answered = p.Answered,
                        }),
                ],
            }),
    ];

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
                PartialFailures = [.. c.PartialFailures.Select(f => new PartialFailureView
                {
                    Kind = f.Kind.ToString(),
                    Target = f.Target,
                    Detail = f.Detail,
                })],
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

        var budget = Math.Clamp(maxPoints, 1, 2_000);

        // Named explicitly rather than left to the store's width-only rule.
        // That rule picks the finest tier the budget allows and never asks
        // whether the tier still reaches back that far, so an hour-wide window
        // from last week chose raw, raw had been compacted away, and the answer
        // came back as an existing series with no points — which reads on
        // screen as a collector problem.
        var resolution = _options.Retention.RetainedResolutionFor(
            to - from, budget, _clock.UtcNow - from);

        var result = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(new EntityId(entityId), counter, instance ?? string.Empty),
            FromUtc = from,
            ToUtc = to,
            MaxPoints = budget,
            Resolution = resolution,
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
