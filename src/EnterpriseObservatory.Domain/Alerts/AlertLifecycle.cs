namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>The outcome of a problem not being observed in a cycle.</summary>
/// <param name="Instance">The next instance, or null when it should be forgotten.</param>
/// <param name="CeasedFiring">
/// Whether the problem stopped firing on this cycle. Feeds flap detection, which
/// is why it is reported even when the instance is discarded: suppressing the
/// noise of an unstable signal and losing the fact that it is unstable are
/// different things.
/// </param>
public readonly record struct AbsenceResult(AlertInstance? Instance, bool CeasedFiring);

/// <summary>
/// The alert state machine. Pure functions: no clock, no storage, no I/O.
/// </summary>
/// <remarks>
/// <para>
/// This is the most behaviourally subtle part of the product, so it lives in
/// the domain where it can be exhaustively tested without infrastructure.
/// </para>
/// <para>
/// Every method takes the current instance and returns the next one. Callers
/// persist the result; nothing here mutates.
/// </para>
/// </remarks>
public static class AlertLifecycle
{
    /// <summary>
    /// Whether a severity participates in the lifecycle at all.
    /// </summary>
    /// <remarks>
    /// Info is a statement about the world, not a call to action. Letting it
    /// occupy the inbox is how alert fatigue starts.
    /// </remarks>
    public static bool EntersLifecycle(AlertSeverity severity) => severity != AlertSeverity.Info;

    /// <summary>
    /// Applies an observation of a problem in the current collection cycle.
    /// </summary>
    /// <param name="existing">The stored instance, or null if this is new.</param>
    /// <param name="observed">What was seen this cycle.</param>
    /// <param name="policy">Hysteresis thresholds.</param>
    /// <param name="nowUtc">Cycle timestamp.</param>
    /// <param name="maintenanceWindows">
    /// Windows that may suppress notification. Suppression never hides the
    /// alert — see <see cref="MaintenanceWindow"/>.
    /// </param>
    public static AlertInstance OnObserved(
        AlertInstance? existing,
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc,
        IReadOnlyList<MaintenanceWindow>? maintenanceWindows = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(policy);

        if (!EntersLifecycle(observed.Severity))
        {
            throw new ArgumentException(
                "Info alerts do not enter the lifecycle.", nameof(observed));
        }

        var suppressedBy = MaintenanceSuppression.WindowSuppressing(
            observed.Entity, maintenanceWindows ?? [], nowUtc);

        if (existing is null)
        {
            return Raise(observed, policy, nowUtc, suppressedBy);
        }

        if (existing.Fingerprint != observed.Fingerprint)
        {
            throw new ArgumentException(
                "Observation does not belong to this instance.", nameof(observed));
        }

        // An operator's clear outlives the condition. Re-observing must not
        // reopen or re-notify; only the fault disappearing ends the clear.
        if (existing is { State: AlertLifecycleState.Resolved, ClearedByOperator: true })
        {
            return existing with
            {
                LastSeenUtc = nowUtc,
                PendingNotification = AlertNotificationKind.None,
                SuppressedByWindowId = suppressedBy,
            };
        }

        var hits = existing.ConsecutiveHits + 1;
        var confirmed = existing.IsConfirmed ||
            policy.IsSatisfied(observed.Severity, hits, nowUtc - existing.FirstSeenUtc);
        var justConfirmed = confirmed && !existing.IsConfirmed;
        var escalated = observed.Severity > existing.Severity;
        var improved = observed.Severity < existing.Severity;
        var returned = existing.State == AlertLifecycleState.Resolved && confirmed;

        var kind = Highest(
            existing.PendingNotification,
            escalated ? AlertNotificationKind.Escalated
                : returned ? AlertNotificationKind.Returned
                : justConfirmed ? AlertNotificationKind.Raised
                : improved ? AlertNotificationKind.Improved
                : AlertNotificationKind.None);

        var next = existing with
        {
            Severity = observed.Severity,
            Title = observed.Title,
            Description = observed.Description,
            ConsecutiveHits = hits,
            IsConfirmed = confirmed,
            LastSeenUtc = nowUtc,
            PendingNotification = confirmed ? kind : AlertNotificationKind.None,
            SuppressedByWindowId = suppressedBy,
        };

        if (justConfirmed)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.Confirmed, nowUtc);
        }

        if (returned)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.ConditionReturned, nowUtc);
        }

        // Escalation revokes an acknowledgement: the operator accepted the
        // problem they were shown, not a worse one.
        if (escalated && existing.State == AlertLifecycleState.Acknowledged)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.SeverityIncreased, nowUtc);
        }
        else if (improved)
        {
            next = next.RecordEvent(AlertTransitionReason.SeverityDecreased, nowUtc);
        }

        return ExpireSilenceIfDue(next, nowUtc);
    }

    /// <summary>
    /// Applies the absence of a problem in the current cycle.
    /// </summary>
    public static AbsenceResult OnAbsent(AlertInstance existing, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        // Never confirmed, now gone: it was flapping. Forget the instance
        // rather than leaving a resolved alert nobody needed to see — but
        // report that it stopped firing, because suppressing the noise and
        // losing the fact that something is unstable are different things.
        if (!existing.IsConfirmed)
        {
            return new AbsenceResult(null, CeasedFiring: true);
        }

        // Already resolved and now absent: this is where a sticky clear ends.
        // The fault is gone, so the instance retires; if it ever returns it
        // will be born fresh and can notify again. It stopped firing on an
        // earlier cycle, so this is not a new cessation.
        if (existing.State == AlertLifecycleState.Resolved)
        {
            return new AbsenceResult(null, CeasedFiring: false);
        }

        var resolved = existing
            .With(AlertLifecycleState.Resolved, AlertTransitionReason.ConditionCleared, nowUtc) with
        {
            ConsecutiveHits = 0,
            PendingNotification = AlertNotificationKind.None,
        };

        return new AbsenceResult(resolved, CeasedFiring: true);
    }

    /// <summary>An operator takes ownership. Notifications stop; the alert stays visible.</summary>
    public static AlertInstance Acknowledge(AlertInstance existing, string actor, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (existing.State == AlertLifecycleState.Resolved)
        {
            return existing;
        }

        return existing.With(
            AlertLifecycleState.Acknowledged, AlertTransitionReason.OperatorAcknowledged, nowUtc, actor) with
        {
            PendingNotification = AlertNotificationKind.None,
        };
    }

    /// <summary>
    /// An operator declares the problem handled, whether or not it is still firing.
    /// </summary>
    public static AlertInstance Clear(AlertInstance existing, string actor, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        return existing.With(
            AlertLifecycleState.Resolved, AlertTransitionReason.OperatorCleared, nowUtc, actor) with
        {
            ClearedByOperator = true,
            PendingNotification = AlertNotificationKind.None,
        };
    }

    /// <summary>Mutes an alert until a deadline.</summary>
    public static AlertInstance Silence(
        AlertInstance existing,
        string actor,
        DateTimeOffset untilUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (untilUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(untilUtc), untilUtc, "A silence must end in the future.");
        }

        if (existing.State == AlertLifecycleState.Resolved)
        {
            return existing;
        }

        return existing.With(
            AlertLifecycleState.Silenced, AlertTransitionReason.OperatorSilenced, nowUtc, actor) with
        {
            SilencedUntilUtc = untilUtc,
            PendingNotification = AlertNotificationKind.None,
        };
    }

    /// <summary>Returns a silenced alert to the inbox once its deadline passes.</summary>
    public static AlertInstance ExpireSilenceIfDue(AlertInstance existing, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (existing.State != AlertLifecycleState.Silenced ||
            existing.SilencedUntilUtc is not { } until ||
            nowUtc < until)
        {
            return existing;
        }

        return existing.With(AlertLifecycleState.Open, AlertTransitionReason.SilenceExpired, nowUtc) with
        {
            SilencedUntilUtc = null,
        };
    }

    /// <summary>Records that a pending notification has been dispatched.</summary>
    /// <remarks>
    /// Exists so the application never has to reach into instance state to
    /// clear a flag, which would put the same rule in two places.
    /// </remarks>
    public static AlertInstance MarkNotified(AlertInstance existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        return existing with { PendingNotification = AlertNotificationKind.None };
    }

    private static AlertNotificationKind Highest(AlertNotificationKind a, AlertNotificationKind b) =>
        a > b ? a : b;

    private static AlertInstance Raise(
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc,
        string? suppressedBy)
    {
        // A brand new instance has been firing for no time at all, so a non-zero
        // minimum duration always defers confirmation to a later cycle.
        var confirmed = policy.IsSatisfied(observed.Severity, 1, TimeSpan.Zero);

        var instance = new AlertInstance
        {
            Fingerprint = observed.Fingerprint,
            Severity = observed.Severity,
            State = AlertLifecycleState.Open,
            Title = observed.Title,
            Description = observed.Description,
            Entity = observed.Entity,
            IsDerived = observed.IsDerived,
            Scope = observed.Scope,
            Category = observed.Category,
            Source = observed.Source,
            ConsecutiveHits = 1,
            IsConfirmed = confirmed,
            ClearedByOperator = false,
            PendingNotification = confirmed ? AlertNotificationKind.Raised : AlertNotificationKind.None,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            SuppressedByWindowId = suppressedBy,
        };

        return instance with
        {
            History = [new AlertTransition
            {
                From = AlertLifecycleState.Open,
                To = AlertLifecycleState.Open,
                Reason = confirmed ? AlertTransitionReason.Confirmed : AlertTransitionReason.Raised,
                AtUtc = nowUtc,
            }],
        };
    }
}
