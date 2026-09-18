using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>
/// These tests exist to lock in product principle 1 — "never fabricate" — at
/// the type level, so that a future refactor cannot quietly reintroduce
/// "green while blind".
/// </summary>
public class HealthStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unknown_is_the_default_health()
    {
        // Forgetting to assign health must not yield Healthy.
        Assert.Equal(HealthState.Unknown, default(HealthState));
    }

    [Fact]
    public void A_newly_constructed_entity_is_unknown_not_healthy()
    {
        var entity = new Entity
        {
            Id = EntityId.New(),
            Kind = EntityKind.EsxiHost,
            DisplayName = "esx01",
            LastSeenUtc = Now,
        };

        Assert.Equal(HealthState.Unknown, entity.Health);
        Assert.Equal(HealthState.Unknown, entity.EffectiveHealth);
    }

    [Fact]
    public void A_vanished_entity_reports_unknown_even_if_it_was_healthy_when_last_seen()
    {
        // Reporting the stale value would be exactly the failure principle 1
        // forbids: showing green for something we cannot currently see.
        var entity = new Entity
        {
            Id = EntityId.New(),
            Kind = EntityKind.EsxiHost,
            DisplayName = "esx01",
            LastSeenUtc = Now.AddDays(-3),
            Health = HealthState.Healthy,
            ObservationState = ObservationState.Vanished,
        };

        Assert.Equal(HealthState.Healthy, entity.Health);
        Assert.Equal(HealthState.Unknown, entity.EffectiveHealth);
    }

    [Fact]
    public void An_entity_in_maintenance_keeps_its_observed_health()
    {
        // Maintenance suppresses alerting, but we can still see the host, so we
        // do not pretend not to know its state.
        var entity = new Entity
        {
            Id = EntityId.New(),
            Kind = EntityKind.EsxiHost,
            DisplayName = "esx01",
            LastSeenUtc = Now,
            Health = HealthState.Warning,
            ObservationState = ObservationState.InMaintenance,
        };

        Assert.Equal(HealthState.Warning, entity.EffectiveHealth);
    }
}

public class RelationshipRulesTests
{
    [Theory]
    [InlineData(RelationshipKind.PartOf, EdgeClass.Containment)]
    [InlineData(RelationshipKind.RunsOn, EdgeClass.Dependency)]
    [InlineData(RelationshipKind.BackedBy, EdgeClass.Dependency)]
    [InlineData(RelationshipKind.ManagedBy, EdgeClass.Dependency)]
    [InlineData(RelationshipKind.SameAs, EdgeClass.Identity)]
    [InlineData(RelationshipKind.ConnectedTo, EdgeClass.Physical)]
    public void Every_relationship_kind_has_a_class(RelationshipKind kind, EdgeClass expected)
    {
        Assert.Equal(expected, RelationshipRules.ClassOf(kind));
    }

    [Fact]
    public void Only_physical_connectivity_may_contain_cycles()
    {
        // A redundant SAN fabric is supposed to contain loops. Forbidding them
        // would make the product unable to model the thing it exists to
        // diagnose. See ADR-0004.
        Assert.True(RelationshipRules.MayContainCycles(RelationshipKind.ConnectedTo));

        Assert.False(RelationshipRules.MayContainCycles(RelationshipKind.PartOf));
        Assert.False(RelationshipRules.MayContainCycles(RelationshipKind.RunsOn));
        Assert.False(RelationshipRules.MayContainCycles(RelationshipKind.BackedBy));
        Assert.False(RelationshipRules.MayContainCycles(RelationshipKind.ManagedBy));
    }

    [Fact]
    public void Health_does_not_propagate_along_physical_or_identity_edges()
    {
        // Physical edges are excluded because they may cycle; identity edges
        // because both ends are the same thing, so there is nothing to
        // propagate.
        Assert.False(RelationshipRules.PropagatesHealth(RelationshipKind.ConnectedTo));
        Assert.False(RelationshipRules.PropagatesHealth(RelationshipKind.SameAs));

        Assert.True(RelationshipRules.PropagatesHealth(RelationshipKind.PartOf));
        Assert.True(RelationshipRules.PropagatesHealth(RelationshipKind.RunsOn));
    }

    [Fact]
    public void Identity_and_physical_edges_are_symmetric()
    {
        Assert.True(RelationshipRules.IsSymmetric(RelationshipKind.SameAs));
        Assert.True(RelationshipRules.IsSymmetric(RelationshipKind.ConnectedTo));

        Assert.False(RelationshipRules.IsSymmetric(RelationshipKind.PartOf));
        Assert.False(RelationshipRules.IsSymmetric(RelationshipKind.RunsOn));
    }
}
