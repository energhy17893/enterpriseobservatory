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

    public required DateTimeOffset LastEvaluatedUtc { get; init; }

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
            .Where(e => e.Covers(ControlId, Entity, nowUtc))
            .OrderBy(e => e.Entity is null ? 1 : 0)
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

    public required string Reason { get; init; }

    /// <summary>The person accountable for the exception, who is not necessarily who typed it.</summary>
    public required string Owner { get; init; }

    /// <summary>Who recorded it, as their audit name. See ADR-0013.</summary>
    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresUtc;

    public bool Covers(string controlId, EntityId entity, DateTimeOffset nowUtc) =>
        !IsExpiredAt(nowUtc) &&
        string.Equals(ControlId, controlId, StringComparison.Ordinal) &&
        (Entity is null || Entity == entity);
}
