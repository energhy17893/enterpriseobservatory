using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Domain.Tests;

public class AlertLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cycle(int n) => T0.AddSeconds(30 * n);

    private static AlertDefinition Alert(AlertSeverity severity = AlertSeverity.Warning) => new()
    {
        Fingerprint = AlertFingerprint.Create("ilo", "Power supply 2 failed", "Hardware", "esx01"),
        Severity = severity,
        Title = "Power supply 2 failed",
        Category = "Hardware",
        Source = "ilo",
    };

    // --- hysteresis -------------------------------------------------------

    [Fact]
    public void A_warning_is_not_confirmed_on_its_first_observation()
    {
        var instance = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));

        Assert.False(instance.IsConfirmed);
        Assert.False(instance.IsVisible);
        Assert.False(instance.NotifyPending);
    }

    [Fact]
    public void A_warning_is_confirmed_on_its_second_consecutive_observation()
    {
        var first = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        var second = AlertLifecycle.OnObserved(first, Alert(), HysteresisPolicy.Default, Cycle(1));

        Assert.True(second.IsConfirmed);
        Assert.True(second.IsVisible);
        Assert.True(second.NotifyPending);
    }

    [Fact]
    public void A_critical_is_confirmed_immediately()
    {
        // The cost of delaying a real outage by a cycle exceeds the cost of an
        // occasional spurious critical.
        var instance = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(0));

        Assert.True(instance.IsConfirmed);
        Assert.True(instance.NotifyPending);
    }

    [Fact]
    public void An_unconfirmed_warning_that_disappears_is_forgotten_entirely()
    {
        // It was flapping. Leaving behind a resolved alert nobody needed to see
        // is itself noise.
        var first = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));

        Assert.Null(AlertLifecycle.OnAbsent(first, Cycle(1)));
    }

    // --- notification discipline -----------------------------------------

    [Fact]
    public void A_persisting_alert_does_not_notify_again_on_every_cycle()
    {
        var i = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(1));
        Assert.True(i.NotifyPending);

        // The dispatcher clears the flag once it has sent.
        i = i with { NotifyPending = false };

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(2));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3));

        Assert.False(i.NotifyPending);
    }

    [Fact]
    public void Escalation_from_warning_to_critical_notifies_again()
    {
        var i = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(1));
        i = i with { NotifyPending = false };

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(2));

        Assert.Equal(AlertSeverity.Critical, i.Severity);
        Assert.True(i.NotifyPending);
    }

    [Fact]
    public void De_escalation_does_not_notify()
    {
        // A problem getting less bad is not news worth waking someone for.
        var i = AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(0));
        i = i with { NotifyPending = false };

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Warning), HysteresisPolicy.Default, Cycle(1));

        Assert.Equal(AlertSeverity.Warning, i.Severity);
        Assert.False(i.NotifyPending);
    }

    // --- acknowledge ------------------------------------------------------

    [Fact]
    public void Acknowledging_stops_notifications_but_keeps_the_alert_visible()
    {
        var i = Confirmed();

        i = AlertLifecycle.Acknowledge(i, "ertugrul", Cycle(2));

        Assert.Equal(AlertLifecycleState.Acknowledged, i.State);
        Assert.False(i.NotifyPending);
        Assert.True(i.IsVisible);
    }

    [Fact]
    public void An_acknowledged_alert_whose_condition_clears_becomes_resolved()
    {
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));

        var next = AlertLifecycle.OnAbsent(i, Cycle(3));

        Assert.NotNull(next);
        Assert.Equal(AlertLifecycleState.Resolved, next.State);
        Assert.False(next.IsVisible);
    }

    [Fact]
    public void Escalation_revokes_an_acknowledgement()
    {
        // The operator accepted the problem they were shown, not a worse one.
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.True(i.NotifyPending);
    }

    // --- sticky clear: the subtle one ------------------------------------

    [Fact]
    public void An_operator_clear_survives_the_condition_still_firing()
    {
        // A permanent hardware fault awaiting a replacement part would
        // otherwise re-alert on every polling cycle and destroy trust in
        // notifications.
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        Assert.Equal(AlertLifecycleState.Resolved, i.State);
        Assert.True(i.ClearedByOperator);

        // The PSU is still dead; we keep observing it for many cycles.
        for (var cycle = 3; cycle < 20; cycle++)
        {
            i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(cycle));
        }

        Assert.Equal(AlertLifecycleState.Resolved, i.State);
        Assert.False(i.NotifyPending);
        Assert.False(i.IsVisible);
    }

    [Fact]
    public void A_cleared_alert_is_retired_once_the_condition_actually_goes_away()
    {
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3));

        // The part is replaced; the fingerprint stops appearing.
        Assert.Null(AlertLifecycle.OnAbsent(i, Cycle(4)));
    }

    [Fact]
    public void A_fault_that_returns_after_a_clear_is_a_fresh_alert_and_notifies()
    {
        // This is what makes the stickiness bounded rather than permanent:
        // once retired, the next occurrence is a new problem.
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        Assert.Null(AlertLifecycle.OnAbsent(i, Cycle(3)));

        var reborn = AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(9));

        Assert.False(reborn.ClearedByOperator);
        Assert.True(reborn.NotifyPending);
        Assert.True(reborn.IsVisible);
    }

    [Fact]
    public void A_condition_that_resolves_on_its_own_and_returns_does_notify_again()
    {
        // Distinct from the cleared case: nobody decided this was handled.
        var i = Confirmed();
        var resolved = AlertLifecycle.OnAbsent(i, Cycle(2));
        Assert.NotNull(resolved);
        Assert.False(resolved.ClearedByOperator);

        var returned = AlertLifecycle.OnObserved(resolved, Alert(), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertLifecycleState.Open, returned.State);
        Assert.True(returned.NotifyPending);
    }

    // --- silence ----------------------------------------------------------

    [Fact]
    public void A_silenced_alert_returns_to_open_when_the_silence_expires()
    {
        var i = AlertLifecycle.Silence(Confirmed(), "ertugrul", Cycle(1).AddHours(2), Cycle(1));
        Assert.Equal(AlertLifecycleState.Silenced, i.State);

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(1).AddHours(3));

        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.Null(i.SilencedUntilUtc);
    }

    [Fact]
    public void A_silence_must_end_in_the_future()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AlertLifecycle.Silence(Confirmed(), "ertugrul", Cycle(1), Cycle(2)));
    }

    // --- guards -----------------------------------------------------------

    [Fact]
    public void Info_alerts_never_enter_the_lifecycle()
    {
        // Info is a statement about the world, not a call to action. Letting it
        // occupy the inbox is how alert fatigue starts.
        Assert.False(AlertLifecycle.EntersLifecycle(AlertSeverity.Info));

        Assert.Throws<ArgumentException>(() =>
            AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Info), HysteresisPolicy.Default, Cycle(0)));
    }

    [Fact]
    public void An_observation_of_a_different_problem_is_rejected()
    {
        var i = Confirmed();
        var other = Alert() with
        {
            Fingerprint = AlertFingerprint.Create("ilo", "Fan 3 failed", "Hardware", "esx01"),
        };

        Assert.Throws<ArgumentException>(() =>
            AlertLifecycle.OnObserved(i, other, HysteresisPolicy.Default, Cycle(2)));
    }

    // --- audit trail ------------------------------------------------------

    [Fact]
    public void Every_transition_is_recorded_with_its_reason_and_actor()
    {
        var i = Confirmed();
        i = AlertLifecycle.Acknowledge(i, "ertugrul", Cycle(2));
        i = AlertLifecycle.Clear(i, "mehmet", Cycle(3));

        var ack = i.History.Single(t => t.Reason == AlertTransitionReason.OperatorAcknowledged);
        Assert.Equal("ertugrul", ack.Actor);

        var clear = i.History.Single(t => t.Reason == AlertTransitionReason.OperatorCleared);
        Assert.Equal("mehmet", clear.Actor);
        Assert.Equal(AlertLifecycleState.Acknowledged, clear.From);
        Assert.Equal(AlertLifecycleState.Resolved, clear.To);
    }

    private static AlertInstance Confirmed()
    {
        var first = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        return AlertLifecycle.OnObserved(first, Alert(), HysteresisPolicy.Default, Cycle(1));
    }
}

public class AlertFingerprintTests
{
    [Fact]
    public void The_same_problem_produces_the_same_fingerprint_every_cycle()
    {
        var a = AlertFingerprint.Create("ilo", "PSU 2 failed", "Hardware", "esx01", "ilo-hw");
        var b = AlertFingerprint.Create("ilo", "PSU 2 failed", "Hardware", "esx01", "ilo-hw");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Casing_and_padding_differences_do_not_split_one_problem_into_two()
    {
        var a = AlertFingerprint.Create("iLO", "PSU 2 Failed", "Hardware", "ESX01");
        var b = AlertFingerprint.Create("ilo", " psu 2 failed ", "hardware", "esx01");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_objects_with_the_same_fault_are_different_problems()
    {
        var a = AlertFingerprint.Create("ilo", "PSU 2 failed", "Hardware", "esx01");
        var b = AlertFingerprint.Create("ilo", "PSU 2 failed", "Hardware", "esx02");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_separator_inside_a_part_cannot_forge_a_boundary()
    {
        // Without escaping, ("a|b", "c") and ("a", "b|c") would collide.
        var a = AlertFingerprint.Create("s", "a|b", "c", "o");
        var b = AlertFingerprint.Create("s", "a", "b|c", "o");

        Assert.NotEqual(a, b);
    }
}
