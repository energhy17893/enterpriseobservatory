using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// P1: the posture screen's per-catalogue scorecard — ADR-0026's five states
/// (passed / failing / accepted / excepted / not evaluated) counted per
/// catalogue, with coverage over evaluable subjects only, and none of it
/// depending on which catalogue happens to be first in
/// <see cref="ComplianceService.Catalogues"/>.
/// </summary>
public class CatalogueScorecardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Scg = new()
    {
        Release = "803-20260612-01",
        Name = "vSphere 8 Security Configuration Guide",
        Controls = [new ComplianceControl { ControlId = "esxi-8.logs-remote", Title = "Remote logging" }],
    };

    private static ComplianceFinding Finding(
        string release, string control, string entity, ComplianceVerdict verdict, FindingAcceptance? accepted = null) =>
        new()
        {
            ControlId = control,
            CatalogueRelease = release,
            Entity = new EntityId(entity),
            Verdict = verdict,
            Expected = "n/a",
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
            Acceptance = accepted,
        };

    private static ComplianceService Service(
        IReadOnlyList<ComplianceCatalogue> catalogues, IReadOnlyList<ComplianceFinding> findings) =>
        new(catalogues, new ReadOnlyStore(findings, []), new StubClock(T0),
            ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production));

    [Fact]
    public void A_catalogues_scorecard_total_is_its_own_subject_count()
    {
        var findings = new[]
        {
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-1", ComplianceVerdict.Failing),
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-2", ComplianceVerdict.Passing),
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-3", ComplianceVerdict.NotEvaluated),
            Finding(
                Scg.Release, "esxi-8.logs-remote", "vc-1:host-4", ComplianceVerdict.Failing,
                new FindingAcceptance { By = "ertugrul", AtUtc = T0 }),
        };

        var view = ComplianceApi.Summary(
            Service([Scg, ContinuityCatalogue.Build(ContinuityCatalogue.Production)], findings));

        var scg = Assert.Single(view.Catalogues, c => c.Id == CatalogueDescriptor.ScgId);

        Assert.Equal(1, scg.Counts.Failing);
        Assert.Equal(1, scg.Counts.Passing);
        Assert.Equal(1, scg.Counts.NotEvaluated);
        Assert.Equal(1, scg.Counts.Accepted);
        Assert.Equal(0, scg.Counts.Excepted);

        // ADR-0026: NotEvaluated is always shown and never in the numerator,
        // but it is still one of the subjects the denominator counts.
        Assert.Equal(3, scg.EvaluableSubjects);
        Assert.Equal(4, scg.TotalSubjects);
        Assert.Equal(3.0 / 4.0, scg.Coverage);

        Assert.Equal(
            scg.Counts.Failing + scg.Counts.Passing + scg.Counts.Accepted + scg.Counts.Excepted +
            scg.Counts.NotEvaluated,
            scg.TotalSubjects);
    }

    [Fact]
    public void Two_catalogues_are_counted_independently()
    {
        var findings = new[]
        {
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-1", ComplianceVerdict.Failing),
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-2", ComplianceVerdict.Failing),
            Finding(ContinuityCatalogue.Release, ContinuityControls.CertEsxi, "vc-1:host-1", ComplianceVerdict.Passing),
        };

        var view = ComplianceApi.Summary(
            Service([Scg, ContinuityCatalogue.Build(ContinuityCatalogue.Production)], findings));

        var scg = Assert.Single(view.Catalogues, c => c.Id == CatalogueDescriptor.ScgId);
        var continuity = Assert.Single(view.Catalogues, c => c.Id == CatalogueDescriptor.ContinuityId);

        Assert.Equal(2, scg.Counts.Failing);
        Assert.Equal(0, scg.Counts.Passing);
        Assert.Equal(2, scg.TotalSubjects);

        Assert.Equal(0, continuity.Counts.Failing);
        Assert.Equal(1, continuity.Counts.Passing);
        Assert.Equal(1, continuity.TotalSubjects);

        Assert.Equal(CatalogueOwner.Broadcom, scg.Owner);
        Assert.Equal(CatalogueOwner.Product, continuity.Owner);
    }

    [Fact]
    public void The_scorecard_does_not_assume_the_vendor_guide_is_first_in_the_list()
    {
        var findings = new[]
        {
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-1", ComplianceVerdict.Failing),
        };

        // Continuity registered first, SCG second -- the reverse of
        // production's Program.cs order.
        var reversed = Service([ContinuityCatalogue.Build(ContinuityCatalogue.Production), Scg], findings);

        var view = ComplianceApi.Summary(reversed);

        Assert.Equal(2, view.Catalogues.Count);
        Assert.Equal(ComplianceSources.Scg, view.Source);
        Assert.Equal(Scg.Release, view.CatalogueRelease);

        var scg = Assert.Single(view.Catalogues, c => c.Owner == CatalogueOwner.Broadcom);
        Assert.Equal(1, scg.Counts.Failing);
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ReadOnlyStore(
        IReadOnlyList<ComplianceFinding> findings, IReadOnlyList<ComplianceWaiver> exceptions) : IComplianceStore
    {
        public IReadOnlyList<ComplianceFinding> Findings => findings;

        public IReadOnlyList<ComplianceWaiver> Exceptions => exceptions;

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
            string? controlId = null, EntityId? entity = null) => ComplianceTransitionsPage.Empty;
    }
}
