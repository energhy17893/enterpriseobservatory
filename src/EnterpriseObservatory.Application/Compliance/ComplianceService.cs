using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Where compliance findings and exceptions live.
/// </summary>
/// <remarks>
/// <para>
/// Its own store, not the alert store. Findings are not alerts — they do not
/// close themselves, they are expected by the hundred, and they are accepted
/// rather than resolved — and keeping them apart is what keeps them out of
/// the inbox. See product-architecture §2.
/// </para>
/// <para>
/// The evaluation is a callback under the store's hold, for the reason
/// <c>IAlertStateStore.Reconcile</c> gives: an operator accepting a finding
/// between a read and a write would otherwise be silently overwritten by the
/// evaluation's result.
/// </para>
/// </remarks>
public interface IComplianceStore
{
    /// <summary>Every finding, of every catalogue release.</summary>
    IReadOnlyList<ComplianceFinding> Findings { get; }

    /// <summary>Every exception, including withdrawn ones — they stay on the record.</summary>
    IReadOnlyList<ComplianceWaiver> Exceptions { get; }

    /// <summary>
    /// Replaces one catalogue release's findings with what <paramref name="evaluate"/>
    /// makes of them.
    /// </summary>
    /// <param name="catalogueRelease">
    /// The release being evaluated. Only its findings are handed to
    /// <paramref name="evaluate"/> and only they are replaced: another
    /// catalogue's findings are that catalogue's evaluation's business.
    /// </param>
    /// <param name="nowUtc">When the evaluation ran, for the transitions it records.</param>
    /// <param name="evaluate">Every finding it returns must belong to <paramref name="catalogueRelease"/>.</param>
    void Evaluate(
        string catalogueRelease,
        DateTimeOffset nowUtc,
        Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate);

    /// <summary>Applies a change to one finding, atomically; null when there is no such finding.</summary>
    ComplianceFinding? Mutate(
        string catalogueRelease,
        string controlId,
        EntityId entity,
        Func<ComplianceFinding, ComplianceFinding> change);

    void AddException(ComplianceWaiver exception);

    /// <summary>
    /// Withdraws an exception, keeping it on the record with who withdrew it
    /// and when; false when there is no standing exception with that id.
    /// </summary>
    bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc);

    /// <summary>
    /// Verdict changes in [<paramref name="sinceUtc"/>, <paramref name="toUtc"/>],
    /// oldest first — the audit trail M5.2's compliance report reads.
    /// </summary>
    /// <param name="toUtc">
    /// The end of the window; null reads through to whatever the store's
    /// newest row is, which is every row a caller who never names an end
    /// still expects.
    /// </param>
    /// <param name="catalogueRelease">
    /// Limits the history to one release; null reads every release, which is
    /// what a report spanning a catalogue upgrade needs.
    /// </param>
    /// <param name="controlId">Limits the history to one control; null reads every control.</param>
    /// <param name="entity">Limits the history to one entity; null reads every entity.</param>
    /// <remarks>
    /// Capped at <see cref="ComplianceTransitionsPage.MaxRows"/>; a scope wide
    /// enough to hit it comes back with <see cref="ComplianceTransitionsPage.Truncated"/>
    /// set rather than the whole match, which is what makes an unbounded
    /// scan of <c>compliance_transition</c> for a wide-enough period or a
    /// large-enough estate a bounded query instead of one the database or a
    /// report page has to absorb in full.
    /// </remarks>
    ComplianceTransitionsPage TransitionsSince(
        DateTimeOffset sinceUtc,
        DateTimeOffset? toUtc = null,
        string? catalogueRelease = null,
        string? controlId = null,
        EntityId? entity = null);
}

/// <summary>Why a compliance command was refused.</summary>
public enum ComplianceFailure
{
    None = 0,

    NotFound,

    /// <summary>Only a failing finding can be accepted.</summary>
    NotFailing,

    /// <summary>The control is not in the loaded catalogue.</summary>
    UnknownControl,

    /// <summary>An exception has to end in the future, and not too far in it.</summary>
    BadExpiry,

    /// <summary>A reason and an owner are both required.</summary>
    MissingDetail,

    /// <summary>A reason or an owner is longer than the record keeps.</summary>
    TooLong,

    /// <summary>Somebody already owns the finding; an acceptance is not overwritten.</summary>
    AlreadyAccepted,
}

/// <summary>What a compliance command did.</summary>
public sealed record ComplianceResult
{
    public required bool Applied { get; init; }

    public ComplianceFinding? Finding { get; init; }

    public ComplianceWaiver? Exception { get; init; }

    public ComplianceFailure Failure { get; init; }

    public static ComplianceResult Done(ComplianceFinding finding) =>
        new() { Applied = true, Finding = finding };

    public static ComplianceResult Done(ComplianceWaiver exception) =>
        new() { Applied = true, Exception = exception };

    public static ComplianceResult Refused(ComplianceFailure failure) =>
        new() { Applied = false, Failure = failure };
}

/// <summary>
/// The compliance engine: evaluates the loaded catalogue and records what
/// people decide about the result.
/// </summary>
/// <remarks>
/// <para>
/// Evaluated on the inventory rhythm. Settings change when somebody changes
/// them, and they are read on that rhythm; judging them every thirty seconds
/// would re-decide a fact nothing had re-read.
/// </para>
/// <para>
/// The two human verbs mean different things. Accepting says "this is mine" —
/// the finding leaves the triage queue and stays non-compliant. An exception
/// says "this does not apply, because, until" — it takes the finding out of
/// the non-compliant count, and it always ends.
/// </para>
/// </remarks>
public sealed class ComplianceService(
    ComplianceCatalogue catalogue, IComplianceStore store, IClock clock)
{
    private readonly IComplianceStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The longest an exception may run.
    /// </summary>
    /// <remarks>
    /// A year: long enough for an annual audit cycle, which is the rhythm on
    /// which a waiver is legitimately re-examined, and short enough that
    /// nobody can record a permanent one by picking a date far away.
    /// </remarks>
    public static TimeSpan MaximumExceptionDuration { get; } = TimeSpan.FromDays(366);

    /// <summary>The longest reason an acceptance or an exception may carry.</summary>
    /// <remarks>
    /// Room for a paragraph and a list of change tickets. A reason is read by
    /// people on a screen and in an audit report, and anything longer than
    /// this is a document that belongs in the ticket it should be citing.
    /// </remarks>
    public const int MaximumReasonLength = 2000;

    /// <summary>The longest owner an exception may name: a person, a team, or a mailbox.</summary>
    public const int MaximumOwnerLength = 200;

    public ComplianceCatalogue Catalogue { get; } =
        catalogue ?? throw new ArgumentNullException(nameof(catalogue));

    /// <summary>Every control, with the reason for each one this product cannot judge.</summary>
    public IReadOnlyList<BoundControl> Controls() => ComplianceEvaluation.Bind(Catalogue);

    public IReadOnlyList<ComplianceFinding> Findings() =>
        [.. _store.Findings.Where(f =>
            string.Equals(f.CatalogueRelease, Catalogue.Release, StringComparison.Ordinal))];

    /// <summary>Every exception, standing or withdrawn.</summary>
    public IReadOnlyList<ComplianceWaiver> Exceptions() => _store.Exceptions;

    /// <summary>
    /// Verdict changes in [<paramref name="sinceUtc"/>, <paramref name="toUtc"/>],
    /// for the loaded catalogue release, optionally narrowed further.
    /// </summary>
    public ComplianceTransitionsPage TransitionsSince(
        DateTimeOffset sinceUtc, DateTimeOffset toUtc, string? controlId = null, EntityId? entity = null) =>
        _store.TransitionsSince(sinceUtc, toUtc, Catalogue.Release, controlId, entity);

    public DateTimeOffset Now => _clock.UtcNow;

    /// <summary>Judges the estate as the inventory last read it.</summary>
    /// <param name="entities">The estate.</param>
    /// <param name="reportingSources">
    /// The sources that answered in the inventory cycle just run; a host of
    /// any other source is judged on what it last reported and marked stale.
    /// Null treats every source as having answered.
    /// </param>
    /// <returns>How many findings the evaluation holds.</returns>
    public int Evaluate(
        IReadOnlyList<Entity> entities, IReadOnlyCollection<string>? reportingSources = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var now = _clock.UtcNow;
        var count = 0;

        _store.Evaluate(Catalogue.Release, now, previous =>
        {
            var findings = ComplianceEvaluation.Evaluate(
                Catalogue, entities, previous, now, reportingSources: reportingSources);
            count = findings.Count;
            return findings;
        });

        return count;
    }

    /// <summary>Records that an operator owns a failing finding.</summary>
    /// <remarks>
    /// Refused when somebody already owns it. Overwriting would erase who took
    /// it on first and why, which is the part an auditor asks about; whoever
    /// wants to take it over says so to the person on the record.
    /// </remarks>
    public ComplianceResult Accept(
        string controlId, EntityId entity, string reason, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var trimmed = reason?.Trim() ?? string.Empty;

        if (trimmed.Length > MaximumReasonLength)
        {
            return ComplianceResult.Refused(ComplianceFailure.TooLong);
        }

        var current = Findings().FirstOrDefault(f =>
            string.Equals(f.ControlId, controlId, StringComparison.Ordinal) && f.Entity == entity);

        if (current is null)
        {
            return ComplianceResult.Refused(ComplianceFailure.NotFound);
        }

        if (current.Verdict != ComplianceVerdict.Failing)
        {
            return ComplianceResult.Refused(ComplianceFailure.NotFailing);
        }

        if (current.Acceptance is not null)
        {
            return ComplianceResult.Refused(ComplianceFailure.AlreadyAccepted);
        }

        var acceptance = new FindingAcceptance
        {
            By = actor.AuditName,
            AtUtc = _clock.UtcNow,
            Reason = trimmed,
        };

        // Applied to the stored finding, not to the copy read above: an
        // evaluation or another operator may have landed in between, and the
        // checks are repeated under the hold for the same reason.
        var changed = _store.Mutate(Catalogue.Release, controlId, entity, f =>
            f.Verdict == ComplianceVerdict.Failing && f.Acceptance is null
                ? f with { Acceptance = acceptance }
                : f);

        return changed switch
        {
            null => ComplianceResult.Refused(ComplianceFailure.NotFound),
            _ when changed.Acceptance == acceptance => ComplianceResult.Done(changed),
            _ when changed.Acceptance is not null => ComplianceResult.Refused(ComplianceFailure.AlreadyAccepted),
            _ => ComplianceResult.Refused(ComplianceFailure.NotFailing),
        };
    }

    /// <summary>Records an exception to a control, for one entity or all of them.</summary>
    public ComplianceResult AddException(
        string controlId,
        EntityId? entity,
        string reason,
        string owner,
        DateTimeOffset expiresUtc,
        OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!Catalogue.Controls.Any(c => string.Equals(c.ControlId, controlId, StringComparison.Ordinal)))
        {
            return ComplianceResult.Refused(ComplianceFailure.UnknownControl);
        }

        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(owner))
        {
            // An exception nobody can explain or answer for is the forgotten
            // kind before it has even been forgotten.
            return ComplianceResult.Refused(ComplianceFailure.MissingDetail);
        }

        if (reason.Trim().Length > MaximumReasonLength || owner.Trim().Length > MaximumOwnerLength)
        {
            return ComplianceResult.Refused(ComplianceFailure.TooLong);
        }

        var now = _clock.UtcNow;

        if (expiresUtc <= now || expiresUtc - now > MaximumExceptionDuration)
        {
            return ComplianceResult.Refused(ComplianceFailure.BadExpiry);
        }

        var exception = new ComplianceWaiver
        {
            Id = Guid.NewGuid().ToString("n"),
            ControlId = controlId,
            Entity = entity,
            Reason = reason.Trim(),
            Owner = owner.Trim(),
            CreatedBy = actor.AuditName,
            CreatedAtUtc = now,
            ExpiresUtc = expiresUtc,
        };

        _store.AddException(exception);

        return ComplianceResult.Done(exception);
    }

    /// <summary>Withdraws an exception; the findings it covered are failing again at once.</summary>
    /// <remarks>
    /// Withdrawn, not deleted: the exception stays on the record with who
    /// withdrew it and when, because "why was this host not counted in March"
    /// is asked long after the answer stopped applying.
    /// </remarks>
    public ComplianceResult RemoveException(string id, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var now = _clock.UtcNow;

        if (_store.Exceptions.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is not { } exception ||
            exception.RemovedAtUtc is not null ||
            !_store.RemoveException(id, actor.AuditName, now))
        {
            return ComplianceResult.Refused(ComplianceFailure.NotFound);
        }

        return ComplianceResult.Done(exception with { RemovedBy = actor.AuditName, RemovedAtUtc = now });
    }
}
