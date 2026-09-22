using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// K3: the compliance views say which catalogue a control comes from (the
/// source) apart from what its expectation rests on (the citation).
/// </summary>
public class ComplianceSourceTests
{
    private static readonly ComplianceCatalogue Scg = new()
    {
        Release = "803-20260612-01",
        Name = "vSphere 8 Security Configuration Guide",
        Controls =
        [
            new ComplianceControl { ControlId = "esxi-8.logs-remote", Title = "Remote logging" },
        ],
    };

    private static ComplianceService Service() => new(
        [Scg, ContinuityCatalogue.Build(ContinuityCatalogue.Production)],
        new EmptyStore(),
        new StubClock(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero)),
        ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production));

    [Fact]
    public void Every_control_carries_its_catalogue_source_apart_from_its_citation()
    {
        var view = ComplianceApi.Summary(Service());

        var scg = Assert.Single(view.Controls, c => c.ControlId == "esxi-8.logs-remote");
        Assert.Equal(ComplianceSources.Scg, scg.Source);
        Assert.Equal(string.Empty, scg.Citation);

        var cert = Assert.Single(view.Controls, c => c.ControlId == ContinuityControls.CertEsxi);
        Assert.Equal(ComplianceSources.Continuity, cert.Source);
        Assert.Equal("Tool default (vCheck 60 days)", cert.Citation);

        Assert.Equal(
            [ComplianceSources.Scg, ComplianceSources.Continuity],
            view.Controls.Select(c => c.Source).Distinct());
        Assert.Equal(ComplianceSources.Scg, view.Source);
    }

    [Fact]
    public void The_two_sources_are_the_broadcom_guide_and_the_products_own_catalogue()
    {
        Assert.Equal("Broadcom SCG", ComplianceSources.Scg);
        Assert.Equal("eo-continuity", ComplianceSources.Continuity);
        Assert.Equal(ComplianceSources.Continuity, ComplianceSources.Of(ContinuityCatalogue.Name));
        Assert.Equal(ComplianceSources.Scg, ComplianceSources.Of(Scg.Name));
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptyStore : IComplianceStore
    {
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
            string? controlId = null, EntityId? entity = null) => ComplianceTransitionsPage.Empty;
    }
}
