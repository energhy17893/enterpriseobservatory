using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>
/// The state machine of ADR-0026 (design note §2): a fresh absence counts
/// towards resolution, an unknown never does, and there is no edge from
/// "we could not look" to "resolved".
/// </summary>
public class ThreeValuedLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cycle(int n) => T0.AddSeconds(30 * n);

    private static readonly TimeSpan Raw = TimeSpan.FromDays(2);

    private static readonly ResolutionPolicy Three = new() { ConsecutiveAbsent = 3 };

    private static AlertDefinition Alert(AlertSeverity severity = AlertSeverity.Warning) => new()
    {
        Fingerprint = AlertFingerprint.Create("platform", "Dropped packets", "Network", "esx01/rx"),
        Severity = severity,
        Title = "Dropped packets",
        Entity = new EntityId("esx01"),
    };

    private static AlertInstance Confirmed()
    {
        var first = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        return AlertLifecycle.MarkNotified(
            AlertLifecycle.OnObserved(first, Alert(), HysteresisPolicy.Default, Cycle(1)));
    }

    private static AlertInstance? Absent(AlertInstance i, DateTimeOffset at, ResolutionPolicy? n = null) =>
        AlertLifecycle.OnAbsent(i, new AlertAbsence { EvidenceAtUtc = at }, n ?? Three, at).Instance;

    private static AlertInstance? Unknown(AlertInstance i, DateTimeOffset at) =>
        AlertLifecycle.OnUnknown(
            i,
            new AlertUnknown { Reason = UnknownReason.SourceSilent, Detail = "vc-1 did not answer" },
            Raw,
            at).Instance;

    // --- resolution needs N fresh absences --------------------------------

    [Fact]
    public void A_confirmed_alert_resolves_only_after_N_consecutive_fresh_absences()
    {
        var i = Absent(Confirmed(), Cycle(2))!;
        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.Equal(1, i.ConsecutiveAbsent);

        i = Absent(i, Cycle(3))!;
        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.Equal(2, i.ConsecutiveAbsent);

        var result = AlertLifecycle.OnAbsent(i, new AlertAbsence { EvidenceAtUtc = Cycle(4) }, Three, Cycle(4));

        Assert.Equal(AlertLifecycleState.Resolved, result.Instance!.State);
        Assert.Equal(AlertTransitionReason.ConditionCleared, result.Instance.History[^1].Reason);

        // A cessation is counted at resolution, not at the first absence.
        Assert.True(result.CeasedFiring);
    }

    [Fact]
    public void An_absence_short_of_N_is_not_a_cessation()
    {
        var result = AlertLifecycle.OnAbsent(
            Confirmed(), new AlertAbsence { EvidenceAtUtc = Cycle(2) }, Three, Cycle(2));

        Assert.False(result.CeasedFiring);
    }

    [Fact]
    public void An_unknown_between_absences_resets_the_count()
    {
        var i = Absent(Confirmed(), Cycle(2))!;
        i = Absent(i, Cycle(3))!;
        i = Unknown(i, Cycle(4))!;
        Assert.Equal(0, i.ConsecutiveAbsent);

        i = Absent(i, Cycle(5))!;
        i = Absent(i, Cycle(6))!;
        Assert.Equal(AlertLifecycleState.Open, i.State);

        i = Absent(i, Cycle(7))!;
        Assert.Equal(AlertLifecycleState.Resolved, i.State);
    }

    [Fact]
    public void A_present_between_absences_resets_the_count()
    {
        var i = Absent(Confirmed(), Cycle(2))!;
        i = Absent(i, Cycle(3))!;
        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(4));

        Assert.Equal(0, i.ConsecutiveAbsent);
    }

    [Fact]
    public void The_resolution_reason_says_why_the_condition_is_gone()
    {
        var result = AlertLifecycle.OnAbsent(
            Confirmed(),
            new AlertAbsence { EvidenceAtUtc = Cycle(2), Because = AbsenceKind.SubjectRemoved },
            ResolutionPolicy.Immediate,
            Cycle(2));

        Assert.Equal(AlertTransitionReason.SubjectRemoved, result.Instance!.History[^1].Reason);
    }

    [Fact]
    public void A_resolution_policy_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolutionPolicy { ConsecutiveAbsent = 0 });
    }

    // --- unknown keeps an alarm open, and marks it --------------------------

    [Fact]
    public void Unknown_keeps_an_open_alarm_open_marks_it_stale_and_does_not_notify()
    {
        var before = Confirmed();

        var i = Unknown(before, Cycle(2))!;

        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.True(i.IsVisible);
        Assert.True(i.IsStale);
        Assert.Equal(Cycle(2), i.StaleSinceUtc);
        Assert.Equal(UnknownReason.SourceSilent, i.StaleReason);
        Assert.Equal("vc-1 did not answer", i.StaleDetail);
        Assert.Equal(before.EvidenceAtUtc, i.EvidenceAtUtc);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);

        // Not a transition, so not history: the stale mark and its reason
        // are on the instance. A quiet cycle writes no row (post-#83: the
        // history had filled with same-state rows).
        Assert.Equal(before.History, i.History);
    }

    [Fact]
    public void Observing_an_open_or_pending_alert_again_adds_no_history_row()
    {
        var pending = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));
        var open = AlertLifecycle.OnObserved(pending, Alert(), HysteresisPolicy.Default, Cycle(1));
        var again = AlertLifecycle.OnObserved(open, Alert(), HysteresisPolicy.Default, Cycle(2));

        Assert.Equal(AlertTransitionReason.Raised, Assert.Single(pending.History).Reason);
        Assert.Equal(pending.History, open.History);
        Assert.Equal(pending.History, again.History);
    }

    [Fact]
    public void No_unknown_input_ever_leads_to_resolved_or_retired()
    {
        // Every state an instance can be in, fed an unknown for a week.
        var open = Confirmed();
        var acknowledged = AlertLifecycle.Acknowledge(open, "op", Cycle(2));
        var silenced = AlertLifecycle.Silence(open, "op", Cycle(1000), Cycle(2));
        var stale = Unknown(open, Cycle(2))!;
        var resolving = Absent(open, Cycle(2))!;

        foreach (var start in new[] { open, acknowledged, silenced, stale, resolving })
        {
            var i = start;

            for (var hour = 1; hour <= 24 * 7; hour++)
            {
                i = Unknown(i, Cycle(2).AddHours(hour));

                Assert.NotNull(i);
                Assert.NotEqual(AlertLifecycleState.Resolved, i.State);
            }

            Assert.DoesNotContain(i!.History, t => t.To == AlertLifecycleState.Resolved);
        }
    }

    [Fact]
    public void Unknown_leaves_a_resolved_alert_as_it_is()
    {
        var resolved = Absent(Confirmed(), Cycle(2), ResolutionPolicy.Immediate)!;

        Assert.Same(resolved, Unknown(resolved, Cycle(3)));
    }

    [Fact]
    public void After_raw_retention_of_unknown_the_alarm_moves_to_the_unknown_state_out_of_the_count()
    {
        var i = Unknown(Confirmed(), Cycle(2))!;

        i = Unknown(i, Cycle(1) + Raw - TimeSpan.FromSeconds(1))!;
        Assert.Equal(AlertLifecycleState.Open, i.State);

        i = Unknown(i, Cycle(1) + Raw)!;

        Assert.Equal(AlertLifecycleState.Unknown, i.State);
        Assert.False(i.IsVisible);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);
        Assert.Equal(AlertTransitionReason.EvidenceExpired, i.History[^1].Reason);
    }

    [Fact]
    public void Evidence_returning_to_an_unknown_alarm_restores_its_sub_state_without_notifying()
    {
        var acknowledged = AlertLifecycle.Acknowledge(Confirmed(), "op", Cycle(2));
        var i = Unknown(acknowledged, Cycle(3))!;
        i = Unknown(i, Cycle(3) + Raw)!;
        Assert.Equal(AlertLifecycleState.Unknown, i.State);

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3) + Raw + TimeSpan.FromMinutes(1));

        Assert.Equal(AlertLifecycleState.Acknowledged, i.State);
        Assert.False(i.IsStale);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);
        Assert.Equal(AlertTransitionReason.EvidenceReturned, i.History[^1].Reason);
    }

    [Fact]
    public void An_escalation_while_evidence_returns_is_still_notified()
    {
        var i = Unknown(Confirmed(), Cycle(2))!;
        i = Unknown(i, Cycle(2) + Raw)!;

        i = AlertLifecycle.OnObserved(
            i, Alert(AlertSeverity.Critical), HysteresisPolicy.Default, Cycle(2) + Raw + TimeSpan.FromMinutes(1));

        Assert.Equal(AlertNotificationKind.Escalated, i.PendingNotification);
    }

    [Fact]
    public void A_fresh_present_on_a_stale_alarm_makes_it_fresh_without_notifying()
    {
        var i = Unknown(Confirmed(), Cycle(2))!;

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(3));

        Assert.False(i.IsStale);
        Assert.Null(i.StaleReason);
        Assert.Equal(Cycle(3), i.EvidenceAtUtc);
        Assert.Equal(AlertNotificationKind.None, i.PendingNotification);

        // Stale to fresh is not a transition either: no row.
        Assert.DoesNotContain(i.History, t => t.Reason == AlertTransitionReason.EvidenceReturned);
    }

    [Fact]
    public void A_fresh_absent_on_an_unknown_alarm_starts_the_count_at_one()
    {
        var i = Unknown(Confirmed(), Cycle(2))!;
        i = Unknown(i, Cycle(2) + Raw)!;

        i = Absent(i, Cycle(2) + Raw + TimeSpan.FromMinutes(1))!;

        Assert.Equal(AlertLifecycleState.Open, i.State);
        Assert.False(i.IsStale);
        Assert.Equal(1, i.ConsecutiveAbsent);
    }

    [Fact]
    public void The_expiry_check_applies_the_fresh_verdict_first()
    {
        // A product outage longer than raw retention: the first cycle back with
        // fresh data applies that data rather than flipping everything to Unknown.
        var i = Confirmed();

        i = AlertLifecycle.OnObserved(i, Alert(), HysteresisPolicy.Default, Cycle(1) + Raw + Raw);

        Assert.Equal(AlertLifecycleState.Open, i.State);
    }

    // --- pending and cleared -------------------------------------------------

    [Fact]
    public void Unknown_keeps_a_pending_alarm_without_counting_it_and_forgets_it_after_raw_retention()
    {
        var pending = AlertLifecycle.OnObserved(null, Alert(), HysteresisPolicy.Default, Cycle(0));

        var kept = Unknown(pending, Cycle(1))!;
        Assert.Equal(pending.ConsecutiveHits, kept.ConsecutiveHits);
        Assert.False(kept.IsConfirmed);

        Assert.Null(Unknown(kept, Cycle(0) + Raw));
    }

    [Fact]
    public void An_operator_clear_retires_after_N_fresh_absences()
    {
        var cleared = AlertLifecycle.Clear(Confirmed(), "op", Cycle(2));

        var i = Absent(cleared, Cycle(3))!;
        i = Absent(i, Cycle(4))!;
        Assert.True(i.ClearedByOperator);

        Assert.Null(Absent(i, Cycle(5)));
    }
}
