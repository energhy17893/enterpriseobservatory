using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Controls the guide answers from a host's typed configuration: services,
/// time, standard switch security and lockdown.
/// </summary>
/// <remarks>
/// The controls are built with the guide's own ids, parameter and baseline
/// text as the shipped files have them, multi-line cells included, so a test
/// that passes here is a statement about the real rows. Every evaluator is
/// shown passing, failing and unread — and unread is never failing.
/// </remarks>
public class ComplianceHostConfigurationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ComplianceControl Control(string id, string installed, string baseline) => new()
    {
        ControlId = id,
        Component = id.StartsWith("esxi-", StringComparison.Ordinal) ? "VMware ESXi" : "ESX",
        Parameter = "N/A",
        InstallationDefault = installed,
        BaselineValue = baseline,
    };

    private static readonly ComplianceControl Snmp8 = Control(
        "esxi-8.deactivate-snmp", "Stopped,\nStart and stop with host", "Stopped,\nStart and stop manually");

    private static readonly ComplianceControl Snmp9 = Control(
        "esx-9.snmp", "Stopped, Start and stop with host", "Stopped, Start and stop manually");

    private static readonly ComplianceControl Cim8 = Control(
        "esxi-8.deactivate-cim", "Stopped,\nStart and stop with host", "Stopped,\nStart and stop manually");

    private static readonly ComplianceControl TimeServices8 = Control(
        "esxi-8.timekeeping-services", "Stopped,\nStart and stop manually", "Running,\nStart and stop with host");

    private static readonly ComplianceControl TimeSources8 = Control(
        "esxi-8.timekeeping-sources",
        "Undefined",
        "Site-Specific or:\n0.vmware.pool.ntp.org,\n1.vmware.pool.ntp.org,\n2.vmware.pool.ntp.org,\n3.vmware.pool.ntp.org");

    private static readonly ComplianceControl Time9 = Control("esx-9.time", "Undefined", "Site-Specific");

    private static readonly ComplianceControl Lockdown8 = Control(
        "esxi-8.lockdown-mode", "lockdownDisabled", "lockdownNormal");

    private static readonly ComplianceControl Lockdown9 = Control(
        "esx-9.lockdown-mode", "lockdownDisabled", "lockdownNormal");

    private static readonly ComplianceControl Forged8 = Control(
        "esxi-8.network-reject-forged-transmit-standardswitch", "Accept", "Reject");

    private static readonly ComplianceControl Forged9 = Control(
        "esx-9.network-standard-reject-forged-transmit", "Accept", "Reject");

    private static readonly ComplianceControl MacChanges8 = Control(
        "esxi-8.network-reject-mac-changes-standardswitch", "Accept", "Reject");

    private static readonly ComplianceControl MacChanges9 = Control(
        "esx-9.network-standard-reject-mac-changes", "Accept", "Reject");

    private static Entity Host(
        IReadOnlyList<HostService>? services = null,
        TimeConfiguration? time = null,
        IReadOnlyList<NetworkSecurityPolicy>? switches = null,
        IReadOnlyList<NetworkSecurityPolicy>? portGroups = null,
        string? lockdown = null) => new()
        {
            Id = new EntityId("vc-1:host-1"),
            Kind = EntityKind.EsxiHost,
            DisplayName = "esx01",
            LastSeenUtc = T0,
            ObservationState = ObservationState.Active,
            Services = services,
            TimeConfiguration = time,
            VirtualSwitchSecurity = switches,
            PortGroupSecurity = portGroups,
            LockdownMode = lockdown,
        };

    private static HostService Service(string key, bool? running, string? policy) =>
        new() { Key = key, Running = running, Policy = policy };

    private static ComplianceFinding One(ComplianceControl control, Entity host) =>
        Assert.Single(ComplianceEvaluation.Evaluate(
            ComplianceEvaluationTests.Catalogue(control), [host], [], T0));

    // --- binding -------------------------------------------------------------------

    [Theory]
    [InlineData("esxi-8.deactivate-snmp")]
    [InlineData("esx-9.snmp")]
    [InlineData("esxi-8.deactivate-cim")]
    [InlineData("esxi-8.timekeeping-services")]
    [InlineData("esxi-8.timekeeping-sources")]
    [InlineData("esx-9.time")]
    [InlineData("esxi-8.lockdown-mode")]
    [InlineData("esx-9.lockdown-mode")]
    [InlineData("esxi-8.network-reject-forged-transmit-standardswitch")]
    [InlineData("esx-9.network-standard-reject-forged-transmit")]
    [InlineData("esxi-8.network-reject-mac-changes-standardswitch")]
    [InlineData("esx-9.network-standard-reject-mac-changes")]
    public void A_control_the_guide_names_no_parameter_for_binds_by_its_id(string id)
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(
            ComplianceEvaluationTests.Catalogue(Control(id, "", ""))));

        Assert.True(bound.IsEvaluated);
    }

    [Fact]
    public void A_setting_check_does_not_bind_to_a_control_merely_named_like_a_service()
    {
        // "snmpd" is a service key, not an advanced setting; only the id binds it.
        var bound = Assert.Single(ComplianceEvaluation.Bind(ComplianceEvaluationTests.Catalogue(
            new ComplianceControl { ControlId = "esx-9.other", Parameter = "snmpd" })));

        Assert.False(bound.IsEvaluated);
    }

    // --- services: SNMP and CIM ------------------------------------------------------

    [Fact]
    public void Snmp_stopped_and_manual_passes()
    {
        var finding = One(Snmp9, Host(services: [Service("snmpd", false, "off")]));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("Start and stop manually", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Snmp_set_to_start_with_the_host_fails_with_its_evidence()
    {
        var finding = One(Snmp8, Host(services: [Service("snmpd", false, "on")]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("snmpd: Stopped, Start and stop with host (policy 'on')", finding.Observed);
        Assert.Equal("snmpd: Stopped, Start and stop manually (policy 'off')", finding.Expected);
    }

    [Fact]
    public void Snmp_running_with_a_manual_policy_fails()
    {
        var finding = One(Snmp9, Host(services: [Service("snmpd", true, "off")]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
    }

    [Fact]
    public void Services_not_reported_are_not_evaluated()
    {
        var finding = One(Snmp9, Host());

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Null(finding.Observed);
        Assert.Contains("did not report its services", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_reported_without_its_policy_is_not_evaluated()
    {
        var finding = One(Snmp9, Host(services: [Service("snmpd", false, null)]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("start policy", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_missing_from_the_list_is_not_evaluated()
    {
        var finding = One(Snmp9, Host(services: [Service("TSM-SSH", false, "off")]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("'snmpd'", finding.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "off", ComplianceVerdict.Passing)]
    [InlineData(true, "on", ComplianceVerdict.Failing)]
    [InlineData(null, "on", ComplianceVerdict.NotEvaluated)]
    public void Cim_is_judged_on_its_watchdog_service(bool? running, string policy, ComplianceVerdict expected)
    {
        var finding = One(Cim8, Host(services: [Service("sfcbd-watchdog", running, policy)]));

        Assert.Equal(expected, finding.Verdict);
    }

    // --- time: vSphere 8 services ----------------------------------------------------

    [Fact]
    public void Ntpd_running_and_starting_with_the_host_passes()
    {
        var finding = One(TimeServices8, Host(services: [Service("ntpd", true, "on")]));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
    }

    [Fact]
    public void Ntpd_stopped_fails_with_its_evidence()
    {
        var finding = One(TimeServices8, Host(
            services: [Service("ntpd", false, "off")],
            time: new TimeConfiguration { Protocol = "ntp", NtpServers = ["10.0.0.1"] }));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("ntpd: Stopped, Start and stop manually (policy 'off')", finding.Observed);
        Assert.StartsWith("ntpd: Running, Start and stop with host", finding.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ptp_host_is_not_failed_for_a_stopped_ntpd()
    {
        var finding = One(TimeServices8, Host(
            services: [Service("ntpd", false, "off"), Service("ptpd", true, "on")],
            time: new TimeConfiguration { Protocol = "ptp", Ptp = new PtpConfiguration { PortDevices = ["vmk0"] } }));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("ptpd: Running", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_time_service_not_reported_is_not_evaluated()
    {
        var finding = One(TimeServices8, Host(services: [Service("snmpd", false, "off")]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("'ntpd'", finding.Reason, StringComparison.Ordinal);
    }

    // --- time: vSphere 8 sources -----------------------------------------------------

    [Fact]
    public void An_ntp_server_passes_the_sources_control()
    {
        var finding = One(TimeSources8, Host(time: new TimeConfiguration
        {
            Protocol = "ntp",
            NtpServers = ["ntp1.corp", "ntp2.corp"],
        }));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("ntp1.corp, ntp2.corp", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void No_ntp_server_fails_the_sources_control_with_the_guides_baseline()
    {
        var finding = One(TimeSources8, Host(time: new TimeConfiguration { Protocol = "ntp" }));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("NTP servers: none", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("0.vmware.pool.ntp.org", finding.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ptp_host_passes_the_sources_control_without_ntp_and_is_told_the_guide_suggests_a_backup()
    {
        var finding = One(TimeSources8, Host(time: new TimeConfiguration
        {
            Protocol = "ptp",
            Ptp = new PtpConfiguration { Domain = 0, PortDevices = ["vmk1"] },
        }));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("PTP on vmk1", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("backup", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Time_configuration_not_reported_is_not_evaluated_for_sources()
    {
        var finding = One(TimeSources8, Host());

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("time configuration", finding.Reason, StringComparison.Ordinal);
    }

    // --- time: VCF 9 -----------------------------------------------------------------

    [Fact]
    public void Ntp_servers_and_a_running_ntpd_pass_the_vcf_time_control()
    {
        var finding = One(Time9, Host(
            services: [Service("ntpd", true, "on")],
            time: new TimeConfiguration { Protocol = "ntp", NtpServers = ["ntp1.corp"] }));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("ntp1.corp", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("ntpd: Running", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Servers_with_ntpd_stopped_fail_the_vcf_time_control()
    {
        var finding = One(Time9, Host(
            services: [Service("ntpd", false, "on")],
            time: new TimeConfiguration { Protocol = "ntp", NtpServers = ["ntp1.corp"] }));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
    }

    [Fact]
    public void No_servers_fail_the_vcf_time_control_even_with_ntpd_running()
    {
        var finding = One(Time9, Host(
            services: [Service("ntpd", true, "on")],
            time: new TimeConfiguration { Protocol = "ntp" }));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
    }

    [Fact]
    public void A_ptp_host_is_judged_on_ptp_for_the_vcf_time_control_and_says_so()
    {
        var finding = One(Time9, Host(
            services: [Service("ntpd", false, "off"), Service("ptpd", true, "on")],
            time: new TimeConfiguration { Protocol = "ptp", Ptp = new PtpConfiguration { PortDevices = ["vmk0"] } }));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("PTP", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Services_not_reported_leave_the_vcf_time_control_not_evaluated()
    {
        var finding = One(Time9, Host(time: new TimeConfiguration { Protocol = "ntp", NtpServers = ["ntp1.corp"] }));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Null(finding.Observed);
    }

    [Fact]
    public void Time_not_reported_leaves_the_vcf_time_control_not_evaluated()
    {
        var finding = One(Time9, Host(services: [Service("ntpd", true, "on")]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
    }

    // --- lockdown --------------------------------------------------------------------

    [Theory]
    [InlineData("lockdownNormal", ComplianceVerdict.Passing)]
    [InlineData("lockdownStrict", ComplianceVerdict.Passing)]
    [InlineData("lockdownDisabled", ComplianceVerdict.Failing)]
    [InlineData("somethingNew", ComplianceVerdict.NotEvaluated)]
    public void Lockdown_must_be_at_least_normal(string mode, ComplianceVerdict expected)
    {
        var finding = One(Lockdown9, Host(lockdown: mode));

        Assert.Equal(expected, finding.Verdict);
    }

    [Fact]
    public void Lockdown_disabled_fails_with_its_evidence()
    {
        var finding = One(Lockdown8, Host(lockdown: "lockdownDisabled"));

        Assert.Equal("lockdownDisabled", finding.Observed);
        Assert.Contains("lockdownNormal", finding.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Lockdown_not_reported_is_not_evaluated()
    {
        var finding = One(Lockdown8, Host());

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("lockdown mode", finding.Reason, StringComparison.Ordinal);
    }

    // --- standard switch security ----------------------------------------------------

    private static NetworkSecurityPolicy Switch(string name, bool? forged, bool? mac = false) => new()
    {
        Scope = NetworkPolicyScope.VirtualSwitch,
        Name = name,
        VirtualSwitchName = name,
        Configured = new SecurityPolicyFlags { ForgedTransmits = forged, MacChanges = mac },
        Effective = new SecurityPolicyFlags { ForgedTransmits = forged, MacChanges = mac },
    };

    private static NetworkSecurityPolicy PortGroup(
        string name, string vswitch, SecurityPolicyFlags? effective, SecurityPolicyFlags? configured = null) => new()
        {
            Scope = NetworkPolicyScope.PortGroup,
            Name = name,
            VirtualSwitchName = vswitch,
            Configured = configured ?? new SecurityPolicyFlags(),
            Effective = effective,
        };

    private static readonly SecurityPolicyFlags Rejecting =
        new() { AllowPromiscuous = false, ForgedTransmits = false, MacChanges = false };

    [Fact]
    public void Every_switch_and_port_group_rejecting_forged_transmits_passes()
    {
        var finding = One(Forged9, Host(
            switches: [Switch("vSwitch0", false)],
            portGroups: [PortGroup("VM Network", "vSwitch0", Rejecting)]));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Equal("Reject on all 1 vSwitches and 1 port groups", finding.Observed);
    }

    [Fact]
    public void A_port_group_inheriting_accept_fails_and_is_named()
    {
        // Configured says nothing (inherited); effective says what applies.
        var finding = One(Forged8, Host(
            switches: [Switch("vSwitch0", true)],
            portGroups: [PortGroup("VM Network", "vSwitch0", Rejecting with { ForgedTransmits = true })]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("Accept on vSwitch 'vSwitch0', port group 'VM Network' on vSwitch0", finding.Observed);
        Assert.Contains("Reject", finding.Expected, StringComparison.Ordinal);
        Assert.Contains("exception", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_effective_policy_is_judged_not_the_configured_one()
    {
        // The port group sets Reject itself, but the platform says Accept
        // applies; the effective reading is the one that decides.
        var finding = One(Forged9, Host(
            switches: [Switch("vSwitch0", false)],
            portGroups:
            [
                PortGroup(
                    "Uplink-PG",
                    "vSwitch0",
                    effective: Rejecting with { ForgedTransmits = true },
                    configured: new SecurityPolicyFlags { ForgedTransmits = false }),
            ]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("'Uplink-PG'", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_group_overriding_a_rejecting_switch_with_accept_fails()
    {
        var finding = One(MacChanges9, Host(
            switches: [Switch("vSwitch0", false, mac: false)],
            portGroups: [PortGroup("Cluster-PG", "vSwitch0", Rejecting with { MacChanges = true })]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("Accept on port group 'Cluster-PG' on vSwitch0", finding.Observed);
    }

    [Fact]
    public void Mac_changes_rejected_everywhere_passes()
    {
        var finding = One(MacChanges8, Host(
            switches: [Switch("vSwitch0", true, mac: false)],
            portGroups: [PortGroup("VM Network", "vSwitch0", Rejecting)]));

        // Forged transmits accepted on the switch is another control's business.
        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
    }

    [Fact]
    public void Port_group_security_not_reported_is_not_evaluated()
    {
        var finding = One(Forged9, Host(switches: [Switch("vSwitch0", false)]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("port groups", finding.Reason, StringComparison.Ordinal);
        Assert.Null(finding.Observed);
    }

    [Fact]
    public void A_port_group_whose_effective_policy_was_not_reported_is_not_evaluated()
    {
        var finding = One(Forged9, Host(
            switches: [Switch("vSwitch0", false)],
            portGroups: [PortGroup("VM Network", "vSwitch0", effective: null)]));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("'VM Network'", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_known_accept_is_a_verdict_even_when_another_port_group_was_not_read()
    {
        var finding = One(Forged9, Host(
            switches: [Switch("vSwitch0", true)],
            portGroups: [PortGroup("VM Network", "vSwitch0", effective: null)]));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("Not reported for port group 'VM Network'", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_with_no_standard_switches_passes()
    {
        var finding = One(Forged9, Host(switches: [], portGroups: []));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Equal("no standard vSwitches or port groups", finding.Observed);
    }
}
