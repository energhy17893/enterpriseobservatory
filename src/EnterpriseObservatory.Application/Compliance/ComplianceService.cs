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

    /// <summary>
    /// Applies a change to one finding — (release, control, entity, subject) —
    /// atomically; null when there is no such finding.
    /// </summary>
    /// <remarks>
    /// The subject is always named, empty only for a finding about the entity
    /// itself: a store that ignored it would change the wrong subject's row.
    /// </remarks>
    ComplianceFinding? Mutate(
        string catalogueRelease,
        string controlId,
        EntityId entity,
        string subject,
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

    /// <summary>
    /// For every (control, entity, subject) of <paramref name="catalogueRelease"/>
    /// with at least one transition at or before <paramref name="atUtc"/>, the
    /// latest such transition -- what "verdict at start" reads (P2's net-change
    /// delta, <see cref="ComplianceService.DeltaSince"/>). A finding with no row
    /// here did not exist yet at <paramref name="atUtc"/>: it is new in the
    /// window, never assigned an invented starting verdict.
    /// </summary>
    IReadOnlyList<ComplianceTransition> LastTransitionsAtOrBefore(string catalogueRelease, DateTimeOffset atUtc);
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
public sealed class ComplianceService
{
    private readonly IComplianceStore _store;

    private readonly IClock _clock;

    private readonly IReadOnlyDictionary<string, IComplianceCheck> _checksById;

    /// <summary>One catalogue: the vendor guide alone.</summary>
    public ComplianceService(ComplianceCatalogue catalogue, IComplianceStore store, IClock clock)
        : this([catalogue ?? throw new ArgumentNullException(nameof(catalogue))], store, clock)
    {
    }

    /// <summary>Several catalogues, each evaluated independently: the vendor guide first.</summary>
    /// <param name="catalogues">The catalogues; the first is the one <see cref="Catalogue"/> names.</param>
    /// <param name="store">Where findings and exceptions live.</param>
    /// <param name="clock">The time.</param>
    /// <param name="checksById">The checks of the catalogues that bind by control id.</param>
    public ComplianceService(
        IReadOnlyList<ComplianceCatalogue> catalogues,
        IComplianceStore store,
        IClock clock,
        IReadOnlyDictionary<string, IComplianceCheck>? checksById = null)
    {
        ArgumentNullException.ThrowIfNull(catalogues);

        if (catalogues.Count == 0)
        {
            throw new ArgumentException("At least one catalogue is needed.", nameof(catalogues));
        }

        Catalogues = catalogues;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _checksById = checksById ?? new Dictionary<string, IComplianceCheck>(StringComparer.Ordinal);
    }

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

    /// <summary>Every catalogue, the vendor guide first.</summary>
    public IReadOnlyList<ComplianceCatalogue> Catalogues { get; }

    /// <summary>
    /// Every loaded catalogue, described for the posture screen's scorecard.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Catalogues"/>' order: a caller building a
    /// scorecard per catalogue (P1) must not read meaning into which one
    /// comes first, only <see cref="Catalogue"/> may.
    /// </remarks>
    public IReadOnlyList<CatalogueDescriptor> Descriptors() =>
        [.. Catalogues.Select(CatalogueDescriptor.Of)];

    /// <summary>
    /// Every control of every catalogue, tagged with its catalogue, with the
    /// reason for each one this product cannot judge.
    /// </summary>
    public IReadOnlyList<BoundControl> Controls() =>
        [.. Catalogues.SelectMany(c => ComplianceEvaluation.Bind(c, checksById: _checksById))];

    /// <summary>The findings of every loaded catalogue release.</summary>
    public IReadOnlyList<ComplianceFinding> Findings()
    {
        var releases = Catalogues.Select(c => c.Release).ToHashSet(StringComparer.Ordinal);

        return [.. _store.Findings.Where(f => releases.Contains(f.CatalogueRelease))];
    }

    /// <summary>Every exception, standing or withdrawn.</summary>
    public IReadOnlyList<ComplianceWaiver> Exceptions() => _store.Exceptions;

    /// <summary>
    /// Verdict changes in [<paramref name="sinceUtc"/>, <paramref name="toUtc"/>],
    /// for the loaded catalogue releases, optionally narrowed further.
    /// </summary>
    /// <remarks>
    /// A control belongs to one catalogue, so a history narrowed to it reads
    /// that release alone. Otherwise every loaded release is read, oldest
    /// first, and the whole is still capped at
    /// <see cref="ComplianceTransitionsPage.MaxRows"/>.
    /// </remarks>
    public ComplianceTransitionsPage TransitionsSince(
        DateTimeOffset sinceUtc, DateTimeOffset toUtc, string? controlId = null, EntityId? entity = null)
    {
        if (controlId is not null)
        {
            // A control no loaded catalogue knows has no history. This used to
            // fall back to "the only catalogue", which throws once more than
            // one is loaded -- a mistyped control id became a 500.
            return ReleaseOf(controlId) is { } release
                ? _store.TransitionsSince(sinceUtc, toUtc, release, controlId, entity)
                : new ComplianceTransitionsPage { Transitions = [], Truncated = false };
        }

        var pages = Catalogues
            .Select(c => _store.TransitionsSince(sinceUtc, toUtc, c.Release, controlId, entity))
            .ToList();

        var merged = pages
            .SelectMany(p => p.Transitions)
            .OrderBy(t => t.AtUtc)
            .ToList();

        var truncated = pages.Any(p => p.Truncated) || merged.Count > ComplianceTransitionsPage.MaxRows;

        return new ComplianceTransitionsPage
        {
            Transitions = [.. merged.Take(ComplianceTransitionsPage.MaxRows)],
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Verdict changes in [<paramref name="sinceUtc"/>, <paramref name="toUtc"/>],
    /// for one named catalogue only -- what a per-catalogue report reads,
    /// never guessing which catalogue is "the" one from list position.
    /// </summary>
    public ComplianceTransitionsPage TransitionsSince(
        DateTimeOffset sinceUtc,
        DateTimeOffset toUtc,
        ComplianceCatalogue catalogue,
        string? controlId,
        EntityId? entity)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return _store.TransitionsSince(sinceUtc, toUtc, catalogue.Release, controlId, entity);
    }

    /// <summary>
    /// One catalogue's NET posture change since <paramref name="sinceUtc"/>:
    /// findings that went Passing -> Failing (worsened) or Failing -> Passing
    /// (improved), counted only for findings evaluated at both ends, plus
    /// findings new in the window and findings NotEvaluated now (ADR-0026: a
    /// round trip through NotEvaluated, e.g. a vCenter outage, is not a
    /// posture change -- see the type this returns).
    /// </summary>
    /// <remarks>
    /// "Verdict at start" is never invented: it is the <c>to_verdict</c> of the
    /// last <c>compliance_transition</c> row at or before <paramref name="sinceUtc"/>
    /// (<see cref="IComplianceStore.LastTransitionsAtOrBefore"/>). A finding
    /// with no such row did not exist yet at <paramref name="sinceUtc"/> and is
    /// counted as new, not as worsened or improved. Acceptance and exceptions
    /// are not posture: both <c>compliance_transition</c> rows and
    /// <see cref="ComplianceFinding.Verdict"/> already carry the evaluated
    /// verdict alone (Failing/Passing/NotEvaluated), never the acceptance or
    /// exception state layered on top of it, so no separate mapping is needed
    /// here -- comparing verdicts already ignores acceptance.
    /// </remarks>
    public ComplianceCatalogueDelta DeltaSince(ComplianceCatalogue catalogue, DateTimeOffset sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var startVerdicts = _store
            .LastTransitionsAtOrBefore(catalogue.Release, sinceUtc)
            .ToDictionary(t => (t.ControlId, t.Entity, t.Subject), t => t.To);

        var worsened = 0;
        var improved = 0;
        var newCount = 0;
        var notEvaluatedNow = 0;

        foreach (var finding in Findings().Where(f => string.Equals(f.CatalogueRelease, catalogue.Release, StringComparison.Ordinal)))
        {
            if (finding.Verdict == ComplianceVerdict.NotEvaluated)
            {
                notEvaluatedNow++;
                continue;
            }

            if (!startVerdicts.TryGetValue((finding.ControlId, finding.Entity, finding.Subject), out var startVerdict))
            {
                newCount++;
                continue;
            }

            if (startVerdict == ComplianceVerdict.Passing && finding.Verdict == ComplianceVerdict.Failing)
            {
                worsened++;
            }
            else if (startVerdict == ComplianceVerdict.Failing && finding.Verdict == ComplianceVerdict.Passing)
            {
                improved++;
            }

            // Otherwise: unchanged (Passing -> Passing, Failing -> Failing), or
            // NotEvaluated at start -- not evaluated at both ends, so not a
            // posture change either way (the outage round trip).
        }

        return new ComplianceCatalogueDelta
        {
            Worsened = worsened,
            Improved = improved,
            New = newCount,
            NotEvaluatedNow = notEvaluatedNow,
        };
    }

    public DateTimeOffset Now => _clock.UtcNow;

    /// <summary>Judges the estate as the inventory last read it, one catalogue at a time.</summary>
    /// <param name="entities">The estate.</param>
    /// <param name="reportingSources">
    /// The sources that answered in the inventory cycle just run; an entity of
    /// any other source is judged on what it last reported and marked stale.
    /// Null treats every source as having answered. Every catalogue is judged
    /// with the same sources: a silent vCenter's clusters are as stale as its
    /// hosts.
    /// </param>
    /// <param name="graph">The estate as a graph; built from <paramref name="entities"/> when null.</param>
    /// <param name="demand">Precomputed demand for N+1 checks; null when none.</param>
    /// <param name="silentNamespaces">Annotation namespaces with no answering source; see <see cref="ComplianceEvaluation.SilentNamespaces"/>.</param>
    /// <returns>How many findings the evaluations hold.</returns>
    /// <remarks>
    /// A catalogue that could not be loaded is skipped; the others are still
    /// judged. The store replaces one release at a time, so no catalogue's
    /// evaluation can touch another's rows.
    /// </remarks>
    public int Evaluate(
        IReadOnlyList<Entity> entities,
        IReadOnlyCollection<string>? reportingSources = null,
        EntityGraph? graph = null,
        DemandSnapshot? demand = null,
        IReadOnlyCollection<string>? silentNamespaces = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var now = _clock.UtcNow;
        var count = 0;

        foreach (var catalogue in Catalogues.Where(c => c.Problem is null))
        {
            _store.Evaluate(catalogue.Release, now, previous =>
            {
                var findings = ComplianceEvaluation.Evaluate(
                    catalogue,
                    entities,
                    previous,
                    now,
                    reportingSources: reportingSources,
                    checksById: _checksById,
                    graph: graph,
                    demand: demand,
                    silentNamespaces: silentNamespaces);
                count += findings.Count;
                return findings;
            });
        }

        return count;
    }

    /// <summary>The release of the catalogue that holds a control; null when none does.</summary>
    private string? ReleaseOf(string? controlId) =>
        controlId is null
            ? null
            : Catalogues.FirstOrDefault(c => c.Controls.Any(
                control => string.Equals(control.ControlId, controlId, StringComparison.Ordinal)))?.Release;

    /// <summary>Records that an operator owns a failing finding.</summary>
    /// <remarks>
    /// Refused when somebody already owns it. Overwriting would erase who took
    /// it on first and why, which is the part an auditor asks about; whoever
    /// wants to take it over says so to the person on the record.
    /// </remarks>
    /// <param name="controlId">The control.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="subject">
    /// The finding's subject; empty only for a finding about the entity itself.
    /// Always named: there is deliberately no overload that assumes it.
    /// </param>
    /// <param name="reason">Why; may be empty.</param>
    /// <param name="actor">Who.</param>
    public ComplianceResult Accept(
        string controlId, EntityId entity, string subject, string reason, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        subject ??= string.Empty;

        var trimmed = reason?.Trim() ?? string.Empty;

        if (trimmed.Length > MaximumReasonLength)
        {
            return ComplianceResult.Refused(ComplianceFailure.TooLong);
        }

        // Only reached with an unknown controlId, when no finding below can
        // match by ControlId regardless of release -- so the fallback value
        // itself carries no meaning, and never names "the first catalogue".
        var release = ReleaseOf(controlId) ?? string.Empty;

        var current = Findings().FirstOrDefault(f =>
            string.Equals(f.CatalogueRelease, release, StringComparison.Ordinal) &&
            string.Equals(f.ControlId, controlId, StringComparison.Ordinal) &&
            f.Entity == entity &&
            string.Equals(f.Subject, subject, StringComparison.Ordinal));

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
        var changed = _store.Mutate(release, controlId, entity, subject, f =>
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
    /// <remarks>
    /// Also for one subject or all of them. The subject is always named —
    /// there is deliberately no overload that assumes "every subject", so an
    /// exception meant for one DRS rule cannot silently cover them all.
    /// </remarks>
    /// <param name="subject">The one subject it covers; null or blank for every subject.</param>
    public ComplianceResult AddException(
        string controlId,
        EntityId? entity,
        string? subject,
        string reason,
        string owner,
        DateTimeOffset expiresUtc,
        OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (ReleaseOf(controlId) is null)
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
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject,
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

/// <summary>
/// One catalogue's NET posture change over a window (P2, revised): findings
/// that worsened (Passing -> Failing) or improved (Failing -> Passing),
/// findings new in the window, and findings NotEvaluated now -- counted from
/// <c>compliance_transition</c>, not a second, independently-maintained
/// total. See <see cref="ComplianceService.DeltaSince"/> and ADR-0026: a
/// round trip through NotEvaluated is not a posture change, so it never
/// contributes to <see cref="Worsened"/> or <see cref="Improved"/>.
/// </summary>
public sealed record ComplianceCatalogueDelta
{
    /// <summary>Findings evaluated at both ends that went Passing -> Failing.</summary>
    public required int Worsened { get; init; }

    /// <summary>Findings evaluated at both ends that went Failing -> Passing.</summary>
    public required int Improved { get; init; }

    /// <summary>Findings with no verdict at the start of the window -- new, not worsened or improved.</summary>
    public required int New { get; init; }

    /// <summary>Findings whose verdict now is NotEvaluated -- counted apart, in neither +/- bucket.</summary>
    public required int NotEvaluatedNow { get; init; }
}
