using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Package D's <c>/health</c> judgment: freshness, not process liveness, with
/// a fake clock standing in for "time passing while nobody reads anything" —
/// the exact shape of the four-hour outage docs/live-verification.md §9
/// measured.
/// </summary>
public class HealthAssessmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

    private static readonly MonitoringOptions Options = new()
    {
        InventoryInterval = TimeSpan.FromMinutes(5),
        ObservationInterval = TimeSpan.FromSeconds(30),
    };

    private static readonly HealthOptions Health = new()
    {
        DegradedAfterIntervals = 3,
        UnhealthyAfter = TimeSpan.FromMinutes(15),
    };

    private static CollectorHealth Of(
        CollectorRole role, DateTimeOffset? lastSuccess, string instanceId = "vc-1") => new()
    {
        InstanceId = instanceId,
        Role = role,
        Health = lastSuccess is null ? HealthState.Unknown : HealthState.Healthy,
        LastSuccessUtc = lastSuccess,
    };

    private static readonly IReadOnlyDictionary<CollectionGapState, int> NoGaps =
        new Dictionary<CollectionGapState, int>();

    [Fact]
    public void No_collectors_configured_is_healthy_not_unhealthy()
    {
        // An installation with nothing configured looks like a healthy empty
        // one everywhere else in the product (ReadModel.Overview); /health
        // must agree rather than report the emptiest possible install as down.
        var report = HealthAssessment.Assess([], Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void A_read_just_now_is_healthy()
    {
        var health = new[] { Of(CollectorRole.Inventory, T0), Of(CollectorRole.Observation, T0) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void A_role_stale_beyond_three_intervals_is_degraded()
    {
        // Observation's interval is 30s; 3 intervals is 90s. 91s stale should
        // tip it into Degraded while staying well under UnhealthyAfter.
        var lastSuccess = T0 - TimeSpan.FromSeconds(91);
        var health = new[] { Of(CollectorRole.Observation, lastSuccess) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var observation = Assert.Single(report.Roles, r => r.Role == CollectorRole.Observation);
        Assert.Equal(ServiceHealthStatus.Degraded, observation.Status);
        Assert.Equal(ServiceHealthStatus.Degraded, report.Status);
    }

    [Fact]
    public void A_role_just_under_three_intervals_stale_is_still_healthy()
    {
        var lastSuccess = T0 - TimeSpan.FromSeconds(89);
        var health = new[] { Of(CollectorRole.Observation, lastSuccess) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void No_successful_read_for_the_configured_minutes_is_unhealthy()
    {
        // The measured failure: the service was up but blind for hours and
        // nothing outside host.log said so. Fifteen minutes (the default)
        // without one successful read must flip /health to Unhealthy.
        var lastSuccess = T0 - TimeSpan.FromMinutes(16);
        var health = new[] { Of(CollectorRole.Inventory, lastSuccess) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var inventory = Assert.Single(report.Roles, r => r.Role == CollectorRole.Inventory);
        Assert.Equal(ServiceHealthStatus.Unhealthy, inventory.Status);
        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void A_configured_source_that_has_never_once_succeeded_is_unhealthy()
    {
        var health = new[] { Of(CollectorRole.Inventory, lastSuccess: null) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void The_worst_of_several_sources_in_a_role_decides_that_roles_status()
    {
        var health = new[]
        {
            Of(CollectorRole.Inventory, T0, "vc-1"),
            Of(CollectorRole.Inventory, T0 - TimeSpan.FromMinutes(20), "vc-2"),
        };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var inventory = Assert.Single(report.Roles, r => r.Role == CollectorRole.Inventory);
        Assert.Equal(ServiceHealthStatus.Unhealthy, inventory.Status);
    }

    [Fact]
    public void A_healthy_reading_crosses_into_unhealthy_as_the_clock_advances()
    {
        // The fake-clock transition test: the same collector health, judged
        // at two different times, must move Healthy -> Unhealthy exactly
        // where the threshold says it should, and not before.
        var lastSuccess = T0;
        var health = new[] { Of(CollectorRole.Inventory, lastSuccess) };

        var stillHealthy = HealthAssessment.Assess(
            health, Options, Health, NoGaps, T0 + TimeSpan.FromMinutes(14));
        var nowUnhealthy = HealthAssessment.Assess(
            health, Options, Health, NoGaps, T0 + TimeSpan.FromMinutes(16));

        Assert.Equal(ServiceHealthStatus.Healthy, stillHealthy.Status);
        Assert.Equal(ServiceHealthStatus.Unhealthy, nowUnhealthy.Status);
    }

    [Fact]
    public void The_events_role_is_judged_alongside_inventory_and_observation()
    {
        // F1 gave the event read its own collector_health row (CollectorRole
        // .Events); /health must report it as its own role rather than
        // silently folding it into inventory's.
        var lastSuccess = T0 - TimeSpan.FromMinutes(20);
        var health = new[] { Of(CollectorRole.Events, lastSuccess) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var events = Assert.Single(report.Roles, r => r.Role == CollectorRole.Events);
        Assert.Equal(ServiceHealthStatus.Unhealthy, events.Status);
        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void Gap_counts_pass_through_by_state()
    {
        var gapCounts = new Dictionary<CollectionGapState, int>
        {
            [CollectionGapState.Open] = 2,
            [CollectionGapState.Unrecoverable] = 1,
            [CollectionGapState.Filled] = 40,
        };

        var report = HealthAssessment.Assess(
            [Of(CollectorRole.Inventory, T0)], Options, Health, gapCounts, T0);

        Assert.Equal(2, report.OpenGaps);
        Assert.Equal(1, report.UnrecoverableGaps);
    }
}
