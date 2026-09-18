using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Response shapes follow the vim25 <c>PerformanceManager</c> schema and were
/// cross-checked against the previous product's parser, which was reading real
/// vCenter traffic in production.
/// </summary>
public class PerfResponseParserTests
{
    private const string CounterResponse = """
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body>
            <QueryPerfCounterResponse xmlns="urn:vim25">
              <returnval>
                <key>2</key>
                <nameInfo><label>Usage</label><key>usage</key></nameInfo>
                <groupInfo><label>CPU</label><key>cpu</key></groupInfo>
                <unitInfo><label>Percent</label><key>percent</key></unitInfo>
                <rollupType>average</rollupType>
                <statsType>rate</statsType>
                <level>1</level>
              </returnval>
              <returnval>
                <key>12</key>
                <nameInfo><label>Ready</label><key>ready</key></nameInfo>
                <groupInfo><label>CPU</label><key>cpu</key></groupInfo>
                <unitInfo><label>Millisecond</label><key>millisecond</key></unitInfo>
                <rollupType>summation</rollupType>
                <statsType>delta</statsType>
                <level>1</level>
              </returnval>
              <returnval>
                <key>180</key>
                <nameInfo><label>Physical device latency</label><key>deviceLatency</key></nameInfo>
                <groupInfo><label>Disk</label><key>disk</key></groupInfo>
                <unitInfo><label>Millisecond</label><key>millisecond</key></unitInfo>
                <rollupType>average</rollupType>
                <statsType>absolute</statsType>
                <level>2</level>
              </returnval>
            </QueryPerfCounterResponse>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    private static Dictionary<int, VsphereCounter> Catalog() =>
        PerfResponseParser.ParseCounters(CounterResponse).ToDictionary(c => c.Id);

    // --- counter metadata -------------------------------------------------

    [Fact]
    public void Counters_are_identified_by_group_name_and_rollup()
    {
        var counters = PerfResponseParser.ParseCounters(CounterResponse);

        Assert.Equal(3, counters.Count);
        Assert.Contains(counters, c => c.Key == "cpu.usage.average");
        Assert.Contains(counters, c => c.Key == "cpu.ready.summation");
        Assert.Contains(counters, c => c.Key == "disk.deviceLatency.average");
    }

    [Fact]
    public void The_rollup_type_is_captured_because_it_is_the_meaning_of_the_number()
    {
        var ready = PerfResponseParser.ParseCounters(CounterResponse).Single(c => c.Name == "ready");

        Assert.Equal(RollupType.Summation, ready.Rollup);
        Assert.Equal("millisecond", ready.Unit);
    }

    [Fact]
    public void The_statistics_level_is_captured_so_availability_can_be_explained()
    {
        // disk.deviceLatency needs level 2; at the default level 1 it is simply
        // absent, and the operator deserves to be told why rather than shown a
        // blank.
        var counters = PerfResponseParser.ParseCounters(CounterResponse);

        Assert.Equal(1, counters.Single(c => c.Name == "usage").Level);
        Assert.Equal(2, counters.Single(c => c.Name == "deviceLatency").Level);
    }

    [Fact]
    public void Malformed_xml_yields_nothing_rather_than_throwing()
    {
        // A vendor returning something unexpected is a collection failure to be
        // reported, not a reason to end the cycle.
        Assert.Empty(PerfResponseParser.ParseCounters("<not-xml"));
        Assert.Empty(PerfResponseParser.ParseSamples(
            "<not-xml", new Dictionary<int, VsphereCounter>(), TimeSpan.FromSeconds(20)));
    }

    // --- samples ----------------------------------------------------------

    [Fact]
    public void The_interval_is_read_from_the_response_not_assumed_from_the_request()
    {
        // The previous parser discarded sampleInfo and relied on always having
        // asked for 20-second samples. That assumption breaks the moment
        // anything queries a datastore, which has no real-time feed.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="VirtualMachine">vm-42</entity>
                <sampleInfo><timestamp>2026-09-18T12:00:00Z</timestamp><interval>300</interval></sampleInfo>
                <value>
                  <id><counterId>12</counterId><instance></instance></id>
                  <value>6000</value>
                </value>
              </returnval>
            </QueryPerfResponse>
            """;

        var samples = PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20));

        var value = Assert.Single(Assert.Single(samples).Values);
        Assert.Equal(TimeSpan.FromSeconds(300), value.Interval);
        // 6000 ms of ready in 300 s is 2%, not the 30% it would be over 20 s.
        Assert.Equal(2d, value.AsPercentageOfInterval(), precision: 6);
    }

    [Fact]
    public void The_fallback_interval_is_used_only_when_the_response_omits_one()
    {
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <value><id><counterId>2</counterId><instance></instance></id><value>42</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var samples = PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(Assert.Single(samples).Values).Interval);
    }

    [Fact]
    public void The_most_recent_point_in_a_series_is_taken()
    {
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value>
                  <id><counterId>2</counterId><instance></instance></id>
                  <value>10</value><value>20</value><value>35</value>
                </value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(35d, value.Raw);
    }

    [Fact]
    public void An_aggregate_series_wins_over_the_per_device_ones()
    {
        // The empty instance is vCenter's own answer for the whole entity.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>180</counterId><instance>vmhba0</instance></id><value>3</value></value>
                <value><id><counterId>180</counterId><instance></instance></id><value>7</value></value>
                <value><id><counterId>180</counterId><instance>vmhba1</instance></id><value>99</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(7d, value.Raw);
        Assert.True(value.IsAggregateInstance);
    }

    [Fact]
    public void Without_an_aggregate_the_worst_device_wins_for_a_latency_counter()
    {
        // Averaging across devices is how one sick path hides behind eleven
        // healthy ones — precisely the failure this product exists to prevent.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>180</counterId><instance>vmhba0</instance></id><value>2</value></value>
                <value><id><counterId>180</counterId><instance>vmhba1</instance></id><value>2</value></value>
                <value><id><counterId>180</counterId><instance>vmhba2</instance></id><value>140</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(140d, value.Raw);
    }

    [Fact]
    public void Without_an_aggregate_a_summation_is_summed_across_devices()
    {
        // A total across devices is still a total, so taking the maximum here
        // would under-report.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="VirtualMachine">vm-42</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>12</counterId><instance>0</instance></id><value>1000</value></value>
                <value><id><counterId>12</counterId><instance>1</instance></id><value>500</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(1500d, value.Raw);
    }

    [Fact]
    public void Counters_we_did_not_ask_for_are_ignored_rather_than_guessed_at()
    {
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>9999</counterId><instance></instance></id><value>1</value></value>
                <value><id><counterId>2</counterId><instance></instance></id><value>42</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal("cpu.usage.average", value.CounterName);
    }

    [Fact]
    public void Several_entities_in_one_response_are_kept_apart()
    {
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>2</counterId><instance></instance></id><value>10</value></value>
              </returnval>
              <returnval>
                <entity type="HostSystem">host-2</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>2</counterId><instance></instance></id><value>90</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var samples = PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20));

        Assert.Equal(2, samples.Count);
        Assert.Equal(10d, samples.Single(s => s.EntityMoRef == "host-1").Values[0].Raw);
        Assert.Equal(90d, samples.Single(s => s.EntityMoRef == "host-2").Values[0].Raw);
    }

    [Fact]
    public void An_empty_series_contributes_nothing_rather_than_a_zero()
    {
        // A zero would read as "measured and idle", which is a different claim
        // from "not measured".
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>2</counterId><instance></instance></id></value>
              </returnval>
            </QueryPerfResponse>
            """;

        Assert.Empty(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);
    }
}
