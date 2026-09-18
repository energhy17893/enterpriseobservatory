using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Readings here are the ones a live vCenter 8 actually returned, so the
/// scaling is anchored to observed behaviour rather than to a reading of the
/// documentation.
/// </summary>
public class VsphereUnitNormalizerTests
{
    [Theory]
    [InlineData(2669, 26.69)]  // observed: host CPU
    [InlineData(1441, 14.41)]  // observed: host memory
    [InlineData(0, 0)]
    [InlineData(10000, 100)]
    public void A_percentage_is_reported_in_hundredths_and_is_scaled_back(double raw, double expected)
    {
        // Passing 2669 through unchanged would report 2669% and trip every
        // threshold in the product on the first cycle.
        Assert.Equal(expected, VsphereUnitNormalizer.Normalize(raw, "percent"), precision: 6);
    }

    [Theory]
    [InlineData("millisecond")]
    [InlineData("kiloBytes")]
    [InlineData("megaBytes")]
    [InlineData("number")]
    [InlineData("watt")]
    public void Units_that_are_not_scaled_pass_through_untouched(string unit)
    {
        Assert.Equal(1234d, VsphereUnitNormalizer.Normalize(1234, unit));
    }

    [Fact]
    public void An_unrecognised_unit_is_left_alone_rather_than_guessed_at()
    {
        // Silently rescaling something on a guess would be worse than the
        // problem it was trying to solve.
        Assert.Equal(1234d, VsphereUnitNormalizer.Normalize(1234, "someNewUnit"));
        Assert.Equal(1234d, VsphereUnitNormalizer.Normalize(1234, null));
    }

    [Fact]
    public void Scaling_is_declared_so_a_diagnostic_view_can_show_both_readings()
    {
        Assert.True(VsphereUnitNormalizer.IsScaled("percent"));
        Assert.False(VsphereUnitNormalizer.IsScaled("millisecond"));
    }

    [Fact]
    public void The_unit_name_is_matched_regardless_of_case_or_padding()
    {
        Assert.Equal(26.69, VsphereUnitNormalizer.Normalize(2669, " Percent "), precision: 6);
    }
}

public class PerfResponseParserScalingTests
{
    private const string Counters = """
        <QueryPerfCounterByLevelResponse xmlns="urn:vim25">
          <returnval>
            <key>2</key>
            <nameInfo><key>usage</key></nameInfo>
            <groupInfo><key>cpu</key></groupInfo>
            <unitInfo><key>percent</key></unitInfo>
            <rollupType>average</rollupType>
            <level>1</level>
          </returnval>
          <returnval>
            <key>180</key>
            <nameInfo><key>deviceLatency</key></nameInfo>
            <groupInfo><key>disk</key></groupInfo>
            <unitInfo><key>millisecond</key></unitInfo>
            <rollupType>average</rollupType>
            <level>1</level>
          </returnval>
        </QueryPerfCounterByLevelResponse>
        """;

    private static Dictionary<int, VsphereCounter> Catalog() =>
        PerfResponseParser.ParseCounters(Counters).ToDictionary(c => c.Id);

    [Fact]
    public void A_parsed_percentage_arrives_already_scaled()
    {
        // The value reaching the domain must be the one the unit claims.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>2</counterId><instance></instance></id><value>2669</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(26.69, value.Raw, precision: 6);
        Assert.Equal("percent", value.Unit);
    }

    [Fact]
    public void A_latency_reading_is_not_rescaled()
    {
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>180</counterId><instance></instance></id><value>7</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(7d, value.Raw);
    }

    [Fact]
    public void Per_device_series_are_scaled_before_they_are_combined()
    {
        // Combining first and scaling after would give the same answer for a
        // maximum but not for a summation, so the order is worth pinning.
        const string xml = """
            <QueryPerfResponse xmlns="urn:vim25">
              <returnval>
                <entity type="HostSystem">host-1</entity>
                <sampleInfo><interval>20</interval></sampleInfo>
                <value><id><counterId>2</counterId><instance>0</instance></id><value>1000</value></value>
                <value><id><counterId>2</counterId><instance>1</instance></id><value>2669</value></value>
              </returnval>
            </QueryPerfResponse>
            """;

        var value = Assert.Single(Assert.Single(
            PerfResponseParser.ParseSamples(xml, Catalog(), TimeSpan.FromSeconds(20))).Values);

        Assert.Equal(26.69, value.Raw, precision: 6);
    }
}
