using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

/// <summary>
/// Proves <see cref="SimplivityReport"/> against instance documents built
/// from HPE's own published OmniStack REST schema
/// (Fixtures/SimpliVity/SIMPLIVITY-SOURCES.md).
/// </summary>
public sealed class SimplivityReportTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SimpliVity", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static SimplivityDocs SampleDocs() => new()
    {
        Version = Load("version.json"),
        Hosts = Load("hosts.json"),
        Clusters = Load("omnistack_clusters.json"),
        VirtualMachines = Load("virtual_machines.json"),
        Backups = Load("backups.json"),
    };

    private static bool HasLine(IReadOnlyList<string> lines, params string[] mustContainAll) =>
        lines.Any(l => mustContainAll.All(part => l.Contains(part, StringComparison.Ordinal)));

    [Fact]
    public void Reports_the_rest_api_version_from_the_unauthenticated_call()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "REST_API_Version", "1.25"));
        Assert.True(HasLine(lines, "SVTFS_Version", "5.2.0.1234"));
    }

    [Fact]
    public void Host_state_distribution_matches_the_sample()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "hosts read", "3"));
        Assert.True(HasLine(lines, "state", "ALIVE=2", "FAULTY=1"));
    }

    [Fact]
    public void Cluster_arbiter_and_upgrade_state_are_read_per_cluster()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "clusters read", "1"));
        Assert.True(HasLine(
            lines, "arbiter_connected=True", "arbiter_required=False", "arbiter_configured=True",
            "upgrade_state=SUCCESS_MIXED_VERSION"));
    }

    [Fact]
    public void Vm_ha_status_distribution_covers_the_four_sampled_values()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "VMs read", "4"));
        Assert.True(HasLine(
            lines, "ha_status", "DEGRADED=1", "OUT_OF_SCOPE=1", "SAFE=1", "SYNCING=1"));
    }

    [Fact]
    public void Backup_state_distribution_matches_the_sample()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "backups read", "3"));
        Assert.True(HasLine(lines, "state", "FAILED=1", "PROTECTED=2"));
    }

    [Fact]
    public void All_sampled_hypervisor_object_ids_look_like_a_vim25_moref()
    {
        // The sample was deliberately written with host-N ids -- see
        // SIMPLIVITY-SOURCES.md for why this is a shape check, not a live
        // measurement.
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.True(HasLine(lines, "hypervisor_object_id looks like a vim25 moRef", "3 of 3"));
    }

    [Fact]
    public void A_hypervisor_object_id_that_is_not_moref_shaped_is_not_counted()
    {
        var hosts = JsonDocument.Parse("""{"hosts":[{"hypervisor_object_id":"not-a-moref-at-all-really"}]}""")
            .RootElement.Clone();

        var lines = SimplivityReport.Generate(new SimplivityDocs { Hosts = hosts });

        Assert.True(HasLine(lines, "hypervisor_object_id looks like a vim25 moRef", "0 of 1"));
    }

    [Fact]
    public void Missing_documents_produce_empty_counts_not_exceptions()
    {
        var lines = SimplivityReport.Generate(new SimplivityDocs());

        Assert.True(HasLine(lines, "hosts read", "0"));
        Assert.True(HasLine(lines, "clusters read", "0"));
        Assert.True(HasLine(lines, "VMs read", "0"));
        Assert.True(HasLine(lines, "backups read", "0"));
    }

    [Fact]
    public void Token_acquisition_time_is_reported_only_in_live_mode()
    {
        var dry = SimplivityReport.Generate(SampleDocs());
        Assert.True(HasLine(dry, "no network in --dry"));

        var live = SimplivityReport.Generate(SampleDocs() with { TokenAcquisition = TimeSpan.FromMilliseconds(42) });
        Assert.True(HasLine(live, "acquired in", "42"));
    }

    [Fact]
    public void Never_prints_a_password()
    {
        var lines = SimplivityReport.Generate(SampleDocs());

        Assert.DoesNotContain(lines, l => l.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("4c1d2e3f-0a1b:HostSystem:host-21", "9x9x9x9x-9x9x:HostSystem:host-99")]
    [InlineData("host-2104", "host-9999")]
    [InlineData("domain-c7", "domain-x9")]
    public void An_id_is_reported_by_its_format_keeping_the_moref_words(string value, string shape) =>
        Assert.Equal(shape, SimplivityReport.IdShape(value));

    [Theory]
    [InlineData("kbvc01.kibar.net", "aaaa99.aaaaa.aaa")]
    [InlineData("10.5.1.23", "99.9.9.99")]
    public void A_management_system_name_keeps_its_format_and_loses_its_letters(string value, string shape) =>
        Assert.Equal(shape, SimplivityReport.NameShape(value));

    [Fact]
    public void Cluster_names_are_masked_under_mask()
    {
        var masked = SimplivityReport.Generate(SampleDocs(), mask: true);
        var plain = SimplivityReport.Generate(SampleDocs());

        Assert.DoesNotContain(masked, l => l.Contains("SVT-Cluster-1", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("SVT-Cluster-1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_short_of_the_total_says_so()
    {
        // 23 September 2026: 500 VMs read and the report said 500, which was
        // the page limit, not the estate.
        var vms = JsonDocument.Parse("""{"virtual_machines":[{"ha_status":"SAFE"}],"count":1234}""")
            .RootElement.Clone();

        var lines = SimplivityReport.Generate(new SimplivityDocs { VirtualMachines = vms });

        Assert.True(HasLine(lines, "VMs read", "1 of count 1234"));
    }
}
