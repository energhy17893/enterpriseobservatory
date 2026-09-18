using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Domain.Tests;

public class AlertLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cycle(int n) => T0.AddSeconds(30 * n);

    private static readonly EntityId Host = new("esx01");

    private static AlertDefinition Alert(AlertSeverity severity = AlertSeverity.Warning) => new()
    {
        Fingerprint = AlertFingerprint.Create("ilo", "Power supply 2 failed", "Hardware", "esx01"),
        Severity = severity,
        Title = "Power supply 2 failed",
        Category = "Hardware",
        Source = "ilo",
        Entity = Host,
    };

    // --- hysteresis -------------------------------------------------------

    [Fact]
    public void A_warning_is_not_confirmed_on_its_first_observation()
    {
        var instance = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));

        Assert.False(instance.IsConfirmed);
        Assert.False(instance.IsVisible);
        Assert.Equal(AlertNotificationKind.None, instance.PendingNotification);
    }

    [Fact]
    public void A_warning_is_confirmed_on_its_second_consecutive_observation()
    {
        var second = Confirmed();

        Assert.True(second.IsConfirmed);
        Assert.True(second.IsVisible);
        Assert.Equal(AlertNotificationKind.Raised, second.PendingNotification);
    }

    [Fact]
    public void A_critical_is_confirmed_immediately()
    {
        // The cost of delaying a real outage by a cycle exceeds the cost of an
        // occasional spurious critical.
        var instance = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(0));

        Assert.True(instance.IsConfirmed);
        Assert.Equal(AlertNotificationKind.Raised, instance.PendingNotification);
    }

    [Fact]
    public void An_unconfirmed_warning_that_disappears_is_forgotten_but_still_counted()
    {
        // Forgetting the instance suppresses the noise; reporting that it
        // ceased is what lets flap detection see the instability at all.
        var first = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));

        var result = AlertLifecycle.OnAbsent(first, Cycle(1));

        Assert.Null(result.Instance);
        Assert.True(result.CeasedFiring);
    }

    // --- notification discipline -----------------------------------------

    [Fact]
    public void A_persisting_alert_does_not_notify_again_on_every_cycle()
    {
        var i = AlertLifecycle.MarkNotified(Confirmed());

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(2));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);
    }

    [Fact]
    public void Escalation_from_warning_to_critical_notifies_again()
    {
        var i = AlertLifecycle.MarkNotified(Confirmed());

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(2));

        Assert.Equal(AlertSeverity.Critical, i.Severity);
        Assert.Equal(AlertNotificationKind.Escalated, i.PendingNotification);
    }

    [Fact]
    public void De_escalation_is_reported_as_an_improvement_not_as_a_page()
    {
        // Operators do want to know a critical has eased. They do not want to
        // be paged for it — routing is the application's call, which is why the
        // domain reports a kind rather than a bare flag.
        var i = AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(0));
        i = AlertLifecycle.MarkNotified(i);

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Warning), HysteresisPolicy.Default, Cycle(1));

        Assert.Equal(AlertSeverity.Warning, i.Severity);
        Assert.Equal(AlertNotificationKind.Improved, i.PendingNotification);
        Assert.Contains(i.History, t => t.Reason == AlertTransitionReason.SeverityDecreased);
    }

    [Fact]
    public void An_improvement_never_outranks_a_pending_escalation()
    {
        var i = AlertLifecycle.MarkNotified(Confirmed());
        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(2));
        Assert.Equal(AlertNotificationKind.Escalated, i.PendingNotification);

        // It eases before anyone has dispatched the escalation.
        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Warning), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertNotificationKind.Escalated, i.PendingNotification);
    }

    // --- acknowledge ------------------------------------------------------

    [Fact]
    public void Acknowledging_stops_notifications_but_keeps_the_alert_visible()
    {
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));

        Assert.Equal(AlertLifecycleState.Acknowledged, i.State);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);
        Assert.True(i.IsVisible);
    }

    [Fact]
    public void An_acknowledged_alert_whose_condition_clears_becomes_resolved()
    {
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));

        var result = AlertLifecycle.OnAbsent(i, Cycle(3));

        Assert.NotNull(result.Instance);
        Assert.Equal(AlertLifecycleState.Resolved, result.Instance.State);
        Assert.False(result.Instance.IsVisible);
        Assert.True(result.CeasedFiring);
    }

    [Fact]
    public void Escalation_revokes_an_acknowledgement()
    {
        // The operator accepted the problem they were shown, not a worse one.
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.Equal(AlertNotificationKind.Escalated, i.PendingNotification);
    }

    // --- sticky clear -----------------------------------------------------

    [Fact]
    public void An_operator_clear_survives_the_condition_still_firing()
    {
        // A permanent hardware fault awaiting a replacement part would
        // otherwise re-alert on every polling cycle and destroy trust in
        // notifications.
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        Assert.Equal(AlertLifecycleState.Resolved, i.State);
        Assert.True(i.ClearedByOperator);

        for (var cycle = 3; cycle < 20; cycle++)
        {
            i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(cycle));
        }

        Assert.Equal(AlertLifecycleState.Resolved, i.State);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);
        Assert.False(i.IsVisible);
    }

    [Fact]
    public void A_cleared_alert_is_retired_once_the_condition_actually_goes_away()
    {
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3));

        var result = AlertLifecycle.OnAbsent(i, Cycle(4));

        Assert.Null(result.Instance);
        // It stopped firing on an earlier cycle, so this is not a new cessation
        // and must not be counted as another flap.
        Assert.False(result.CeasedFiring);
    }

    [Fact]
    public void A_fault_that_returns_after_a_clear_is_a_fresh_alert_and_notifies()
    {
        var i = AlertLifecycle.Clear(Confirmed(), "ertugrul", Cycle(2));
        Assert.Null(AlertLifecycle.OnAbsent(i, Cycle(3)).Instance);

        var reborn = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(9));

        Assert.False(reborn.ClearedByOperator);
        Assert.Equal(AlertNotificationKind.Raised, reborn.PendingNotification);
        Assert.True(reborn.IsVisible);
    }

    [Fact]
    public void A_condition_that_resolves_on_its_own_and_returns_does_notify_again()
    {
        // Distinct from the cleared case: nobody decided this was handled.
        var resolved = AlertLifecycle.OnAbsent(Confirmed(), Cycle(2)).Instance;
        Assert.NotNull(resolved);
        Assert.False(resolved.ClearedByOperator);

        var returned = AlertLifecycle.OnObserved(resolved, Alert(), HysteresisPolicy.Default, Cycle(3));

        Assert.Equal(AlertLifecycleState.Open, returned.State);
        Assert.Equal(AlertNotificationKind.Returned, returned.PendingNotification);
    }

    // --- maintenance windows ----------------------------------------------

    [Fact]
    public void A_maintenance_window_withholds_notification_without_hiding_the_alert()
    {
        // Hiding it would be fabrication, and would also destroy the record of
        // what actually broke during planned work.
        var window = new MaintenanceWindow
        {
            Id = "mw-1",
            Title = "Firmware upgrade",
            StartUtc = Cycle(0),
            EndUtc = Cycle(100),
            Entities = [Host],
        };

        var i = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(1), [window]);

        Assert.True(i.IsVisible);
        Assert.Equal("mw-1", i.SuppressedByWindowId);
        Assert.Equal(AlertNotificationKind.Raised, i.PendingNotification);
        Assert.False(i.ShouldNotify);
    }

    [Fact]
    public void Notification_resumes_once_the_window_closes()
    {
        var window = new MaintenanceWindow
        {
            Id = "mw-1",
            Title = "Firmware upgrade",
            StartUtc = Cycle(0),
            EndUtc = Cycle(5),
            Entities = [Host],
        };

        var i = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(1), [window]);
        Assert.False(i.ShouldNotify);

        i = AlertLifecycle.OnObserved(i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(6), [window]);

        Assert.Null(i.SuppressedByWindowId);
        Assert.True(i.ShouldNotify);
    }

    [Fact]
    public void A_window_covering_other_entities_does_not_suppress_this_one()
    {
        var window = new MaintenanceWindow
        {
            Id = "mw-1",
            Title = "Other rack",
            StartUtc = Cycle(0),
            EndUtc = Cycle(100),
            Entities = [new EntityId("esx99")],
        };

        var i = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(1), [window]);

        Assert.Null(i.SuppressedByWindowId);
        Assert.True(i.ShouldNotify);
    }

    [Fact]
    public void A_window_with_no_entities_covers_the_whole_estate()
    {
        var window = new MaintenanceWindow
        {
            Id = "mw-all",
            Title = "Datacentre power test",
            StartUtc = Cycle(0),
            EndUtc = Cycle(100),
        };

        var i = AlertLifecycle.OnObserved(
            null, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(1), [window]);

        Assert.Equal("mw-all", i.SuppressedByWindowId);
        Assert.False(i.ShouldNotify);
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
        Assert.False(AlertLifecycle.EntersLifecycle(AlertSeverity.Info));

        Assert.Throws<ArgumentException>(() =>
            AlertLifecycle.OnObserved(null, Alert(AlertSeverity.Info), HysteresisPolicy.Default, Cycle(0)));
    }

    [Fact]
    public void An_observation_of_a_different_problem_is_rejected()
    {
        var other = Alert() with
        {
            Fingerprint = AlertFingerprint.Create("ilo", "Fan 3 failed", "Hardware", "esx01"),
        };

        Assert.Throws<ArgumentException>(() =>
            AlertLifecycle.OnObserved(Confirmed(), other, HysteresisPolicy.Default, Cycle(2)));
    }

    // --- audit trail ------------------------------------------------------

    [Fact]
    public void Every_transition_is_recorded_with_its_reason_and_actor()
    {
        var i = AlertLifecycle.Acknowledge(Confirmed(), "ertugrul", Cycle(2));
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

public class FlapDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly AlertFingerprint Original =
        AlertFingerprint.Create("ilo", "Power supply 2 failed", "Hardware", "esx01");

    private static FlapHistory Empty() => new() { Fingerprint = Original, ObjectName = "esx01" };

    [Fact]
    public void A_stable_signal_never_flaps()
    {
        var history = Empty().RecordCeased(T0, FlapPolicy.Default);

        Assert.False(history.IsFlapping(T0, FlapPolicy.Default));
        Assert.Null(FlapDetection.Evaluate(history, T0, FlapPolicy.Default));
    }

    [Fact]
    public void Crossing_the_threshold_raises_an_alert_of_its_own()
    {
        // The point of the whole mechanism: an unconfirmed alert that appears
        // and vanishes fifty times leaves no instance behind, so without this
        // the instability would be perfectly invisible.
        var history = Empty();
        for (var i = 0; i < FlapPolicy.Default.Threshold; i++)
        {
            history = history.RecordCeased(T0.AddMinutes(i * 2), FlapPolicy.Default);
        }

        var now = T0.AddMinutes(10);
        Assert.True(history.IsFlapping(now, FlapPolicy.Default));

        var alert = FlapDetection.Evaluate(history, now, FlapPolicy.Default);

        Assert.NotNull(alert);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("Reliability", alert.Category);
    }

    [Fact]
    public void The_flapping_alert_is_a_different_problem_from_the_original()
    {
        // "The PSU failed" is a hardware problem. "This PSU reading will not
        // hold still" is a reliability problem, and the action is not the same,
        // so they must never collapse into one alert.
        var history = Empty();
        for (var i = 0; i < FlapPolicy.Default.Threshold; i++)
        {
            history = history.RecordCeased(T0.AddMinutes(i), FlapPolicy.Default);
        }

        var alert = FlapDetection.Evaluate(history, T0.AddMinutes(10), FlapPolicy.Default);

        Assert.NotNull(alert);
        Assert.NotEqual(Original, alert.Fingerprint);
    }

    [Fact]
    public void Transitions_that_age_out_of_the_window_stop_counting()
    {
        var policy = new FlapPolicy { Threshold = 3, Window = TimeSpan.FromMinutes(10) };

        var history = Empty()
            .RecordCeased(T0, policy)
            .RecordCeased(T0.AddMinutes(1), policy)
            .RecordCeased(T0.AddMinutes(2), policy);

        Assert.True(history.IsFlapping(T0.AddMinutes(3), policy));

        // An hour later those three are ancient history.
        Assert.False(history.IsFlapping(T0.AddHours(1), policy));
    }

    [Fact]
    public void A_flapping_alert_is_raised_once_not_on_every_cycle()
    {
        var history = Empty();
        for (var i = 0; i < FlapPolicy.Default.Threshold; i++)
        {
            history = history.RecordCeased(T0.AddMinutes(i), FlapPolicy.Default);
        }

        var now = T0.AddMinutes(10);
        Assert.NotNull(FlapDetection.Evaluate(history, now, FlapPolicy.Default));

        history = history with { Reported = true };
        Assert.Null(FlapDetection.Evaluate(history, now, FlapPolicy.Default));
    }
}

public class MaintenanceWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static MaintenanceWindow Window(params EntityId[] entities) => new()
    {
        Id = "mw-1",
        Title = "Firmware upgrade",
        StartUtc = T0,
        EndUtc = T0.AddHours(4),
        Entities = entities,
    };

    [Fact]
    public void A_window_is_inclusive_of_its_start_and_exclusive_of_its_end()
    {
        var window = Window();

        Assert.False(window.IsActiveAt(T0.AddSeconds(-1)));
        Assert.True(window.IsActiveAt(T0));
        Assert.True(window.IsActiveAt(T0.AddHours(4).AddSeconds(-1)));
        Assert.False(window.IsActiveAt(T0.AddHours(4)));
    }

    [Fact]
    public void An_alert_with_no_entity_is_only_covered_by_an_estate_wide_window()
    {
        // A collector that could not reach anything has no entity to attribute
        // its failure to. Silencing that during unrelated rack work would be
        // wrong; silencing it during a whole-estate window is reasonable.
        Assert.False(Window(new EntityId("esx01")).Covers(null));
        Assert.True(Window().Covers(null));
    }

    [Fact]
    public void The_responsible_window_is_identified_not_just_flagged()
    {
        var windows = new[] { Window(new EntityId("esx99")), Window(new EntityId("esx01")) with { Id = "mw-2" } };

        var id = MaintenanceSuppression.WindowSuppressing(new EntityId("esx01"), windows, T0.AddHours(1));

        Assert.Equal("mw-2", id);
    }
}
