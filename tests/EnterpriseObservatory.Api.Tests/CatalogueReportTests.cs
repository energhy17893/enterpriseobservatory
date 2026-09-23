using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// P2: <c>GET /api/reports/compliance</c> takes a catalogue id from P1's
/// registry (<see cref="CatalogueDescriptor.Id"/>) instead of always
/// reporting on the first registered catalogue -- the last consumer of that
/// assumption, removed with <c>ComplianceService.Catalogue</c>. Registered in reverse
/// (continuity first, SCG second, the opposite of production's Program.cs
/// order) so nothing here can pass by accident on list position.
/// </summary>
public class CatalogueReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Scg = new()
    {
        Release = "803-20260612-01",
        Name = "vSphere 8 Security Configuration Guide",
        Controls = [new ComplianceControl { ControlId = "esxi-8.logs-remote", Title = "Remote logging" }],
    };

    private static ComplianceFinding Finding(string release, string control, string entity, ComplianceVerdict verdict) => new()
    {
        ControlId = control,
        CatalogueRelease = release,
        Entity = new EntityId(entity),
        Verdict = verdict,
        Expected = "n/a",
        FirstSeenUtc = T0,
        LastEvaluatedUtc = T0,
    };

    private static ComplianceService Service()
    {
        var findings = new[]
        {
            Finding(Scg.Release, "esxi-8.logs-remote", "vc-1:host-1", ComplianceVerdict.Failing),
            Finding(ContinuityCatalogue.Release, ContinuityControls.CertEsxi, "vc-1:host-1", ComplianceVerdict.Passing),
        };

        // Continuity registered first, SCG second -- the reverse of
        // production's Program.cs order (CatalogueScorecardTests does the
        // same for the scorecard; this proves the report doesn't reintroduce
        // the assumption P1 removed from it).
        return new ComplianceService(
            [ContinuityCatalogue.Build(ContinuityCatalogue.Production), Scg],
            new ReadOnlyStore(findings),
            new StubClock(T0),
            ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production));
    }

    [Fact]
    public void The_report_reads_the_catalogue_named_by_id_not_by_position()
    {
        var scgReport = ComplianceApi.Report(Service(), CatalogueDescriptor.ScgId, null, null, null, null);

        Assert.NotNull(scgReport);
        Assert.Equal(Scg.Name, scgReport.CatalogueName);
        Assert.Equal(Scg.Release, scgReport.CatalogueRelease);
        Assert.Single(scgReport.Findings, f => f.ControlId == "esxi-8.logs-remote");
    }

    [Fact]
    public void Every_registered_catalogue_produces_its_own_report()
    {
        var continuityReport = ComplianceApi.Report(
            Service(), CatalogueDescriptor.ContinuityId, null, null, null, null);

        Assert.NotNull(continuityReport);
        Assert.Equal(ContinuityCatalogue.Name, continuityReport.CatalogueName);
        Assert.Equal(ContinuityCatalogue.Release, continuityReport.CatalogueRelease);
        Assert.Single(continuityReport.Findings, f => f.ControlId == ContinuityControls.CertEsxi);

        // Neither report picked up the other catalogue's finding.
        Assert.DoesNotContain(continuityReport.Findings, f => f.ControlId == "esxi-8.logs-remote");
    }

    [Fact]
    public void An_unknown_catalogue_id_resolves_to_no_report()
    {
        Assert.Null(ComplianceApi.Report(Service(), "not-a-registered-catalogue", null, null, null, null));
    }

    [Fact]
    public void Omitting_the_catalogue_id_resolves_to_the_vendor_guide_by_ownership_not_position()
    {
        var report = ComplianceApi.Report(Service(), null, null, null, null, null);

        Assert.NotNull(report);
        Assert.Equal(Scg.Name, report.CatalogueName);
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ReadOnlyStore(IReadOnlyList<ComplianceFinding> findings) : IComplianceStore
    {
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
            string? controlId = null, EntityId? entity = null) => ComplianceTransitionsPage.Empty;
    }
}
