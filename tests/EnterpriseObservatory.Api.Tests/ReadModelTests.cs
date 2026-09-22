using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

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
    private readonly StubCoverageStore _coverage = new();
    private readonly StubObservationStore _observations = new();
    private readonly StubComplianceStore _compliance = new();

    private ReadModel Model() =>
        new(_graphs, _alerts, _collectors, _coverage, _observations, MonitoringOptions.Default, new StubClock(T0), _compliance);

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

    // --- freshness (ADR-0026 point 3) ----------------------------------------

    [Fact]
    public void The_open_count_is_two_numbers_fresh_and_stale_and_unknown_is_counted_apart()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical),
            Alert("b", AlertSeverity.Warning) with
            {
                StaleSinceUtc = T0.AddMinutes(-5),
                StaleReason = UnknownReason.SourceSilent,
                StaleDetail = "source 'vc-1' did not report this cycle",
            },
            Alert("c", AlertSeverity.Warning) with { State = AlertLifecycleState.Unknown });

        var overview = Model().Overview();

        Assert.Equal(1, overview.FreshOpenAlerts);
        Assert.Equal(1, overview.StaleOpenAlerts);
        Assert.Equal(1, overview.UnknownAlerts);

        // Unknown is out of the open counts (design note §2).
        Assert.Equal(1, overview.WarningAlerts);
    }

    [Fact]
    public void An_alert_view_carries_its_evidence_time_and_why_it_is_stale()
    {
        GivenAlerts(Alert("b", AlertSeverity.Warning) with
        {
            EvidenceAtUtc = T0.AddMinutes(-7),
            StaleSinceUtc = T0.AddMinutes(-5),
            StaleReason = UnknownReason.SourceSilent,
            StaleDetail = "source 'vc-1' did not report this cycle",
        });

        var view = Assert.Single(Model().Alerts().Items);

        Assert.Equal(T0.AddMinutes(-7), view.EvidenceAtUtc);
        Assert.True(view.IsStale);
        Assert.Equal(T0.AddMinutes(-5), view.StaleSinceUtc);
        Assert.Equal(UnknownReason.SourceSilent, view.StaleReason);
        Assert.Equal("source 'vc-1' did not report this cycle", view.StaleDetail);
    }

    [Fact]
    public void Alerts_in_the_unknown_state_are_listed_under_their_own_filter_only()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical),
            Alert("c", AlertSeverity.Warning) with { State = AlertLifecycleState.Unknown });

        Assert.DoesNotContain(Model().Alerts().Items, a => a.State == AlertLifecycleState.Unknown);

        var unknown = Assert.Single(Model().Alerts(state: AlertLifecycleState.Unknown).Items);
        Assert.Equal(AlertLifecycleState.Unknown, unknown.State);
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

    // --- alert report (M5.1) -----------------------------------------------

    [Fact]
    public void The_report_includes_open_alerts_regardless_of_the_range()
    {
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { FirstSeenUtc = T0.AddDays(-90) });

        var report = Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0);

        Assert.Single(report.Rows);
    }

    [Fact]
    public void The_report_includes_an_alert_resolved_inside_the_range()
    {
        var resolvedAt = T0.AddHours(-2);
        GivenAlerts(Resolved("a", AlertSeverity.Warning, resolvedAt));

        var report = Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0);

        Assert.Single(report.Rows);
    }

    [Fact]
    public void The_report_includes_an_alert_resolved_inside_the_range_after_it_has_been_retired()
    {
        // The row goes the cycle after the alert resolves; the history stays
        // (ADR-0026, migration 14). "Resolved in the window" is read from it.
        var resolvedAt = T0.AddHours(-2);
        var alert = Resolved("a", AlertSeverity.Warning, resolvedAt);
        GivenAlerts(alert);
        _alerts.Retire(alert.Fingerprint);
        Assert.Empty(_alerts.All);

        var report = Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0);

        var row = Assert.Single(report.Rows);
        Assert.Equal(AlertLifecycleState.Resolved, row.State);
    }

    [Fact]
    public void The_report_excludes_an_alert_resolved_outside_the_range()
    {
        var resolvedAt = T0.AddDays(-30);
        GivenAlerts(Resolved("a", AlertSeverity.Warning, resolvedAt));

        var report = Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0);

        Assert.Empty(report.Rows);
    }

    [Fact]
    public void An_unconfirmed_alert_never_reaches_the_report()
    {
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { IsConfirmed = false });

        Assert.Empty(Model().AlertsReport().Rows);
    }

    [Fact]
    public void The_report_defaults_to_the_last_seven_days_when_no_range_is_given()
    {
        GivenAlerts(Resolved("recent", AlertSeverity.Warning, T0.AddDays(-1)));
        GivenAlerts(Resolved("old", AlertSeverity.Warning, T0.AddDays(-10)));

        var report = Model().AlertsReport();

        Assert.Equal(T0.AddDays(-7), report.FromUtc);
        Assert.Equal(T0, report.ToUtc);
    }

    [Fact]
    public void The_report_carries_the_entity_name_and_kind()
    {
        GivenEntities(Host("h1", HealthState.Critical));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") });

        var row = Assert.Single(Model().AlertsReport().Rows);

        Assert.Equal("h1.corp.local", row.EntityName);
        Assert.Equal(EntityKind.EsxiHost, row.EntityKind);
    }

    [Fact]
    public void The_report_names_who_acknowledged_and_who_cleared_it_and_when()
    {
        var alert = Alert("a", AlertSeverity.Critical) with
        {
            State = AlertLifecycleState.Resolved,
            History =
            [
                new AlertTransition
                {
                    From = AlertLifecycleState.Open,
                    To = AlertLifecycleState.Acknowledged,
                    Reason = AlertTransitionReason.OperatorAcknowledged,
                    AtUtc = T0.AddHours(-3),
                    Actor = "alice",
                },
                new AlertTransition
                {
                    From = AlertLifecycleState.Acknowledged,
                    To = AlertLifecycleState.Resolved,
                    Reason = AlertTransitionReason.OperatorCleared,
                    AtUtc = T0.AddHours(-1),
                    Actor = "bob",
                },
            ],
        };
        GivenAlerts(alert);

        var row = Assert.Single(Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0).Rows);

        Assert.Equal("alice", row.AcknowledgedBy);
        Assert.Equal(T0.AddHours(-3), row.AcknowledgedAtUtc);
        Assert.Equal("bob", row.ClearedBy);
        Assert.Equal(T0.AddHours(-1), row.ClearedAtUtc);
    }

    [Fact]
    public void A_condition_that_cleared_itself_names_nobody()
    {
        // Only an operator's own clear is attributed. The condition simply
        // going away is not something a person did.
        var alert = Alert("a", AlertSeverity.Warning) with
        {
            State = AlertLifecycleState.Resolved,
            History =
            [
                new AlertTransition
                {
                    From = AlertLifecycleState.Open,
                    To = AlertLifecycleState.Resolved,
                    Reason = AlertTransitionReason.ConditionCleared,
                    AtUtc = T0.AddHours(-1),
                },
            ],
        };
        GivenAlerts(alert);

        var row = Assert.Single(Model().AlertsReport(fromUtc: T0.AddDays(-1), toUtc: T0).Rows);

        Assert.Null(row.ClearedBy);
    }

    [Fact]
    public void The_report_summarizes_counts_by_severity_and_state()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical),
            Alert("b", AlertSeverity.Warning),
            Alert("c", AlertSeverity.Warning) with { State = AlertLifecycleState.Acknowledged });

        var summary = Model().AlertsReport().Summary;

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.BySeverity[nameof(AlertSeverity.Critical)]);
        Assert.Equal(2, summary.BySeverity[nameof(AlertSeverity.Warning)]);
        Assert.Equal(0, summary.BySeverity[nameof(AlertSeverity.Info)]);
        Assert.Equal(2, summary.ByState[nameof(AlertLifecycleState.Open)]);
        Assert.Equal(1, summary.ByState[nameof(AlertLifecycleState.Acknowledged)]);
        Assert.Equal(0, summary.ByState[nameof(AlertLifecycleState.Resolved)]);
    }

    [Fact]
    public void The_report_honours_the_same_filters_as_the_alert_list()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical) with { Category = "Hardware", Source = "ilo-1" },
            Alert("b", AlertSeverity.Critical) with { Category = "Configuration", Source = "vc-1" });

        Assert.Single(Model().AlertsReport(category: "Hardware").Rows);
        Assert.Single(Model().AlertsReport(source: "vc-1").Rows);
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

    /// <summary>A resolved instance, with the transition that resolved it -- what the report windows by.</summary>
    private static AlertInstance Resolved(string id, AlertSeverity severity, DateTimeOffset resolvedAtUtc) =>
        Alert(id, severity) with
        {
            State = AlertLifecycleState.Resolved,
            History =
            [
                new AlertTransition
                {
                    From = AlertLifecycleState.Open,
                    To = AlertLifecycleState.Resolved,
                    Reason = AlertTransitionReason.ConditionCleared,
                    AtUtc = resolvedAtUtc,
                },
            ],
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

        public void Set(IEnumerable<AlertInstance> instances)
        {
            _instances = [.. instances];
            _history.AddRange(_instances);
        }

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

        /// <summary>What the durable history would hold: every instance ever set, retired or not.</summary>
        private readonly List<AlertInstance> _history = [];

        /// <summary>Takes an instance out of the store and leaves it in the history, as retiring does.</summary>
        public void Retire(AlertFingerprint fingerprint) =>
            _instances = [.. _instances.Where(i => i.Fingerprint != fingerprint)];

        public IReadOnlyList<AlertInstance> ResolvedBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        [
            .. _history
                .Concat(_instances)
                .DistinctBy(i => (i.Fingerprint, i.FirstSeenUtc))
                .Where(i => i.History.Count > 0 && i.History[^1].To == AlertLifecycleState.Resolved)
                .Where(i => i.History.Any(t =>
                    t.To == AlertLifecycleState.Resolved && t.From != AlertLifecycleState.Resolved &&
                    t.AtUtc >= fromUtc && t.AtUtc <= toUtc)),
        ];

        public int PruneHistory(DateTimeOffset olderThanUtc) =>
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

    // --- time to full ----------------------------------------------------

    private const double Gb = 1024d * 1024 * 1024;

    private static Entity Datastore(string id) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.Datastore,
        DisplayName = "vmfs01",
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
    };

    private static AggregatedSample Bucket(DateTimeOffset at, double value) => new()
    {
        StartUtc = at,
        Min = value,
        Max = value,
        Sum = value,
        Count = 1,
        Last = value,
    };

    private void GivenCapacity(double bytes) =>
        _observations.Recorded[CapacityCounters.DatastoreCapacity] = [Bucket(T0.AddMinutes(-5), bytes)];

    [Fact]
    public void A_datastore_page_says_when_it_fills_and_over_what_window()
    {
        // 1 GB a day for 21 days, hourly, on course for 50 GB of 100 now: fifty
        // days left, and the window it was measured over travels with it.
        GivenEntities(Datastore("vc-1:ds-1"));
        GivenCapacity(100 * Gb);
        _observations.Recorded[CapacityCounters.DatastoreUsed] =
        [
            .. Enumerable.Range(0, 21 * 24).Select(i => Bucket(T0.AddHours(i - (21 * 24)), (29 + (i / 24d)) * Gb)),
        ];

        var forecast = Model().Entity("vc-1:ds-1")!.TimeToFull!;

        Assert.True(forecast.IsForecast);
        Assert.Equal(50d, forecast.Days!.Value, 3);
        Assert.Equal(T0.AddDays(50), forecast.FullAtUtc!.Value, TimeSpan.FromMinutes(1));
        Assert.Equal(T0.AddDays(-21), forecast.WindowFromUtc);
        Assert.Equal(T0.AddHours(-1), forecast.WindowToUtc);
        Assert.Equal(21 * 24, forecast.PointsUsed);
        Assert.StartsWith("Fills in 50 days (on ", forecast.Summary, StringComparison.Ordinal);
        Assert.Contains("21 days of history", forecast.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_datastore_page_shows_a_refusal_as_an_answer()
    {
        // A day of history is not a trend. The page must say so rather than
        // show nothing, or "not filling" and "not computed" look the same.
        GivenEntities(Datastore("vc-1:ds-1"));
        GivenCapacity(100 * Gb);
        _observations.Recorded[CapacityCounters.DatastoreUsed] =
        [
            .. Enumerable.Range(0, 24).Select(i => Bucket(T0.AddHours(i - 24), (30 + i) * Gb)),
        ];

        var refusal = Model().Entity("vc-1:ds-1")!.TimeToFull!;

        Assert.False(refusal.IsForecast);
        Assert.Equal("InsufficientHistory", refusal.Reason);
        Assert.Null(refusal.FullAtUtc);
        Assert.StartsWith("Cannot estimate a fill date: ", refusal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("..", refusal.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_datastore_with_no_capacity_recorded_says_so()
    {
        GivenEntities(Datastore("vc-1:ds-1"));

        var refusal = Model().Entity("vc-1:ds-1")!.TimeToFull!;

        Assert.False(refusal.IsForecast);
        Assert.Equal("NoCapacity", refusal.Reason);
    }

    [Fact]
    public void Only_a_datastore_carries_a_fill_date()
    {
        GivenEntities(Host("h1", HealthState.Healthy));

        Assert.Null(Model().Entity("h1")!.TimeToFull);
        Assert.Empty(_observations.Queries);
    }

    // --- HA scorecard (M8.1) ------------------------------------------------

    private static readonly ClusterHighAvailabilityPolicy HaRules = ClusterHighAvailabilityPolicy.Default;

    private static Entity Cluster(string id, params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.Cluster,
        DisplayName = "Prod-Cluster",
        SourceInstanceId = "vc-1",
        Health = HealthState.Unknown,
        LastSeenUtc = T0,
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void A_cluster_page_shows_its_ha_configuration()
    {
        GivenEntities(Cluster(
            "vc-1:domain-c1",
            (HaRules.EnabledSetting, "true"),
            (HaRules.AdmissionControlEnabledSetting, "true"),
            ("dasConfig.admissionControlPolicy.type", "ClusterFailoverResourceAdmissionControlPolicy"),
            (HaRules.HostMonitoringSetting, "enabled"),
            ("dasConfig.vmMonitoring", "vmAndAppMonitoring"),
            (HaRules.ApdResponseSetting, "restartConservative"),
            (HaRules.PdlResponseSetting, "restartAggressive"),
            (HaRules.HeartbeatDatastoreCountSetting, "2"),
            ("dasConfig.hBDatastoreCandidatePolicy", "allFeasibleDsWithUserPreference"),
            (HaRules.IgnoreRedundantNetworkWarningSetting, "false")));

        var card = Model().Entity("vc-1:domain-c1")!.HaScorecard!;

        Assert.True(card.Enabled);
        Assert.True(card.AdmissionControlEnabled);
        Assert.Equal("ClusterFailoverResourceAdmissionControlPolicy", card.AdmissionControlPolicyType);
        Assert.Equal("enabled", card.HostMonitoring);
        Assert.Equal("vmAndAppMonitoring", card.VmMonitoring);
        Assert.Equal("restartConservative", card.ApdResponse);
        Assert.Equal("restartAggressive", card.PdlResponse);
        Assert.Equal(2, card.HeartbeatDatastoreCount);
        Assert.Equal("allFeasibleDsWithUserPreference", card.HeartbeatDatastoreCandidatePolicy);
        Assert.False(card.RedundantNetworkWarningSilenced);
        Assert.Empty(card.Findings);
    }

    [Fact]
    public void A_cluster_with_no_ha_configuration_read_shows_an_all_null_card()
    {
        GivenEntities(Cluster("vc-1:domain-c1"));

        var card = Model().Entity("vc-1:domain-c1")!.HaScorecard!;

        Assert.Null(card.Enabled);
        Assert.Null(card.HeartbeatDatastoreCount);
        Assert.Empty(card.Findings);
    }

    [Fact]
    public void The_scorecard_carries_the_clusters_ha_findings_in_every_state()
    {
        GivenEntities(Cluster("vc-1:domain-c1", (HaRules.EnabledSetting, "true")));
        _compliance.Rows.AddRange(
        [
            Finding(ContinuityControls.HaAdmissionControl, new EntityId("vc-1:domain-c1"), ComplianceVerdict.Failing),
            Finding(ContinuityControls.HaEnabled, new EntityId("vc-1:domain-c1"), ComplianceVerdict.Passing),
            Finding(ContinuityControls.DrsRule, new EntityId("vc-1:domain-c1"), ComplianceVerdict.Failing, "uuid-1"),
        ]);

        var card = Model().Entity("vc-1:domain-c1")!.HaScorecard!;

        Assert.Equal(
            [ContinuityControls.HaEnabled, ContinuityControls.HaAdmissionControl],
            card.Findings.Select(f => f.ControlId));
        Assert.Equal(FindingState.Failing, card.Findings[1].State);
        Assert.Equal("HA admission control is enabled", card.Findings[1].Title);
    }

    [Fact]
    public void An_ha_alarm_does_not_appear_on_the_ha_scorecard()
    {
        // The scorecard reads findings now (ADR-0024); an alarm, even one
        // left over from the retired rule, is not one of them.
        GivenEntities(Cluster("vc-1:domain-c1", (HaRules.EnabledSetting, "false")));
        GivenAlerts(Alert("cluster-ha-scorecard-ha-disabled", AlertSeverity.Critical) with
        {
            Category = "Configuration",
            Entity = new EntityId("vc-1:domain-c1"),
        });

        var card = Model().Entity("vc-1:domain-c1")!.HaScorecard!;

        Assert.Empty(card.Findings);
    }

    [Fact]
    public void Only_a_cluster_carries_an_ha_scorecard()
    {
        GivenEntities(Host("h1", HealthState.Healthy));

        Assert.Null(Model().Entity("h1")!.HaScorecard);
    }

    // --- cluster N+1 (M8.2) --------------------------------------------------

    private void GivenTwoHostCluster(double cpuUsagePercent, double memUsagePercent)
    {
        GivenEntities(
            Cluster("vc-1:domain-c1"), Host("h1", HealthState.Healthy), Host("h2", HealthState.Healthy));

        GivenRelationships(
            new Relationship
            {
                From = new EntityId("h1"),
                To = new EntityId("vc-1:domain-c1"),
                Kind = RelationshipKind.PartOf,
                ObservedAtUtc = T0,
            },
            new Relationship
            {
                From = new EntityId("h2"),
                To = new EntityId("vc-1:domain-c1"),
                Kind = RelationshipKind.PartOf,
                ObservedAtUtc = T0,
            });

        // The stub keys recorded points by counter only, so both hosts read
        // the same value -- enough to check the arithmetic, not to tell the
        // hosts apart.
        _observations.Recorded["cpu.usage.average"] = [Bucket(T0, cpuUsagePercent)];
        _observations.Recorded["mem.usage.average"] = [Bucket(T0, memUsagePercent)];
    }

    [Fact]
    public void A_cluster_page_shows_n_plus_one_for_cpu_and_memory_separately()
    {
        // 2 hosts: available after losing one = 1 * 90% = 0.9 host-equivalents.
        // CPU: 2 * 30% = 0.6, holds. Memory: 2 * 90% = 1.8, already fails.
        GivenTwoHostCluster(cpuUsagePercent: 30, memUsagePercent: 90);

        var failover = Model().Entity("vc-1:domain-c1")!.ClusterFailover!;

        Assert.Equal(2, failover.HostCount);

        Assert.Equal(0.6, failover.Cpu.DemandHosts!.Value, 6);
        Assert.Equal(0.9, failover.Cpu.AvailableAfterFailoverHosts, 6);
        Assert.True(failover.Cpu.HoldsNow);

        Assert.Equal(1.8, failover.Memory.DemandHosts!.Value, 6);
        Assert.False(failover.Memory.HoldsNow);
    }

    [Fact]
    public void The_date_is_shown_as_a_forecast_or_a_refusal_never_a_missing_answer()
    {
        GivenTwoHostCluster(cpuUsagePercent: 30, memUsagePercent: 30);

        var cpu = Model().Entity("vc-1:domain-c1")!.ClusterFailover!.Cpu;

        Assert.NotNull(cpu.Date);
        Assert.False(string.IsNullOrWhiteSpace(cpu.Date!.Summary));
    }

    [Fact]
    public void A_single_host_cluster_has_no_n_plus_one_answer()
    {
        // N+1 asks what survives losing one host, which cannot be asked of a
        // cluster with only one.
        GivenEntities(Cluster("vc-1:domain-c1"), Host("h1", HealthState.Healthy));
        GivenRelationships(new Relationship
        {
            From = new EntityId("h1"),
            To = new EntityId("vc-1:domain-c1"),
            Kind = RelationshipKind.PartOf,
            ObservedAtUtc = T0,
        });

        Assert.Null(Model().Entity("vc-1:domain-c1")!.ClusterFailover);
    }

    [Fact]
    public void Only_a_cluster_carries_an_n_plus_one_answer()
    {
        GivenEntities(Host("h1", HealthState.Healthy));

        Assert.Null(Model().Entity("h1")!.ClusterFailover);
    }

    // --- capacity report (M5.3) --------------------------------------------

    private void GivenFree(double bytes) =>
        _observations.Recorded[CapacityCounters.DatastoreFree] = [Bucket(T0.AddMinutes(-5), bytes)];

    private void GivenProvisioned(double bytes) =>
        _observations.Recorded[CapacityCounters.DatastoreProvisioned] = [Bucket(T0.AddMinutes(-5), bytes)];

    [Fact]
    public void A_datastore_with_no_capacity_reading_is_a_refusal_row_not_a_blank_one()
    {
        GivenEntities(Datastore("vc-1:ds-1"));

        var row = Assert.Single(Model().CapacityReport().Rows);

        Assert.False(row.TimeToFull.IsForecast);
        Assert.Equal("NoCapacity", row.TimeToFull.Reason);
        Assert.StartsWith("Cannot estimate", row.TimeToFull.Summary, StringComparison.Ordinal);
        Assert.Null(row.CapacityBytes);
        Assert.Equal(1, Model().CapacityReport().Summary.NoEstimateCount);
        Assert.Equal(1, Model().CapacityReport().Summary.NoEstimateByReason["NoCapacity"]);
    }

    [Fact]
    public void The_capacity_report_computes_used_percent_and_overcommit_ratio()
    {
        GivenEntities(Datastore("vc-1:ds-1"));
        GivenCapacity(100 * Gb);
        GivenFree(20 * Gb);
        GivenProvisioned(120 * Gb);

        var row = Assert.Single(Model().CapacityReport().Rows);

        Assert.Equal(80 * Gb, row.UsedBytes);
        Assert.Equal(80d, row.PercentUsed);
        Assert.Equal(1.2d, row.OvercommitRatio);
        Assert.Equal(1, Model().CapacityReport().Summary.OvercommittedCount);
    }

    [Fact]
    public void The_capacity_report_counts_datastores_filling_soon()
    {
        // 1 GB a day for twenty days, ninety of a hundred now: ten days left --
        // inside the 30-day warning window, outside the 7-day critical one.
        GivenEntities(Datastore("vc-1:ds-1"));
        GivenCapacity(100 * Gb);
        _observations.Recorded[CapacityCounters.DatastoreUsed] =
        [
            .. Enumerable.Range(0, 21 * 24).Select(i => Bucket(T0.AddHours(i - (21 * 24)), (70 + (i / 24d)) * Gb)),
        ];

        var summary = Model().CapacityReport().Summary;

        Assert.Equal(1, summary.FillingWithin30Days);
        Assert.Equal(0, summary.FillingWithin7Days);
    }

    [Fact]
    public void A_vanished_datastore_is_not_on_the_capacity_report()
    {
        GivenEntities(Datastore("vc-1:ds-1") with { ObservationState = ObservationState.Vanished });
        GivenCapacity(100 * Gb);

        Assert.Empty(Model().CapacityReport().Rows);
    }

    // --- continuity report (M8.10), from findings (K2) -------------------------

    private static ComplianceFinding Finding(
        string control,
        EntityId entity,
        ComplianceVerdict verdict,
        string subject = "",
        bool stale = false,
        FindingAcceptance? acceptance = null) => new()
        {
            ControlId = control,
            CatalogueRelease = ContinuityCatalogue.Release,
            Entity = entity,
            EntityName = entity.Value,
            Subject = subject,
            Verdict = verdict,
            Expected = "expected",
            Observed = "observed",
            Reason = verdict == ComplianceVerdict.NotEvaluated ? "not read" : null,
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
            Stale = stale,
            Acceptance = acceptance,
        };

    private void GivenFindings(params ComplianceFinding[] findings) => _compliance.Rows.AddRange(findings);

    private static readonly EntityId ClusterC1 = new("vc-1:domain-c1");

    private static readonly EntityId VCenter1 = new("vc-1:vcenter");

    private ReadModel Model(IReadOnlyList<ContinuityCheck> continuity) =>
        new(_graphs, _alerts, _collectors, _coverage, _observations, MonitoringOptions.Default, new StubClock(T0),
            _compliance, continuity);

    private static Entity VCenter(string id) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.VCenter,
        DisplayName = "vcsa.corp.local",
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
    };

    private static Entity Vm(string id) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.VirtualMachine,
        DisplayName = id.Replace("vc-1:", string.Empty, StringComparison.Ordinal),
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
    };

    private static Relationship Edge(EntityId from, EntityId to, RelationshipKind kind) => new()
    {
        From = from,
        To = to,
        Kind = kind,
        ObservedAtUtc = T0,
    };

    /// <summary>A check registered by a test alone: the report must not have heard of it.</summary>
    private sealed class ProbeCheck(EntityKind appliesTo) : IComplianceCheck
    {
        public EntityKind AppliesTo => appliesTo;

        public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context) =>
            throw new NotSupportedException();
    }

    private static ContinuityCheck Probe(string id, EntityKind appliesTo, string citation = "") =>
        new(new ComplianceControl { ControlId = id, Title = $"Probe {id}", Source = citation }, new ProbeCheck(appliesTo));

    [Fact]
    public void Before_any_evaluation_the_report_says_so_rather_than_showing_all_clear()
    {
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")));

        var summary = Model().ContinuityReport().Summary;

        Assert.False(summary.Evaluated);
        Assert.Contains("not been evaluated yet", summary.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cluster_with_no_ha_settings_reports_not_collected()
    {
        GivenEntities(Cluster("vc-1:domain-c1"));
        GivenFindings(Finding(ContinuityControls.HaEnabled, ClusterC1, ComplianceVerdict.NotEvaluated));

        var report = Model().ContinuityReport();
        var row = Assert.Single(report.Rows);

        Assert.False(row.HaSettingsCollected);
        Assert.Equal(1, row.Control(ContinuityControls.HaEnabled).NotEvaluated);
        Assert.Equal(0, row.Totals.Failing);
        Assert.False(report.Summary.HaInputsCollected);
        Assert.Contains("has not been read yet", report.Summary.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_control_of_the_catalogue_has_exactly_one_place_in_the_report()
    {
        // The report reads the catalogue: every registered control is listed,
        // placed by the entity kind its check applies to -- none hard-coded.
        var report = Model().ContinuityReport();

        Assert.Equal(
            ContinuityCatalogue.Production.Select(c => c.Control.ControlId),
            report.Controls.Select(c => c.ControlId));

        Assert.Equal(ContinuityReportScope.VCenter, report.Controls.Single(c => c.ControlId == ContinuityControls.CertVCenter).Scope);
        Assert.Equal(ContinuityReportScope.Cluster, report.Controls.Single(c => c.ControlId == ContinuityControls.MaintEvc).Scope);
        Assert.Equal(ContinuityReportScope.Entity, report.Controls.Single(c => c.ControlId == ContinuityControls.CertEsxi).Scope);
        Assert.Equal(ContinuityReportScope.Entity, report.Controls.Single(c => c.ControlId == ContinuityControls.MaintCdrom).Scope);
        Assert.Equal(ContinuityReportScope.Entity, report.Controls.Single(c => c.ControlId == ContinuityControls.MaintSingleHostDatastore).Scope);

        // One summary row per entity-level control, whether or not it has a finding yet.
        Assert.Equal(
            report.Controls.Where(c => c.Scope == ContinuityReportScope.Entity).Select(c => c.ControlId),
            report.ControlRows.Select(r => r.ControlId));
    }

    [Fact]
    public void The_eight_maintenance_and_expiry_controls_the_old_report_missed_are_on_it()
    {
        var hostId = new EntityId("vc-1:host-1");
        var vmId = new EntityId("vc-1:vm-1");
        var dsId = new EntityId("vc-1:ds-1");

        GivenEntities(
            Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")),
            Host("vc-1:host-1", HealthState.Healthy),
            Vm("vc-1:vm-1"),
            Datastore("vc-1:ds-1"),
            VCenter("vc-1:vcenter"));
        GivenFindings(
            Finding(ContinuityControls.MaintCdrom, vmId, ComplianceVerdict.Failing),
            Finding(ContinuityControls.MaintConsolidation, vmId, ComplianceVerdict.Passing),
            Finding(ContinuityControls.MaintSingleHostDatastore, dsId, ComplianceVerdict.Failing),
            Finding(ContinuityControls.MaintEvc, ClusterC1, ComplianceVerdict.NotEvaluated),
            Finding(ContinuityControls.CertEsxi, hostId, ComplianceVerdict.Failing),
            Finding(ContinuityControls.CertVCenter, VCenter1, ComplianceVerdict.Passing));

        var report = Model().ContinuityReport();

        Assert.Equal(["vm-1"], report.ControlRows.Single(r => r.ControlId == ContinuityControls.MaintCdrom).FailingNames);
        Assert.Equal(1, report.ControlRows.Single(r => r.ControlId == ContinuityControls.MaintConsolidation).Counts.Passing);
        Assert.Equal(["vmfs01"], report.ControlRows.Single(r => r.ControlId == ContinuityControls.MaintSingleHostDatastore).FailingNames);
        Assert.Equal(["vc-1:host-1.corp.local"], report.ControlRows.Single(r => r.ControlId == ContinuityControls.CertEsxi).FailingNames);
        Assert.Equal(1, Assert.Single(report.Rows).Control(ContinuityControls.MaintEvc).NotEvaluated);
        Assert.Equal(1, Assert.Single(report.VCenters).Control(ContinuityControls.CertVCenter).Passing);
    }

    [Fact]
    public void A_newly_registered_control_grows_the_report_with_no_report_change()
    {
        // Registered here alone -- the report code has never heard of either id.
        var checks = (IReadOnlyList<ContinuityCheck>)
        [
            .. ContinuityCatalogue.Production,
            Probe("eo-cont.test-vm-probe", EntityKind.VirtualMachine, "Test basis"),
            Probe("eo-cont.test-cluster-probe", EntityKind.Cluster),
        ];

        var vmId = new EntityId("vc-1:vm-7");
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")), Vm("vc-1:vm-7"));
        GivenFindings(
            Finding("eo-cont.test-vm-probe", vmId, ComplianceVerdict.Failing),
            Finding("eo-cont.test-cluster-probe", ClusterC1, ComplianceVerdict.Failing));

        var report = Model(checks).ContinuityReport();

        var vmRow = Assert.Single(report.ControlRows, r => r.ControlId == "eo-cont.test-vm-probe");
        Assert.Equal("Probe eo-cont.test-vm-probe", vmRow.Title);
        Assert.Equal("Test basis", vmRow.Citation);
        Assert.Equal(EntityKind.VirtualMachine, vmRow.AppliesTo);
        Assert.Equal(["vm-7"], vmRow.FailingNames);

        var cluster = Assert.Single(report.Rows);
        Assert.Equal(1, cluster.Control("eo-cont.test-cluster-probe").Failing);
        Assert.True(cluster.HasFailing);
        Assert.Equal(1, report.Summary.ByControl["eo-cont.test-vm-probe"].Failing);
    }

    [Fact]
    public void A_summary_row_names_at_most_ten_failing_entities_and_counts_the_rest()
    {
        var hosts = Enumerable.Range(1, 15).Select(i => Host($"vc-1:host-{i:00}", HealthState.Healthy)).ToArray();
        GivenEntities(hosts);
        GivenFindings(
        [
            .. hosts.Take(13).Select(h => Finding(ContinuityControls.CertEsxi, h.Id, ComplianceVerdict.Failing)),
            .. hosts.Skip(13).Select(h => Finding(ContinuityControls.CertEsxi, h.Id, ComplianceVerdict.Passing)),
        ]);

        var row = Model().ContinuityReport().ControlRows.Single(r => r.ControlId == ContinuityControls.CertEsxi);

        Assert.Equal(13, row.Counts.Failing);
        Assert.Equal(2, row.Counts.Passing);
        Assert.Equal(ContinuityControlRow.MaxNamesListed, row.FailingNames.Count);
        Assert.Equal("vc-1:host-01.corp.local", row.FailingNames[0]);
        Assert.Equal(3, row.MoreFailing);
    }

    [Fact]
    public void The_vcenter_has_its_own_section_with_its_certificate_and_root_alarms()
    {
        GivenEntities(VCenter("vc-1:vcenter"), Cluster("vc-1:domain-c1"));
        GivenFindings(Finding(ContinuityControls.CertVCenter, VCenter1, ComplianceVerdict.Failing));
        GivenAlerts(
            Alert("licence-expiry", AlertSeverity.Warning) with { Entity = VCenter1, Category = "vCenter", Title = "License expiry" },
            Alert("cluster-thing", AlertSeverity.Critical) with { Entity = ClusterC1 });

        var report = Model().ContinuityReport();
        var vcenter = Assert.Single(report.VCenters);

        Assert.Equal("vcsa.corp.local", vcenter.VCenterName);
        Assert.Equal(1, vcenter.Control(ContinuityControls.CertVCenter).Failing);
        var finding = Assert.Single(vcenter.Findings);
        Assert.Equal(ContinuityControls.CertVCenter, finding.ControlId);
        var alarm = Assert.Single(vcenter.Alarms);
        Assert.Equal("License expiry", alarm.Title);

        // Not a cluster: the vCenter never opens a cluster row.
        Assert.DoesNotContain(report.Rows, r => r.ClusterId == VCenter1.Value);
    }

    [Fact]
    public void Findings_are_counted_by_state_per_control()
    {
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")));
        GivenFindings(
            Finding(ContinuityControls.HaEnabled, ClusterC1, ComplianceVerdict.Passing),
            Finding(ContinuityControls.HaAdmissionControl, ClusterC1, ComplianceVerdict.Failing,
                acceptance: new FindingAcceptance { By = "ertugrul", AtUtc = T0, Reason = "budget" }),
            Finding(ContinuityControls.HaHostMonitoring, ClusterC1, ComplianceVerdict.Failing),
            Finding(ContinuityControls.DrsRule, ClusterC1, ComplianceVerdict.Failing, "uuid-1", stale: true),
            Finding(ContinuityControls.NPlusOneCpu, ClusterC1, ComplianceVerdict.NotEvaluated));

        var report = Model().ContinuityReport();
        var row = Assert.Single(report.Rows);

        Assert.Equal(1, row.Control(ContinuityControls.HaEnabled).Passing);
        Assert.Equal(1, row.Control(ContinuityControls.HaAdmissionControl).Accepted);
        Assert.Equal(1, row.Control(ContinuityControls.HaHostMonitoring).Failing);
        Assert.Equal(1, row.Control(ContinuityControls.DrsRule).Failing);
        Assert.Equal(1, row.Control(ContinuityControls.DrsRule).Stale);
        Assert.Equal(1, row.Control(ContinuityControls.NPlusOneCpu).NotEvaluated);
        Assert.Equal(2, row.Totals.Failing);
        Assert.True(row.HasFailing);

        Assert.True(report.Summary.Evaluated);
        Assert.Null(report.Summary.Note);
        Assert.Equal(1, report.Summary.ByControl[ContinuityControls.HaAdmissionControl].Accepted);
        Assert.Equal(ContinuityCatalogue.Production.Count, report.Summary.ByControl.Count);
    }

    [Fact]
    public void Maintenance_and_expiry_findings_are_counted_in_the_summary_by_control()
    {
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")));
        GivenFindings(
            Finding(ContinuityControls.MaintCdrom, new EntityId("vc-1:vm-1"), ComplianceVerdict.Failing),
            Finding(ContinuityControls.CertVCenter, VCenter1, ComplianceVerdict.Passing));

        var summary = Model().ContinuityReport().Summary;

        Assert.Equal(1, summary.ByControl[ContinuityControls.MaintCdrom].Failing);
        Assert.Equal(1, summary.ByControl[ContinuityControls.CertVCenter].Passing);
        Assert.Equal(1, summary.Totals.Failing);
    }

    [Fact]
    public void An_exception_counts_as_excepted_not_failing()
    {
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "true")));
        GivenFindings(Finding(ContinuityControls.HaAdmissionControl, ClusterC1, ComplianceVerdict.Failing));
        _compliance.Waivers.Add(new ComplianceWaiver
        {
            Id = "x1",
            ControlId = ContinuityControls.HaAdmissionControl,
            Reason = "test cluster",
            Owner = "ops",
            CreatedBy = "ertugrul",
            CreatedAtUtc = T0,
            ExpiresUtc = T0.AddDays(30),
        });

        var row = Assert.Single(Model().ContinuityReport().Rows);

        Assert.Equal(1, row.Control(ContinuityControls.HaAdmissionControl).Excepted);
        Assert.Equal(0, row.Totals.Failing);
        Assert.False(row.HasFailing);
    }

    [Fact]
    public void Findings_under_a_cluster_roll_up_to_it_through_the_graph_and_others_do_not()
    {
        var hostId = new EntityId("vc-1:host-1");
        var otherHost = new EntityId("vc-1:host-2");
        var vmId = new EntityId("vc-1:vm-1");

        GivenEntities(
            Cluster("vc-1:domain-c1"),
            Host("vc-1:host-1", HealthState.Warning),
            Host("vc-1:host-2", HealthState.Warning),
            Vm("vc-1:vm-1"));
        GivenRelationships(
            Edge(hostId, ClusterC1, RelationshipKind.PartOf),
            Edge(vmId, hostId, RelationshipKind.RunsOn));
        GivenFindings(
            Finding(ContinuityControls.PathSingleHba, hostId, ComplianceVerdict.Failing, "vmhba1"),
            Finding(ContinuityControls.PathSingle, hostId, ComplianceVerdict.Passing, "naa.1"),
            Finding(ContinuityControls.MaintCdrom, vmId, ComplianceVerdict.Failing),
            Finding(ContinuityControls.PathSingleHba, otherHost, ComplianceVerdict.Failing, "vmhba1"));

        var row = Assert.Single(Model().ContinuityReport().Rows);

        Assert.Equal(2, row.Contained.Failing);
        Assert.Equal(1, row.Contained.Passing);
        Assert.Equal(["vc-1:host-1.corp.local", "vm-1"], row.ContainedAffectedNames);
        Assert.True(row.HasFailing);
    }

    [Fact]
    public void Alarms_no_longer_feed_the_continuity_report()
    {
        GivenEntities(Cluster("vc-1:domain-c1", ("dasConfig.enabled", "false")));
        GivenAlerts(Alert("cluster-ha-scorecard-ha-disabled", AlertSeverity.Critical) with { Entity = ClusterC1 });

        var row = Assert.Single(Model().ContinuityReport().Rows);

        Assert.Equal(0, row.Totals.Failing);
        Assert.False(row.HasFailing);
    }

    [Fact]
    public void The_summary_names_clusters_with_a_failing_finding()
    {
        var loud = new EntityId("vc-1:domain-c2");

        GivenEntities(
            Cluster("vc-1:domain-c1") with { DisplayName = "Quiet" },
            Cluster("vc-1:domain-c2") with { DisplayName = "Loud" });
        GivenFindings(
            Finding(ContinuityControls.DrsRule, loud, ComplianceVerdict.Failing, "uuid-1"),
            Finding(ContinuityControls.DrsRule, ClusterC1, ComplianceVerdict.Passing, "uuid-2"));

        var summary = Model().ContinuityReport().Summary;

        Assert.Equal(1, summary.ClustersWithFailingCount);
        Assert.Equal(["Loud"], summary.ClustersWithFailingNames);
    }

    [Fact]
    public void A_vanished_cluster_is_not_on_the_continuity_report()
    {
        GivenEntities(Cluster("vc-1:domain-c1") with { ObservationState = ObservationState.Vanished });

        Assert.Empty(Model().ContinuityReport().Rows);
    }

    [Fact]
    public void Another_catalogues_findings_are_not_continuity_findings()
    {
        GivenEntities(Cluster("vc-1:domain-c1"));
        GivenFindings(Finding(ContinuityControls.HaEnabled, ClusterC1, ComplianceVerdict.Failing) with
        {
            CatalogueRelease = "803-20260612-01",
        });

        Assert.False(Model().ContinuityReport().Summary.Evaluated);
    }

    /// <summary>Findings and exceptions as the store holds them; nothing is evaluated here.</summary>
    private sealed class StubComplianceStore : IComplianceStore
    {
        public List<ComplianceFinding> Rows { get; } = [];

        public List<ComplianceWaiver> Waivers { get; } = [];

        public IReadOnlyList<ComplianceFinding> Findings => Rows;

        public IReadOnlyList<ComplianceWaiver> Exceptions => Waivers;

        public void Evaluate(
            string catalogueRelease,
            DateTimeOffset nowUtc,
            Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate) =>
            throw new NotSupportedException();

        public ComplianceFinding? Mutate(
            string catalogueRelease, string controlId, EntityId entity, string subject,
            Func<ComplianceFinding, ComplianceFinding> change) => throw new NotSupportedException();

        public void AddException(ComplianceWaiver exception) => Waivers.Add(exception);

        public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc) =>
            throw new NotSupportedException();

        public ComplianceTransitionsPage TransitionsSince(
            DateTimeOffset sinceUtc, DateTimeOffset? toUtc = null, string? catalogueRelease = null,
            string? controlId = null, EntityId? entity = null) => ComplianceTransitionsPage.Empty;
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

        /// <summary>Points to answer with, by counter; anything absent was never recorded.</summary>
        public Dictionary<string, List<AggregatedSample>> Recorded { get; } = new(StringComparer.Ordinal);

        public List<SeriesQuery> Queries { get; } = [];

        public SeriesResult Query(SeriesQuery query)
        {
            LastQuery = query;
            Queries.Add(query);

            if (!Recorded.TryGetValue(query.Key.Counter, out var points))
            {
                return new SeriesResult
                {
                    Key = query.Key,
                    Resolution = query.Resolution ?? SeriesResolution.Raw,
                    Exists = false,
                };
            }

            // The newest end is kept when truncating, as the real store does.
            var inWindow = points
                .Where(p => p.StartUtc >= query.FromUtc && p.StartUtc < query.ToUtc)
                .OrderBy(p => p.StartUtc)
                .ToList();

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = query.Resolution ?? SeriesResolution.Raw,
                Points = [.. inWindow.Skip(Math.Max(0, inWindow.Count - query.MaxPoints))],
                Exists = true,
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

        public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) => new();
    }

    // --- coverage ---------------------------------------------------------

    [Fact]
    public void Coverage_puts_the_blind_rows_first()
    {
        // A report of forty rows in which two matter is read by nobody if the
        // two are in the middle.
        _coverage.Replace("vc-1",
            [
                Cover("name", asked: 10, answered: 10),
                Cover("config.option", asked: 10, answered: 0),
                Cover("hardware.systemInfo.uuid", asked: 10, answered: 7),
            ],
            T0);

        var properties = Assert.Single(Model().Coverage()).Properties;

        Assert.Equal("config.option", properties[0].Property);
        Assert.True(properties[0].IsBlind);
        Assert.Equal("hardware.systemInfo.uuid", properties[1].Property);
        Assert.Equal("name", properties[2].Property);
    }

    [Fact]
    public void Coverage_keeps_the_complete_rows()
    {
        // "This was checked and it is fine" is what makes the blind rows
        // trustworthy. A panel that only ever showed problems could not tell a
        // healthy estate from a report that stopped running.
        _coverage.Replace("vc-1", [Cover("name", asked: 10, answered: 10)], T0);

        var row = Assert.Single(Assert.Single(Model().Coverage()).Properties);

        Assert.False(row.IsBlind);
        Assert.Equal(10, row.Answered);
    }

    [Fact]
    public void Coverage_carries_when_it_was_measured()
    {
        // A stale number presented without a timestamp reads as current, which
        // is worse than a blank page.
        _coverage.Replace("vc-1", [Cover("name", asked: 1, answered: 1)], T0);

        Assert.Equal(T0, Assert.Single(Model().Coverage()).MeasuredAtUtc);
    }

    [Fact]
    public void Coverage_from_several_sources_is_ordered_and_kept_apart()
    {
        _coverage.Replace("vc-2", [Cover("name", asked: 4, answered: 0)], T0);
        _coverage.Replace("vc-1", [Cover("name", asked: 10, answered: 10)], T0);

        var sources = Model().Coverage();

        Assert.Equal(["vc-1", "vc-2"], sources.Select(c => c.InstanceId));
    }

    [Fact]
    public void A_source_that_measured_nothing_is_still_listed()
    {
        // "Reported and measured no coverage" is not "never heard from", and
        // dropping the row would make the two look the same.
        _coverage.Replace("vc-1", [], T0);

        Assert.Empty(Assert.Single(Model().Coverage()).Properties);
    }

    private static PropertyCoverage Cover(string property, int asked, int answered) => new()
    {
        ObjectType = "HostSystem",
        Property = property,
        Asked = asked,
        Answered = answered,
    };

    private sealed class StubHealthStore : ICollectorHealthStore
    {
        private readonly List<CollectorHealth> _health = [];

        public IReadOnlyList<CollectorHealth> Current => _health;

        public void Merge(IReadOnlyList<CollectorHealth> health) => _health.AddRange(health);
    }

    private sealed class StubCoverageStore : ICoverageStore
    {
        private readonly Dictionary<string, SourceCoverage> _coverage = new(StringComparer.Ordinal);

        public IReadOnlyList<SourceCoverage> Current => [.. _coverage.Values];

        public void Replace(
            string sourceInstanceId,
            IReadOnlyList<PropertyCoverage> coverage,
            DateTimeOffset measuredAtUtc) =>
            _coverage[sourceInstanceId] = new SourceCoverage
            {
                SourceInstanceId = sourceInstanceId,
                MeasuredAtUtc = measuredAtUtc,
                Properties = [.. coverage],
            };
    }
}
