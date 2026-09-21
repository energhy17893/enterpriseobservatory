using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Host services, time, standard switch security and lockdown mode, read the
/// way a vim25 <c>RetrievePropertiesEx</c> reply would carry them.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures follow the published vim25 schema and have <strong>not</strong>
/// been compared with a live vCenter's reply. They prove the reader, not the
/// shape; nobody should read "these pass" as "the paths are confirmed".
/// </para>
/// <para>
/// Most of these tests are about one distinction: not reported is null,
/// reported and empty is empty, and an unset port-group field is unset rather
/// than false. The compliance engine that reads this stays quiet on the first
/// and speaks on the second, so the reader must never turn one into the other.
/// </para>
/// </remarks>
public class HostConfigurationParserTests
{
    private static PropertyObject Host(string propSets) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-3615</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">esx07.corp.local</val></propSet>
                  {propSets}
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    private static readonly PropertyObject Bare = Host(string.Empty);

    // --- services -----------------------------------------------------------

    private const string Services = """
        <propSet>
          <name>config.service</name>
          <val xsi:type="HostServiceInfo">
            <service>
              <key>TSM-SSH</key><label>SSH</label><required>false</required>
              <uninstallable>false</uninstallable><running>true</running>
              <ruleset>sshServer</ruleset><policy>off</policy>
            </service>
            <service>
              <key>TSM</key><label>ESXi Shell</label><required>false</required>
              <uninstallable>false</uninstallable><running>false</running><policy>off</policy>
            </service>
            <service>
              <key>ntpd</key><label>NTP Daemon</label><required>false</required>
              <uninstallable>false</uninstallable><running>true</running>
              <ruleset>ntpClient</ruleset><policy>on</policy>
            </service>
          </val>
        </propSet>
        """;

    [Fact]
    public void Services_are_read_with_key_running_state_and_policy()
    {
        var services = HostConfigurationParser.ReadServices(Host(Services));

        Assert.NotNull(services);
        Assert.Equal(["TSM-SSH", "TSM", "ntpd"], services.Select(s => s.Key));

        var ssh = services[0];
        Assert.Equal("SSH", ssh.Label);
        Assert.True(ssh.Running);
        Assert.Equal("off", ssh.Policy);
    }

    [Fact]
    public void Running_and_policy_are_kept_apart_because_they_disagree_in_the_case_that_matters()
    {
        // SSH switched on by hand with policy "off": running now, gone after a
        // reboot. Collapsing the two would lose which finding it is.
        var ssh = HostConfigurationParser.ReadServices(Host(Services))!.Single(s => s.Key == "TSM-SSH");

        Assert.True(ssh.Running);
        Assert.Equal("off", ssh.Policy);
    }

    [Fact]
    public void Services_not_reported_are_null_rather_than_none()
    {
        Assert.Null(HostConfigurationParser.ReadServices(Bare));
    }

    [Fact]
    public void Services_reported_empty_are_empty_rather_than_null()
    {
        var services = HostConfigurationParser.ReadServices(Host("""
            <propSet><name>config.service</name><val xsi:type="HostServiceInfo"></val></propSet>
            """));

        Assert.NotNull(services);
        Assert.Empty(services);
    }

    [Fact]
    public void A_service_whose_running_state_is_missing_is_null_rather_than_stopped()
    {
        var service = Assert.Single(HostConfigurationParser.ReadServices(Host("""
            <propSet>
              <name>config.service</name>
              <val xsi:type="HostServiceInfo">
                <service><key>TSM-SSH</key><label>SSH</label></service>
              </val>
            </propSet>
            """))!);

        Assert.Null(service.Running);
        Assert.Null(service.Policy);
    }

    // --- time -------------------------------------------------------------

    private const string TimeZone = """
        <timeZone><key>UTC</key><name>UTC</name><description>UTC</description><gmtOffset>0</gmtOffset></timeZone>
        """;

    [Fact]
    public void NTP_servers_are_read_in_order_with_the_protocol()
    {
        var time = HostConfigurationParser.ReadTimeConfiguration(Host($"""
            <propSet>
              <name>config.dateTimeInfo</name>
              <val xsi:type="HostDateTimeInfo">
                {TimeZone}
                <systemClockProtocol>ntp</systemClockProtocol>
                <ntpConfig>
                  <server>10.0.0.1</server>
                  <server>ntp2.corp.local</server>
                  <configFile>server 10.0.0.1</configFile>
                </ntpConfig>
              </val>
            </propSet>
            """));

        Assert.NotNull(time);
        Assert.Equal("ntp", time.Protocol);
        Assert.Equal(["10.0.0.1", "ntp2.corp.local"], time.NtpServers);
        Assert.Null(time.Ptp);
    }

    [Fact]
    public void A_PTP_host_carries_its_PTP_configuration_so_a_rule_does_not_see_no_time_source()
    {
        // A rule that looked only at NTP would call this host unsynchronised.
        var time = HostConfigurationParser.ReadTimeConfiguration(Host($"""
            <propSet>
              <name>config.dateTimeInfo</name>
              <val xsi:type="HostDateTimeInfo">
                {TimeZone}
                <systemClockProtocol>ptp</systemClockProtocol>
                <ntpConfig></ntpConfig>
                <ptpConfig>
                  <domain>0</domain>
                  <port><index>0</index><deviceType>virtualNic</deviceType><device>vmk1</device></port>
                  <port><index>1</index><deviceType>none</deviceType></port>
                </ptpConfig>
                <enabled>true</enabled>
              </val>
            </propSet>
            """));

        Assert.NotNull(time);
        Assert.Equal("ptp", time.Protocol);
        Assert.True(time.Enabled);
        Assert.Empty(time.NtpServers);
        Assert.NotNull(time.Ptp);
        Assert.Equal(0, time.Ptp.Domain);
        Assert.Equal(["vmk1"], time.Ptp.PortDevices);
    }

    [Fact]
    public void An_older_host_without_a_protocol_field_reports_null_rather_than_a_guessed_ntp()
    {
        var time = HostConfigurationParser.ReadTimeConfiguration(Host($"""
            <propSet>
              <name>config.dateTimeInfo</name>
              <val xsi:type="HostDateTimeInfo">
                {TimeZone}
                <ntpConfig><server>10.0.0.1</server></ntpConfig>
              </val>
            </propSet>
            """));

        Assert.NotNull(time);
        Assert.Null(time.Protocol);
        Assert.Null(time.Enabled);
        Assert.Equal(["10.0.0.1"], time.NtpServers);
    }

    [Fact]
    public void Time_read_with_no_NTP_configuration_is_an_empty_server_list()
    {
        // The structure arrived, so the host has answered: it has no servers.
        // That is a finding, and must not be mistaken for "not read".
        var time = HostConfigurationParser.ReadTimeConfiguration(Host($"""
            <propSet>
              <name>config.dateTimeInfo</name>
              <val xsi:type="HostDateTimeInfo">{TimeZone}</val>
            </propSet>
            """));

        Assert.NotNull(time);
        Assert.Empty(time.NtpServers);
    }

    [Fact]
    public void Time_not_reported_is_null()
    {
        Assert.Null(HostConfigurationParser.ReadTimeConfiguration(Bare));
    }

    // --- standard switch security -----------------------------------------

    private const string Network = """
        <propSet>
          <name>config.network.vswitch</name>
          <val xsi:type="ArrayOfHostVirtualSwitch">
            <HostVirtualSwitch xsi:type="HostVirtualSwitch">
              <name>vSwitch0</name>
              <key>key-vim.host.VirtualSwitch-vSwitch0</key>
              <numPorts>2560</numPorts>
              <portgroup>key-vim.host.PortGroup-Management Network</portgroup>
              <portgroup>key-vim.host.PortGroup-VM Network</portgroup>
              <pnic>key-vim.host.PhysicalNic-vmnic0</pnic>
              <mtu>1500</mtu>
              <spec>
                <numPorts>128</numPorts>
                <policy>
                  <security>
                    <allowPromiscuous>false</allowPromiscuous>
                    <macChanges>true</macChanges>
                    <forgedTransmits>true</forgedTransmits>
                  </security>
                  <nicTeaming><policy>loadbalance_srcid</policy></nicTeaming>
                </policy>
                <mtu>1500</mtu>
              </spec>
            </HostVirtualSwitch>
          </val>
        </propSet>
        <propSet>
          <name>config.network.portgroup</name>
          <val xsi:type="ArrayOfHostPortGroup">
            <HostPortGroup xsi:type="HostPortGroup">
              <key>key-vim.host.PortGroup-VM Network</key>
              <vswitch>key-vim.host.VirtualSwitch-vSwitch0</vswitch>
              <computedPolicy>
                <security>
                  <allowPromiscuous>false</allowPromiscuous>
                  <macChanges>true</macChanges>
                  <forgedTransmits>true</forgedTransmits>
                </security>
              </computedPolicy>
              <spec>
                <name>VM Network</name>
                <vlanId>0</vlanId>
                <vswitchName>vSwitch0</vswitchName>
                <policy>
                  <security></security>
                  <nicTeaming><policy>loadbalance_srcid</policy></nicTeaming>
                </policy>
              </spec>
            </HostPortGroup>
            <HostPortGroup xsi:type="HostPortGroup">
              <key>key-vim.host.PortGroup-Management Network</key>
              <vswitch>key-vim.host.VirtualSwitch-vSwitch0</vswitch>
              <computedPolicy>
                <security>
                  <allowPromiscuous>false</allowPromiscuous>
                  <macChanges>false</macChanges>
                  <forgedTransmits>true</forgedTransmits>
                </security>
              </computedPolicy>
              <spec>
                <name>Management Network</name>
                <vlanId>10</vlanId>
                <vswitchName>vSwitch0</vswitchName>
                <policy>
                  <security><macChanges>false</macChanges></security>
                </policy>
              </spec>
            </HostPortGroup>
          </val>
        </propSet>
        """;

    [Fact]
    public void A_vSwitch_policy_is_read_with_its_name_and_is_its_own_effective_policy()
    {
        var vswitch = Assert.Single(HostConfigurationParser.ReadVirtualSwitchSecurity(Host(Network))!);

        Assert.Equal(NetworkPolicyScope.VirtualSwitch, vswitch.Scope);
        Assert.Equal("vSwitch0", vswitch.Name);
        Assert.False(vswitch.Configured.AllowPromiscuous);
        Assert.True(vswitch.Configured.MacChanges);
        Assert.True(vswitch.Configured.ForgedTransmits);
        Assert.Equal(vswitch.Configured, vswitch.Effective);
    }

    [Fact]
    public void A_port_group_that_inherits_carries_absent_as_absent_not_as_false()
    {
        // false is the secure answer for all three, so reading an inheriting
        // port group as false would report it locked down while its switch
        // accepts forged transmits.
        var vmNetwork = HostConfigurationParser.ReadPortGroupSecurity(Host(Network))!
            .Single(p => p.Name == "VM Network");

        Assert.Equal(NetworkPolicyScope.PortGroup, vmNetwork.Scope);
        Assert.Equal("vSwitch0", vmNetwork.VirtualSwitchName);
        Assert.Null(vmNetwork.Configured.AllowPromiscuous);
        Assert.Null(vmNetwork.Configured.MacChanges);
        Assert.Null(vmNetwork.Configured.ForgedTransmits);

        // What actually applies comes from the platform, not from inference.
        Assert.NotNull(vmNetwork.Effective);
        Assert.True(vmNetwork.Effective.ForgedTransmits);
    }

    [Fact]
    public void A_port_group_override_is_kept_next_to_the_fields_it_left_inherited()
    {
        var management = HostConfigurationParser.ReadPortGroupSecurity(Host(Network))!
            .Single(p => p.Name == "Management Network");

        Assert.False(management.Configured.MacChanges);
        Assert.Null(management.Configured.AllowPromiscuous);
        Assert.Null(management.Configured.ForgedTransmits);
    }

    [Fact]
    public void A_port_group_without_a_computed_policy_has_no_effective_reading()
    {
        var portGroup = Assert.Single(HostConfigurationParser.ReadPortGroupSecurity(Host("""
            <propSet>
              <name>config.network.portgroup</name>
              <val xsi:type="ArrayOfHostPortGroup">
                <HostPortGroup xsi:type="HostPortGroup">
                  <spec><name>Isolated</name><vswitchName>vSwitch1</vswitchName><vlanId>0</vlanId></spec>
                </HostPortGroup>
              </val>
            </propSet>
            """))!);

        Assert.Null(portGroup.Effective);
        Assert.Null(portGroup.Configured.ForgedTransmits);
    }

    [Fact]
    public void Switches_and_port_groups_not_reported_are_null_rather_than_none()
    {
        Assert.Null(HostConfigurationParser.ReadVirtualSwitchSecurity(Bare));
        Assert.Null(HostConfigurationParser.ReadPortGroupSecurity(Bare));
    }

    [Fact]
    public void Switches_reported_empty_are_empty()
    {
        // A host moved wholly onto a distributed switch has no standard one.
        var host = Host("""
            <propSet><name>config.network.vswitch</name><val xsi:type="ArrayOfHostVirtualSwitch"></val></propSet>
            """);

        var switches = HostConfigurationParser.ReadVirtualSwitchSecurity(host);

        Assert.NotNull(switches);
        Assert.Empty(switches);
    }

    // --- lockdown ---------------------------------------------------------

    [Theory]
    [InlineData("lockdownDisabled")]
    [InlineData("lockdownNormal")]
    [InlineData("lockdownStrict")]
    public void Lockdown_mode_is_carried_in_the_platform_words(string mode)
    {
        var host = Host($"""
            <propSet><name>config.lockdownMode</name><val xsi:type="HostLockdownMode">{mode}</val></propSet>
            """);

        Assert.Equal(mode, HostConfigurationParser.ReadLockdownMode(host));
    }

    [Fact]
    public void Lockdown_mode_not_reported_is_null()
    {
        Assert.Null(HostConfigurationParser.ReadLockdownMode(Bare));
    }

    // --- through ToHost ----------------------------------------------------

    [Fact]
    public void The_host_record_carries_everything_read_here()
    {
        var host = VsphereClient.ToHost(Host(Services + Network + $"""
            <propSet>
              <name>config.dateTimeInfo</name>
              <val xsi:type="HostDateTimeInfo">{TimeZone}<ntpConfig><server>10.0.0.1</server></ntpConfig></val>
            </propSet>
            <propSet><name>config.lockdownMode</name><val xsi:type="HostLockdownMode">lockdownNormal</val></propSet>
            """));

        Assert.Equal(3, host.Services!.Count);
        Assert.Equal(["10.0.0.1"], host.TimeConfiguration!.NtpServers);
        Assert.Single(host.VirtualSwitchSecurity!);
        Assert.Equal(2, host.PortGroupSecurity!.Count);
        Assert.Equal("lockdownNormal", host.LockdownMode);
    }

    [Fact]
    public void A_host_that_reported_none_of_it_carries_nulls_throughout()
    {
        var host = VsphereClient.ToHost(Bare);

        Assert.Null(host.Services);
        Assert.Null(host.TimeConfiguration);
        Assert.Null(host.VirtualSwitchSecurity);
        Assert.Null(host.PortGroupSecurity);
        Assert.Null(host.LockdownMode);
    }

    [Fact]
    public void Every_path_read_here_is_one_the_collector_asks_for()
    {
        var asked = VsphereClient.InventoryPropertiesFor("HostSystem");

        Assert.Contains(HostConfigurationParser.ServicesPath, asked);
        Assert.Contains(HostConfigurationParser.DateTimePath, asked);
        Assert.Contains(HostConfigurationParser.VirtualSwitchPath, asked);
        Assert.Contains(HostConfigurationParser.PortGroupPath, asked);
        Assert.Contains(HostConfigurationParser.LockdownModePath, asked);
    }
}
