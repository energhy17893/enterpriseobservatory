using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Reading the handful of host advanced settings anything asks about.
/// </summary>
/// <remarks>
/// The whole table is over a thousand rows per host, so the parser's job is as
/// much about what it drops as what it keeps. These tests are mostly about the
/// dropping, and about the distinction the rest of the product depends on: a
/// setting nobody configured is an empty value, a setting nobody read is a
/// missing key, and the two must not become the same thing here.
/// </remarks>
public class AdvancedSettingsTests
{
    private static string Page(string optionRows) =>
        $"""
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="HostSystem">host-1</obj>
              <propSet>
                <name>config.option</name>
                <val xsi:type="ArrayOfOptionValue" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  {optionRows}
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    private static string Option(string key, string value) =>
        $"""
        <OptionValue xsi:type="OptionValue">
          <key>{key}</key>
          <value xsi:type="xsd:string">{value}</value>
        </OptionValue>
        """;

    private static IReadOnlyDictionary<string, string> Read(string optionRows) =>
        VsphereClient.ReadAdvancedSettings(
            PropertyCollectorParser.ParsePage(Page(optionRows)).Objects[0]);

    [Fact]
    public void A_wanted_setting_is_kept_with_its_value()
    {
        var settings = Read(Option("Syslog.global.logHost", "udp://10.0.0.5:514"));

        Assert.Equal("udp://10.0.0.5:514", settings["Syslog.global.logHost"]);
    }

    [Fact]
    public void A_setting_nobody_asked_for_is_dropped()
    {
        // The point of the allowlist. A host reports over a thousand of these
        // and keeping them all would write five figures of rows per cycle to
        // answer a handful of questions.
        var settings = Read(
            Option("Syslog.global.logHost", "udp://10.0.0.5:514") +
            Option("Misc.LogToSerial", "0") +
            Option("Net.TcpipHeapSize", "512"));

        Assert.Single(settings);
        Assert.False(settings.ContainsKey("Misc.LogToSerial"));
    }

    [Fact]
    public void A_configured_setting_left_empty_is_kept_as_empty()
    {
        // The distinction the rule leans on. An empty syslog target is the
        // host's answer, and it is the answer that is a finding.
        var settings = Read(Option("Syslog.global.logHost", string.Empty));

        Assert.True(settings.ContainsKey("Syslog.global.logHost"));
        Assert.Equal(string.Empty, settings["Syslog.global.logHost"]);
    }

    [Fact]
    public void A_host_that_reported_no_options_carries_no_settings()
    {
        // Not an empty value for every wanted key -- no keys at all. A rule
        // must be able to tell "nobody set this" from "nobody read this".
        var host = PropertyCollectorParser.ParsePage(
            """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet>
                    <name>name</name>
                    <val xsi:type="xsd:string" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">esx-1</val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects[0];

        Assert.Empty(VsphereClient.ReadAdvancedSettings(host));
    }

    [Fact]
    public void A_row_with_no_key_is_dropped_rather_than_stored_under_nothing()
    {
        var settings = Read(
            Option(string.Empty, "orphan") +
            Option("Syslog.global.logHost", "udp://10.0.0.5:514"));

        Assert.Single(settings);
        Assert.False(settings.ContainsKey(string.Empty));
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case()
    {
        // A key differing only in case would drop the setting silently, and
        // the failure would look exactly like a host that does not report it
        // -- which the rule treats as "not read" and stays quiet about.
        var settings = Read(Option("syslog.GLOBAL.loghost", "udp://10.0.0.5:514"));

        Assert.Equal("udp://10.0.0.5:514", settings["Syslog.global.logHost"]);
    }

    [Fact]
    public void Every_wanted_name_survives_a_round_trip()
    {
        // Guards the list against a typo in a constant. A misspelled name
        // never matches anything a host sends, so the setting is simply always
        // absent -- and absent reads as "not collected", which is silent.
        var rows = string.Concat(AdvancedSettings.Wanted.Select(k => Option(k, "x")));

        var settings = Read(rows);

        Assert.Equal(AdvancedSettings.Wanted.Count, settings.Count);
        Assert.All(AdvancedSettings.Wanted, k => Assert.True(settings.ContainsKey(k)));
    }
}
