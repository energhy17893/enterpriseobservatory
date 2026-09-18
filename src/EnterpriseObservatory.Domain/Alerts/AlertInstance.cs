namespace EnterpriseObservatory.Domain.Alerts;

public enum AlertLifecycleState
{
    /// <summary>Firing and not yet handled.</summary>
    Open = 0,

    /// <summary>An operator has taken ownership. Notifications stop.</summary>
    Acknowledged = 1,

    /// <summary>Muted until a deadline, then returns to <see cref="Open"/>.</summary>
    Silenced = 2,

    /// <summary>No longer firing, or cleared by an operator.</summary>
    Resolved = 3,
}

/// <summary>Why an instance moved between states. Kept for auditing.</summary>
public enum AlertTransitionReason
{
    Raised,
    Confirmed,
    SeverityIncreased,
    SeverityDecreased,
    ConditionCleared,
    ConditionReturned,
    OperatorAcknowledged,
    OperatorCleared,
    OperatorSilenced,
    SilenceExpired,
}

/// <summary>One recorded change in an alert's life.</summary>
public sealed record AlertTransition
{
    public required AlertLifecycleState From { get; init; }

    public required AlertLifecycleState To { get; init; }

    public required AlertTransitionReason Reason { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Which operator acted, for operator-initiated transitions.</summary>
    public string? Actor { get; init; }
}

/// <summary>
/// The durable state of one problem over its whole life.
/// </summary>
/// <remarks>
/// <para>
/// The per-cycle alert list is a <em>view</em>; this is the truth. In the
/// previous product some screens read the snapshot list while others read
/// lifecycle instances, so the same alert could appear acknowledged on one page
/// and open on another. See ADR-0007.
/// </para>
/// <para>
/// Instances are immutable; every transition produces a new one and appends to
/// <see cref="History"/>, so "why did this alert fire and who closed it" is
/// always answerable.
/// </para>
/// </remarks>
public sealed record AlertInstance
{
    public required AlertFingerprint Fingerprint { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required AlertLifecycleState State { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public EntityId? Entity { get; init; }

    /// <summary>Consecutive observations so far, used for hysteresis.</summary>
    public required int ConsecutiveHits { get; init; }

    /// <summary>
    /// Whether hysteresis has been satisfied. Unconfirmed instances are
    /// invisible and never notify.
    /// </summary>
    public required bool IsConfirmed { get; init; }

    /// <summary>
    /// Set when an operator resolved this deliberately rather than the
    /// condition going away.
    /// </summary>
    /// <remarks>
    /// This is what makes a clear "stick": re-observing the same condition must
    /// not reopen or re-notify. A permanent hardware fault awaiting a part
    /// would otherwise re-alert on every polling cycle and destroy the
    /// operator's trust in notifications.
    /// </remarks>
    public required bool ClearedByOperator { get; init; }

    /// <summary>Whether a notification is owed for this instance.</summary>
    public required bool NotifyPending { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    public DateTimeOffset? SilencedUntilUtc { get; init; }

    public IReadOnlyList<AlertTransition> History { get; init; } = [];

    /// <summary>
    /// Whether an operator should see this in the alert inbox.
    /// </summary>
    /// <remarks>
    /// Resolved alerts and unconfirmed ones are both hidden — the first because
    /// it is over, the second because we are not yet sure it is real.
    /// </remarks>
    public bool IsVisible =>
        IsConfirmed && State is AlertLifecycleState.Open
            or AlertLifecycleState.Acknowledged
            or AlertLifecycleState.Silenced;

    internal AlertInstance With(
        AlertLifecycleState state,
        AlertTransitionReason reason,
        DateTimeOffset atUtc,
        string? actor = null)
    {
        if (state == State)
        {
            return this;
        }

        return this with
        {
            State = state,
            History = [.. History, new AlertTransition
            {
                From = State,
                To = state,
                Reason = reason,
                AtUtc = atUtc,
                Actor = actor,
            }],
        };
    }
}
