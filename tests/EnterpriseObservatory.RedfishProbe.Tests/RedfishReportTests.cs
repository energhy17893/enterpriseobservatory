using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

/// <summary>
/// Proves <see cref="RedfishReport"/> against recorded DMTF sample JSON
/// (Fixtures/Redfish/DMTF-SOURCES.md), the way M6.0b's definition of done
/// asks: the parsers exercised before any iLO credential exists.
/// </summary>
public sealed class RedfishReportTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Redfish", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static RedfishDocs SampleDocs() => new()
    {
        Manager = Load("manager.json"),
        Power = Load("power.json"),
        Thermal = Load("thermal.json"),
        System = Load("computersystem.json"),
        Controllers = [Load("storage.json")],
        Drives = [Load("drive-1.json")],
        Memory = [Load("memory-dimm1.json")],
        FirmwareInventoryCollection = Load("firmwareinventory-collection.json"),
        LogEntries = Load("logentries.json"),
    };

    private static bool HasLine(IReadOnlyList<string> lines, params string[] mustContainAll) =>
        lines.Any(l => mustContainAll.All(part => l.Contains(part, StringComparison.Ordinal)));

    [Fact]
    public void Reports_the_managers_model_and_firmware_version_in_full()
    {
        // A product generation and firmware version are not
        // customer-identifying, so they print in full -- unlike
        // SerialNumber/UUID below, which mask on request.
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "Managers/1.Model", "Joo Janta 200"));
        Assert.True(HasLine(lines, "Managers/1.FirmwareVersion", "1.45.455b66-rev4"));
    }

    [Fact]
    public void Firmware_version_and_model_print_unmasked_even_under_mask()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: true);

        Assert.True(HasLine(lines, "Managers/1.FirmwareVersion", "1.45.455b66-rev4"));
    }

    [Fact]
    public void Detects_the_ilo_generation_from_firmware_version_and_prints_it_even_under_mask()
    {
        var docs = SampleDocs() with { Manager = Load("manager-ilo6.json") };

        var masked = RedfishReport.Generate(docs, mask: true);
        var unmasked = RedfishReport.Generate(docs, mask: false);

        Assert.True(HasLine(masked, "iLO generation", "iLO 6"));
        Assert.True(HasLine(unmasked, "iLO generation", "iLO 6"));
    }

    [Fact]
    public void An_unrecognized_model_and_firmware_report_the_generation_as_unknown()
    {
        // The generic DMTF manager sample has neither "iLO 5" nor "iLO 6"
        // anywhere in it -- the parser must say so, not guess.
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "iLO generation", "unknown"));
    }

    [Fact]
    public void Reports_the_storage_controller_count()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "storage controllers read", "1"));
    }

    [Fact]
    public void Drive_health_distribution_matches_the_sample()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "drive Status.Health", "OK=1"));
    }

    [Fact]
    public void Thermal_redundancy_is_present_but_power_redundancy_is_absent_in_the_sample()
    {
        // The DMTF public-rackmount1 sample nests a Redundancy[] under Thermal
        // (per-fan and at the root) but never under Power -- the report must
        // say so per-resource, not assume both behave the same way.
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "Chassis/1/Power", "Redundancy[]", "absent"));
        Assert.True(HasLine(lines, "Chassis/1/Thermal", "Redundancy[]", "present"));
    }

    [Fact]
    public void Counts_one_power_supply_and_two_fans()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "PSU", "count", "1", "status: Warning=1"));
        Assert.True(HasLine(lines, "fan", "count", "2", "status: OK=2"));
    }

    [Fact]
    public void Drive_failure_predicted_distribution_matches_the_sample()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "drives read", "1"));
        Assert.True(HasLine(lines, "FailurePredicted = false", "1"));
        Assert.True(HasLine(lines, "FailurePredicted = true", "0"));
    }

    [Fact]
    public void Hpe_oem_extensions_are_absent_from_the_generic_dmtf_sample()
    {
        // This is the point of using an unmodified DMTF mockup: Oem.Hpe.* is
        // HPE-specific and must not silently appear from somewhere in the
        // parser. Every one of these reads ABSENT/absent against this fixture.
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "Oem.Hpe.WearStatus", "0 of 1"));
        Assert.True(HasLine(lines, "Oem.Hpe.DIMMStatus", "0 of 1"));
        Assert.True(HasLine(lines, "Oem.Hpe.AggregateHealthStatus", "ABSENT"));
        Assert.True(HasLine(lines, "AgentlessManagementService", "absent"));
        Assert.True(HasLine(lines, "Oem.Hpe.Severity", "0 of 2"));
        Assert.True(HasLine(lines, "Oem.Hpe.Repaired", "0 of 2"));
    }

    [Fact]
    public void Iml_reports_both_entries_and_the_newer_one_as_newest()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "entries", "2"));
        Assert.True(HasLine(lines, "newest Created", "2012-03-07T14:45:00"));
    }

    [Fact]
    public void Identity_fields_are_present_and_masked_on_request()
    {
        var unmasked = RedfishReport.Generate(SampleDocs(), mask: false);
        Assert.True(HasLine(unmasked, "SerialNumber", "437XR1138R2"));

        var masked = RedfishReport.Generate(SampleDocs(), mask: true);
        Assert.DoesNotContain(masked, l => l.Contains("437XR1138R2", StringComparison.Ordinal));
    }

    [Fact]
    public void Firmware_inventory_counts_the_declared_total_and_the_member_list_separately()
    {
        // The sample's own Members@odata.count (2) disagrees with its
        // Members[] length (3) -- a real quirk of the recorded document, not
        // something the parser should paper over by picking one.
        var lines = RedfishReport.Generate(SampleDocs(), mask: false);

        Assert.True(HasLine(lines, "Members@odata.count", "2"));
        Assert.True(HasLine(lines, "Members[] length", "3"));
    }

    [Fact]
    public void Missing_documents_are_reported_as_not_read_rather_than_zero()
    {
        var docs = new RedfishDocs();
        var lines = RedfishReport.Generate(docs, mask: false);

        Assert.True(HasLine(lines, "Managers/1", "NOT READ"));
        Assert.True(HasLine(lines, "UpdateService/FirmwareInventory", "NOT READ"));
        Assert.True(HasLine(lines, "LogServices/IML/Entries", "NOT READ"));
        Assert.True(HasLine(lines, "Systems/1", "NOT READ"));
    }

    [Fact]
    public void Never_prints_a_password_or_the_env_var_name_carrying_one()
    {
        var lines = RedfishReport.Generate(SampleDocs(), mask: true);

        Assert.DoesNotContain(lines, l => l.Contains("password", StringComparison.OrdinalIgnoreCase));
    }
}
