using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

/// <summary>
/// Proves the per-path depth override reaches <c>Oem.Hpe.*</c> -- a live run
/// found the default depth (2) stopped one level short, so
/// <c>Oem.Hpe.AggregateHealthStatus</c> printed only as "Hpe &lt;object&gt;
/// x40", never its own fields.
/// </summary>
public sealed class ShapeDumpTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Redfish", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string Capture(JsonElement root, int depth)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            ShapeDump.Print("label", root, depth);
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void Default_depth_stops_one_level_short_of_AggregateHealthStatus_children()
    {
        var system = Load("computersystem-hpe-oem.json");
        var output = Capture(system, depth: 2);

        Assert.Contains("Hpe <object>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AggregateHealthStatus", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentlessManagementService", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deeper_per_path_depth_surfaces_AggregateHealthStatus_and_its_children()
    {
        var system = Load("computersystem-hpe-oem.json");
        var output = Capture(system, depth: 6);

        Assert.Contains("AggregateHealthStatus <object>", output, StringComparison.Ordinal);
        Assert.Contains("AgentlessManagementService <string>", output, StringComparison.Ordinal);
        Assert.Contains("BiosOrHardwareHealth <object>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_deeper_depth_also_reaches_PowerSupplies_Oem_Hpe()
    {
        var power = Load("power-hpe-oem.json");
        var output = Capture(power, depth: 6);

        Assert.Contains("PowerSupplyStatus <object>", output, StringComparison.Ordinal);
        Assert.Contains("LineInputStatus <string>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_depth_does_not_reach_PowerSupplies_Oem_Hpe()
    {
        var power = Load("power-hpe-oem.json");
        var output = Capture(power, depth: 2);

        Assert.DoesNotContain("PowerSupplyStatus", output, StringComparison.Ordinal);
    }
}
