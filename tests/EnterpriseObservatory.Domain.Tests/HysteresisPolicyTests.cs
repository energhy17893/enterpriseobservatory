using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Domain.Tests;

public class HysteresisPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static AlertDefinition Alert(AlertSeverity severity = AlertSeverity.Warning) => new()
    {
        Fingerprint = AlertFingerprint.Create("vsphere", "Host CPU high", "Performance", "esx01"),
        Severity = severity,
        Title = "Host CPU high",
    };

    [Fact]
    public void By_default_a_minimum_duration_is_not_imposed()
    {
        // Matching the previous product, so nothing changes unless configured.
        Assert.Equal(TimeSpan.Zero, HysteresisPolicy.Default.WarningMinimumDuration);
        Assert.Equal(TimeSpan.Zero, HysteresisPolicy.Default.CriticalMinimumDuration);
    }

    [Fact]
    public void Hit_count_alone_does_not_confirm_when_a_minimum_duration_is_set()
    {
        var policy = new HysteresisPolicy
        {
            WarningConsecutiveHits = 2,
            WarningMinimumDuration = TimeSpan.FromMinutes(5),
        };

        // Two hits thirty seconds apart satisfies the count but not the clock.
        var i = AlertLifecycle.OnObserved(null, Alert(), policy, T0);
        i = AlertLifecycle.OnObserved(i, Alert(), policy, T0.AddSeconds(30));

        Assert.False(i.IsConfirmed);
        Assert.False(i.IsVisible);
    }

    [Fact]
    public void Confirmation_arrives_once_the_condition_has_persisted_long_enough()
    {
        var policy = new HysteresisPolicy
        {
            WarningConsecutiveHits = 2,
            WarningMinimumDuration = TimeSpan.FromMinutes(5),
        };

        var i = AlertLifecycle.OnObserved(null, Alert(), policy, T0);
        i = AlertLifecycle.OnObserved(i, Alert(), policy, T0.AddSeconds(30));
        i = AlertLifecycle.OnObserved(i, Alert(), policy, T0.AddMinutes(6));

        Assert.True(i.IsConfirmed);
        Assert.Equal(AlertNotificationKind.Raised, i.PendingNotification);
    }

    [Fact]
    public void A_duration_filter_is_immune_to_the_polling_interval()
    {
        // This is the reason duration exists alongside hit count. Halving the
        // polling interval to get fresher data would halve a hit-count filter,
        // which is never what anyone intended.
        var policy = new HysteresisPolicy
        {
            WarningConsecutiveHits = 2,
            WarningMinimumDuration = TimeSpan.FromMinutes(5),
        };

        // Poll every 5 seconds instead of 30: many more hits, same elapsed time.
        var i = AlertLifecycle.OnObserved(null, Alert(), policy, T0);
        for (var s = 5; s <= 60; s += 5)
        {
            i = AlertLifecycle.OnObserved(i, Alert(), policy, T0.AddSeconds(s));
        }

        Assert.True(i.ConsecutiveHits > 10);
        Assert.False(i.IsConfirmed);
    }

    [Fact]
    public void A_critical_still_confirms_immediately_under_the_default_policy()
    {
        // Delaying a confirmed critical trades away the thing the product
        // exists to do, so the default must never do it.
        var i = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, T0);

        Assert.True(i.IsConfirmed);
    }

    [Fact]
    public void A_minimum_duration_can_be_imposed_on_criticals_but_must_be_deliberate()
    {
        var policy = new HysteresisPolicy { CriticalMinimumDuration = TimeSpan.FromMinutes(2) };

        var i = AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Critical), policy, T0);
        Assert.False(i.IsConfirmed);

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), policy, T0.AddMinutes(3));
        Assert.True(i.IsConfirmed);
    }

    [Fact]
    public void Confirmation_once_granted_is_not_withdrawn()
    {
        var policy = new HysteresisPolicy { WarningConsecutiveHits = 1 };

        var i = AlertLifecycle.OnObserved(null, Alert(), policy, T0);
        Assert.True(i.IsConfirmed);

        i = AlertLifecycle.OnObserved(i, Alert(), policy, T0.AddSeconds(30));
        Assert.True(i.IsConfirmed);
    }
}
