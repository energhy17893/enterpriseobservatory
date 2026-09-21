using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using Microsoft.Extensions.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The shipped editions of Broadcom's guide, loaded exactly as the host loads them.
/// </summary>
/// <remarks>
/// The counts are literals on purpose. They are what the published files say
/// today — vSphere 8.0: 156 controls, 75 non-default; VCF 9.1: 260 controls,
/// 160 non-default — and a re-vendored file that changed them should be an
/// event somebody acknowledges here, not a silent shift in what the
/// compliance screen claims to cover.
/// </remarks>
public class ComplianceCatalogueSourceTests
{
    private static string Edition(string name) =>
        Path.Combine(AppContext.BaseDirectory, "catalogues", "scg", name);

    [Fact]
    public void The_vsphere_8_edition_loads_with_its_release()
    {
        var catalogue = ComplianceCatalogueSource.LoadDirectory(Edition("vsphere-8.0"), "vsphere-8.0");

        Assert.Null(catalogue.Problem);
        Assert.Equal("803-20260612-01", catalogue.Release);
        Assert.Equal(75, catalogue.Controls.Count);
        Assert.Equal(81, catalogue.DefaultControlsSkipped);

        var evaluated = ComplianceEvaluation.Bind(catalogue).Where(b => b.IsEvaluated).Select(b => b.Control.ControlId);

        Assert.Equal(
            [
                "esxi-8.deactivate-cim",
                "esxi-8.deactivate-snmp",
                "esxi-8.lockdown-mode",
                "esxi-8.logs-audit-local",
                "esxi-8.logs-audit-remote",
                "esxi-8.logs-remote",
                "esxi-8.network-reject-forged-transmit-standardswitch",
                "esxi-8.network-reject-mac-changes-standardswitch",
                "esxi-8.shell-interactive-timeout",
                "esxi-8.shell-timeout",
                "esxi-8.timekeeping-services",
                "esxi-8.timekeeping-sources",
            ],
            evaluated.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_vcf_9_1_edition_loads_with_its_release()
    {
        var catalogue = ComplianceCatalogueSource.LoadDirectory(Edition("vcf-9.1"), "vcf-9.1");

        Assert.Null(catalogue.Problem);
        Assert.Equal("910-20260612-01", catalogue.Release);
        Assert.Equal(160, catalogue.Controls.Count);
        Assert.Equal(100, catalogue.DefaultControlsSkipped);

        var bound = ComplianceEvaluation.Bind(catalogue);

        Assert.Equal(
            [
                "esx-9.ad-admin-group-name",
                "esx-9.lockdown-mode",
                "esx-9.log-audit-forwarding",
                "esx-9.log-audit-local",
                "esx-9.log-forwarding",
                "esx-9.network-standard-reject-forged-transmit",
                "esx-9.network-standard-reject-mac-changes",
                "esx-9.shell-interactive-timeout",
                "esx-9.shell-timeout",
                "esx-9.snmp",
                "esx-9.time",
            ],
            bound.Where(b => b.IsEvaluated).Select(b => b.Control.ControlId).Order(StringComparer.Ordinal));

        // Every other control is carried, with a reason, rather than dropped.
        Assert.All(
            bound.Where(b => !b.IsEvaluated),
            b => Assert.StartsWith("No data collected", b.NotEvaluatedReason, StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_edition_is_the_one_the_reference_estate_runs()
    {
        var catalogue = ComplianceCatalogueSource.Load(new ConfigurationBuilder().Build());

        Assert.Equal("vsphere-8.0", catalogue.Name);
        Assert.Null(catalogue.Problem);
    }

    [Fact]
    public void A_missing_directory_is_carried_as_a_problem_rather_than_stopping_the_service()
    {
        var catalogue = ComplianceCatalogueSource.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Compliance:Catalogue"] = Path.Combine(Path.GetTempPath(), "eo-no-such-" + Guid.NewGuid()),
            })
            .Build());

        Assert.NotNull(catalogue.Problem);
        Assert.Empty(catalogue.Controls);
    }

    [Fact]
    public void A_downloaded_edition_without_a_version_is_refused()
    {
        var directory = Directory.CreateTempSubdirectory("eo-scg-");

        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "controls.csv"),
                "SCG ID,Configuration Parameter,Is the Default?\nesx-9.x,A,NO\n");

            var catalogue = ComplianceCatalogueSource.LoadDirectory(directory.FullName, "downloaded");

            Assert.Contains("VERSION", catalogue.Problem, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
