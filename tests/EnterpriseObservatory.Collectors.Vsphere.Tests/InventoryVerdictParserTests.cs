using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Collection PR 1: verdicts vCenter already computes, and the M8.4 / M8.7
/// inputs, read into <c>Entity.Settings</c> keys.
/// </summary>
/// <remarks>
/// The fixtures follow the shapes measured live on 22 September 2026
/// (docs/measurements/collection-pr1-shapes.md): element names, nesting and
/// xsi:types as the probe printed them. Two are <strong>not</strong> live
/// shapes: a non-empty <c>configIssue</c> (every object on the measured
/// estate had none) and <c>storageStatusInfo</c> (absent there); both follow
/// the published vim25 schema.
/// </remarks>
public class InventoryVerdictParserTests
{
    private static PropertyObject Single(string type, string moRef, string propSets) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="{type}">{moRef}</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">x</val></propSet>
                  {propSets}
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    // --- configIssue: every type --------------------------------------------

    private const string NoIssues =
        """<propSet><name>configIssue</name><val xsi:type="ArrayOfEvent"></val></propSet>""";

    private const string TwoIssues = """
        <propSet>
          <name>configIssue</name>
          <val xsi:type="ArrayOfEvent">
            <Event xsi:type="HostNoRedundantManagementNetworkEvent"><key>1</key><chainId>1</chainId><createdTime>2026-09-22T08:00:00Z</createdTime><userName></userName></Event>
            <Event xsi:type="HostShortNameToIpFailedEvent"><key>2</key><chainId>2</chainId><createdTime>2026-09-22T08:00:00Z</createdTime><userName></userName></Event>
          </val>
        </propSet>
        """;

    [Theory]
    [InlineData("HostSystem")]
    [InlineData("VirtualMachine")]
    [InlineData("ClusterComputeResource")]
    [InlineData("Datastore")]
    public void An_empty_configIssue_is_a_count_of_zero_not_an_absence(string type)
    {
        // Measured: every object on the live estate carried configIssue as an
        // empty array. "vCenter says nothing is wrong" is a fact a check can
        // state; an absent key would be "we do not know".
        var verdicts = InventoryVerdictParser.Read(Single(type, "obj-1", NoIssues));

        Assert.Equal("0", verdicts[InventoryVerdicts.ConfigIssueCount]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.ConfigIssueTypes));
    }

    [Fact]
    public void Config_issues_are_counted_and_named_by_their_event_type()
    {
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", TwoIssues));

        Assert.Equal("2", verdicts[InventoryVerdicts.ConfigIssueCount]);
        Assert.Equal(
            "HostNoRedundantManagementNetworkEvent, HostShortNameToIpFailedEvent",
            verdicts[InventoryVerdicts.ConfigIssueTypes]);
    }

    [Fact]
    public void An_unread_configIssue_leaves_no_key_at_all()
    {
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", string.Empty));

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.ConfigIssueCount));
    }

    // --- host hardware health -------------------------------------------------

    private static string Sensor(string name, string state) => $"""
        <numericSensorInfo>
          <name>{name}</name>
          <healthState><label>{state}</label><summary>{state}</summary><key>{state}</key></healthState>
          <currentReading>1</currentReading><unitModifier>0</unitModifier><baseUnits>RPM</baseUnits>
          <rateUnits></rateUnits><sensorType>fan</sensorType><id>1</id><timeStamp>2026-09-22</timeStamp>
        </numericSensorInfo>
        """;

    private static readonly string Health = $"""
        <propSet>
          <name>runtime.healthSystemRuntime</name>
          <val xsi:type="HealthSystemRuntime">
            <systemHealthInfo>
              {Sensor("Fan 1", "green")}
              {Sensor("Fan 2", "green")}
              {Sensor("PSU 2", "red")}
              {Sensor("Temp 7", "unknown")}
            </systemHealthInfo>
            <hardwareStatusInfo>
              <memoryStatusInfo><name>DIMM A1</name><status><label>Green</label><summary>ok</summary><key>Green</key></status></memoryStatusInfo>
              <cpuStatusInfo><name>CPU 1</name><status><label>Yellow</label><summary>warn</summary><key>Yellow</key></status></cpuStatusInfo>
            </hardwareStatusInfo>
          </val>
        </propSet>
        """;

    [Fact]
    public void Hardware_sensors_are_counted_per_health_state()
    {
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", Health));

        Assert.Equal("4", verdicts[InventoryVerdicts.HardwareSensorCount]);
        Assert.Equal("2", verdicts[InventoryVerdicts.HardwareSensorsGreen]);
        Assert.Equal("0", verdicts[InventoryVerdicts.HardwareSensorsYellow]);
        Assert.Equal("1", verdicts[InventoryVerdicts.HardwareSensorsRed]);
        Assert.Equal("1", verdicts[InventoryVerdicts.HardwareSensorsUnknown]);
    }

    [Fact]
    public void Red_and_yellow_hardware_is_named_from_sensors_and_status_items_alike()
    {
        // hardwareStatusInfo capitalises its keys (Green, Yellow) where the
        // sensors do not; both are read, neither is trusted to one spelling.
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", Health));

        Assert.Equal("PSU 2 (red); CPU 1 (yellow)", verdicts[InventoryVerdicts.HardwareAlerting]);
    }

    [Fact]
    public void A_host_whose_health_was_not_read_says_nothing_about_its_hardware()
    {
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", string.Empty));

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.HardwareSensorCount));
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.HardwareAlerting));
    }

    // --- host certificate ------------------------------------------------------

    private static string CertificateProperty(DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=esx01", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notAfter.AddYears(-1), notAfter);

        // xsd:byte is signed: vim25 sends each byte as its own element.
        var bytes = string.Concat(certificate.RawData.Select(b =>
            $"<byte>{unchecked((sbyte)b).ToString(CultureInfo.InvariantCulture)}</byte>"));

        return $"<propSet><name>config.certificate</name><val xsi:type=\"ArrayOfByte\">{bytes}</val></propSet>";
    }

    [Fact]
    public void The_host_certificate_expiry_is_read_from_the_certificate_itself()
    {
        var notAfter = new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1", CertificateProperty(notAfter)));

        Assert.Equal(
            notAfter,
            DateTimeOffset.Parse(verdicts[InventoryVerdicts.CertificateNotAfter], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Only_the_expiry_and_the_fingerprint_of_the_host_certificate_are_kept()
    {
        // M8.7: the certificate itself (DER or PEM) never leaves the parser.
        var notAfter = new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=esx01", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(notAfter.AddYears(-1), notAfter);
        var bytes = string.Concat(certificate.RawData.Select(b =>
            $"<byte>{unchecked((sbyte)b).ToString(CultureInfo.InvariantCulture)}</byte>"));

        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1",
            $"<propSet><name>config.certificate</name><val xsi:type=\"ArrayOfByte\">{bytes}</val></propSet>"));

        Assert.Equal(
            [InventoryVerdicts.CertificateNotAfter, InventoryVerdicts.CertificateSha256],
            verdicts.Keys.Where(k => k.StartsWith("certificate.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), verdicts[InventoryVerdicts.CertificateSha256]);
        Assert.All(verdicts.Values, v => Assert.True(v.Length < 100, "no value carries the certificate itself"));
    }

    [Fact]
    public void A_certificate_that_does_not_parse_leaves_no_expiry_rather_than_a_guess()
    {
        var verdicts = InventoryVerdictParser.Read(Single("HostSystem", "host-1",
            "<propSet><name>config.certificate</name><val xsi:type=\"ArrayOfByte\"><byte>1</byte><byte>2</byte></val></propSet>"));

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.CertificateNotAfter));
    }

    // --- virtual machine -------------------------------------------------------

    private const string Devices = """
        <propSet>
          <name>config.hardware.device</name>
          <val xsi:type="ArrayOfVirtualDevice">
            <VirtualDevice xsi:type="VirtualCdrom">
              <key>3000</key>
              <backing xsi:type="VirtualCdromIsoBackingInfo"><fileName>[iso] x.iso</fileName></backing>
              <connectable><startConnected>true</startConnected><allowGuestControl>true</allowGuestControl><connected>true</connected><status>ok</status></connectable>
            </VirtualDevice>
            <VirtualDevice xsi:type="VirtualCdrom">
              <key>3001</key>
              <backing xsi:type="VirtualCdromRemotePassthroughBackingInfo"><deviceName></deviceName></backing>
              <connectable><startConnected>false</startConnected><allowGuestControl>true</allowGuestControl><connected>true</connected><status>ok</status></connectable>
            </VirtualDevice>
            <VirtualDevice xsi:type="VirtualCdrom">
              <key>3002</key>
              <backing xsi:type="VirtualCdromRemoteAtapiBackingInfo"><deviceName></deviceName></backing>
              <connectable><startConnected>false</startConnected><allowGuestControl>true</allowGuestControl><connected>false</connected><status>untried</status></connectable>
            </VirtualDevice>
            <VirtualDevice xsi:type="VirtualDisk"><key>2000</key><capacityInKB>1</capacityInKB></VirtualDevice>
          </val>
        </propSet>
        """;

    private const string VmRuntime = """
        <propSet><name>runtime.connectionState</name><val xsi:type="VirtualMachineConnectionState">connected</val></propSet>
        <propSet><name>runtime.consolidationNeeded</name><val xsi:type="xsd:boolean">true</val></propSet>
        """;

    [Fact]
    public void Connected_CD_drives_are_counted_and_ISO_backed_ones_apart()
    {
        // M8.4: a connected ISO blocks vMotion when the ISO lives on storage
        // the destination cannot see. Measured live: 130 CD drives, 3 backing
        // types, 2 connected.
        var verdicts = InventoryVerdictParser.Read(Single("VirtualMachine", "vm-1", Devices));

        Assert.Equal("2", verdicts[InventoryVerdicts.ConnectedCdroms]);
        Assert.Equal("1", verdicts[InventoryVerdicts.ConnectedIsoCdroms]);
    }

    [Fact]
    public void A_machine_with_devices_but_no_CD_drive_reports_zero_connected()
    {
        var verdicts = InventoryVerdictParser.Read(Single("VirtualMachine", "vm-1", """
            <propSet>
              <name>config.hardware.device</name>
              <val xsi:type="ArrayOfVirtualDevice">
                <VirtualDevice xsi:type="VirtualDisk"><key>2000</key><capacityInKB>1</capacityInKB></VirtualDevice>
              </val>
            </propSet>
            """));

        Assert.Equal("0", verdicts[InventoryVerdicts.ConnectedCdroms]);
    }

    [Fact]
    public void Consolidation_and_connection_state_are_carried_as_vCenter_words_them()
    {
        var verdicts = InventoryVerdictParser.Read(Single("VirtualMachine", "vm-1", VmRuntime));

        Assert.Equal("true", verdicts[InventoryVerdicts.ConsolidationNeeded]);
        Assert.Equal("connected", verdicts[InventoryVerdicts.ConnectionState]);
    }

    [Fact]
    public void A_machine_whose_devices_were_not_read_says_nothing_about_its_CD_drives()
    {
        var verdicts = InventoryVerdictParser.Read(Single("VirtualMachine", "vm-1", VmRuntime));

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.ConnectedCdroms));
    }

    // --- cluster EVC -------------------------------------------------------------

    [Fact]
    public void A_cluster_summary_with_an_EVC_key_has_EVC_enabled()
    {
        // summary.currentEVCModeKey is refused as InvalidProperty (measured):
        // ClusterComputeResource.summary is declared as ComputeResourceSummary.
        // So summary is read whole, the way configurationEx is.
        var verdicts = InventoryVerdictParser.Read(Single("ClusterComputeResource", "domain-c1", """
            <propSet>
              <name>summary</name>
              <val xsi:type="ClusterComputeResourceSummary">
                <totalCpu>1</totalCpu><numHosts>4</numHosts>
                <currentEVCModeKey>intel-icelake</currentEVCModeKey>
                <usageSummary><totalVmCount>3</totalVmCount></usageSummary>
              </val>
            </propSet>
            """));

        Assert.Equal("true", verdicts[InventoryVerdicts.EvcEnabled]);
        Assert.Equal("intel-icelake", verdicts[InventoryVerdicts.EvcModeKey]);
    }

    [Fact]
    public void A_cluster_summary_without_an_EVC_key_has_EVC_disabled()
    {
        // Measured: 2 of 3 live clusters carried no currentEVCModeKey. The
        // summary was read, so its absence is "EVC is off", not "unknown".
        var verdicts = InventoryVerdictParser.Read(Single("ClusterComputeResource", "domain-c1", """
            <propSet>
              <name>summary</name>
              <val xsi:type="ClusterComputeResourceSummary">
                <totalCpu>1</totalCpu><numHosts>4</numHosts>
                <usageSummary><totalVmCount>3</totalVmCount></usageSummary>
              </val>
            </propSet>
            """));

        Assert.Equal("false", verdicts[InventoryVerdicts.EvcEnabled]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.EvcModeKey));
    }

    [Fact]
    public void A_cluster_whose_summary_was_not_read_says_nothing_about_EVC()
    {
        var verdicts = InventoryVerdictParser.Read(Single("ClusterComputeResource", "domain-c1", string.Empty));

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.EvcEnabled));
    }

    // --- datastore -----------------------------------------------------------------

    private static string Mount(string host, bool mounted) => $"""
        <DatastoreHostMount xsi:type="DatastoreHostMount">
          <key type="HostSystem">{host}</key>
          <mountInfo><path>/vmfs/volumes/x</path><accessMode>readWrite</accessMode><mounted>{(mounted ? "true" : "false")}</mounted><accessible>true</accessible></mountInfo>
        </DatastoreHostMount>
        """;

    [Fact]
    public void A_datastore_counts_the_hosts_that_actually_mount_it()
    {
        // M8.4: a datastore mounted on one host pins its VMs to that host.
        // Measured live: 12 of 41 datastores on one host.
        var verdicts = InventoryVerdictParser.Read(Single("Datastore", "datastore-1", $"""
            <propSet><name>summary.maintenanceMode</name><val xsi:type="xsd:string">normal</val></propSet>
            <propSet>
              <name>host</name>
              <val xsi:type="ArrayOfDatastoreHostMount">
                {Mount("host-1", mounted: true)}
                {Mount("host-2", mounted: true)}
                {Mount("host-3", mounted: false)}
              </val>
            </propSet>
            """));

        Assert.Equal("2", verdicts[InventoryVerdicts.MountedHostCount]);
        Assert.Equal("normal", verdicts[InventoryVerdicts.MaintenanceMode]);
    }

    // --- the request list -------------------------------------------------------

    [Theory]
    [InlineData("HostSystem", "configIssue")]
    [InlineData("HostSystem", "runtime.healthSystemRuntime")]
    [InlineData("HostSystem", "config.certificate")]
    [InlineData("VirtualMachine", "configIssue")]
    [InlineData("VirtualMachine", "runtime.connectionState")]
    [InlineData("VirtualMachine", "runtime.consolidationNeeded")]
    [InlineData("VirtualMachine", "config.hardware.device")]
    [InlineData("ClusterComputeResource", "configIssue")]
    [InlineData("ClusterComputeResource", "summary")]
    [InlineData("Datastore", "configIssue")]
    [InlineData("Datastore", "summary.maintenanceMode")]
    [InlineData("Datastore", "host")]
    public void Every_path_that_passed_the_live_gate_is_requested(string type, string path) =>
        Assert.Contains(path, VsphereClient.InventoryPropertiesFor(type));

    [Theory]
    [InlineData("ClusterComputeResource", "summary.currentEVCModeKey")] // InvalidProperty, measured
    [InlineData("HostSystem", "configManager.certificateManager")]      // its certificateInfo: NoPermission
    public void A_path_that_failed_the_live_gate_is_never_requested(string type, string path) =>
        Assert.DoesNotContain(path, VsphereClient.InventoryPropertiesFor(type));

    [Fact]
    public void Paths_every_object_carries_are_expected_and_ones_that_may_be_empty_are_not()
    {
        var rows = VsphereClient.MeasureCoverage(PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects><obj type="HostSystem">host-1</obj><propSet><name>name</name><val>h</val></propSet></objects>
                <objects><obj type="VirtualMachine">vm-1</obj><propSet><name>name</name><val>v</val></propSet></objects>
                <objects><obj type="ClusterComputeResource">domain-c1</obj><propSet><name>name</name><val>c</val></propSet></objects>
                <objects><obj type="Datastore">datastore-1</obj><propSet><name>name</name><val>d</val></propSet></objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

        bool Expected(string type, string path) =>
            rows.Any(r => r.ObjectType == type && r.Property == path);

        Assert.True(Expected("VirtualMachine", "runtime.connectionState"));
        Assert.True(Expected("VirtualMachine", "runtime.consolidationNeeded"));
        Assert.True(Expected("VirtualMachine", "config.hardware.device"));
        Assert.True(Expected("ClusterComputeResource", "summary"));

        // Empty arrays, optional in the schema, or absent on a host vCenter
        // cannot reach: none of these is proof of a failed read.
        Assert.False(Expected("HostSystem", "configIssue"));
        Assert.False(Expected("HostSystem", "runtime.healthSystemRuntime"));
        Assert.False(Expected("HostSystem", "config.certificate"));
        Assert.False(Expected("Datastore", "summary.maintenanceMode"));
        Assert.False(Expected("Datastore", "host"));
    }

    // --- into the entity ----------------------------------------------------------

    [Fact]
    public void The_mappers_carry_the_verdicts()
    {
        var vm = VsphereClient.ToVirtualMachine(Single("VirtualMachine", "vm-1", VmRuntime));
        var host = VsphereClient.ToHost(Single("HostSystem", "host-1", TwoIssues));

        Assert.Equal("true", vm.Verdicts[InventoryVerdicts.ConsolidationNeeded]);
        Assert.Equal("2", host.Verdicts[InventoryVerdicts.ConfigIssueCount]);
    }
}

/// <summary>The verdicts reach <c>Entity.Settings</c> beside what was there.</summary>
public class InventoryVerdictSettingsTests
{
    private sealed class FixedClock : Application.Collection.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeApi(VsphereInventoryPayload payload) : IVsphereInventoryApi
    {
        public string InstanceId => "vc-1";

        public Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken ct) =>
            Task.FromResult(payload);
    }

    private static Dictionary<string, string> Verdict(string key, string value) =>
        new(StringComparer.OrdinalIgnoreCase) { [key] = value };

    [Fact]
    public async Task Every_entity_kind_carries_its_verdicts_in_settings_without_losing_the_rest()
    {
        var payload = new VsphereInventoryPayload
        {
            VCenterName = "vc01",
            Hosts =
            [
                new VsphereHost
                {
                    MoRef = "host-1", Name = "esx01", ConnectionState = "connected",
                    AdvancedSettings = new Dictionary<string, string> { ["Syslog.global.logHost"] = "udp://x" },
                    Verdicts = Verdict(InventoryVerdicts.ConfigIssueCount, "0"),
                },
            ],
            VirtualMachines =
            [
                new VsphereVirtualMachine
                {
                    MoRef = "vm-1", Name = "app", PowerState = "poweredOn",
                    Verdicts = Verdict(InventoryVerdicts.ConsolidationNeeded, "true"),
                },
            ],
            Clusters =
            [
                new VsphereCluster
                {
                    MoRef = "domain-c1", Name = "c1", HighAvailabilityEnabled = true, DrsEnabled = true,
                    HaSettings = new Dictionary<string, string> { [ClusterHaSettings.Enabled] = "true" },
                    Verdicts = Verdict(InventoryVerdicts.EvcEnabled, "false"),
                },
            ],
            Datastores =
            [
                new VsphereDatastore
                {
                    MoRef = "datastore-1", Name = "ds1", Accessible = true, Type = "VMFS",
                    Verdicts = Verdict(InventoryVerdicts.MountedHostCount, "1"),
                },
            ],
        };

        var snapshot = await new VsphereInventorySource(new FakeApi(payload), new FixedClock())
            .ReadAsync(CancellationToken.None);

        IReadOnlyDictionary<string, string> SettingsOf(string moRef) =>
            snapshot.Entities.Single(e => e.Id.ToString().EndsWith(moRef, StringComparison.Ordinal)).Settings;

        Assert.Equal("0", SettingsOf("host-1")[InventoryVerdicts.ConfigIssueCount]);
        Assert.Equal("udp://x", SettingsOf("host-1")["Syslog.global.logHost"]);

        Assert.Equal("true", SettingsOf("vm-1")[InventoryVerdicts.ConsolidationNeeded]);
        Assert.Equal("poweredOn", SettingsOf("vm-1")["powerState"]);

        Assert.Equal("false", SettingsOf("domain-c1")[InventoryVerdicts.EvcEnabled]);
        Assert.Equal("true", SettingsOf("domain-c1")[ClusterHaSettings.Enabled]);

        Assert.Equal("1", SettingsOf("datastore-1")[InventoryVerdicts.MountedHostCount]);
        Assert.Equal("VMFS", SettingsOf("datastore-1")["type"]);
    }

    [Fact]
    public async Task The_vcenter_entity_carries_its_endpoint_certificate()
    {
        var payload = new VsphereInventoryPayload
        {
            VCenterName = "vc01",
            VCenterVerdicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [InventoryVerdicts.CertificateNotAfter] = "2027-03-01T12:00:00.0000000+00:00",
                [InventoryVerdicts.CertificateSha256] = "AB12",
            },
        };

        var snapshot = await new VsphereInventorySource(new FakeApi(payload), new FixedClock())
            .ReadAsync(CancellationToken.None);

        var vCenter = snapshot.Entities.Single(e => e.Kind == EnterpriseObservatory.Domain.EntityKind.VCenter);
        Assert.Equal("AB12", vCenter.Settings[InventoryVerdicts.CertificateSha256]);
        Assert.Equal("2027-03-01T12:00:00.0000000+00:00", vCenter.Settings[InventoryVerdicts.CertificateNotAfter]);
    }
}
