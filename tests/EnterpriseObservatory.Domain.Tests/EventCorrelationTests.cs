using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>
/// Working out which alerts are one thing going wrong.
/// </summary>
/// <remarks>
/// The tests that matter most here are the negative ones. Grouping too eagerly
/// is worse than not grouping at all: it hides a second failure inside the
/// first, and the operator has no reason to look for it.
/// </remarks>
public class EventCorrelationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly CorrelationPolicy Policy = CorrelationPolicy.Default;

    // --- the chain the product exists to make visible ---------------------

    [Fact]
    public void One_dead_switch_port_is_one_event_across_the_whole_chain()
    {
        // The chain from ADR-0007 §5.2: a switch port to an HBA to a host to
        // the virtual machines on it. Twelve rows in the previous product,
        // correlated in the operator's head at three in the morning.
        var graph = Graph(
            [
                Entity("port-3", EntityKind.SanSwitchPort),
                Entity("hba-2", EntityKind.HbaPort),
                Entity("esx01", EntityKind.EsxiHost),
                Entity("vm-db", EntityKind.VirtualMachine),
                Entity("vm-web", EntityKind.VirtualMachine),
            ],
            [
                Edge("port-3", "hba-2", RelationshipKind.ConnectedTo),
                Edge("hba-2", "esx01", RelationshipKind.PartOf),
                Edge("vm-db", "esx01", RelationshipKind.RunsOn),
                Edge("vm-web", "esx01", RelationshipKind.RunsOn),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("sfp", "port-3", AlertSeverity.Critical),
             Alert("path", "esx01", AlertSeverity.Warning),
             Alert("latency-db", "vm-db", AlertSeverity.Warning),
             Alert("latency-web", "vm-web", AlertSeverity.Warning)],
            graph,
            Policy);

        var incident = Assert.Single(result.Events);

        Assert.Equal(new EntityId("port-3"), incident.Root);
        Assert.Equal(4, incident.Alerts.Count);
        Assert.Equal(AlertSeverity.Critical, incident.Severity);
        Assert.Empty(result.Ungrouped);
    }

    [Fact]
    public void The_path_that_proves_the_group_is_reported()
    {
        // ADR-0007 requires it: topological correlation is only as good as the
        // graph, so a wrong group has to be visibly wrong rather than merely
        // wrong.
        var graph = Graph(
            [Entity("esx01", EntityKind.EsxiHost), Entity("vm-db", EntityKind.VirtualMachine)],
            [Edge("vm-db", "esx01", RelationshipKind.RunsOn)]);

        var result = EventCorrelator.Correlate(
            [Alert("down", "esx01", AlertSeverity.Critical),
             Alert("slow", "vm-db", AlertSeverity.Warning)],
            graph,
            Policy);

        var link = Assert.Single(Assert.Single(result.Events).Explanation);

        Assert.Equal(new EntityId("esx01"), link.From);
        Assert.Equal(new EntityId("vm-db"), link.To);
        Assert.Equal(RelationshipKind.RunsOn, link.Kind);
    }

    [Fact]
    public void A_failing_datastore_and_everything_on_it_is_one_event()
    {
        var graph = Graph(
            [
                Entity("ds-1", EntityKind.Datastore),
                Entity("vm-a", EntityKind.VirtualMachine),
                Entity("vm-b", EntityKind.VirtualMachine),
            ],
            [
                Edge("vm-a", "ds-1", RelationshipKind.BackedBy),
                Edge("vm-b", "ds-1", RelationshipKind.BackedBy),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("ds", "ds-1", AlertSeverity.Critical),
             Alert("a", "vm-a", AlertSeverity.Warning),
             Alert("b", "vm-b", AlertSeverity.Warning)],
            graph,
            Policy);

        Assert.Equal(3, Assert.Single(result.Events).Alerts.Count);
    }

    // --- what must never be grouped ---------------------------------------

    [Fact]
    public void Two_hosts_in_one_cluster_are_two_problems()
    {
        // The failure mode that makes naive correlation useless. Both hosts are
        // connected — through the cluster — and a PSU failing in one explains
        // nothing about a fan failing in the other. Following impact direction
        // rather than connectivity is what keeps them apart.
        var graph = Graph(
            [
                Entity("cluster-1", EntityKind.Cluster),
                Entity("esx01", EntityKind.EsxiHost),
                Entity("esx17", EntityKind.EsxiHost),
            ],
            [
                Edge("esx01", "cluster-1", RelationshipKind.PartOf),
                Edge("esx17", "cluster-1", RelationshipKind.PartOf),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("psu", "esx01", AlertSeverity.Critical),
             Alert("fan", "esx17", AlertSeverity.Warning)],
            graph,
            Policy);

        Assert.Empty(result.Events);
        Assert.Equal(2, result.Ungrouped.Count);
    }

    [Fact]
    public void Two_machines_on_a_healthy_host_are_two_problems()
    {
        // Same shape one level down. Nothing upstream is alerting, so there is
        // nothing to say these have a common cause.
        var graph = Graph(
            [
                Entity("esx01", EntityKind.EsxiHost),
                Entity("vm-a", EntityKind.VirtualMachine),
                Entity("vm-b", EntityKind.VirtualMachine),
            ],
            [
                Edge("vm-a", "esx01", RelationshipKind.RunsOn),
                Edge("vm-b", "esx01", RelationshipKind.RunsOn),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("a", "vm-a", AlertSeverity.Warning), Alert("b", "vm-b", AlertSeverity.Warning)],
            graph,
            Policy);

        Assert.Empty(result.Events);
    }

    [Fact]
    public void Everything_managed_by_one_vcenter_is_not_one_event()
    {
        // Every entity in an estate is managed by the same one or two things.
        // Correlating through that edge would collapse the entire product into
        // a single incident — which is why ManagedBy carries no impact.
        var graph = Graph(
            [
                Entity("vc-1", EntityKind.VCenter),
                Entity("esx01", EntityKind.EsxiHost),
                Entity("esx17", EntityKind.EsxiHost),
            ],
            [
                Edge("esx01", "vc-1", RelationshipKind.ManagedBy),
                Edge("esx17", "vc-1", RelationshipKind.ManagedBy),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("a", "esx01", AlertSeverity.Warning),
             Alert("b", "esx17", AlertSeverity.Warning),
             Alert("c", "vc-1", AlertSeverity.Warning)],
            graph,
            Policy);

        Assert.Empty(result.Events);
    }

    [Fact]
    public void A_chain_longer_than_the_policy_allows_is_not_one_event()
    {
        // Far enough apart and everything in an estate connects to everything
        // else. The bound is what keeps the claim credible.
        var entities = Enumerable.Range(0, 8).Select(i => Entity($"e{i}", EntityKind.EsxiHost)).ToList();
        var edges = Enumerable.Range(0, 7)
            .Select(i => Edge($"e{i}", $"e{i + 1}", RelationshipKind.ConnectedTo))
            .ToList();

        var result = EventCorrelator.Correlate(
            [Alert("first", "e0", AlertSeverity.Warning), Alert("last", "e7", AlertSeverity.Warning)],
            Graph(entities, edges),
            Policy with { MaxHops = 4 });

        Assert.Empty(result.Events);
    }

    [Fact]
    public void One_entitys_own_alerts_are_not_an_event()
    {
        // Folding them would add a layer that explains nothing.
        var graph = Graph([Entity("esx01", EntityKind.EsxiHost)], []);

        var result = EventCorrelator.Correlate(
            [Alert("psu", "esx01", AlertSeverity.Critical),
             Alert("fan", "esx01", AlertSeverity.Warning)],
            graph,
            Policy);

        Assert.Empty(result.Events);
        Assert.Equal(2, result.Ungrouped.Count);
    }

    // --- no alert may vanish ----------------------------------------------

    [Fact]
    public void Every_alert_ends_up_in_exactly_one_place()
    {
        // ADR-0007 §5.1: no alert lives only inside a group, and none may be
        // lost between the two lists either. Counted twice it would be acted on
        // twice; counted never it would be invisible.
        var graph = Graph(
            [
                Entity("esx01", EntityKind.EsxiHost),
                Entity("vm-a", EntityKind.VirtualMachine),
                Entity("esx17", EntityKind.EsxiHost),
            ],
            [Edge("vm-a", "esx01", RelationshipKind.RunsOn)]);

        var alerts = new[]
        {
            Alert("down", "esx01", AlertSeverity.Critical),
            Alert("slow", "vm-a", AlertSeverity.Warning),
            Alert("fan", "esx17", AlertSeverity.Warning),
            Collector("unreachable"),
        };

        var result = EventCorrelator.Correlate(alerts, graph, Policy);

        var placed = result.Events.SelectMany(e => e.Alerts).Concat(result.Ungrouped).ToList();

        Assert.Equal(alerts.Length, placed.Count);
        Assert.Equal(alerts.Length, placed.Distinct().Count());
    }

    [Fact]
    public void An_alert_about_nothing_in_particular_is_never_grouped()
    {
        // A collector being unreachable is about the collector, not the estate.
        // There is no entity to reason from, and inventing one would be the
        // fabrication principle 1 forbids.
        var result = EventCorrelator.Correlate(
            [Collector("a"), Collector("b"), Collector("c")],
            Graph([], []),
            Policy);

        Assert.Empty(result.Events);
        Assert.Equal(3, result.Ungrouped.Count);
    }

    [Fact]
    public void An_alert_reachable_from_two_roots_belongs_to_the_nearer_one()
    {
        // Exactly one owner, or it is counted twice and acted on twice. The
        // nearer failure is the better explanation.
        var graph = Graph(
            [
                Entity("near", EntityKind.EsxiHost),
                Entity("far", EntityKind.SanSwitch),
                Entity("middle", EntityKind.EsxiHost),
                Entity("victim", EntityKind.VirtualMachine),
            ],
            [
                Edge("victim", "near", RelationshipKind.RunsOn),
                Edge("far", "middle", RelationshipKind.ConnectedTo),
                Edge("middle", "near", RelationshipKind.ConnectedTo),
            ]);

        var result = EventCorrelator.Correlate(
            [Alert("n", "near", AlertSeverity.Warning),
             Alert("f", "far", AlertSeverity.Critical),
             Alert("v", "victim", AlertSeverity.Warning)],
            graph,
            Policy);

        // "far" reaches everything, so it is the only root and owns them all.
        var incident = Assert.Single(result.Events);

        Assert.Equal(new EntityId("far"), incident.Root);
        Assert.Equal(3, incident.Alerts.Count);
    }

    // --- coincidence -------------------------------------------------------

    [Fact]
    public void Alerts_that_merely_coincide_are_suggested_and_not_folded()
    {
        // The evidence is only that they appeared together, which is as true of
        // one failure as of three unrelated ones at lunchtime. A separate type
        // rather than a flag, so nothing can fold one by forgetting to check.
        var result = EventCorrelator.Correlate(
            [Alert("a", "e1", AlertSeverity.Warning, T0),
             Alert("b", "e2", AlertSeverity.Warning, T0.AddSeconds(20)),
             Alert("c", "e3", AlertSeverity.Warning, T0.AddSeconds(40))],
            Graph([], []),
            Policy);

        Assert.Empty(result.Events);
        Assert.Equal(3, Assert.Single(result.Suggestions).Alerts.Count);
        Assert.Equal(3, result.Ungrouped.Count);
    }

    [Fact]
    public void Two_alerts_a_minute_apart_are_a_tuesday()
    {
        var result = EventCorrelator.Correlate(
            [Alert("a", "e1", AlertSeverity.Warning, T0),
             Alert("b", "e2", AlertSeverity.Warning, T0.AddSeconds(30))],
            Graph([], []),
            Policy);

        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Alerts_spread_across_the_day_are_not_a_coincidence()
    {
        var result = EventCorrelator.Correlate(
            [Alert("a", "e1", AlertSeverity.Warning, T0),
             Alert("b", "e2", AlertSeverity.Warning, T0.AddHours(3)),
             Alert("c", "e3", AlertSeverity.Warning, T0.AddHours(6))],
            Graph([], []),
            Policy);

        Assert.Empty(result.Suggestions);
    }

    // --- fixtures ----------------------------------------------------------

    private static EntityGraph Graph(
        IReadOnlyList<Entity> entities, IReadOnlyList<Relationship> relationships) =>
        EntityGraph.Empty with
        {
            Entities = entities.ToDictionary(e => e.Id),
            Relationships = relationships,
        };

    private static Entity Entity(string id, EntityKind kind) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id,
        SourceInstanceId = "vc-1",
        Health = HealthState.Critical,
        LastSeenUtc = T0,
    };

    private static Relationship Edge(string from, string to, RelationshipKind kind) => new()
    {
        From = new EntityId(from),
        To = new EntityId(to),
        Kind = kind,
        ObservedAtUtc = T0,
    };

    private static AlertInstance Alert(
        string id, string entity, AlertSeverity severity, DateTimeOffset? seen = null) =>
        Base(id, severity, seen) with { Entity = new EntityId(entity) };

    private static AlertInstance Collector(string id) => Base(id, AlertSeverity.Warning, null);

    private static AlertInstance Base(string id, AlertSeverity severity, DateTimeOffset? seen) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Hardware", id, id),
        Severity = severity,
        State = AlertLifecycleState.Open,
        Title = id,
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = seen ?? T0,
        LastSeenUtc = seen ?? T0,
    };
}
