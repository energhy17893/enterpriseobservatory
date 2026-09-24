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
    /// <param name="evidenceAtUtc">
    /// Time of the newest input the observation rests on; the cycle time when
    /// not given, which is what a direct producer's observation is.
    /// </param>
    public static AlertInstance OnObserved(
        AlertInstance? existing,
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc,
        IReadOnlyList<MaintenanceWindow>? maintenanceWindows = null,
        DateTimeOffset? evidenceAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(policy);

        if (!EntersLifecycle(observed.Severity))
        {
            throw new ArgumentException(
                "Info alerts do not enter the lifecycle.", nameof(observed));
        }

        var evidence = evidenceAtUtc ?? nowUtc;

        var suppressedBy = MaintenanceSuppression.WindowSuppressing(
            observed.Entity, maintenanceWindows ?? [], nowUtc);

        if (existing is null)
        {
            return Raise(observed, policy, nowUtc, suppressedBy, evidence);
        }

        if (existing.Fingerprint != observed.Fingerprint)
        {
            throw new ArgumentException(
                "Observation does not belong to this instance.", nameof(observed));
        }

        // A clear resolves the episode, and wins its own cycle: evidence no
        // newer than the clear is what the operator cleared. Newer evidence
        // is the condition reported again, a new episode (vROps "Cancel
        // alert", Zabbix manual close). Silence is for "stop it for a while".
        if (existing is { State: AlertLifecycleState.Resolved, ClearedByOperator: true })
        {
            var clear = existing.History.LastOrDefault(t => t.Reason == AlertTransitionReason.OperatorCleared);
            if (clear is null || evidence > clear.AtUtc)
            {
                return Reopen(observed, nowUtc, suppressedBy, evidence, clear);
            }

            return existing with
            {
                LastSeenUtc = nowUtc,
                EvidenceAtUtc = evidence,
                ConsecutiveAbsent = 0,
                PendingNotification = AlertNotificationKind.None,
                SuppressedByWindowId = suppressedBy,
            };
        }

        // Fresh evidence for a stale or unknown alarm: back to what it was,
        // without a notification (ADR-0026 point 5). Everything below then
        // runs on the restored instance, so an escalation is still one.
        existing = Refresh(existing, evidence, nowUtc) with { ConsecutiveAbsent = 0 };

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

            // The source belongs to the evidence; the fingerprint is the identity.
            Source = observed.Source,
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
    /// Applies a fresh statement that the problem is not there (ADR-0026).
    /// </summary>
    /// <remarks>
    /// The only way to resolve an alert, and it takes the evidence and N: a
    /// confirmed alert resolves on the Nth consecutive fresh absence, and an
    /// unknown in between (see <see cref="OnUnknown"/>) resets the count. A
    /// cessation is reported at resolution, not at the first absence.
    /// </remarks>
    public static AbsenceResult OnAbsent(
        AlertInstance existing,
        AlertAbsence absence,
        ResolutionPolicy resolution,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(absence);
        ArgumentNullException.ThrowIfNull(resolution);

        // Never confirmed, now gone: it was flapping. Forget the instance
        // rather than leaving a resolved alert nobody needed to see — but
        // report that it stopped firing, because suppressing the noise and
        // losing the fact that something is unstable are different things.
        if (!existing.IsConfirmed)
        {
            return new AbsenceResult(null, CeasedFiring: true);
        }

        if (existing.State == AlertLifecycleState.Resolved)
        {
            // A clear held against a still-reported condition ends once the
            // fault has been gone N times over.
            // It stopped firing on an earlier cycle, so this is not a new
            // cessation; if it ever returns it is born fresh and can notify.
            if (existing.ClearedByOperator && existing.ConsecutiveAbsent + 1 < resolution.ConsecutiveAbsent)
            {
                return new AbsenceResult(
                    existing with
                    {
                        ConsecutiveAbsent = existing.ConsecutiveAbsent + 1,
                        EvidenceAtUtc = absence.EvidenceAtUtc,
                    },
                    CeasedFiring: false);
            }

            return new AbsenceResult(null, CeasedFiring: false);
        }

        // Evidence that follows a blind stretch starts the count again: the
        // unknown reset it, whatever it was before.
        var count = (existing.IsStale || existing.State == AlertLifecycleState.Unknown
            ? 0
            : existing.ConsecutiveAbsent) + 1;

        var next = Refresh(existing, absence.EvidenceAtUtc, nowUtc) with { ConsecutiveAbsent = count };

        if (count < resolution.ConsecutiveAbsent)
        {
            return new AbsenceResult(next, CeasedFiring: false);
        }

        var resolved = next
            .With(AlertLifecycleState.Resolved, ResolvedBecause(absence.Because), nowUtc) with
        {
            ConsecutiveHits = 0,
            ConsecutiveAbsent = 0,
            PendingNotification = AlertNotificationKind.None,
        };

        return new AbsenceResult(resolved, CeasedFiring: true);
    }

    /// <summary>
    /// Applies "nothing could be said about this problem this cycle".
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no edge from here to resolved (ADR-0026). An open alarm stays
    /// open and is marked stale, with the reason and when it happened; the
    /// absence count starts again. After <paramref name="rawRetention"/> without
    /// fresh evidence it moves to <see cref="AlertLifecycleState.Unknown"/>,
    /// out of the open count — the samples it rested on are gone from the store
    /// by then. Neither step notifies: the source going quiet is already its
    /// own alert.
    /// </para>
    /// <para>
    /// The one instance this can forget is an unconfirmed one past raw
    /// retention: it was never shown to anyone.
    /// </para>
    /// </remarks>
    /// <param name="rawRetention">Read from the retention policy, never copied (ADR-0017).</param>
    public static AbsenceResult OnUnknown(
        AlertInstance existing,
        AlertUnknown unknown,
        TimeSpan rawRetention,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(unknown);

        if (existing.State == AlertLifecycleState.Resolved)
        {
            return new AbsenceResult(existing, CeasedFiring: false);
        }

        var expired = nowUtc - existing.EvidenceAtUtc >= rawRetention;

        if (!existing.IsConfirmed)
        {
            return new AbsenceResult(expired ? null : existing, CeasedFiring: false);
        }

        var next = existing with
        {
            StaleReason = unknown.Reason,
            StaleDetail = unknown.Detail,
            ConsecutiveAbsent = 0,
        };

        if (existing.State == AlertLifecycleState.Unknown)
        {
            return new AbsenceResult(next, CeasedFiring: false);
        }

        if (!existing.IsStale)
        {
            next = (next with { StaleSinceUtc = nowUtc })
                .RecordEvent(AlertTransitionReason.EvidenceLost, nowUtc, detail: Describe(unknown));
        }

        if (expired)
        {
            next = next.With(
                AlertLifecycleState.Unknown,
                AlertTransitionReason.EvidenceExpired,
                nowUtc,
                detail: Describe(unknown));
        }

        return new AbsenceResult(next, CeasedFiring: false);
    }

    private static string Describe(AlertUnknown unknown) => $"{unknown.Reason}: {unknown.Detail}";

    private static AlertTransitionReason ResolvedBecause(AbsenceKind because) => because switch
    {
        AbsenceKind.SubjectRemoved => AlertTransitionReason.SubjectRemoved,
        AbsenceKind.Superseded => AlertTransitionReason.Superseded,
        AbsenceKind.Expired => AlertTransitionReason.Expired,
        AbsenceKind.RuleRetired => AlertTransitionReason.RuleRetired,
        _ => AlertTransitionReason.ConditionCleared,
    };

    /// <summary>
    /// Takes a stale or unknown instance back to fresh on new evidence; a fresh
    /// one just gets the new evidence time.
    /// </summary>
    private static AlertInstance Refresh(AlertInstance existing, DateTimeOffset evidenceAtUtc, DateTimeOffset nowUtc)
    {
        var fresh = existing with
        {
            EvidenceAtUtc = evidenceAtUtc,
            StaleSinceUtc = null,
            StaleReason = null,
            StaleDetail = null,
        };

        if (existing.State == AlertLifecycleState.Unknown)
        {
            return fresh.With(StateBeforeUnknown(existing), AlertTransitionReason.EvidenceReturned, nowUtc);
        }

        return existing.IsStale
            ? fresh.RecordEvent(AlertTransitionReason.EvidenceReturned, nowUtc)
            : fresh;
    }

    /// <summary>The operator's sub-state an alert held before it went unknown.</summary>
    private static AlertLifecycleState StateBeforeUnknown(AlertInstance instance) =>
        instance.History.LastOrDefault(t => t.To == AlertLifecycleState.Unknown)?.From is { } from &&
        from != AlertLifecycleState.Unknown
            ? from
            : AlertLifecycleState.Open;

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

    /// <summary>The actor recorded on a transition the product made itself.</summary>
    public const string SystemActor = "system";

    /// <summary>
    /// Resolves an alarm whose condition is now judged as a compliance finding
    /// (ADR-0024): "moved to compliance finding".
    /// </summary>
    /// <remarks>
    /// A silence is dropped, not carried anywhere: it has no reason and no
    /// end the finding could use, and turning it into an acceptance would be
    /// the indefinite exception the finding lifecycle forbids. An alarm
    /// already resolved is returned as it is, so moving twice changes nothing.
    /// </remarks>
    public static AlertInstance MoveToFinding(AlertInstance existing, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (existing.State == AlertLifecycleState.Resolved)
        {
            return existing;
        }

        return existing.With(
            AlertLifecycleState.Resolved, AlertTransitionReason.MovedToFinding, nowUtc, SystemActor) with
        {
            SilencedUntilUtc = null,
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

    private static readonly HysteresisPolicy ConfirmAtOnce = new() { WarningConsecutiveHits = 1, CriticalConsecutiveHits = 1 };

    /// <summary>
    /// A new episode for a cleared condition reported again. Confirmed at once:
    /// the condition was confirmed before the clear and never went away.
    /// </summary>
    private static AlertInstance Reopen(
        AlertDefinition observed,
        DateTimeOffset nowUtc,
        string? suppressedBy,
        DateTimeOffset evidenceAtUtc,
        AlertTransition? clear)
    {
        var raised = Raise(observed, ConfirmAtOnce, nowUtc, suppressedBy, evidenceAtUtc);
        var detail = clear is null
            ? "cleared, condition reported again"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"cleared by {clear.Actor} at {clear.AtUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}, condition reported again");

        return raised with
        {
            History = [raised.History[0] with { Reason = AlertTransitionReason.Raised, Detail = detail }],
        };
    }

    private static AlertInstance Raise(
        AlertDefinition observed,
        HysteresisPolicy policy,
        DateTimeOffset nowUtc,
        string? suppressedBy,
        DateTimeOffset evidenceAtUtc)
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
            EvidenceAtUtc = evidenceAtUtc,
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
                EvidenceAtUtc = evidenceAtUtc,
            }],
        };
    }
}
