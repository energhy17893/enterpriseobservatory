using System.Xml.Linq;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereSoapRequestsTests
{
    private static readonly VsphereCounter CpuUsage = new()
    {
        Id = 2, Group = "cpu", Name = "usage", Rollup = RollupType.Average, Unit = "percent",
    };

    private static readonly VsphereCounter CpuReady = new()
    {
        Id = 12, Group = "cpu", Name = "ready", Rollup = RollupType.Summation, Unit = "millisecond",
    };

    private static XDocument Parse(string soap) => XDocument.Parse(soap);

    private static IEnumerable<XElement> Named(XDocument document, string localName) =>
        document.Descendants().Where(e => e.Name.LocalName == localName);

    [Fact]
    public void Every_request_is_well_formed_xml()
    {
        // The failure this prevents is a malformed envelope that the server
        // rejects with a generic fault, which then looks like a connectivity
        // problem rather than a bug in our own request.
        string[] requests =
        [
            VsphereSoapRequests.RetrieveServiceContent(),
            VsphereSoapRequests.Login("SessionManager", "svc-readonly@vsphere.local", "p"),
            VsphereSoapRequests.Logout("SessionManager"),
            VsphereSoapRequests.QueryMaxQueryMetrics("VpxSettings"),
            VsphereSoapRequests.QueryPerfCounterByLevel("PerfMgr"),
            VsphereSoapRequests.QueryAvailablePerfMetric("PerfMgr", "host-1", "HostSystem", 20),
            VsphereSoapRequests.QueryPerf("PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3),
            VsphereSoapRequests.CreateContainerView("ViewManager", "group-d1", ["HostSystem"]),
            VsphereSoapRequests.DestroyView("session-view-1"),
            VsphereSoapRequests.ContinueRetrievePropertiesEx("propertyCollector", "token-1"),
        ];

        foreach (var request in requests)
        {
            var exception = Record.Exception(() => Parse(request));
            Assert.True(exception is null, $"Malformed request: {exception?.Message}");
        }
    }

    [Fact]
    public void The_counter_catalogue_is_requested_at_the_level_that_returns_all_of_it()
    {
        // An earlier version used a different call and came back with 28
        // counters where a vCenter defines several hundred.
        var soap = VsphereSoapRequests.QueryPerfCounterByLevel("PerfMgr", level: 4);

        Assert.Equal("4", Assert.Single(Named(Parse(soap), "level")).Value);
        Assert.Single(Named(Parse(soap), "QueryPerfCounterByLevel"));
    }

    [Fact]
    public void A_performance_query_carries_one_spec_per_entity()
    {
        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["host-1", "host-2", "host-3"], "HostSystem", [CpuUsage, CpuReady], 20, 3);

        Assert.Equal(3, Named(Parse(soap), "querySpec").Count());
    }

    [Fact]
    public void Every_requested_counter_appears_in_every_spec()
    {
        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["host-1", "host-2"], "HostSystem", [CpuUsage, CpuReady], 20, 3);

        // 2 entities x 2 counters
        Assert.Equal(4, Named(Parse(soap), "metricId").Count());
    }

    [Fact]
    public void Every_device_series_is_requested_so_the_parser_can_choose()
    {
        // instance "*" returns the per-device series and the aggregate; picking
        // between them is the parser's decision, not the request's.
        var soap = VsphereSoapRequests.QueryPerf("PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3);

        Assert.All(Named(Parse(soap), "instance"), e => Assert.Equal("*", e.Value));
    }

    [Fact]
    public void More_than_one_sample_is_requested()
    {
        // The previous product asked for exactly one 20-second sample, so a
        // spike between polls was simply not seen. Asking for a few costs
        // nothing and the parser still reports the latest as current state.
        var soap = VsphereSoapRequests.QueryPerf("PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3);

        Assert.Equal("3", Assert.Single(Named(Parse(soap), "maxSample")).Value);
    }

    [Fact]
    public void The_interval_travels_with_the_request()
    {
        // Datastores have no real-time feed, so they must be asked at 300s.
        var realtime = VsphereSoapRequests.QueryPerf("PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3);
        var historical = VsphereSoapRequests.QueryPerf("PerfMgr", ["ds-1"], "Datastore", [CpuUsage], 300, 3);

        Assert.Equal("20", Assert.Single(Named(Parse(realtime), "intervalId")).Value);
        Assert.Equal("300", Assert.Single(Named(Parse(historical), "intervalId")).Value);
    }

    [Fact]
    public void A_property_request_names_every_path_it_wants()
    {
        var soap = VsphereSoapRequests.RetrievePropertiesEx(
            "propertyCollector", "group-d1", "view-1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["HostSystem"] = ["name", "summary.overallStatus", "runtime.connectionState"],
                ["Datastore"] = ["name", "summary.capacity"],
            },
            maxObjects: 100);

        var paths = Named(Parse(soap), "pathSet").Select(e => e.Value).ToList();

        Assert.Contains("summary.overallStatus", paths);
        Assert.Contains("runtime.connectionState", paths);
        Assert.Contains("summary.capacity", paths);
        Assert.Equal(2, Named(Parse(soap), "propSet").Count());
    }

    [Fact]
    public void A_property_request_bounds_the_page_size()
    {
        // Unbounded retrieval on a large inventory can be refused outright.
        var soap = VsphereSoapRequests.RetrievePropertiesEx(
            "propertyCollector", "group-d1", "view-1",
            new Dictionary<string, IReadOnlyList<string>> { ["HostSystem"] = ["name"] },
            maxObjects: 250);

        Assert.Equal("250", Assert.Single(Named(Parse(soap), "maxObjects")).Value);
    }

    // --- escaping ---------------------------------------------------------

    [Fact]
    public void A_password_containing_xml_punctuation_does_not_break_the_request()
    {
        // Passwords routinely contain & and <. An unescaped one produces a
        // malformed request that fails in a way nobody traces back to a
        // punctuation mark — it just looks like the credentials are wrong.
        var soap = VsphereSoapRequests.Login("SessionManager", "svc@vsphere.local", "a&b<c>\"d\"");

        var exception = Record.Exception(() => Parse(soap));
        Assert.Null(exception);

        Assert.Equal("a&b<c>\"d\"", Assert.Single(Named(Parse(soap), "password")).Value);
    }

    [Fact]
    public void An_object_name_containing_xml_punctuation_survives_a_round_trip()
    {
        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["host-<1>&2"], "HostSystem", [CpuUsage], 20, 3);

        Assert.Equal("host-<1>&2", Assert.Single(Named(Parse(soap), "entity")).Value);
    }

    [Fact]
    public void An_entity_type_is_carried_as_an_attribute_not_guessed()
    {
        var soap = VsphereSoapRequests.QueryPerf("PerfMgr", ["vm-1"], "VirtualMachine", [CpuReady], 20, 3);

        var entity = Assert.Single(Named(Parse(soap), "entity"));
        Assert.Equal("VirtualMachine", entity.Attribute("type")?.Value);
    }
}
