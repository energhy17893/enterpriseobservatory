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
    IReadOnlyList<ComplianceFinding> Findings { get; }

    IReadOnlyList<ComplianceWaiver> Exceptions { get; }

    /// <summary>
    /// Replaces every finding with what <paramref name="evaluate"/> makes of the current ones.
    /// </summary>
    void Evaluate(Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate);

    /// <summary>Applies a change to one finding, atomically; null when there is no such finding.</summary>
    ComplianceFinding? Mutate(
        string controlId, EntityId entity, Func<ComplianceFinding, ComplianceFinding> change);

    void AddException(ComplianceWaiver exception);

    /// <summary>Removes an exception; false when there was none with that id.</summary>
    bool RemoveException(string id);
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

    public ComplianceCatalogue Catalogue { get; } =
        catalogue ?? throw new ArgumentNullException(nameof(catalogue));

    /// <summary>Every control, with the reason for each one this product cannot judge.</summary>
    public IReadOnlyList<BoundControl> Controls() => ComplianceEvaluation.Bind(Catalogue);

    public IReadOnlyList<ComplianceFinding> Findings() =>
        [.. _store.Findings.Where(f =>
            string.Equals(f.CatalogueRelease, Catalogue.Release, StringComparison.Ordinal))];

    public IReadOnlyList<ComplianceWaiver> Exceptions() => _store.Exceptions;

    public DateTimeOffset Now => _clock.UtcNow;

    /// <summary>Judges the estate as the inventory last read it.</summary>
    /// <returns>How many findings the evaluation holds.</returns>
    public int Evaluate(IReadOnlyList<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var now = _clock.UtcNow;
        var count = 0;

        _store.Evaluate(previous =>
        {
            var findings = ComplianceEvaluation.Evaluate(Catalogue, entities, previous, now);
            count = findings.Count;
            return findings;
        });

        return count;
    }

    /// <summary>Records that an operator owns a failing finding.</summary>
    public ComplianceResult Accept(
        string controlId, EntityId entity, string reason, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

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

        var acceptance = new FindingAcceptance
        {
            By = actor.AuditName,
            AtUtc = _clock.UtcNow,
            Reason = reason?.Trim() ?? string.Empty,
        };

        // Applied to the stored finding, not to the copy read above: an
        // evaluation may have landed in between, and the check is repeated
        // under the hold for the same reason.
        var changed = _store.Mutate(controlId, entity, f =>
            f.Verdict == ComplianceVerdict.Failing ? f with { Acceptance = acceptance } : f);

        return changed is null
            ? ComplianceResult.Refused(ComplianceFailure.NotFound)
            : changed.Acceptance == acceptance
                ? ComplianceResult.Done(changed)
                : ComplianceResult.Refused(ComplianceFailure.NotFailing);
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
    public ComplianceResult RemoveException(string id)
    {
        if (_store.Exceptions.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is not { } exception ||
            !_store.RemoveException(id))
        {
            return ComplianceResult.Refused(ComplianceFailure.NotFound);
        }

        return ComplianceResult.Done(exception);
    }
}
