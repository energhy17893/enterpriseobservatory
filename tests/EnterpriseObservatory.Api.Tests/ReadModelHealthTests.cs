using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Health;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// Every screen that shows an entity's colour shows the one derived from its
/// alerts (ADR-0018, ADR-0026) — the explorer, the detail page, the edges and
/// the overview counts agree.
/// </summary>
public partial class ReadModelTests
{
    [Fact]
    public void The_explorer_shows_health_derived_from_the_entitys_alerts()
    {
        GivenEntities(Host("h1", HealthState.Healthy), Host("h2", HealthState.Warning));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") });

        var items = Model().Entities().Items;

        var h1 = items.Single(e => e.Id == "h1");
        Assert.Equal(HealthState.Critical, h1.Health);
        Assert.Equal(HealthBasis.Alerts, h1.HealthBasis);

        // No alerts: the collector value, unchanged.
        var h2 = items.Single(e => e.Id == "h2");
        Assert.Equal(HealthState.Warning, h2.Health);
        Assert.Equal(HealthBasis.Collector, h2.HealthBasis);

        // Worst first, by the derived colour.
        Assert.Equal("h1", items[0].Id);
    }

    [Fact]
    public void The_explorer_filters_by_the_derived_health()
    {
        GivenEntities(Host("h1", HealthState.Healthy), Host("h2", HealthState.Healthy));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") });

        var critical = Assert.Single(Model().Entities(health: HealthState.Critical).Items);
        Assert.Equal("h1", critical.Id);
        Assert.Equal("h2", Assert.Single(Model().Entities(health: HealthState.Healthy).Items).Id);
    }

    [Fact]
    public void A_stale_critical_entity_is_red_with_its_since_on_the_detail_page()
    {
        GivenEntities(Host("h1", HealthState.Healthy));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with
        {
            Entity = new EntityId("h1"),
            StaleSinceUtc = T0.AddHours(-2),
            StaleReason = UnknownReason.SourceSilent,
        });

        var entity = Model().Entity("h1")!.Entity;

        Assert.Equal(HealthState.Critical, entity.Health);
        Assert.True(entity.HealthIsStale);
        Assert.Equal(T0.AddHours(-2), entity.HealthStaleSinceUtc);
    }

    [Fact]
    public void An_entity_whose_only_alerts_are_unknown_is_grey()
    {
        GivenEntities(Host("h1", HealthState.Healthy));
        GivenAlerts(Alert("a", AlertSeverity.Critical) with
        {
            Entity = new EntityId("h1"),
            State = AlertLifecycleState.Unknown,
        });

        var entity = Model().Entity("h1")!.Entity;

        Assert.Equal(HealthState.Unknown, entity.Health);
        Assert.Equal(HealthBasis.UnknownAlerts, entity.HealthBasis);
    }

    [Fact]
    public void Compliance_findings_do_not_change_an_entitys_health()
    {
        // Findings are posture, not state (ADR-0024): a failing control is on
        // the compliance screen, not in the entity's colour.
        GivenEntities(Host("h1", HealthState.Healthy));
        GivenFindings(Finding(ContinuityControls.HaEnabled, new EntityId("h1"), ComplianceVerdict.Failing));

        var entity = Model().Entity("h1")!.Entity;

        Assert.Equal(HealthState.Healthy, entity.Health);
        Assert.Equal(HealthBasis.Collector, entity.HealthBasis);
    }

    [Fact]
    public void The_overview_counts_entities_by_their_derived_health()
    {
        GivenEntities(Host("h1", HealthState.Healthy), Host("h2", HealthState.Healthy), Host("h3", HealthState.Healthy));
        GivenAlerts(
            Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1"), StaleSinceUtc = T0.AddHours(-1) },
            Alert("b", AlertSeverity.Warning) with { Entity = new EntityId("h2"), State = AlertLifecycleState.Unknown });

        var overview = Model().Overview();

        Assert.Equal(1, overview.EntitiesByHealth[nameof(HealthState.Critical)]);
        Assert.Equal(1, overview.EntitiesByHealth[nameof(HealthState.Unknown)]);
        Assert.Equal(1, overview.EntitiesByHealth[nameof(HealthState.Healthy)]);
        Assert.Equal(1, overview.EntitiesWithStaleHealth);
    }

    [Fact]
    public void An_edge_shows_the_other_entitys_derived_health()
    {
        GivenEntities(Host("h1", HealthState.Healthy), Host("h2", HealthState.Healthy));
        GivenRelationships(new Relationship
        {
            From = new EntityId("h1"),
            To = new EntityId("h2"),
            Kind = RelationshipKind.ConnectedTo,
            ObservedAtUtc = T0,
        });
        GivenAlerts(Alert("a", AlertSeverity.Warning) with { Entity = new EntityId("h2") });

        var edge = Assert.Single(Model().Entity("h1")!.Relationships);

        Assert.Equal(HealthState.Warning, edge.OtherHealth);

        // And no propagation back to h1 (ADR-0018).
        Assert.Equal(HealthState.Healthy, Model().Entity("h1")!.Entity.Health);
    }
}
