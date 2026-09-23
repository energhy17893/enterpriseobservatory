using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// P2: the posture scorecard's "last 7 days" delta -- a COUNT over
/// <c>compliance_transition</c> rows (ADR-0026's replay-from-the-log
/// principle; no snapshot table), scoped to one catalogue's release.
/// </summary>
/// <remarks>
/// The brief that started this work assumed <c>compliance_transition</c> had
/// no <c>subject</c> column and needed one added as migration 17, with old
/// rows counted as "subject unknown". That was wrong: migration 11 already
/// added it, <c>NOT NULL DEFAULT ''</c>, and the store already writes it on
/// every insert -- there is no unknown case. What the column actually
/// protects is what this test proves: a transition is per (release, control,
/// entity, subject), so two subjects of the same control and entity are two
/// transitions, not one a coarser key would have collapsed.
/// </remarks>
public class ComplianceCatalogueDeltaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Catalogue = ComplianceEvaluationTests.Catalogue(
        ComplianceEvaluationTests.LogForwarding);

    private static readonly EntityId Host1 = new("vc-1:host-1");

    private static ComplianceTransition Transition(
        string control, EntityId entity, string subject, ComplianceVerdict? from, ComplianceVerdict? to) => new()
    {
        CatalogueRelease = Catalogue.Release,
        ControlId = control,
        Entity = entity,
        Subject = subject,
        From = from,
        To = to,
        AtUtc = T0,
    };

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class Store(IReadOnlyList<ComplianceTransition> transitions) : IComplianceStore
    {
        public string? ReleaseAsked { get; private set; }

        public IReadOnlyList<ComplianceFinding> Findings => [];

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
            string? controlId = null, EntityId? entity = null)
        {
            ReleaseAsked = catalogueRelease;
            return new ComplianceTransitionsPage { Transitions = transitions, Truncated = false };
        }
    }

    [Fact]
    public void Two_subjects_of_the_same_control_and_entity_count_as_two_failing_in()
    {
        var transitions = new[]
        {
            Transition("esx-9.log-forwarding", Host1, "rule-a", ComplianceVerdict.Passing, ComplianceVerdict.Failing),
            Transition("esx-9.log-forwarding", Host1, "rule-b", ComplianceVerdict.Passing, ComplianceVerdict.Failing),
        };

        var service = new ComplianceService([Catalogue], new Store(transitions), new Clock(T0));

        var delta = service.DeltaSince(Catalogue, T0.AddDays(-7));

        Assert.Equal(2, delta.FailingIn);
    }

    [Fact]
    public void An_entity_level_transition_with_no_subject_counts_once()
    {
        var transitions = new[]
        {
            Transition("esx-9.log-forwarding", Host1, "", ComplianceVerdict.Failing, ComplianceVerdict.Passing),
        };

        var service = new ComplianceService([Catalogue], new Store(transitions), new Clock(T0));

        var delta = service.DeltaSince(Catalogue, T0.AddDays(-7));

        Assert.Equal(1, delta.FailingOut);
        Assert.Equal(0, delta.FailingIn);
    }

    [Fact]
    public void Entering_not_evaluated_is_counted_apart_from_failing()
    {
        var transitions = new[]
        {
            Transition("esx-9.log-forwarding", Host1, "", ComplianceVerdict.Passing, ComplianceVerdict.NotEvaluated),
        };

        var service = new ComplianceService([Catalogue], new Store(transitions), new Clock(T0));

        var delta = service.DeltaSince(Catalogue, T0.AddDays(-7));

        Assert.Equal(1, delta.NotEvaluatedIn);
        Assert.Equal(0, delta.FailingIn);
        Assert.Equal(0, delta.FailingOut);
    }

    [Fact]
    public void Leaving_the_evaluation_while_failing_counts_as_failing_out()
    {
        // to_verdict NULL: the subject left the evaluation (ADR/K1 remarks on
        // ComplianceTransition). Still "stopped failing" for the delta.
        var transitions = new[]
        {
            Transition("esx-9.log-forwarding", Host1, "rule-a", ComplianceVerdict.Failing, null),
        };

        var service = new ComplianceService([Catalogue], new Store(transitions), new Clock(T0));

        var delta = service.DeltaSince(Catalogue, T0.AddDays(-7));

        Assert.Equal(1, delta.FailingOut);
    }

    [Fact]
    public void The_delta_is_scoped_to_the_catalogues_own_release()
    {
        var store = new Store([]);
        var service = new ComplianceService([Catalogue], store, new Clock(T0));

        service.DeltaSince(Catalogue, T0.AddDays(-7));

        Assert.Equal(Catalogue.Release, store.ReleaseAsked);
    }
}
