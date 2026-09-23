using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

public class RedfishDumpTests
{
    [Fact]
    public void Masks_identifying_values_keeping_names_types_and_formats()
    {
        var system = JsonNode.Parse("""
            {
              "SerialNumber": "CZJ92403X7",
              "UUID": "37393150-3636-5A43-4A39-323430335837",
              "HostName": "alhcesx04",
              "PowerState": "On",
              "ProcessorSummary": { "Count": 2 },
              "Oem": { "Hpe": { "HostCorrelation": {
                "HostFQDN": "alhcesx04.kibar.net",
                "IPAddress": [ "10.20.30.40" ],
                "HostMACAddress": [ "94:40:c9:aa:bb:cc" ] } } }
            }
            """)!;
        var iml = JsonNode.Parse("""
            { "Message": "Server CZJ92403X7 at 10.20.30.40 alhcesx04", "Version": "10.54.7.0",
              "Uuid": "37393150-3636-5a43-4a39-323430335837" }
            """)!;

        var masker = new DumpMasker();
        masker.AddHost("KibarHolding-alhcesx04-ilo.kibar.net");
        masker.Collect(system);
        masker.Collect(iml);
        masker.Apply(system);
        masker.Apply(iml);

        var serial = system["SerialNumber"]!.GetValue<string>();
        Assert.NotEqual("CZJ92403X7", serial);
        Assert.Matches("^[A-Z]{3}[0-9]{5}[A-Z][0-9]$", serial);

        var uuid = system["UUID"]!.GetValue<string>();
        Assert.NotEqual("37393150-3636-5A43-4A39-323430335837", uuid);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", uuid);
        Assert.Equal(uuid, iml["Uuid"]!.GetValue<string>(), ignoreCase: true); // the fold key survives masking

        var host = system["HostName"]!.GetValue<string>();
        Assert.StartsWith("host-", host);
        var correlation = system["Oem"]!["Hpe"]!["HostCorrelation"]!;
        Assert.Equal(host + ".example.invalid", correlation["HostFQDN"]!.GetValue<string>());
        Assert.Matches(@"^192\.0\.2\.\d+$", correlation["IPAddress"]![0]!.GetValue<string>());
        Assert.DoesNotMatch("(?i)aa:bb:cc", correlation["HostMACAddress"]![0]!.GetValue<string>());

        var message = iml["Message"]!.GetValue<string>();
        Assert.DoesNotMatch("CZJ92403X7|10\\.20\\.30\\.40|alhcesx04", message);
        Assert.Contains(serial, message);
        Assert.Equal("10.54.7.0", iml["Version"]!.GetValue<string>());

        Assert.Equal("On", system["PowerState"]!.GetValue<string>());
        Assert.Equal(2, system["ProcessorSummary"]!["Count"]!.GetValue<int>());
        Assert.Equal(
            ["SerialNumber", "UUID", "HostName", "PowerState", "ProcessorSummary", "Oem"],
            system.AsObject().Select(p => p.Key));
        Assert.DoesNotMatch("(?i)kibar", system.ToJsonString());
    }

    [Theory]
    [InlineData("/redfish/v1/Systems/1", "Systems_1.json")]
    [InlineData("/redfish/v1/Systems/1/Storage/DE07C000/Drives/0", "Systems_1_Storage_DE07C000_Drives_0.json")]
    [InlineData("/redfish/v1/Systems/1/LogServices/IML/Entries?$top=5", "Systems_1_LogServices_IML_Entries_top.json")]
    [InlineData("/redfish/v1/Systems/1/LogServices/IML/Entries?$filter=x", "Systems_1_LogServices_IML_Entries_filter.json")]
    public void Names_one_file_per_path(string path, string file) =>
        Assert.Equal(file, RedfishDump.FileNameOf(path));
}
