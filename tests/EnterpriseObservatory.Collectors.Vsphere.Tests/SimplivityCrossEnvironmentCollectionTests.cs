using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// S2b (eo-simplivity): the OVC's reservation and resource pool, the
/// vmkernel and switch MTU, and the lockdown exception users, read into
/// <c>Entity.Settings</c> keys.
/// </summary>
/// <remarks>
/// The fixtures under Fixtures/S2b are live KBVc01 replies (25 September
/// 2026), masked: addresses, MACs, the switch UUID, moRef numbers and one
/// port group name. The only shape not seen live is a non-empty lockdown
/// exception list (every measured host had lockdown off and an empty list);
/// it follows the published schema, <c>returnval</c> strings.
/// </remarks>
public class SimplivityCrossEnvironmentCollectionTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "S2b", name));

    private static PropertyObject Object(string fixture) =>
        Assert.Single(PropertyCollectorParser.ParsePage(Fixture(fixture)).Objects);

    [Fact]
    public void The_ovc_reservation_and_resource_pool_are_carried()
    {
        var vm = Object("vm-reservation.xml");
        var pool = Object("vm-resourcepool.xml");
        var merged = vm with { Values = new Dictionary<string, string>(vm.Values.Concat(pool.Values), StringComparer.Ordinal) };

        var verdicts = InventoryVerdictParser.Read(merged);

        Assert.Equal("124928", verdicts[InventoryVerdicts.MemoryReservationMb]);
        Assert.Equal("resgroup-302", verdicts[InventoryVerdicts.ResourcePool]);
    }

    [Fact]
    public void The_cluster_root_resource_pool_is_carried() =>
        Assert.Equal("resgroup-302", InventoryVerdictParser.Read(Object("cluster-resourcepool.xml"))[InventoryVerdicts.ResourcePool]);

    [Fact]
    public void Vmkernel_mtu_is_carried_per_standard_port_group_and_a_distributed_port_is_left_out()
    {
        var verdicts = InventoryVerdictParser.Read(Object("host-vnic.xml"));

        // vmk0 on a distributed port (empty port group) is not in the list.
        Assert.Equal("SVT_StorPG=9000\nVMkernel=9000", verdicts[InventoryVerdicts.VmkernelMtu]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.PortGroupSwitchMtu));
    }

    [Fact]
    public void Each_standard_port_group_carries_its_switch_mtu()
    {
        var verdicts = InventoryVerdictParser.Read(Object("host-vswitch.xml"));

        Assert.Equal(
            "VMkernel=9000\nSVT_FedPortGroup=9000\nSVT_StoragePortGroup=9000\nSVT_StorPG=9000\nLocalNET=1500",
            verdicts[InventoryVerdicts.PortGroupSwitchMtu]);
    }

    [Fact]
    public void An_empty_lockdown_exception_reply_is_an_empty_list() =>
        Assert.Empty(VsphereClient.ParseLockdownExceptions(Fixture("lockdown-exceptions-none.xml")));

    [Fact]
    public void The_new_paths_are_asked_on_the_fast_read()
    {
        Assert.Contains("config.memoryAllocation.reservation", VsphereClient.FastPropertiesFor("VirtualMachine"));
        Assert.Contains("resourcePool", VsphereClient.FastPropertiesFor("VirtualMachine"));
        Assert.Contains("resourcePool", VsphereClient.FastPropertiesFor("ClusterComputeResource"));
        Assert.Contains("config.network.vnic", VsphereClient.FastPropertiesFor("HostSystem"));
        Assert.Contains("configManager.hostAccessManager", VsphereClient.FastPropertiesFor("HostSystem"));
    }

    [Fact]
    public async Task Only_a_host_in_lockdown_mode_is_asked_for_its_exception_users()
    {
        var server = new LockdownServer();

        var payload = await Client(server).RetrieveInventoryAsync(CancellationToken.None);

        var body = Assert.Single(server.LockdownBodies);
        Assert.Contains(">hostAccessManager-1<", body, StringComparison.Ordinal);
        Assert.Equal("svc-a\nsvc-b", payload.Hosts.Single(h => h.MoRef == "host-1").Verdicts[InventoryVerdicts.LockdownExceptions]);
        Assert.False(payload.Hosts.Single(h => h.MoRef == "host-2").Verdicts.ContainsKey(InventoryVerdicts.LockdownExceptions));
        Assert.Empty(payload.Failures);
    }

    [Fact]
    public async Task A_refused_lockdown_read_is_a_failure_and_the_list_stays_unread()
    {
        var payload = await Client(new LockdownServer { Refuse = true }).RetrieveInventoryAsync(CancellationToken.None);

        Assert.False(payload.Hosts.Single(h => h.MoRef == "host-1").Verdicts.ContainsKey(InventoryVerdicts.LockdownExceptions));
        var failure = Assert.Single(payload.Failures);
        Assert.Contains("QueryLockdownExceptions", failure.Target, StringComparison.Ordinal);
        Assert.True(failure.IsPermissionDenied);
    }

    private static VsphereClient Client(HttpMessageHandler server)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        return new VsphereClient(new VsphereSessionChannel(server, options), options);
    }

    private sealed class LockdownServer : HttpMessageHandler
    {
        public bool Refuse { get; init; }

        public List<string> LockdownBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContent);
                case "Login":
                    return Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");
                case "CreateContainerView":
                    return Ok("<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>");
                case "DestroyView":
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");
                case "QueryLockdownExceptions":
                    LockdownBodies.Add(body);
                    return Refuse
                        ? NoPermission()
                        : Ok("<QueryLockdownExceptionsResponse xmlns=\"urn:vim25\"><returnval>svc-a</returnval><returnval>svc-b</returnval></QueryLockdownExceptionsResponse>");
                case "RetrievePropertiesEx" when body.Contains("type=\"Folder\"", StringComparison.Ordinal):
                    return Ok("<RetrievePropertiesExResponse xmlns=\"urn:vim25\"><returnval /></RetrievePropertiesExResponse>");
                case "RetrievePropertiesEx":
                    return Ok(InventoryPage);
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private static string Host(string moRef, string mode) => $"""
            <objects>
              <obj type="HostSystem">{moRef}</obj>
              <propSet><name>name</name><val xsi:type="xsd:string">{moRef}</val></propSet>
              <propSet><name>config.lockdownMode</name><val xsi:type="HostLockdownMode">{mode}</val></propSet>
              <propSet><name>configManager.hostAccessManager</name><val type="HostAccessManager" xsi:type="ManagedObjectReference">hostAccessManager-{moRef[5..]}</val></propSet>
            </objects>
            """;

        private static readonly string InventoryPage = $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                {Host("host-1", "lockdownNormal")}
                {Host("host-2", "lockdownDisabled")}
              </returnval>
            </RetrievePropertiesExResponse>
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

        private static HttpResponseMessage NoPermission() => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <soapenv:Body>
                    <soapenv:Fault>
                      <faultcode>ServerFaultCode</faultcode>
                      <faultstring>Permission to perform this operation was denied.</faultstring>
                      <detail><NoPermissionFault xmlns="urn:vim25" xsi:type="NoPermission" /></detail>
                    </soapenv:Fault>
                  </soapenv:Body>
                </soapenv:Envelope>
                """, Encoding.UTF8, "text/xml"),
        };
    }
}
