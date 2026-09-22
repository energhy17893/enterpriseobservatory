namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>
/// What happened to an alert, so the application can decide how loudly to say it.
/// </summary>
/// <remarks>
/// Ordered by urgency; when several apply in one cycle the highest wins.
/// </remarks>
public enum AlertNotificationKind
{
    /// <summary>Nothing to say.</summary>
    None = 0,

    /// <summary>The problem improved but is still present. Worth recording, not worth paging.</summary>
    Improved = 1,

    /// <summary>Confirmed for the first time.</summary>
    Raised = 2,

    /// <summary>Resolved on its own and has come back.</summary>
    Returned = 3,

    /// <summary>Got worse.</summary>
    Escalated = 4,
}

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

    /// <summary>
    /// No fresh evidence for as long as raw retention (ADR-0026): neither open
    /// nor resolved. Out of the inbox and the open count, and never notified.
    /// The first fresh verdict decides where it goes.
    /// </summary>
    Unknown = 4,
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

    /// <summary>
    /// The rule that raised it now judges the same condition as a compliance
    /// finding (ADR-0024); the alarm was resolved as "moved to compliance
    /// finding" and its silence, if any, was not carried over as an acceptance.
    /// </summary>
    MovedToFinding,

    /// <summary>The rule could not judge an open alert this cycle; it stays open, marked stale.</summary>
    EvidenceLost,

    /// <summary>A fresh verdict arrived for a stale or unknown alert.</summary>
    EvidenceReturned,

    /// <summary>Stale for as long as raw retention: moved to <see cref="AlertLifecycleState.Unknown"/>.</summary>
    EvidenceExpired,

    /// <summary>Resolved because the subject is gone from a freshly read table.</summary>
    SubjectRemoved,

    /// <summary>Resolved because another alert now covers the same condition.</summary>
    Superseded,

    /// <summary>Resolved because an occurrence aged out; the type declares it two-valued.</summary>
    Expired,
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

    /// <summary>What was missing, for <see cref="AlertTransitionReason.EvidenceLost"/> and its kin.</summary>
    public string? Detail { get; init; }

    /// <summary>The instance's evidence time when this happened.</summary>
    public DateTimeOffset? EvidenceAtUtc { get; init; }
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

    /// <summary>Whether the platform inferred this rather than observing it.</summary>
    public bool IsDerived { get; init; }

    /// <summary>
    /// Which evaluation owns this alert. See <see cref="AlertDefinition.Scope"/>.
    /// </summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>
    /// What kind of problem this is, e.g. Hardware or Configuration.
    /// </summary>
    /// <remarks>
    /// Carried on the instance rather than looked up from the definition,
    /// because the definition is a per-cycle observation and is gone by the
    /// time anyone reads the inbox. The fingerprint encodes it but is opaque on
    /// purpose, so it cannot be taken back out.
    /// </remarks>
    public string Category { get; init; } = string.Empty;

    /// <summary>Which collector or subsystem reported this.</summary>
    public string Source { get; init; } = string.Empty;

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

    /// <summary>What kind of notification, if any, is owed for this instance.</summary>
    /// <remarks>
    /// The kind rather than a bare flag, because not every notification
    /// deserves the same channel. "It got worse" should reach whoever is on
    /// call; "it got better" should reach the record, not a pager at 3am.
    /// Routing is the application's decision — the domain only says what
    /// happened.
    /// </remarks>
    public required AlertNotificationKind PendingNotification { get; init; }

    /// <summary>
    /// The maintenance window currently suppressing notification, if any.
    /// </summary>
    /// <remarks>
    /// The alert is still raised and still visible; only notification is
    /// withheld. Recording which window is responsible lets an operator judge
    /// whether it should be, and leaves an honest record of what broke during
    /// planned work.
    /// </remarks>
    public string? SuppressedByWindowId { get; init; }

    /// <summary>Whether a notification should actually be dispatched now.</summary>
    public bool ShouldNotify =>
        PendingNotification != AlertNotificationKind.None && SuppressedByWindowId is null;

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    public DateTimeOffset? SilencedUntilUtc { get; init; }

    public IReadOnlyList<AlertTransition> History { get; init; } = [];

    /// <summary>
    /// The rule that owns this alert, or null for a direct producer (collection
    /// alerts, store failures, rule failures, compaction, flapping).
    /// </summary>
    /// <remarks>
    /// Null is the two-valued kind with N = 1 (design note §1.2): a producer
    /// that signed the cycle as run and did not report it has found it gone;
    /// one that did not run leaves it open and stale. Anything with a rule id is
    /// judged three-valued, and a cycle in which its rule says nothing about it
    /// is "not reported", never "gone".
    /// </remarks>
    public string? RuleId { get; init; }

    private readonly DateTimeOffset? _evidenceAtUtc;

    /// <summary>Time of the newest input the last fresh verdict rested on.</summary>
    /// <remarks>Falls back to <see cref="LastSeenUtc"/> for an instance built without one.</remarks>
    public DateTimeOffset EvidenceAtUtc
    {
        get => _evidenceAtUtc ?? LastSeenUtc;
        init => _evidenceAtUtc = value;
    }

    /// <summary>When the evidence stopped being fresh; null while it is fresh.</summary>
    public DateTimeOffset? StaleSinceUtc { get; init; }

    public UnknownReason? StaleReason { get; init; }

    public string? StaleDetail { get; init; }

    /// <summary>Consecutive fresh absences so far; resolution needs N of them.</summary>
    public int ConsecutiveAbsent { get; init; }

    /// <summary>Open, but the last cycles could not recheck it.</summary>
    public bool IsStale => StaleSinceUtc is not null;

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
        string? actor = null,
        string? detail = null)
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
                Detail = detail,
                EvidenceAtUtc = EvidenceAtUtc,
            }],
        };
    }

    /// <summary>
    /// Records something that happened without changing state — only when an
    /// operator did it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history is the durable record (<c>alert_history</c>) and holds
    /// transitions, not observations: an episode's opening, a real state change
    /// (<see cref="With"/>), or an explicit operator action. Measured after
    /// #83: <c>storage-latency-blind-spot</c> wrote 307 "Raised" rows in 43
    /// minutes for the same fingerprints, and same-state rows (evidence lost
    /// and back, severity eased) are the same kind of noise one cycle at a
    /// time. What they said stays on the instance instead: stale since, why,
    /// and the pending "improved" notification.
    /// </para>
    /// <para>
    /// A system event without an actor is therefore not written, which keeps
    /// the in-memory history and the stored one the same list, so a restart
    /// cannot shift the position the next row is written at.
    /// </para>
    /// </remarks>
    internal AlertInstance RecordEvent(
        AlertTransitionReason reason,
        DateTimeOffset atUtc,
        string? actor = null,
        string? detail = null) =>
        actor is null ? this : this with
        {
            History = [.. History, new AlertTransition
            {
                From = State,
                To = State,
                Reason = reason,
                AtUtc = atUtc,
                Actor = actor,
                Detail = detail,
                EvidenceAtUtc = EvidenceAtUtc,
            }],
        };
}
