using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The inventory read in two tiers: a fast one for topology and state, and a
/// configuration one for the heavy, slowly changing properties, carried into
/// every fast read with its read time.
/// </summary>
/// <remarks>
/// The property shapes are copied from the captured XML in
/// <c>VsphereInventoryPropertyTests</c> (<c>HostWithPaths</c>,
/// <c>VmWithLayout</c>) and <c>AdvancedSettingsTests</c>, never typed afresh.
/// </remarks>
public class TwoTierInventoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 20, 38, 0, TimeSpan.Zero);

    [Fact]
    public void The_two_tiers_ask_for_disjoint_paths_and_the_heavy_ones_are_slow()
    {
        foreach (var type in new[] { "HostSystem", "VirtualMachine" })
        {
            Assert.Empty(VsphereClient.FastPropertiesFor(type).Intersect(VsphereClient.ConfigurationPropertiesFor(type)));
        }

        Assert.Equal(
            ["config.option", "config.storageDevice.scsiLun", "config.certificate", "runtime.healthSystemRuntime"],
            VsphereClient.ConfigurationPropertiesFor("HostSystem"));
        Assert.Equal(
            ["config.hardware.device", "layoutEx.file", "layoutEx.disk"],
            VsphereClient.ConfigurationPropertiesFor("VirtualMachine"));

        // Topology, state and relationships stay on the fast read.
        Assert.Contains("parent", VsphereClient.FastPropertiesFor("HostSystem"));
        Assert.Contains("config.storageDevice.multipathInfo", VsphereClient.FastPropertiesFor("HostSystem"));
        Assert.Contains("runtime.host", VsphereClient.FastPropertiesFor("VirtualMachine"));
        Assert.Contains("snapshot", VsphereClient.FastPropertiesFor("VirtualMachine"));
    }

    [Fact]
    public async Task Each_tier_sends_only_its_own_paths()
    {
        var server = new TwoTierServer();
        var client = Client(server, new ManualTime(T0));

        await client.RetrieveConfigurationAsync(CancellationToken.None);
        await client.RetrieveInventoryAsync(CancellationToken.None);

        Assert.Equal(
            [.. VsphereClient.ConfigurationPropertiesFor("HostSystem"), .. VsphereClient.ConfigurationPropertiesFor("VirtualMachine")],
            PathsOf(server.SlowBodies.Single()));
        Assert.DoesNotContain(PathsOf(server.FastBodies.Single()), p =>
            VsphereClient.ConfigurationPropertiesFor("HostSystem").Contains(p) ||
            VsphereClient.ConfigurationPropertiesFor("VirtualMachine").Contains(p));
    }

    [Fact]
    public async Task A_fresh_collector_s_first_fast_read_asks_both_tiers_and_the_next_only_the_fast_one()
    {
        // No "not read" window after a restart: with nothing carried, the
        // fast read seeds the carry itself.
        var time = new ManualTime(T0);
        var server = new TwoTierServer();
        var client = Client(server, time);

        var first = await client.RetrieveInventoryAsync(CancellationToken.None);

        var host = Assert.Single(first.Hosts);
        Assert.Equal("udp://10.0.0.5:514", host.AdvancedSettings["Syslog.global.logHost"]);
        Assert.Equal("naa.600508b1001cb7368fc569b9146949ad", host.StoragePaths[0].StorageDeviceId);
        Assert.Equal(T0, host.ConfigurationReadAtUtc);
        Assert.Equal(42949672960L + 8589934592L, Assert.Single(first.VirtualMachines).SnapshotBytes);
        Assert.True(first.Coverage.Single(c => c.Property == "config.option").IsComplete);
        Assert.Contains("config.option", PathsOf(server.FastBodies[0]));
        Assert.Contains("layoutEx.file", PathsOf(server.FastBodies[0]));

        time.Now = T0.AddMinutes(2);
        var second = await client.RetrieveInventoryAsync(CancellationToken.None);

        Assert.DoesNotContain(PathsOf(server.FastBodies[1]), p =>
            VsphereClient.ConfigurationPropertiesFor("HostSystem").Contains(p) ||
            VsphereClient.ConfigurationPropertiesFor("VirtualMachine").Contains(p));
        var carried = Assert.Single(second.Hosts);
        Assert.Equal("udp://10.0.0.5:514", carried.AdvancedSettings["Syslog.global.logHost"]);
        Assert.Equal(T0, carried.ConfigurationReadAtUtc);
        Assert.Empty(server.SlowBodies);
    }

    [Fact]
    public async Task After_the_seeding_fast_read_the_first_configuration_pass_asks_nothing_and_the_next_one_reads()
    {
        var time = new ManualTime(T0);
        var server = new TwoTierServer();
        var client = Client(server, time);
        var source = new VsphereConfigurationSource(client, freshFor: TimeSpan.FromMinutes(15));

        await client.RetrieveInventoryAsync(CancellationToken.None);
        var callsAfterSeed = server.Calls.Count;

        time.Now = T0.AddSeconds(30);
        var first = await source.ReadAsync(CancellationToken.None);

        Assert.True(first.Skipped);
        Assert.Empty(first.Failures);
        Assert.Equal(callsAfterSeed, server.Calls.Count);

        time.Now = T0.AddMinutes(15);
        var next = await source.ReadAsync(CancellationToken.None);

        Assert.False(next.Skipped);
        Assert.Equal(2, next.ObjectsRead);
        Assert.Single(server.SlowBodies);
    }

    [Fact]
    public void A_configuration_path_is_not_counted_on_an_object_that_tier_has_not_reached()
    {
        // "Not asked yet" is not "blind".
        var objects = PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects><obj type="HostSystem">host-1</obj><propSet><name>name</name><val>esx01</val></propSet></objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects;

        var rows = VsphereClient.MeasureCoverage(objects, new HashSet<string>(StringComparer.Ordinal));

        Assert.DoesNotContain(rows, c => c.Property == "config.option");
        Assert.Contains(rows, c => c.Property == "name" && c.IsComplete);
    }

    [Fact]
    public async Task A_fast_read_carries_the_configuration_and_does_not_delete_it()
    {
        var time = new ManualTime(T0);
        var client = Client(new TwoTierServer(), time);

        await client.RetrieveConfigurationAsync(CancellationToken.None);
        time.Now = T0.AddMinutes(2);
        await client.RetrieveInventoryAsync(CancellationToken.None);
        time.Now = T0.AddMinutes(4);
        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        var host = Assert.Single(payload.Hosts);
        Assert.Equal("udp://10.0.0.5:514", host.AdvancedSettings["Syslog.global.logHost"]);
        Assert.Equal("naa.600508b1001cb7368fc569b9146949ad", host.StoragePaths[0].StorageDeviceId);
        Assert.Equal(T0, host.ConfigurationReadAtUtc);

        var vm = Assert.Single(payload.VirtualMachines);
        Assert.Equal(42949672960L + 8589934592L, vm.SnapshotBytes);
        Assert.Equal(T0, vm.ConfigurationReadAtUtc);

        var option = payload.Coverage.Single(c => c.Property == "config.option");
        Assert.True(option.IsComplete);

        // And through the source, into the entity's settings with its read time.
        var snapshot = await new VsphereInventorySource(new Fixed(payload), new FixedClock(time.Now)).ReadAsync(CancellationToken.None);
        var settings = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost).Settings;
        Assert.Equal("udp://10.0.0.5:514", settings["Syslog.global.logHost"]);
        Assert.Equal("2026-09-23T20:38:00.0000000Z", settings[InventoryVerdicts.ConfigurationReadAtUtc]);
    }

    [Fact]
    public async Task A_configuration_read_updates_only_its_own_keys()
    {
        var time = new ManualTime(T0);
        var server = new TwoTierServer();
        var client = Client(server, time);

        await client.RetrieveConfigurationAsync(CancellationToken.None);
        server.LogHost = "udp://10.0.0.9:514";
        server.HostName = "esx07-renamed.corp.local";
        time.Now = T0.AddMinutes(15);
        await client.RetrieveConfigurationAsync(CancellationToken.None);

        var host = Assert.Single((await client.RetrieveInventoryAsync(CancellationToken.None)).Hosts);

        Assert.Equal("udp://10.0.0.9:514", host.AdvancedSettings["Syslog.global.logHost"]);
        Assert.Equal(T0.AddMinutes(15), host.ConfigurationReadAtUtc);

        // The name came from the fast read, the only one that asks for it.
        Assert.Equal("esx07-renamed.corp.local", host.Name);
        Assert.Equal(2, server.SlowBodies.Count);
        Assert.DoesNotContain("name", PathsOf(server.SlowBodies[^1]));
    }

    [Fact]
    public async Task Past_the_carry_forward_limit_the_old_reading_is_not_used_and_the_fast_read_reseeds()
    {
        var time = new ManualTime(T0);
        var server = new TwoTierServer();
        var client = Client(server, time);

        await client.RetrieveConfigurationAsync(CancellationToken.None);
        server.LogHost = "udp://10.0.0.9:514";

        // ADR-0026's two-day carry-forward limit.
        var later = T0 + TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1);
        time.Now = later;

        var host = Assert.Single((await client.RetrieveInventoryAsync(CancellationToken.None)).Hosts);

        Assert.Equal(later, host.ConfigurationReadAtUtc);
        Assert.Equal("udp://10.0.0.9:514", host.AdvancedSettings["Syslog.global.logHost"]);
        Assert.Contains("config.option", PathsOf(server.FastBodies.Single()));
    }

    [Fact]
    public async Task A_configuration_read_over_budget_keeps_what_it_read_and_says_it_is_partial()
    {
        using var cutOff = new CancellationTokenSource();
        var time = new ManualTime(T0);
        var server = new TwoTierServer { SlowFirstPageHasMore = true, OnSlowNextPage = cutOff.Cancel };
        var client = Client(server, time);

        var read = await new VsphereConfigurationSource(client).ReadAsync(cutOff.Token);

        Assert.False(read.Complete);
        Assert.Equal(2, read.ObjectsRead);
        var timeout = Assert.Single(read.Failures);
        Assert.Equal(CollectionFailureKind.Timeout, timeout.Kind);
        Assert.Contains("CancelRetrievePropertiesEx", server.Calls);
        Assert.Contains("DestroyView", server.Calls);

        // The page that arrived is carried.
        var host = Assert.Single((await client.RetrieveInventoryAsync(CancellationToken.None)).Hosts);
        Assert.Equal(T0, host.ConfigurationReadAtUtc);
    }

    private static List<string> PathsOf(string body) =>
        [.. XDocument.Parse(body).Descendants().Where(e => e.Name.LocalName == "pathSet").Select(e => e.Value)];

    private static VsphereClient Client(TwoTierServer server, TimeProvider time)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        return new VsphereClient(new VsphereSessionChannel(server, options), options) { Time = time };
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class Fixed(VsphereInventoryPayload payload) : IVsphereInventoryApi
    {
        public string InstanceId => "vc-test";

        public Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(payload);
    }

    /// <summary>A vCenter that answers the fast and the configuration retrieval apart.</summary>
    private sealed class TwoTierServer : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        public List<string> FastBodies { get; } = [];

        public List<string> SlowBodies { get; } = [];

        public string LogHost { get; set; } = "udp://10.0.0.5:514";

        public string HostName { get; set; } = "esx07.corp.local";

        public bool SlowFirstPageHasMore { get; init; }

        public Action? OnSlowNextPage { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(method);

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContent);
                case "Login":
                    return Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");
                case "CreateContainerView":
                    return Ok("<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">session[1]view-1</returnval></CreateContainerViewResponse>");
                case "DestroyView":
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");
                case "CancelRetrievePropertiesEx":
                    return Ok("<CancelRetrievePropertiesExResponse xmlns=\"urn:vim25\" />");
                case "ContinueRetrievePropertiesEx":
                    OnSlowNextPage?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Ok(Page(string.Empty, string.Empty));
                case "RetrievePropertiesEx" when body.Contains("type=\"Folder\"", StringComparison.Ordinal):
                    return Ok("<RetrievePropertiesExResponse xmlns=\"urn:vim25\" />");
                case "RetrievePropertiesEx" when body.Contains(">parent<", StringComparison.Ordinal):
                    FastBodies.Add(body);
                    return Ok(Page(
                        string.Empty,
                        body.Contains(">config.option<", StringComparison.Ordinal) ? BothObjects() : FastObjects));
                case "RetrievePropertiesEx" when body.Contains(">config.option<", StringComparison.Ordinal):
                    SlowBodies.Add(body);
                    return Ok(Page(SlowFirstPageHasMore ? "<token>session[1]token-1</token>" : string.Empty, SlowObjects));
                case "RetrievePropertiesEx":
                    // Anything else (the view count) answers empty.
                    return Ok("<RetrievePropertiesExResponse xmlns=\"urn:vim25\" />");
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private static string Page(string token, string objects) => $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                {token}
                {objects}
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        /// <summary>A seeding read's reply: each object with both tiers' properties.</summary>
        private string BothObjects()
        {
            const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XElement Parse(string objects) => XElement.Parse($"<r xmlns:xsi=\"{Xsi}\">{objects}</r>");

            var fast = Parse(FastObjects);
            var slow = Parse(SlowObjects);

            foreach (var o in fast.Elements("objects"))
            {
                o.Add(slow.Elements("objects")
                    .Single(s => s.Element("obj")!.Value == o.Element("obj")!.Value)
                    .Elements("propSet"));
            }

            return string.Concat(fast.Elements());
        }

        private string FastObjects => $"""
            <objects>
              <obj type="HostSystem">host-3615</obj>
              <propSet><name>name</name><val>{HostName}</val></propSet>
              <propSet>
                <name>config.storageDevice.multipathInfo</name>
                <val xsi:type="HostMultipathInfo">
                  <lun>
                    <key>key-vim.host.MultipathInfo.LogicalUnit-0200000000600508b1001cb736</key>
                    <id>0200000000600508b1001cb736</id>
                    <lun type="ScsiLun">key-vim.host.ScsiDisk-0200000000600508b1001cb736</lun>
                    <path>
                      <key>key-vim.host.MultipathInfo.Path-vmhba0:C0:T0:L1</key>
                      <name>vmhba0:C0:T0:L1</name>
                      <pathState>active</pathState>
                      <adapter type="HostFibreChannelHba">key-vim.host.FibreChannelHba-vmhba0</adapter>
                    </path>
                  </lun>
                </val>
              </propSet>
            </objects>
            <objects>
              <obj type="VirtualMachine">vm-88</obj>
              <propSet><name>name</name><val>fs</val></propSet>
            </objects>
            """;

        private string SlowObjects => $"""
            <objects>
              <obj type="HostSystem">host-3615</obj>
              <propSet>
                <name>config.option</name>
                <val xsi:type="ArrayOfOptionValue">
                  <OptionValue xsi:type="OptionValue">
                    <key>Syslog.global.logHost</key>
                    <value xsi:type="xsd:string">{LogHost}</value>
                  </OptionValue>
                  <OptionValue xsi:type="OptionValue">
                    <key>Misc.LogToSerial</key>
                    <value xsi:type="xsd:string">0</value>
                  </OptionValue>
                </val>
              </propSet>
              <propSet>
                <name>config.storageDevice.scsiLun</name>
                <val>
                  <ScsiLun xsi:type="HostScsiDisk">
                    <key>key-vim.host.ScsiDisk-0200000000600508b1001cb736</key>
                    <uuid>0200000000600508b1001cb736</uuid>
                    <canonicalName>naa.600508b1001cb7368fc569b9146949ad</canonicalName>
                    <deviceName>/vmfs/devices/disks/naa.600508b1001cb7368fc569b9146949ad</deviceName>
                  </ScsiLun>
                </val>
              </propSet>
            </objects>
            <objects>
              <obj type="VirtualMachine">vm-88</obj>
              <propSet>
                <name>layoutEx.file</name>
                <val>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>1</key><name>[vmfs01] fs/fs-flat.vmdk</name><type>diskExtent</type>
                    <size>107374182400</size><uniqueSize>107374182400</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>2</key><name>[vmfs01] fs/fs-000001-delta.vmdk</name><type>diskExtent</type>
                    <size>42949672960</size><uniqueSize>42949672960</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>3</key><name>[vmfs01] fs/fs-Snapshot1.vmsn</name><type>snapshotMemory</type>
                    <size>8589934592</size><uniqueSize>8589934592</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                </val>
              </propSet>
              <propSet>
                <name>layoutEx.disk</name>
                <val>
                  <VirtualMachineFileLayoutExDiskLayout>
                    <key>2000</key>
                    <chain><fileKey>1</fileKey></chain>
                    <chain><fileKey>2</fileKey></chain>
                  </VirtualMachineFileLayoutExDiskLayout>
                </val>
              </propSet>
            </objects>
            """;

        private const string ServiceContent = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
                  <rootFolder type="Folder">group-d1</rootFolder>
                  <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
                  <viewManager type="ViewManager">ViewManager</viewManager>
                  <about><name>vc-test</name><apiVersion>8.0.3.0</apiVersion></about>
                  <sessionManager type="SessionManager">SessionManager</sessionManager>
                  <perfManager type="PerformanceManager">PerfMgr</perfManager>
                </returnval></RetrieveServiceContentResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        private static HttpResponseMessage Ok(string xml) =>
            new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
    }
}
