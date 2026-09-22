using EnterpriseObservatory.Application.Health;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// ADR-0018 (health is the most severe alert against the entity, no
/// propagation) with ADR-0026's two additions (stale keeps the colour and says
/// since when; Unknown-only is grey, never green).
/// </summary>
public class EntityHealthDerivationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly EntityId H1 = new("vc-1:host-1");
    private static readonly EntityId H2 = new("vc-1:host-2");

    [Fact]
    public void With_no_alerts_the_collector_value_stands()
    {
        var health = EntityHealth.Derive(Host(H1, HealthState.Warning), []);

        Assert.Equal(HealthState.Warning, health.Health);
        Assert.Equal(HealthBasis.Collector, health.Basis);
        Assert.False(health.IsStale);
        Assert.Null(health.StaleSinceUtc);
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, HealthState.Critical)]
    [InlineData(AlertSeverity.Warning, HealthState.Warning)]
    public void An_open_alert_sets_the_health_to_its_severity(AlertSeverity severity, HealthState expected)
    {
        // The collector says green; the alert against the entity decides.
        var health = EntityHealth.Derive(Host(H1, HealthState.Healthy), [Alert("a", H1, severity)]);

        Assert.Equal(expected, health.Health);
        Assert.Equal(HealthBasis.Alerts, health.Basis);
    }

    [Fact]
    public void The_most_severe_open_alert_wins()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [Alert("a", H1, AlertSeverity.Warning), Alert("b", H1, AlertSeverity.Critical), Alert("c", H1, AlertSeverity.Warning)]);

        Assert.Equal(HealthState.Critical, health.Health);
    }

    [Fact]
    public void Alerts_override_the_collector_even_when_it_says_worse()
    {
        // ADR-0018: the badge derives from alerts only. A red collector rollup
        // with only a Warning alert against the entity is Warning.
        var health = EntityHealth.Derive(Host(H1, HealthState.Critical), [Alert("a", H1, AlertSeverity.Warning)]);

        Assert.Equal(HealthState.Warning, health.Health);
    }

    [Theory]
    [InlineData(AlertLifecycleState.Acknowledged)]
    [InlineData(AlertLifecycleState.Silenced)]
    public void Acknowledged_and_silenced_alerts_still_colour_the_entity(AlertLifecycleState state)
    {
        // Taking ownership or muting stops notifications; it does not fix anything.
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [Alert("a", H1, AlertSeverity.Critical) with { State = state }]);

        Assert.Equal(HealthState.Critical, health.Health);
    }

    [Fact]
    public void A_stale_critical_keeps_the_entity_red_and_says_since_when()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [Alert("a", H1, AlertSeverity.Critical) with { StaleSinceUtc = T0.AddHours(-3) }]);

        Assert.Equal(HealthState.Critical, health.Health);
        Assert.Equal(HealthBasis.Alerts, health.Basis);
        Assert.True(health.IsStale);
        Assert.Equal(T0.AddHours(-3), health.StaleSinceUtc);
    }

    [Fact]
    public void A_fresh_alert_at_the_worst_severity_makes_the_colour_fresh()
    {
        // The colour is rechecked as long as one alert that sets it is fresh.
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [
                Alert("a", H1, AlertSeverity.Critical) with { StaleSinceUtc = T0.AddHours(-3) },
                Alert("b", H1, AlertSeverity.Critical),
            ]);

        Assert.Equal(HealthState.Critical, health.Health);
        Assert.False(health.IsStale);
        Assert.Null(health.StaleSinceUtc);
    }

    [Fact]
    public void A_stale_critical_is_not_softened_by_a_fresh_warning()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [
                Alert("a", H1, AlertSeverity.Critical) with { StaleSinceUtc = T0.AddHours(-3) },
                Alert("b", H1, AlertSeverity.Warning),
            ]);

        Assert.Equal(HealthState.Critical, health.Health);
        Assert.True(health.IsStale);
    }

    [Fact]
    public void Several_stale_alerts_at_the_worst_severity_date_from_the_last_one_to_go_stale()
    {
        // The colour was last backed by fresh evidence when the last of them lost it.
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [
                Alert("a", H1, AlertSeverity.Critical) with { StaleSinceUtc = T0.AddHours(-3) },
                Alert("b", H1, AlertSeverity.Critical) with { StaleSinceUtc = T0.AddHours(-1) },
            ]);

        Assert.Equal(T0.AddHours(-1), health.StaleSinceUtc);
    }

    [Fact]
    public void Only_unknown_alerts_make_the_entity_grey_never_green()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [Alert("a", H1, AlertSeverity.Critical) with { State = AlertLifecycleState.Unknown }]);

        Assert.Equal(HealthState.Unknown, health.Health);
        Assert.Equal(HealthBasis.UnknownAlerts, health.Basis);
    }

    [Fact]
    public void An_active_alert_outranks_an_unknown_one()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [
                Alert("a", H1, AlertSeverity.Critical) with { State = AlertLifecycleState.Unknown },
                Alert("b", H1, AlertSeverity.Warning),
            ]);

        Assert.Equal(HealthState.Warning, health.Health);
        Assert.Equal(HealthBasis.Alerts, health.Basis);
    }

    [Fact]
    public void Resolved_and_unconfirmed_alerts_do_not_count()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy),
            [
                Alert("a", H1, AlertSeverity.Critical) with { State = AlertLifecycleState.Resolved },
                Alert("b", H1, AlertSeverity.Critical) with { IsConfirmed = false },
                Alert("c", H1, AlertSeverity.Critical) with { State = AlertLifecycleState.Unknown, IsConfirmed = false },
            ]);

        Assert.Equal(HealthState.Healthy, health.Health);
        Assert.Equal(HealthBasis.Collector, health.Basis);
    }

    [Fact]
    public void Another_entitys_alert_does_not_colour_this_one()
    {
        // ADR-0018: no propagation, and the entity's own alerts only.
        var health = EntityHealth.Derive(Host(H1, HealthState.Healthy), [Alert("a", H2, AlertSeverity.Critical)]);

        Assert.Equal(HealthState.Healthy, health.Health);
    }

    [Fact]
    public void A_vanished_entity_is_unknown_whatever_its_alerts_say()
    {
        var health = EntityHealth.Derive(
            Host(H1, HealthState.Healthy) with { ObservationState = ObservationState.Vanished },
            [Alert("a", H1, AlertSeverity.Critical)]);

        Assert.Equal(HealthState.Unknown, health.Health);
        Assert.Equal(HealthBasis.NotObserved, health.Basis);
    }

    [Fact]
    public void Deriving_for_many_entities_matches_deriving_one_at_a_time()
    {
        Entity[] entities = [Host(H1, HealthState.Healthy), Host(H2, HealthState.Warning)];
        AlertInstance[] alerts = [Alert("a", H1, AlertSeverity.Critical), Alert("b", null, AlertSeverity.Critical)];

        var all = EntityHealth.DeriveAll(entities, alerts);

        Assert.Equal(EntityHealth.Derive(entities[0], alerts), all[H1]);
        Assert.Equal(EntityHealth.Derive(entities[1], alerts), all[H2]);
        Assert.Equal(HealthState.Critical, all[H1].Health);
        Assert.Equal(HealthState.Warning, all[H2].Health);
    }

    private static Entity Host(EntityId id, HealthState health) => new()
    {
        Id = id,
        Kind = EntityKind.EsxiHost,
        DisplayName = id.Value,
        SourceInstanceId = "vc-1",
        Health = health,
        LastSeenUtc = T0,
    };

    private static AlertInstance Alert(string id, EntityId? entity, AlertSeverity severity) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Hardware", id, id),
        Severity = severity,
        State = AlertLifecycleState.Open,
        Title = id,
        Entity = entity,
        Category = "Hardware",
        Source = "vc-1",
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
    };
}
