namespace EnterpriseObservatory.Domain.Compliance;

/// <summary>What the product concluded when it last looked, before any human decision.</summary>
public enum ComplianceVerdict
{
    /// <summary>The setting was read and does not meet the baseline.</summary>
    Failing,

    /// <summary>The setting was read and meets the baseline.</summary>
    Passing,

    /// <summary>
    /// Nothing was concluded, and the finding says why.
    /// </summary>
    /// <remarks>
    /// Never counted as passing. A control nobody looked at is not a control
    /// that is met, and a compliance score that treats the two alike is the
    /// "green while blind" failure the first principle forbids.
    /// </remarks>
    NotEvaluated,
}

/// <summary>A finding's state as an operator sees it: the verdict, then what people decided.</summary>
public enum FindingState
{
    Failing,

    Passing,

    NotEvaluated,

    /// <summary>
    /// Still failing, and somebody has said they own it.
    /// </summary>
    /// <remarks>
    /// Acceptance moves a finding out of the queue that needs triage and
    /// nothing else: it is still non-compliant and still counted as such.
    /// Taking a finding out of the non-compliant count is what an
    /// <see cref="ComplianceWaiver"/> is for, and that always has an end.
    /// </remarks>
    Accepted,

    /// <summary>Failing, and covered by an exception that has not expired.</summary>
    Excepted,
}

/// <summary>Who accepted a finding, when, and why.</summary>
public sealed record FindingAcceptance
{
    /// <summary>The operator, as their audit name. See ADR-0013.</summary>
    public required string By { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// One control judged against one entity.
/// </summary>
/// <remarks>
/// <para>
/// Not an alert, and the differences are the reason for the type. An alert
/// closes itself when its condition passes and is noise if it cannot; a
/// finding is true for months, is expected by the hundred on the first day,
/// and ends when somebody fixes the setting or decides not to. So it lives on
/// its own screen and in its own table, and never in the inbox — the mistake
/// vROps made, where one hardening check fired on half the estate on day one
/// and taught operators to stop reading alerts.
/// </para>
/// <para>
/// The verdict is stored; the state is derived. Whether an exception still
/// covers a finding depends on the clock, and computing it at read time is
/// what makes an expired exception turn the finding back into
/// <see cref="FindingState.Failing"/> without a job having to remember to.
/// </para>
/// </remarks>
public sealed record ComplianceFinding
{
    public required string ControlId { get; init; }

    /// <summary>The catalogue release the control was taken from.</summary>
    public required string CatalogueRelease { get; init; }

    public required EntityId Entity { get; init; }

    /// <summary>For display only; see <see cref="Domain.Entity.DisplayName"/>.</summary>
    public string EntityName { get; init; } = string.Empty;

    /// <summary>
    /// The thing on the entity the operator will fix — a DRS rule's uuid, an
    /// HBA — or empty when the finding is about the entity itself (every
    /// vendor-guide finding).
    /// </summary>
    /// <remarks>
    /// Part of the identity: (release, control, entity, subject). One cause is
    /// one finding however many things it affects, so the subject is the
    /// cause, never the symptom.
    /// </remarks>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// How the subject is shown, e.g. a DRS rule's name. Display only, never
    /// identity: renaming the rule changes this and keeps the finding and its
    /// acceptance.
    /// </summary>
    public string? SubjectLabel { get; init; }

    public required ComplianceVerdict Verdict { get; init; }

    /// <summary>Why nothing was concluded. Set for <see cref="ComplianceVerdict.NotEvaluated"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>What the entity reported, verbatim; null when nothing was read.</summary>
    public string? Observed { get; init; }

    /// <summary>What the baseline asks for, in words an operator can check.</summary>
    public string Expected { get; init; } = string.Empty;

    /// <summary>When the current verdict was first reached.</summary>
    /// <remarks>
    /// Reset when the verdict changes. "Failing since" is what an auditor asks,
    /// and a date carried over from a period when the control passed would
    /// answer it wrongly.
    /// </remarks>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>When the evidence this verdict rests on was read.</summary>
    /// <remarks>
    /// The host's last-seen time, not the moment the judgement ran. A verdict
    /// re-derived from settings read yesterday is yesterday's verdict, and
    /// stamping it with today's date would present it as a fresh reading.
    /// </remarks>
    public required DateTimeOffset LastEvaluatedUtc { get; init; }

    /// <summary>
    /// Whether the host's source did not report in the cycle that produced this finding.
    /// </summary>
    /// <remarks>
    /// The verdict is then the last one this product could reach, carried
    /// rather than dropped — a vCenter being unreachable is not the host
    /// becoming compliant — but it is not current, and must never be shown as
    /// if it were. <see cref="LastEvaluatedUtc"/> says how old it is.
    /// </remarks>
    public bool Stale { get; init; }

    /// <summary>Set while somebody owns this failure; cleared when it passes.</summary>
    public FindingAcceptance? Acceptance { get; init; }

    /// <summary>
    /// The state an operator sees, given the exceptions in force and the time.
    /// </summary>
    /// <remarks>
    /// Only a failing finding can be excepted or accepted. A passing one needs
    /// neither, and one that was never evaluated cannot be waived: waiving it
    /// would record a decision about a setting nobody has read.
    /// </remarks>
    public FindingState StateAt(IReadOnlyList<ComplianceWaiver> exceptions, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        return Verdict switch
        {
            ComplianceVerdict.Passing => FindingState.Passing,
            ComplianceVerdict.NotEvaluated => FindingState.NotEvaluated,
            _ when CoveringException(exceptions, nowUtc) is not null => FindingState.Excepted,
            _ when Acceptance is not null => FindingState.Accepted,
            _ => FindingState.Failing,
        };
    }

    /// <summary>The unexpired exception covering this finding, if any; the narrowest wins.</summary>
    public ComplianceWaiver? CoveringException(
        IReadOnlyList<ComplianceWaiver> exceptions, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        return exceptions
            .Where(e => e.Covers(ControlId, Entity, Subject, nowUtc))
            .OrderBy(e => e.Entity is null ? 1 : 0)
            .ThenBy(e => e.Subject is null ? 1 : 0)
            .ThenByDescending(e => e.ExpiresUtc)
            .FirstOrDefault();
    }
}

/// <summary>
/// A decision that a control does not apply, for a reason, until a date.
/// </summary>
/// <remarks>
/// <para>
/// What the screen and the guide call an exception. The type is a waiver only
/// because .NET reserves the <c>Exception</c> suffix for things that are thrown.
/// </para>
/// <para>
/// Always ends. An exception nobody has to renew is one nobody remembers, and
/// a forgotten exception is the most expensive finding in an audit — it looks
/// like a decision and is only an absence of one. When it expires the finding
/// it covered is failing again, with no job needed: see
/// <see cref="ComplianceFinding.StateAt"/>.
/// </para>
/// <para>
/// Keyed by control id rather than by catalogue release, so re-issuing the
/// same edition of the guide does not silently drop every waiver. Ids that
/// change between major editions leave the old exception covering nothing,
/// which is the honest outcome: a new edition is a new decision.
/// </para>
/// </remarks>
public sealed record ComplianceWaiver
{
    public required string Id { get; init; }

    public required string ControlId { get; init; }

    /// <summary>The one entity it covers, or null for every entity the control applies to.</summary>
    public EntityId? Entity { get; init; }

    /// <summary>
    /// The one subject it covers, or null for every subject — which is what
    /// every exception written before subjects existed means, and keeps meaning.
    /// </summary>
    public string? Subject { get; init; }

    public required string Reason { get; init; }

    /// <summary>The person accountable for the exception, who is not necessarily who typed it.</summary>
    public required string Owner { get; init; }

    /// <summary>Who recorded it, as their audit name. See ADR-0013.</summary>
    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Who withdrew it, as their audit name; null while it stands.</summary>
    /// <remarks>
    /// Withdrawn rather than deleted. An auditor asking why a host was not
    /// counted as failing in March needs the exception that covered it then,
    /// and who ended it, long after it stopped applying.
    /// </remarks>
    public string? RemovedBy { get; init; }

    public DateTimeOffset? RemovedAtUtc { get; init; }

    public bool IsRemovedAt(DateTimeOffset nowUtc) => RemovedAtUtc is { } removed && nowUtc >= removed;

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresUtc;

    /// <summary>
    /// Whether it covers one finding: control ∧ (entity null or equal) ∧ (subject null or equal).
    /// </summary>
    /// <remarks>
    /// The finding's subject is always named, empty only for a finding about
    /// the entity itself; an overload assuming it would misjudge every
    /// subject-bearing finding.
    /// </remarks>
    public bool Covers(string controlId, EntityId entity, string subject, DateTimeOffset nowUtc) =>
        !IsExpiredAt(nowUtc) &&
        !IsRemovedAt(nowUtc) &&
        string.Equals(ControlId, controlId, StringComparison.Ordinal) &&
        (Entity is null || Entity == entity) &&
        (Subject is null || string.Equals(Subject, subject, StringComparison.Ordinal));
}

/// <summary>
/// A finding's verdict changing, as the audit trail records it.
/// </summary>
/// <remarks>
/// Written only when the verdict changes, never per evaluation: a control
/// that failed for a year is one row saying when it started. The first verdict
/// a finding ever has comes from null, and a finding that leaves the
/// evaluation — its host gone, its control no longer judged — goes to null.
/// </remarks>
public sealed record ComplianceTransition
{
    public required string CatalogueRelease { get; init; }

    public required string ControlId { get; init; }

    public required EntityId Entity { get; init; }

    /// <summary>The finding's subject; empty when it has none. See <see cref="ComplianceFinding.Subject"/>.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>How the subject was shown at the time; display only.</summary>
    public string? SubjectLabel { get; init; }

    /// <summary>The verdict before; null when the finding is new.</summary>
    public ComplianceVerdict? From { get; init; }

    /// <summary>The verdict after; null when the finding left the evaluation.</summary>
    public ComplianceVerdict? To { get; init; }

    /// <summary>
    /// What the entity reported behind the new verdict, verbatim; null when
    /// nothing was read. For a finding that left the evaluation, the last
    /// thing it observed.
    /// </summary>
    public string? Observed { get; init; }

    /// <summary>
    /// Who had accepted the finding, carried only when it left the evaluation.
    /// </summary>
    /// <remarks>
    /// The row goes when its subject goes — the device removed, the rule
    /// deleted — and the acceptance lived on the row. Carried here so the
    /// history keeps the decision and the reason; the row goes, the record
    /// does not.
    /// </remarks>
    public string? AcceptedBy { get; init; }

    /// <summary>Why it had been accepted; carried with <see cref="AcceptedBy"/>.</summary>
    public string? AcceptedReason { get; init; }

    /// <summary>When the setting behind the new verdict was read; null when the finding left.</summary>
    public DateTimeOffset? EvidenceUtc { get; init; }

    /// <summary>When the evaluation that saw the change ran.</summary>
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// A page of <see cref="ComplianceTransition"/> rows, as the compliance
/// store's history query returns them.
/// </summary>
/// <remarks>
/// The history table has no upper bound in principle -- a report spanning a
/// wide-enough period, or a control on a large-enough estate, can ask for
/// more rows than a single response should carry. A cap makes that request
/// answerable rather than a query the database or the browser rendering the
/// report chokes on; <see cref="Truncated"/> is what tells the reader the
/// answer is a prefix, not the whole period, rather than letting a silently
/// short list read as a quiet period.
/// </remarks>
public sealed record ComplianceTransitionsPage
{
    /// <summary>The largest number of rows a single query returns.</summary>
    /// <remarks>
    /// Fifty thousand: generous for the reference estate's actual volume --
    /// a few thousand transitions a year across a few hundred hosts -- and
    /// still small enough that a query, a CSV and a browser tab holding it
    /// all stay fast. A report that needs more than this in one period is a
    /// report that should narrow its scope with <c>control</c> or
    /// <c>entity</c>, not one this cap should quietly grow to fit.
    /// </remarks>
    public const int MaxRows = 50_000;

    public required IReadOnlyList<ComplianceTransition> Transitions { get; init; }

    /// <summary>Whether more rows matched than <see cref="MaxRows"/> allowed through.</summary>
    public required bool Truncated { get; init; }

    public static readonly ComplianceTransitionsPage Empty = new()
    {
        Transitions = [],
        Truncated = false,
    };
}
