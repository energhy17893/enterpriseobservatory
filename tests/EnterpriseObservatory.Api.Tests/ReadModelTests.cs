using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// The read surface, held to ADR-0007's governing rule: however many screens
/// there are, they are projections of one model.
/// </summary>
public class ReadModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly StubGraphStore _graphs = new();
    private readonly StubAlertStore _alerts = new();
    private readonly StubHealthStore _collectors = new();
    private readonly StubObservationStore _observations = new();

    private ReadModel Model() =>
        new(_graphs, _alerts, _collectors, _observations, MonitoringOptions.Default, new StubClock(T0));

    // --- overview ---------------------------------------------------------

    [Fact]
    public void The_overview_counts_alerts_by_severity()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical),
            Alert("b", AlertSeverity.Warning),
            Alert("c", AlertSeverity.Warning));

        var overview = Model().Overview();

        Assert.Equal(1, overview.CriticalAlerts);
        Assert.Equal(2, overview.WarningAlerts);
    }

    [Fact]
    public void An_unconfirmed_alert_is_not_counted_anywhere()
    {
        // Hysteresis exists so a one-cycle blip does not reach anybody. An
        // unconfirmed alert appearing in the overview count would defeat it
        // while leaving the inbox looking correct.
        GivenAlerts(Alert("a", AlertSeverity.Warning) with { IsConfirmed = false });

        var overview = Model().Overview();

        Assert.Equal(0, overview.WarningAlerts);
        Assert.Empty(Model().Alerts().Items);
    }

    [Fact]
    public void Every_health_state_appears_even_with_nothing_in_it()
    {
        // A missing key reads as "no answer" in a client; a zero reads as
        // "none", which is what we mean.
        GivenEntities(Host("h1", HealthState.Healthy));

        var overview = Model().Overview();

        Assert.Equal(Enum.GetValues<HealthState>().Length, overview.EntitiesByHealth.Count);
        Assert.Equal(0, overview.EntitiesByHealth[nameof(HealthState.Critical)]);
        Assert.Equal(1, overview.EntitiesByHealth[nameof(HealthState.Healthy)]);
    }

    [Fact]
    public void A_vanished_entity_is_counted_as_vanished_and_not_as_healthy()
    {
        GivenEntities(Host("h1", HealthState.Healthy) with
        {
            ObservationState = ObservationState.Vanished,
        });

        var overview = Model().Overview();

        Assert.Equal(1, overview.VanishedEntities);
        Assert.Equal(0, overview.EntitiesByHealth[nameof(HealthState.Healthy)]);
    }

    [Fact]
    public void The_overview_says_how_stale_it_may_be()
    {
        // ADR-0007 §6: stale data must never be presented as fresh. The client
        // cannot judge that without being told when we last actually looked.
        GivenCollectors(
            Health("vc-1", CollectorRole.Inventory, T0.AddMinutes(-14)),
            Health("vc-1", CollectorRole.Observation, T0.AddSeconds(-20)));

        var overview = Model().Overview();

        Assert.Equal(T0, overview.GeneratedAtUtc);
        Assert.Equal(T0.AddMinutes(-14), overview.OldestSuccessfulReadUtc);
    }

    [Fact]
    public void A_failing_collector_is_on_the_overview()
    {
        // It changes what every other number on the screen means: anything a
        // failing collector covers is unknown, not healthy.
        GivenCollectors(
            Health("vc-1", CollectorRole.Inventory, T0) with { Health = HealthState.Unknown },
            Health("vc-1", CollectorRole.Observation, T0));

        Assert.Equal(1, Model().Overview().FailingCollectors);
    }

    // --- the inbox --------------------------------------------------------

    [Fact]
    public void The_inbox_shows_the_worst_first()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Warning),
            Alert("b", AlertSeverity.Critical));

        var page = Model().Alerts();

        Assert.Equal(AlertSeverity.Critical, page.Items[0].Severity);
    }

    [Fact]
    public void A_resolved_alert_is_not_in_the_inbox()
    {
        GivenAlerts(Alert("a", AlertSeverity.Critical) with
        {
            State = AlertLifecycleState.Resolved,
        });

        Assert.Empty(Model().Alerts().Items);
    }

    [Fact]
    public void An_alert_suppressed_by_maintenance_is_still_shown()
    {
        // Maintenance stops the paging, not the reporting. Hiding it would mean
        // an operator working inside a window cannot see what they are doing.
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { SuppressedByWindowId = "w1" });

        var alert = Assert.Single(Model().Alerts().Items);

        Assert.Equal("w1", alert.SuppressedByWindowId);
        Assert.Equal(1, Model().Overview().SuppressedAlerts);
    }

    [Fact]
    public void An_alert_carries_the_name_of_the_entity_it_is_about()
    {
        // Resolved here so a client rendering ten alerts does not need the
        // whole graph, and never falls back to showing a raw identifier.
        GivenEntities(Host("h1", HealthState.Critical));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") });

        Assert.Equal("h1.corp.local", Assert.Single(Model().Alerts().Items).EntityName);
    }

    [Fact]
    public void Filters_narrow_the_one_list_rather_than_producing_another()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical) with { Category = "Hardware", Source = "ilo-1" },
            Alert("b", AlertSeverity.Critical) with { Category = "Configuration", Source = "vc-1" });

        Assert.Single(Model().Alerts(category: "Hardware").Items);
        Assert.Single(Model().Alerts(source: "vc-1").Items);
        Assert.Equal(2, Model().Alerts().Total);
    }

    [Fact]
    public void A_page_reports_the_total_as_well_as_its_slice()
    {
        // So the client can say "1–2 of 5" rather than discovering the end by
        // running out.
        GivenAlerts([.. Enumerable.Range(0, 5).Select(i => Alert($"a{i}", AlertSeverity.Warning))]);

        var page = Model().Alerts(limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public void A_page_size_beyond_the_maximum_is_clamped_rather_than_honoured()
    {
        // An unbounded page is a request to serialise the entire estate.
        GivenAlerts(Alert("a", AlertSeverity.Warning));

        Assert.Equal(ReadModel.MaxLimit, Model().Alerts(limit: 100_000).Limit);
    }

    // --- the explorer -----------------------------------------------------

    [Fact]
    public void A_vanished_entity_is_hidden_unless_it_is_asked_for()
    {
        GivenEntities(
            Host("h1", HealthState.Healthy),
            Host("h2", HealthState.Healthy) with { ObservationState = ObservationState.Vanished });

        Assert.Single(Model().Entities().Items);
        Assert.Equal(2, Model().Entities(includeVanished: true).Total);
    }

    [Fact]
    public void A_vanished_entity_reports_unknown_health_not_its_last_colour()
    {
        GivenEntities(Host("h1", HealthState.Healthy) with
        {
            ObservationState = ObservationState.Vanished,
        });

        var view = Assert.Single(Model().Entities(includeVanished: true).Items);

        Assert.Equal(HealthState.Unknown, view.Health);
    }

    [Fact]
    public void The_explorer_carries_each_entitys_alert_count()
    {
        GivenEntities(Host("h1", HealthState.Critical), Host("h2", HealthState.Healthy));
        GivenAlerts(
            Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") },
            Alert("b", AlertSeverity.Warning) with { Entity = new EntityId("h1") });

        var items = Model().Entities().Items;

        Assert.Equal(2, items.Single(e => e.Id == "h1").AlertCount);
        Assert.Equal(0, items.Single(e => e.Id == "h2").AlertCount);
    }

    // --- entity detail ----------------------------------------------------

    [Fact]
    public void An_unknown_entity_is_not_an_empty_one()
    {
        Assert.Null(Model().Entity("nope"));
    }

    [Fact]
    public void Detail_shows_edges_in_both_directions_and_says_which_way_they_point()
    {
        // "This VM runs on that host" and "this host runs that VM" are
        // different sentences; the client must not have to guess which one it
        // is holding.
        GivenEntities(Host("vm1", HealthState.Healthy), Host("h1", HealthState.Healthy));
        GivenRelationships(new Relationship
        {
            From = new EntityId("vm1"),
            To = new EntityId("h1"),
            Kind = RelationshipKind.RunsOn,
            ObservedAtUtc = T0,
        });

        var fromVm = Assert.Single(Model().Entity("vm1")!.Relationships);
        var fromHost = Assert.Single(Model().Entity("h1")!.Relationships);

        Assert.True(fromVm.IsOutgoing);
        Assert.Equal("h1", fromVm.OtherId);

        Assert.False(fromHost.IsOutgoing);
        Assert.Equal("vm1", fromHost.OtherId);
    }

    [Fact]
    public void An_entitys_alerts_are_the_same_instances_the_inbox_shows()
    {
        // The defect ADR-0007 exists to prevent: in the previous product the
        // alert page read lifecycle instances while vendor pages read the raw
        // per-cycle snapshot, so the same alert could be acknowledged on one
        // screen and open on another.
        GivenEntities(Host("h1", HealthState.Critical));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with
        {
            Entity = new EntityId("h1"),
            State = AlertLifecycleState.Acknowledged,
        });

        var onEntity = Assert.Single(Model().Entity("h1")!.Alerts);
        var inInbox = Assert.Single(Model().Alerts().Items);

        Assert.Equal(inInbox, onEntity);
    }

    [Fact]
    public void An_edge_to_an_entity_we_do_not_have_is_not_rendered()
    {
        // A link the operator cannot follow is worse than no link.
        GivenEntities(Host("vm1", HealthState.Healthy));
        GivenRelationships(new Relationship
        {
            From = new EntityId("vm1"),
            To = new EntityId("gone"),
            Kind = RelationshipKind.RunsOn,
            ObservedAtUtc = T0,
        });

        Assert.Empty(Model().Entity("vm1")!.Relationships);
    }

    // --- collectors -------------------------------------------------------

    [Fact]
    public void The_two_roles_of_one_source_are_reported_separately()
    {
        GivenCollectors(
            Health("vc-1", CollectorRole.Inventory, T0) with { Health = HealthState.Unknown },
            Health("vc-1", CollectorRole.Observation, T0));

        var views = Model().Collectors();

        Assert.Equal(2, views.Count);
        Assert.Equal(HealthState.Unknown, views.Single(c => c.Role == "Inventory").Health);
        Assert.Equal(HealthState.Healthy, views.Single(c => c.Role == "Observation").Health);
    }

    // --- the grouped view -------------------------------------------------

    [Fact]
    public void The_two_views_account_for_exactly_the_same_alerts()
    {
        // ADR-0007 5.1: no alert may live only inside a group, and none may
        // fall between the two lists either. Counted twice it is acted on
        // twice; counted never it is invisible.
        GivenEntities(Host("esx01", HealthState.Critical), Host("vm-a", HealthState.Warning));
        GivenRelationships(new Relationship
        {
            From = new EntityId("vm-a"),
            To = new EntityId("esx01"),
            Kind = RelationshipKind.RunsOn,
            ObservedAtUtc = T0,
        });

        GivenAlerts(
            Alert("down", AlertSeverity.Critical) with { Entity = new EntityId("esx01") },
            Alert("slow", AlertSeverity.Warning) with { Entity = new EntityId("vm-a") },
            Alert("lonely", AlertSeverity.Warning));

        var board = Model().Events();
        var flat = Model().Alerts();

        var inBoard = board.Events
            .SelectMany(e => e.Alerts)
            .Concat(board.Ungrouped)
            .Select(a => a.Fingerprint)
            .ToList();

        Assert.Equal(flat.Total, board.TotalAlerts);
        Assert.Equal(board.TotalAlerts, inBoard.Count);
        Assert.Equal(inBoard.Count, inBoard.Distinct().Count());
    }

    [Fact]
    public void An_event_header_carries_the_real_counts()
    {
        // An operator will not accept a folded group without seeing what was
        // folded into it.
        GivenEntities(Host("esx01", HealthState.Critical), Host("vm-a", HealthState.Warning));
        GivenRelationships(new Relationship
        {
            From = new EntityId("vm-a"),
            To = new EntityId("esx01"),
            Kind = RelationshipKind.RunsOn,
            ObservedAtUtc = T0,
        });

        GivenAlerts(
            Alert("down", AlertSeverity.Critical) with { Entity = new EntityId("esx01") },
            Alert("slow", AlertSeverity.Warning) with { Entity = new EntityId("vm-a") });

        var incident = Assert.Single(Model().Events().Events);

        Assert.Equal(2, incident.AlertCount);
        Assert.Equal(2, incident.EntityCount);
        Assert.Equal(2, incident.Alerts.Count);
        Assert.Equal(AlertSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void The_explanation_names_the_entities_rather_than_their_identifiers()
    {
        // "esx01.corp.local runs vm-a.corp.local" is an explanation; a pair of
        // opaque ids is a puzzle.
        GivenEntities(Host("esx01", HealthState.Critical), Host("vm-a", HealthState.Warning));
        GivenRelationships(new Relationship
        {
            From = new EntityId("vm-a"),
            To = new EntityId("esx01"),
            Kind = RelationshipKind.RunsOn,
            ObservedAtUtc = T0,
        });

        GivenAlerts(
            Alert("down", AlertSeverity.Critical) with { Entity = new EntityId("esx01") },
            Alert("slow", AlertSeverity.Warning) with { Entity = new EntityId("vm-a") });

        var link = Assert.Single(Assert.Single(Model().Events().Events).Explanation);

        Assert.Equal("esx01.corp.local", link.FromName);
        Assert.Equal("vm-a.corp.local", link.ToName);
    }

    [Fact]
    public void A_coincidence_is_offered_and_never_folded_into_an_event()
    {
        // Three unrelated alerts at lunchtime look exactly like one failure,
        // so the product points rather than claims.
        GivenAlerts(
            Alert("a", AlertSeverity.Warning),
            Alert("b", AlertSeverity.Warning),
            Alert("c", AlertSeverity.Warning));

        var board = Model().Events();

        Assert.Empty(board.Events);
        Assert.Equal(3, Assert.Single(board.Suggestions).Alerts.Count);
        Assert.Equal(3, board.Ungrouped.Count);
    }

    // --- fixtures ---------------------------------------------------------

    private void GivenEntities(params Entity[] entities) =>
        _graphs.Replace(_graphs.Current with { Entities = entities.ToDictionary(e => e.Id) });

    private void GivenRelationships(params Relationship[] relationships) =>
        _graphs.Replace(_graphs.Current with { Relationships = relationships });

    private void GivenAlerts(params AlertInstance[] alerts) => _alerts.Set(alerts);

    private void GivenCollectors(params CollectorHealth[] health) => _collectors.Merge(health);

    private static Entity Host(string id, HealthState health) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.EsxiHost,
        DisplayName = $"{id}.corp.local",
        SourceInstanceId = "vc-1",
        Health = health,
        LastSeenUtc = T0,
    };

    private static AlertInstance Alert(string id, AlertSeverity severity) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Hardware", id, id),
        Severity = severity,
        State = AlertLifecycleState.Open,
        Title = id,
        Description = $"{id} is unhappy.",
        Category = "Hardware",
        Source = "vc-1",
        Scope = AlertScopes.Inventory,
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
    };

    private static CollectorHealth Health(string id, CollectorRole role, DateTimeOffset lastSuccess) => new()
    {
        InstanceId = id,
        Role = role,
        Health = HealthState.Healthy,
        LastSuccessUtc = lastSuccess,
    };

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubGraphStore : IEntityGraphStore
    {
        public EntityGraph Current { get; private set; } = EntityGraph.Empty;

        public void Replace(EntityGraph graph) => Current = graph;
    }

    private sealed class StubAlertStore : IAlertStateStore
    {
        private List<AlertInstance> _instances = [];

        public IReadOnlyList<AlertInstance> All => _instances;

        public void Set(IEnumerable<AlertInstance> instances) => _instances = [.. instances];

        public IReadOnlyList<AlertInstance> InstancesIn(string scope) =>
            [.. _instances.Where(i => i.Scope == scope)];

        public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope) => [];

        public AlertReconciliationResult Reconcile(
            string scope,
            Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile) =>
            throw new NotSupportedException("The read model never writes.");

        public AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change) =>
            throw new NotSupportedException("The read model never writes.");

        public IReadOnlyList<AlertInstance> MutateMany(
            IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change) =>
            throw new NotSupportedException("The read model never writes.");

        public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints) =>
            throw new NotSupportedException("The read model never writes.");
    }

    // --- resolution selection ---------------------------------------------

    [Fact]
    public void A_recent_window_is_answered_from_the_finest_tier()
    {
        // The ordinary case, unchanged: an hour wide and an hour old, well
        // inside the two-day raw window.
        Model().Series("vc-1:host-1", "cpu.usage.average", null, T0.AddHours(-1), T0, 720);

        Assert.Equal(SeriesResolution.Raw, _observations.LastQuery!.Resolution);
    }

    [Fact]
    public void A_narrow_window_in_the_past_is_answered_from_a_tier_that_still_has_it()
    {
        // The defect this closes. Resolution used to be chosen from the width
        // of the range alone, so an hour-wide window picked Raw whether it was
        // this hour or one from a fortnight ago -- and raw is kept two days.
        // The five-minute bucket was sitting there for another twenty-eight,
        // unreachable, and the screen said "Nothing was recorded in this
        // window", which its own code comment calls a collector problem.
        var tenDaysAgo = T0.AddDays(-10);

        Model().Series(
            "vc-1:host-1", "cpu.usage.average", null, tenDaysAgo, tenDaysAgo.AddHours(1), 720);

        Assert.Equal(SeriesResolution.FiveMinutes, _observations.LastQuery!.Resolution);
    }

    [Fact]
    public void A_window_older_than_anything_kept_still_answers_in_the_coarsest_tier()
    {
        // Beyond every window. It comes back empty, which is true, and it
        // comes back with a resolution named rather than as an exception --
        // the caller gets the shape of answer it always gets and the emptiness
        // is data.
        var longAgo = T0.AddDays(-200);

        Model().Series("vc-1:host-1", "cpu.usage.average", null, longAgo, longAgo.AddHours(1), 720);

        Assert.Equal(SeriesResolution.OneHour, _observations.LastQuery!.Resolution);
    }

    /// <summary>
    /// Measurements are covered by the persistence tests against the real
    /// store; the read model only passes them through.
    /// </summary>
    private sealed class StubObservationStore : IObservationStore
    {
        public List<Observation> Appended { get; } = [];

        public void Append(IReadOnlyList<Observation> observations) => Appended.AddRange(observations);

        /// <summary>The last query it was handed, so the caller's choice is checkable.</summary>
        public SeriesQuery? LastQuery { get; private set; }

        public SeriesResult Query(SeriesQuery query)
        {
            LastQuery = query;

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = query.Resolution ?? SeriesResolution.Raw,
                Exists = false,
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

        public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) => new();
    }

    private sealed class StubHealthStore : ICollectorHealthStore
    {
        private readonly List<CollectorHealth> _health = [];

        public IReadOnlyList<CollectorHealth> Current => _health;

        public void Merge(IReadOnlyList<CollectorHealth> health) => _health.AddRange(health);
    }
}
