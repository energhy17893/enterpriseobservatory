using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// P2, revised: the posture scorecard's "last 7 days" delta counts NET
/// posture change, not raw transitions -- a catalogue's whole estate going
/// NotEvaluated during a vCenter outage and coming back is not "N things
/// broke and N things got fixed" (ADR-0026). <see cref="ComplianceService.DeltaSince"/>
/// compares each current finding's verdict against its verdict at the start
/// of the window (the last <c>compliance_transition</c> row at or before
/// then, from <see cref="IComplianceStore.LastTransitionsAtOrBefore"/>), and
/// only for findings evaluated (Passing or Failing) at both ends.
/// </summary>
public class ComplianceCatalogueDeltaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Start = T0.AddDays(-7);

    private static readonly ComplianceCatalogue Catalogue = ComplianceEvaluationTests.Catalogue(
        ComplianceEvaluationTests.LogForwarding);

    private const string ControlId = "esx-9.log-forwarding";

    private static readonly EntityId Host1 = new("vc-1:host-1");

    private static ComplianceFinding Finding(
        EntityId entity, string subject, ComplianceVerdict verdict) => new()
        {
            ControlId = ControlId,
            CatalogueRelease = Catalogue.Release,
            Entity = entity,
            Subject = subject,
            Verdict = verdict,
            Expected = "n/a",
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
        };

    private static ComplianceTransition StartVerdict(
        EntityId entity, string subject, ComplianceVerdict to) => new()
        {
            CatalogueRelease = Catalogue.Release,
            ControlId = ControlId,
            Entity = entity,
            Subject = subject,
            To = to,
            AtUtc = Start,
        };

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// Findings as they stand now, and the "verdict at start" rows
    /// <see cref="ComplianceService.DeltaSince"/> asks for -- exactly the two
    /// things it reads, nothing else.
    /// </summary>
    private sealed class Store(
        IReadOnlyList<ComplianceFinding> findings,
        IReadOnlyList<ComplianceTransition> startVerdicts) : IComplianceStore
    {
        public string? ReleaseAsked { get; private set; }

        public DateTimeOffset? AtAsked { get; private set; }

        public IReadOnlyList<ComplianceFinding> Findings => findings;

        public IReadOnlyList<ComplianceWaiver> Exceptions => [];

        public void Evaluate(
            string catalogueRelease,
            DateTimeOffset nowUtc,
            Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate) =>
            throw new NotSupportedException();

        public ComplianceFinding? Mutate(
            string catalogueRelease, string controlId, EntityId entity, string subject,
            Func<ComplianceFinding, ComplianceFinding> change) => throw new NotSupportedException();

        public void AddException(ComplianceWaiver exception) => throw new NotSupportedException();

        public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc) =>
            throw new NotSupportedException();

        public ComplianceTransitionsPage TransitionsSince(
            DateTimeOffset sinceUtc, DateTimeOffset? toUtc = null, string? catalogueRelease = null,
            string? controlId = null, EntityId? entity = null) =>
            throw new NotSupportedException("DeltaSince reads LastTransitionsAtOrBefore, not this.");

        public IReadOnlyList<ComplianceTransition> LastTransitionsAtOrBefore(
            string catalogueRelease, DateTimeOffset atUtc)
        {
            ReleaseAsked = catalogueRelease;
            AtAsked = atUtc;
            return startVerdicts;
        }
    }

    private static ComplianceCatalogueDelta Delta(
        IReadOnlyList<ComplianceFinding> findings, IReadOnlyList<ComplianceTransition> startVerdicts)
    {
        var service = new ComplianceService([Catalogue], new Store(findings, startVerdicts), new Clock(T0));
        return service.DeltaSince(Catalogue, Start);
    }

    [Fact]
    public void An_outage_round_trip_through_not_evaluated_is_not_a_posture_change()
    {
        // Failing at start, NotEvaluated in between (not itself asked about --
        // only the start and now matter), Failing again now.
        var delta = Delta(
            [Finding(Host1, "", ComplianceVerdict.Failing)],
            [StartVerdict(Host1, "", ComplianceVerdict.Failing)]);

        Assert.Equal(0, delta.Worsened);
        Assert.Equal(0, delta.Improved);
    }

    [Fact]
    public void Passing_at_start_and_failing_now_is_one_worsened()
    {
        var delta = Delta(
            [Finding(Host1, "", ComplianceVerdict.Failing)],
            [StartVerdict(Host1, "", ComplianceVerdict.Passing)]);

        Assert.Equal(1, delta.Worsened);
        Assert.Equal(0, delta.Improved);
    }

    [Fact]
    public void Failing_at_start_and_passing_now_is_one_improved()
    {
        var delta = Delta(
            [Finding(Host1, "", ComplianceVerdict.Passing)],
            [StartVerdict(Host1, "", ComplianceVerdict.Failing)]);

        Assert.Equal(0, delta.Worsened);
        Assert.Equal(1, delta.Improved);
    }

    [Fact]
    public void A_finding_first_seen_inside_the_window_is_new_not_worsened()
    {
        // No row at or before Start at all: the finding did not exist yet.
        var delta = Delta([Finding(Host1, "", ComplianceVerdict.Failing)], []);

        Assert.Equal(1, delta.New);
        Assert.Equal(0, delta.Worsened);
        Assert.Equal(0, delta.Improved);
    }

    [Fact]
    public void Two_subjects_of_one_control_and_entity_are_counted_independently()
    {
        var delta = Delta(
            [
                Finding(Host1, "rule-a", ComplianceVerdict.Failing),
                Finding(Host1, "rule-b", ComplianceVerdict.Passing),
            ],
            [
                StartVerdict(Host1, "rule-a", ComplianceVerdict.Passing),
                StartVerdict(Host1, "rule-b", ComplianceVerdict.Failing),
            ]);

        Assert.Equal(1, delta.Worsened);
        Assert.Equal(1, delta.Improved);
    }

    [Fact]
    public void A_finding_not_evaluated_now_is_counted_apart_and_in_neither_sign()
    {
        var delta = Delta(
            [Finding(Host1, "", ComplianceVerdict.NotEvaluated)],
            [StartVerdict(Host1, "", ComplianceVerdict.Failing)]);

        Assert.Equal(1, delta.NotEvaluatedNow);
        Assert.Equal(0, delta.Worsened);
        Assert.Equal(0, delta.Improved);
        Assert.Equal(0, delta.New);
    }

    [Fact]
    public void An_unchanged_finding_is_in_no_bucket()
    {
        var delta = Delta(
            [Finding(Host1, "", ComplianceVerdict.Failing)],
            [StartVerdict(Host1, "", ComplianceVerdict.Failing)]);

        Assert.Equal(0, delta.Worsened);
        Assert.Equal(0, delta.Improved);
        Assert.Equal(0, delta.New);
        Assert.Equal(0, delta.NotEvaluatedNow);
    }

    [Fact]
    public void The_delta_asks_the_store_for_its_own_release_and_the_start_of_the_window()
    {
        var store = new Store([], []);
        var service = new ComplianceService([Catalogue], store, new Clock(T0));

        service.DeltaSince(Catalogue, Start);

        Assert.Equal(Catalogue.Release, store.ReleaseAsked);
        Assert.Equal(Start, store.AtAsked);
    }
}
