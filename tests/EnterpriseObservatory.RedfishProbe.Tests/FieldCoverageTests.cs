using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

public class FieldCoverageTests
{
    [Fact]
    public void Filled_absent_and_null_are_counted_apart_with_enum_values()
    {
        using var page = JsonDocument.Parse("""
            { "virtual_machines": [
                { "id": "a", "ha_status": "SAFE" },
                { "id": "b", "ha_status": "DEGRADED" },
                { "id": "c", "ha_status": null },
                { "id": "d" } ] }
            """);

        var lines = FieldCoverage.Lines("virtual_machines", page.RootElement, ["id", "ha_status"]).ToList();

        Assert.Equal("  virtual_machines (4 objects)", lines[0]);
        Assert.Contains("filled 4/4, absent 0, null 0", lines[1], StringComparison.Ordinal);
        Assert.Contains("filled 2/4, absent 1, null 1", lines[2], StringComparison.Ordinal);
        Assert.Contains("SAFE=1", lines[2], StringComparison.Ordinal);
        Assert.Contains("DEGRADED=1", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_field_the_collector_reads_is_measured()
    {
        var vms = FieldCoverage.Read.Single(r => r.Endpoint == "virtual_machines").Fields;
        var clusters = FieldCoverage.Read.Single(r => r.Endpoint == "omnistack_clusters").Fields;

        Assert.Contains("ha_status", vms);
        Assert.Contains("arbiter_connected", clusters);
    }
}
