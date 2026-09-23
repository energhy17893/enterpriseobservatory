using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Package D's <c>/health</c> judgment: whether the product's cycles run and
/// its store takes writes — not process liveness, and not the union of its
/// sources — with a fake clock standing in for "time passing while nobody
/// attempts anything", the exact shape of the four-hour outage
/// docs/live-verification.md §9 measured.
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

    /// <summary>A source whose last attempt was its last success, unless told otherwise.</summary>
    private static CollectorHealth Of(
        CollectorRole role,
        DateTimeOffset? lastSuccess,
        string instanceId = "vc-1",
        DateTimeOffset? lastAttempt = null,
        CollectionFailureKind? failureKind = null) => new()
    {
        InstanceId = instanceId,
        Role = role,
        Health = failureKind is null && lastSuccess is not null ? HealthState.Healthy : HealthState.Unknown,
        LastSuccessUtc = lastSuccess,
        LastAttemptUtc = lastAttempt ?? lastSuccess,
        LastFailureKind = failureKind,
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
    public void No_attempt_for_the_configured_minutes_is_unhealthy()
    {
        // The measured failure: the service was up but blind for hours and
        // nothing outside host.log said so. Fifteen minutes (the default)
        // without one attempt must flip /health to Unhealthy.
        var lastSuccess = T0 - TimeSpan.FromMinutes(16);
        var health = new[] { Of(CollectorRole.Inventory, lastSuccess) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var inventory = Assert.Single(report.Roles, r => r.Role == CollectorRole.Inventory);
        Assert.Equal(ServiceHealthStatus.Unhealthy, inventory.Status);
        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void A_polled_source_never_attempted_is_unhealthy()
    {
        // No attempt and no success recorded: no evidence any cycle ran.
        var health = new[] { Of(CollectorRole.Inventory, lastSuccess: null) };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void The_freshest_attempt_decides_a_roles_status_not_the_stalest_source()
    {
        // Reverses "the worst source decides" (2026-09-23): vc-2 silent for
        // twenty minutes is vc-2's own alert, not the product being down,
        // while vc-1's attempt just now proves the cycle runs.
        var health = new[]
        {
            Of(CollectorRole.Inventory, T0, "vc-1"),
            Of(CollectorRole.Inventory, T0 - TimeSpan.FromMinutes(20), "vc-2"),
        };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        var inventory = Assert.Single(report.Roles, r => r.Role == CollectorRole.Inventory);
        Assert.Equal(ServiceHealthStatus.Healthy, inventory.Status);
        Assert.Equal(T0 - TimeSpan.FromMinutes(20), inventory.OldestLastSuccessUtc);
    }

    [Fact]
    public void A_disabled_connection_counts_toward_no_role_and_shows_as_not_polled()
    {
        // Switched off by an operator: its last attempt stops ageing only
        // because nobody attempts it, and that must not page anyone.
        var health = new[]
        {
            Of(CollectorRole.Inventory, T0 - TimeSpan.FromMinutes(1), "vc-live", T0 - TimeSpan.FromMinutes(1)),
            Of(CollectorRole.Inventory, T0 - TimeSpan.FromDays(2), "vc-off", T0 - TimeSpan.FromDays(2)),
        };

        var report = HealthAssessment.Assess(
            health, Options, Health, NoGaps, T0, disabledConnections: new HashSet<string> { "vc-off" });

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
        Assert.Equal(SourceStatus.NotPolled, Assert.Single(report.Sources, s => s.InstanceId == "vc-off").Status);
        Assert.Equal(
            ServiceHealthStatus.Unhealthy,
            HealthAssessment.Assess([health[1]], Options, Health, NoGaps, T0).Status);
    }

    [Fact]
    public void Unreachable_customer_sources_and_an_unpolled_one_leave_the_product_healthy()
    {
        // The live estate on 2026-09-23: two vCenters failing on the
        // customer's DNS/SSL — attempted a minute ago, last success hours
        // back — and one connection whose kind has no collector. /health was
        // 503 permanently; the product was working.
        var stale = T0 - TimeSpan.FromHours(2);
        var justNow = T0 - TimeSpan.FromMinutes(1);
        var health = new[]
        {
            Of(CollectorRole.Inventory, stale, "cls-vcenter", justNow, CollectionFailureKind.Unreachable),
            Of(CollectorRole.Inventory, stale, "svt-vcenter", justNow, CollectionFailureKind.Unreachable),
            Of(CollectorRole.Inventory, null, "hyperv-1", T0 - TimeSpan.FromHours(3), CollectionFailureKind.NotConfigured),
        };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
        Assert.True(report.StoreReachable);

        var cls = Assert.Single(report.Sources, s => s.InstanceId == "cls-vcenter");
        Assert.Equal(SourceStatus.Unknown, cls.Status);
        Assert.Equal(stale, cls.LastSuccessUtc);
        Assert.Equal(justNow, cls.LastAttemptUtc);
        Assert.Equal(SourceStatus.Unknown, Assert.Single(report.Sources, s => s.InstanceId == "svt-vcenter").Status);

        var unpolled = Assert.Single(report.Sources, s => s.InstanceId == "hyperv-1");
        Assert.Equal(SourceStatus.NotPolled, unpolled.Status);
        Assert.Null(unpolled.LastSuccessUtc);
    }

    [Fact]
    public void An_unpolled_source_counts_toward_no_role()
    {
        // NotConfigured with no success and a stale attempt would be
        // Unhealthy under any freshness rule; it is not polled, so it is not
        // judged at all.
        var health = new[]
        {
            Of(CollectorRole.Observation, null, "hyperv-1", T0 - TimeSpan.FromHours(3), CollectionFailureKind.NotConfigured),
        };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Healthy, report.Status);
        Assert.Equal(SourceStatus.NotPolled, Assert.Single(report.Sources).Status);
    }

    [Fact]
    public void A_role_with_no_attempt_since_its_sources_last_failed_is_unhealthy()
    {
        // Failing is fine; not trying is not. The last attempt sixteen
        // minutes ago means the cycle stopped, whatever the sources said.
        var attempt = T0 - TimeSpan.FromMinutes(16);
        var health = new[]
        {
            Of(CollectorRole.Inventory, null, "vc-1", attempt, CollectionFailureKind.Unreachable),
            Of(CollectorRole.Inventory, T0 - TimeSpan.FromHours(1), "vc-2", attempt, CollectionFailureKind.Timeout),
        };

        var report = HealthAssessment.Assess(health, Options, Health, NoGaps, T0);

        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public void A_store_that_refused_its_last_write_is_unhealthy_even_with_fresh_cycles()
    {
        var health = new[] { Of(CollectorRole.Inventory, T0), Of(CollectorRole.Observation, T0) };

        var report = HealthAssessment.Assess(
            health, Options, Health, NoGaps, T0, storeFailure: "connection refused");

        Assert.Equal(ServiceHealthStatus.Unhealthy, report.Status);
        Assert.False(report.StoreReachable);
        Assert.Equal("connection refused", report.StoreFailure);
        Assert.All(report.Roles, r => Assert.Equal(ServiceHealthStatus.Healthy, r.Status));
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
