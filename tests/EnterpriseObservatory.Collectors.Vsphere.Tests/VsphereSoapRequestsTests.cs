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
        //
        // This test passed while every datastore in a live estate went
        // unmeasured. Sending the right interval is half the request; the other
        // half is below, and checking one half is how a request can be provably
        // correct and still return nothing.
        var realtime = VsphereSoapRequests.QueryPerf("PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3);
        var historical = VsphereSoapRequests.QueryPerf("PerfMgr", ["ds-1"], "Datastore", [CpuUsage], 300, 3);

        Assert.Equal("20", Assert.Single(Named(Parse(realtime), "intervalId")).Value);
        Assert.Equal("300", Assert.Single(Named(Parse(historical), "intervalId")).Value);
    }

    [Fact]
    public void A_historical_query_carries_the_time_range_it_needs()
    {
        // vim25 honours maxSample only for real-time series. For a historical
        // interval it is ignored, and a query with no range returns nothing —
        // no fault, no error, an empty answer that reads exactly like an idle
        // datastore. Forty-one of them read that way for months.
        var from = new DateTimeOffset(2026, 9, 19, 23, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(20);

        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["ds-1"], "Datastore", [CpuUsage], 300, 3, (from, to));

        var parsed = Parse(soap);

        Assert.Equal(
            "2026-09-19T23:00:00.0000000Z",
            Assert.Single(Named(parsed, "startTime")).Value);
        Assert.Equal(
            "2026-09-19T23:20:00.0000000Z",
            Assert.Single(Named(parsed, "endTime")).Value);
    }

    [Fact]
    public void A_real_time_query_carries_no_range()
    {
        // Real-time is selected by maxSample, and a range would narrow it to
        // whatever clock skew exists between us and the server.
        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["host-1"], "HostSystem", [CpuUsage], 20, 3);

        Assert.Empty(Named(Parse(soap), "startTime"));
        Assert.Empty(Named(Parse(soap), "endTime"));
    }

    [Fact]
    public void The_range_sits_where_the_schema_requires()
    {
        // PerfQuerySpec is an xsd:sequence: entity, startTime?, endTime?,
        // maxSample?, metricId*, intervalId?, format?. An element in the wrong
        // position is not ignored — the server rejects the whole request with
        // "Unexpected element tag", so the order is a contract rather than a
        // tidiness preference.
        var from = new DateTimeOffset(2026, 9, 19, 23, 0, 0, TimeSpan.Zero);

        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["ds-1"], "Datastore", [CpuUsage], 300, 3, (from, from.AddMinutes(20)));

        var spec = Assert.Single(Named(Parse(soap), "querySpec"));
        var order = spec.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(
            ["entity", "startTime", "endTime", "maxSample", "metricId", "intervalId", "format"],
            order);
    }

    [Fact]
    public void The_window_is_wide_enough_to_outlast_the_rollup_lag()
    {
        // vCenter finishes a five-minute bucket some minutes after the fact.
        // A window of one interval would intermittently return nothing and the
        // series would have holes nobody could account for.
        Assert.True(VsphereIntervals.HistoricalWindow.TotalSeconds
            >= VsphereIntervals.HistoricalLevel1Seconds * 3);
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
    public void A_query_spec_puts_its_children_in_schema_order()
    {
        // PerfQuerySpec is an XSD sequence, so order is part of the contract.
        // A live vCenter 8 rejected an out-of-order spec with
        //   Unexpected element tag "vim25:metricId" seen
        //   while parsing serialized DataObject of type vim.PerformanceManager.QuerySpec
        // The schema order is entity, startTime?, endTime?, maxSample?,
        // metricId*, intervalId?, format?.
        var soap = VsphereSoapRequests.QueryPerf(
            "PerfMgr", ["host-1"], "HostSystem", [CpuUsage, CpuReady], 20, 3);

        var spec = Assert.Single(Named(Parse(soap), "querySpec"));
        var order = spec.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(
            ["entity", "maxSample", "metricId", "metricId", "intervalId", "format"],
            order);
    }

    [Fact]
    public void An_entity_type_is_carried_as_an_attribute_not_guessed()
    {
        var soap = VsphereSoapRequests.QueryPerf("PerfMgr", ["vm-1"], "VirtualMachine", [CpuReady], 20, 3);

        var entity = Assert.Single(Named(Parse(soap), "entity"));
        Assert.Equal("VirtualMachine", entity.Attribute("type")?.Value);
    }

    [Fact]
    public void Alarm_definitions_are_asked_for_by_reference_not_by_sweeping_the_inventory()
    {
        // One object spec per alarm and no container view. A view over every
        // Alarm would read hundreds of definitions to name the two that are
        // actually triggered, on every inventory cycle.
        var soap = VsphereSoapRequests.RetrieveAlarmDefinitions("propCollector", ["alarm-115", "alarm-7"]);

        var objects = Named(Parse(soap), "obj").ToList();

        Assert.Equal(["alarm-115", "alarm-7"], objects.Select(o => o.Value.Trim()));
        Assert.All(objects, o => Assert.Equal("Alarm", o.Attribute("type")?.Value));
        Assert.Empty(Named(Parse(soap), "selectSet"));
    }

    [Fact]
    public void An_alarm_is_asked_for_its_name_rather_than_its_localisation_key()
    {
        // info.systemName is "alarm.MemoryHealthAlarm" and info.name is "Host
        // memory status". Only one of those is readable by somebody who does
        // not already know the answer.
        var paths = Named(Parse(VsphereSoapRequests.RetrieveAlarmDefinitions("pc", ["alarm-115"])), "pathSet")
            .Select(p => p.Value.Trim())
            .ToList();

        Assert.Contains("info.name", paths);
        Assert.DoesNotContain("info.systemName", paths);
    }

    [Fact]
    public void A_windowed_query_gives_each_entity_its_own_window_and_no_sample_cap()
    {
        var from1 = new DateTimeOffset(2026, 9, 22, 11, 58, 0, TimeSpan.Zero);
        var from2 = new DateTimeOffset(2026, 9, 22, 11, 59, 20, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

        var soap = Parse(VsphereSoapRequests.QueryPerfWindows(
            "PerfMgr",
            [new PerfQueryTarget("vm-1", from1, to), new PerfQueryTarget("vm-2", from2, to)],
            "VirtualMachine", [CpuReady], 20));

        var specs = Named(soap, "querySpec").ToList();
        Assert.Equal(2, specs.Count);

        // Both ends always; maxSample never, because with a window it keeps
        // the newest samples and drops the oldest (m1).
        Assert.Empty(Named(soap, "maxSample"));
        Assert.Equal(
            ["2026-09-22T11:58:00.0000000Z", "2026-09-22T11:59:20.0000000Z"],
            specs.Select(s => s.Elements().Single(e => e.Name.LocalName == "startTime").Value));
        Assert.All(specs, s => Assert.Equal(
            "2026-09-22T12:00:00.0000000Z", s.Elements().Single(e => e.Name.LocalName == "endTime").Value));

        // Schema order: entity, startTime, endTime, metricId*, intervalId, format.
        Assert.Equal(
            ["entity", "startTime", "endTime", "metricId", "intervalId", "format"],
            specs[0].Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void The_server_clock_is_asked_of_the_service_instance()
    {
        var soap = Parse(VsphereSoapRequests.CurrentTime());

        Assert.Equal("ServiceInstance", Assert.Single(Named(soap, "_this")).Value);
        Assert.Single(Named(soap, "CurrentTime"));
    }

    [Fact]
    public void The_server_clock_reply_is_read_as_utc()
    {
        const string reply = """
            <?xml version="1.0" encoding="UTF-8"?>
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <CurrentTimeResponse xmlns="urn:vim25"><returnval>2026-09-22T14:00:05.123+02:00</returnval></CurrentTimeResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 12, 0, 5, 123, TimeSpan.Zero),
            VsphereSoapRequests.ParseCurrentTime(reply));
    }
}