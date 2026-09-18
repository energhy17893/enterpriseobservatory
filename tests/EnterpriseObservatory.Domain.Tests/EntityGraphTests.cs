using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

public class EntityGraphTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityRetentionPolicy Policy = EntityRetentionPolicy.Default;

    private static Entity Host(string id, string source = "vc-1", DateTimeOffset? seen = null) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.EsxiHost,
        DisplayName = id,
        SourceInstanceId = source,
        Health = HealthState.Healthy,
        LastSeenUtc = seen ?? T0,
    };

    private static Relationship Edge(string from, string to, RelationshipKind kind = RelationshipKind.RunsOn) => new()
    {
        From = new EntityId(from),
        To = new EntityId(to),
        Kind = kind,
        ObservedAtUtc = T0,
    };

    // --- basic merging ----------------------------------------------------

    [Fact]
    public void Newly_observed_entities_enter_the_graph()
    {
        var graph = EntityGraph.Empty.Merge([Host("h1"), Host("h2")], [], ["vc-1"], T0, Policy);

        Assert.Equal(2, graph.Entities.Count);
        Assert.Equal(2, graph.Active.Count());
    }

    [Fact]
    public void An_entity_that_stops_being_reported_vanishes_rather_than_disappearing()
    {
        var graph = EntityGraph.Empty
            .Merge([Host("h1"), Host("h2")], [], ["vc-1"], T0, Policy)
            .Merge([Host("h1", seen: T0.AddMinutes(1))], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Equal(2, graph.Entities.Count);
        Assert.Single(graph.Active);
        Assert.Equal(ObservationState.Vanished, graph.Entities[new EntityId("h2")].ObservationState);
    }

    [Fact]
    public void A_vanished_entity_reports_unknown_health_whatever_it_looked_like_before()
    {
        var graph = EntityGraph.Empty
            .Merge([Host("h1")], [], ["vc-1"], T0, Policy)
            .Merge([], [], ["vc-1"], T0.AddMinutes(1), Policy);

        var entity = graph.Entities[new EntityId("h1")];

        Assert.Equal(HealthState.Healthy, entity.Health);
        Assert.Equal(HealthState.Unknown, entity.EffectiveHealth);
    }

    [Fact]
    public void An_entity_that_comes_back_is_active_again()
    {
        var graph = EntityGraph.Empty
            .Merge([Host("h1")], [], ["vc-1"], T0, Policy)
            .Merge([], [], ["vc-1"], T0.AddMinutes(1), Policy)
            .Merge([Host("h1", seen: T0.AddMinutes(2))], [], ["vc-1"], T0.AddMinutes(2), Policy);

        Assert.Equal(ObservationState.Active, graph.Entities[new EntityId("h1")].ObservationState);
    }

    // --- the distinction that matters -------------------------------------

    [Fact]
    public void A_source_that_did_not_answer_does_not_make_its_entities_vanish()
    {
        // "These machines are gone" and "we could not look" lead to opposite
        // actions. The second is already reported as a collector alert; saying
        // the first as well would be a claim we cannot support.
        var graph = EntityGraph.Empty
            .Merge([Host("h1", "vc-1"), Host("i1", "ilo-1")], [], ["vc-1", "ilo-1"], T0, Policy);

        // Only vCenter answers this cycle; the iLO collector is unreachable.
        graph = graph.Merge(
            [Host("h1", "vc-1", T0.AddMinutes(1))], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Equal(ObservationState.Active, graph.Entities[new EntityId("i1")].ObservationState);
    }

    [Fact]
    public void A_source_that_answered_but_omitted_an_entity_does_make_it_vanish()
    {
        // It looked and did not find it. That is a real observation.
        var graph = EntityGraph.Empty
            .Merge([Host("h1"), Host("h2")], [], ["vc-1"], T0, Policy)
            .Merge([Host("h1", seen: T0.AddMinutes(1))], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Equal(ObservationState.Vanished, graph.Entities[new EntityId("h2")].ObservationState);
    }

    // --- retention --------------------------------------------------------

    [Fact]
    public void A_vanished_entity_is_kept_for_the_retention_period()
    {
        var graph = EntityGraph.Empty
            .Merge([Host("h1")], [], ["vc-1"], T0, Policy)
            .Merge([], [], ["vc-1"], T0.AddDays(29), Policy);

        Assert.Single(graph.Entities);
    }

    [Fact]
    public void A_vanished_entity_is_forgotten_once_retention_expires()
    {
        var graph = EntityGraph.Empty
            .Merge([Host("h1")], [], ["vc-1"], T0, Policy)
            .Merge([], [], ["vc-1"], T0.AddDays(31), Policy);

        Assert.Empty(graph.Entities);
    }

    [Fact]
    public void Retention_runs_from_when_it_was_last_seen_not_from_the_restart()
    {
        // Otherwise restarting the service would reset every clock and keep
        // long-dead entities alive indefinitely.
        var graph = EntityGraph.Empty.Merge([Host("h1")], [], ["vc-1"], T0, Policy);

        // Several cycles pass with it missing; the clock keeps running.
        graph = graph.Merge([], [], ["vc-1"], T0.AddDays(10), Policy);
        graph = graph.Merge([], [], ["vc-1"], T0.AddDays(20), Policy);
        Assert.Single(graph.Entities);

        graph = graph.Merge([], [], ["vc-1"], T0.AddDays(31), Policy);
        Assert.Empty(graph.Entities);
    }

    // --- maintenance ------------------------------------------------------

    [Fact]
    public void Maintenance_reported_by_the_collector_is_preserved()
    {
        var inMaintenance = Host("h1") with { ObservationState = ObservationState.InMaintenance };

        var graph = EntityGraph.Empty
            .Merge([Host("h1")], [], ["vc-1"], T0, Policy)
            .Merge([inMaintenance], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Equal(ObservationState.InMaintenance, graph.Entities[new EntityId("h1")].ObservationState);
    }

    // --- relationships ----------------------------------------------------

    [Fact]
    public void Edges_that_disappear_are_removed_rather_than_accumulating()
    {
        // A VM that moved host must not keep its old RunsOn, or impact analysis
        // will blame a host that is no longer involved.
        var graph = EntityGraph.Empty.Merge(
            [Host("vm1"), Host("h1"), Host("h2")],
            [Edge("vm1", "h1")],
            ["vc-1"], T0, Policy);

        graph = graph.Merge(
            [Host("vm1", seen: T0.AddMinutes(1)), Host("h1", seen: T0.AddMinutes(1)), Host("h2", seen: T0.AddMinutes(1))],
            [Edge("vm1", "h2")],
            ["vc-1"], T0.AddMinutes(1), Policy);

        var edge = Assert.Single(graph.Relationships);
        Assert.Equal(new EntityId("h2"), edge.To);
    }

    [Fact]
    public void Edges_from_a_source_that_did_not_answer_are_left_alone()
    {
        var graph = EntityGraph.Empty.Merge(
            [Host("h1", "vc-1"), Host("i1", "ilo-1"), Host("p1", "ilo-1")],
            [Edge("i1", "p1", RelationshipKind.PartOf)],
            ["vc-1", "ilo-1"], T0, Policy);

        // Only vCenter answers; the iLO collector's edge must survive.
        graph = graph.Merge(
            [Host("h1", "vc-1", T0.AddMinutes(1))], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Single(graph.Relationships);
    }

    [Fact]
    public void An_edge_to_a_forgotten_entity_is_dropped()
    {
        // An edge pointing at nothing is not an edge.
        var graph = EntityGraph.Empty.Merge(
            [Host("vm1"), Host("h1")], [Edge("vm1", "h1")], ["vc-1"], T0, Policy);

        graph = graph.Merge([Host("vm1", seen: T0.AddDays(31))], [], ["vc-1"], T0.AddDays(31), Policy);

        Assert.Empty(graph.Relationships);
    }

    // --- immutability -----------------------------------------------------

    [Fact]
    public void Merging_produces_a_new_graph_and_leaves_the_old_one_alone()
    {
        // A reader is never looking at a half-applied update.
        var first = EntityGraph.Empty.Merge([Host("h1")], [], ["vc-1"], T0, Policy);
        var second = first.Merge([Host("h1"), Host("h2")], [], ["vc-1"], T0.AddMinutes(1), Policy);

        Assert.Single(first.Entities);
        Assert.Equal(2, second.Entities.Count);
    }

    [Fact]
    public void Merging_nothing_into_nothing_is_not_an_error()
    {
        var graph = EntityGraph.Empty.Merge([], [], [], T0, Policy);

        Assert.Empty(graph.Entities);
        Assert.Empty(graph.Relationships);
    }
}
