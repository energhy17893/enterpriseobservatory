namespace EnterpriseObservatory.Domain.Alerts;

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
    public static AlertInstance OnObserved(
        AlertInstance? existing,
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(policy);

        if (!EntersLifecycle(observed.Severity))
        {
            throw new ArgumentException(
                "Info alerts do not enter the lifecycle.", nameof(observed));
        }

        if (existing is null)
        {
            return Raise(observed, policy, nowUtc);
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
            return existing with { LastSeenUtc = nowUtc, NotifyPending = false };
        }

        var hits = existing.ConsecutiveHits + 1;
        var confirmed = existing.IsConfirmed || hits >= policy.RequiredHits(observed.Severity);
        var justConfirmed = confirmed && !existing.IsConfirmed;
        var escalated = observed.Severity > existing.Severity;

        var next = existing with
        {
            Severity = observed.Severity,
            Title = observed.Title,
            Description = observed.Description,
            ConsecutiveHits = hits,
            IsConfirmed = confirmed,
            LastSeenUtc = nowUtc,
            // Notify on first confirmation and on escalation. Not on every
            // cycle, and not on de-escalation — a problem getting less bad is
            // not news worth waking someone for.
            NotifyPending = existing.NotifyPending || justConfirmed || escalated,
        };

        if (justConfirmed)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.Confirmed, nowUtc);
        }

        // The condition came back after resolving on its own.
        if (existing.State == AlertLifecycleState.Resolved && confirmed)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.ConditionReturned, nowUtc) with
            {
                NotifyPending = true,
            };
        }

        // Escalation revokes an acknowledgement: the operator accepted the
        // problem they were shown, not a worse one.
        if (escalated && existing.State == AlertLifecycleState.Acknowledged)
        {
            next = next.With(AlertLifecycleState.Open, AlertTransitionReason.SeverityIncreased, nowUtc);
        }

        next = ExpireSilenceIfDue(next, nowUtc);

        return next;
    }

    /// <summary>
    /// Applies the absence of a problem in the current cycle.
    /// </summary>
    /// <returns>
    /// The next instance, or <c>null</c> when it should be forgotten entirely.
    /// </returns>
    public static AlertInstance? OnAbsent(AlertInstance existing, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        // Never confirmed, now gone: it was flapping. Forget it rather than
        // leaving a resolved alert nobody ever needed to see.
        if (!existing.IsConfirmed)
        {
            return null;
        }

        // Already resolved and now absent: this is where a sticky clear ends.
        // The fault is gone, so the instance retires; if it ever returns it
        // will be born fresh and can notify again.
        if (existing.State == AlertLifecycleState.Resolved)
        {
            return null;
        }

        return existing.With(AlertLifecycleState.Resolved, AlertTransitionReason.ConditionCleared, nowUtc) with
        {
            ConsecutiveHits = 0,
            NotifyPending = false,
        };
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
            NotifyPending = false,
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
            NotifyPending = false,
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
            NotifyPending = false,
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

    private static AlertInstance Raise(
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc)
    {
        var confirmed = policy.RequiredHits(observed.Severity) <= 1;

        var instance = new AlertInstance
        {
            Fingerprint = observed.Fingerprint,
            Severity = observed.Severity,
            State = AlertLifecycleState.Open,
            Title = observed.Title,
            Description = observed.Description,
            Entity = observed.Entity,
            ConsecutiveHits = 1,
            IsConfirmed = confirmed,
            ClearedByOperator = false,
            NotifyPending = confirmed,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
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
