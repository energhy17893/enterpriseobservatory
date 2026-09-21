using EnterpriseObservatory.Application.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Reading Broadcom's controls file as data.
/// </summary>
/// <remarks>
/// A hand-made fixture in the published shape, not the real file: the tests
/// are about the parser's decisions — what is kept, what is refused, what a
/// quoted paragraph does — and each one needs a row built to provoke it. The
/// shipped editions are loaded whole in the host's tests.
/// </remarks>
public class ComplianceCatalogueTests
{
    // The 9.1 column names, in a shortened order, with the traps the real file
    // has: a byte order mark, a quoted multi-line field, a doubled quote, and
    // a trailing blank row.
    private const string Fixture =
        "\uFEFFSCG ID,Component Name,Description/Title,Discussion,Implementation Priority," +
        "Configuration Parameter,Installation Default Value,Baseline Suggested Value," +
        "Is the Default?,PowerCLI Command Assessment\r\n" +
        "esx-9.log-forwarding,ESX,Forward logs,\"Logs die with the host.\r\nSecond line, with a comma.\"," +
        "P0,Syslog.global.logHost,Undefined,Site-Specific Log Server,NO," +
        "\"Get-VMHost | Get-AdvancedSetting Syslog.global.logHost\"\r\n" +
        "esx-9.shell-timeout,ESX,Shell timeout,,P0,UserVars.ESXiShellTimeOut,0,600,NO,\r\n" +
        "esx-9.already-fine,ESX,Default is fine,,P1,Some.Setting,1,1,YES,\r\n" +
        "esx-9.ad-admin-group-name,ESX,Admin group,,P0,Config.HostAgent.plugins.hostsvc.esxAdminsGroup," +
        "\"\"\"ESX Admins\"\"\",Site-Specific group,NO,\r\n" +
        "esx-9.lockdown-mode,ESX,Lockdown,,P0,N/A,lockdownDisabled,lockdownNormal,NO,\r\n" +
        ",,,,,,,,,\r\n";

    private static Domain.Compliance.ComplianceCatalogue Parse(string csv = Fixture) =>
        ScgCatalogueParser.Parse(csv, "910-20260612-01", "vcf-9.1");

    [Fact]
    public void Only_controls_whose_default_misses_the_baseline_are_kept()
    {
        var catalogue = Parse();

        Assert.Equal(
            ["esx-9.log-forwarding", "esx-9.shell-timeout", "esx-9.ad-admin-group-name", "esx-9.lockdown-mode"],
            catalogue.Controls.Select(c => c.ControlId));
        Assert.Equal(1, catalogue.DefaultControlsSkipped);
    }

    [Fact]
    public void The_release_travels_with_the_catalogue()
    {
        var catalogue = Parse();

        Assert.Equal("910-20260612-01", catalogue.Release);
        Assert.Equal("vcf-9.1", catalogue.Name);
        Assert.Null(catalogue.Problem);
    }

    [Fact]
    public void A_quoted_paragraph_with_line_breaks_stays_one_control()
    {
        var control = Parse().Controls[0];

        Assert.Equal("Syslog.global.logHost", control.Parameter);
        Assert.Equal("P0", control.Priority);
        Assert.Equal("Get-VMHost | Get-AdvancedSetting Syslog.global.logHost", control.Assessment);
    }

    [Fact]
    public void A_doubled_quote_is_one_quote()
    {
        var control = Parse().Controls.Single(c => c.ControlId == "esx-9.ad-admin-group-name");

        Assert.Equal("\"ESX Admins\"", control.InstallationDefault);
    }

    [Fact]
    public void The_vsphere_8_column_names_are_understood()
    {
        // The 8.0 edition calls the component column "Component" and orders
        // everything differently; columns are found by name.
        const string eight =
            "SCG ID,Product,Component,Implementation Priority,Description/Title,Configuration Parameter," +
            "Installation Default Value,Baseline Suggested Value,Is the Default?,,,\n" +
            "esxi-8.logs-remote,VMware vSphere,VMware ESXi,P0,Remote logging,Syslog.global.logHost," +
            "Undefined,Site-Specific Log Server,NO,,,\n";

        var control = Assert.Single(ScgCatalogueParser.Parse(eight, "803-20260612-01", "vsphere-8.0").Controls);

        Assert.Equal("esxi-8.logs-remote", control.ControlId);
        Assert.Equal("VMware ESXi", control.Component);
        Assert.Equal("Syslog.global.logHost", control.Parameter);
    }

    [Fact]
    public void A_file_missing_the_default_column_is_refused_by_name()
    {
        // Read as empty, the file would load with no controls, which looks
        // exactly like a clean estate.
        var refusal = Assert.Throws<FormatException>(() => Parse(
            "SCG ID,Configuration Parameter\nesx-9.x,Some.Setting\n"));

        Assert.Contains("Is the Default?", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_listed_twice_is_refused()
    {
        var refusal = Assert.Throws<FormatException>(() => Parse(
            "SCG ID,Configuration Parameter,Is the Default?\nesx-9.x,A,NO\nesx-9.x,B,NO\n"));

        Assert.Contains("esx-9.x", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_default_marker_is_kept_rather_than_dropped()
    {
        var catalogue = Parse("SCG ID,Configuration Parameter,Is the Default?\nesx-9.x,A,Site-Specific\n");

        Assert.Single(catalogue.Controls);
    }

    [Fact]
    public void A_file_that_ends_inside_quotes_is_refused()
    {
        Assert.Throws<FormatException>(() => Parse(
            "SCG ID,Configuration Parameter,Is the Default?\nesx-9.x,\"A,NO\n"));
    }
}
