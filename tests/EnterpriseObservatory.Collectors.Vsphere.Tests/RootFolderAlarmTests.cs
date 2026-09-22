using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Collection PR 2: the root folder's <c>triggeredAlarmState</c>, read in a
/// call of its own so vCenter-scoped alarms (its own licence expiry) arrive.
/// </summary>
/// <remarks>
/// Shapes as measured live (docs/measurements/collection-pr2-shapes.md): the
/// root folder carried 4 alarm states, 3 raised on the folder itself and one
/// on a host that the host's own list also carried, key for key.
/// </remarks>
public class RootFolderAlarmTests
{
    private static VsphereClient Client(RootAlarmServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("https://vc.invalid") },
            new VsphereConnectionOptions
            {
                BaseAddress = new Uri("https://vc.invalid"),
                Username = "svc-readonly@vsphere.local",
                Password = Secret.From("not-a-real-password"),
                InstanceId = "vc-test",
            });

    [Fact]
    public async Task Alarms_raised_on_the_root_folder_arrive_and_one_seen_twice_arrives_once()
    {
        using var client = Client(new RootAlarmServer());

        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        Assert.Equal("group-d1", payload.RootFolderMoRef);
        Assert.Equal(2, payload.TriggeredAlarms.Count);

        var root = Assert.Single(payload.TriggeredAlarms, a => a.EntityMoRef == "group-d1");
        Assert.Equal("Folder", root.EntityType);
        Assert.Equal("License expiry", root.AlarmName);
        Assert.Equal("yellow", root.OverallStatus);

        // Carried by the host and by the root: one fact, not two.
        Assert.Single(payload.TriggeredAlarms, a => a.Key == "alarm-115.host-1");
        Assert.Empty(payload.Failures);
    }

    [Fact]
    public async Task A_refused_root_read_is_a_failure_and_the_inventory_still_arrives()
    {
        using var client = Client(new RootAlarmServer { RefuseRoot = true });

        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        Assert.Single(payload.Hosts);
        Assert.Single(payload.TriggeredAlarms, a => a.Key == "alarm-115.host-1");
        var failure = Assert.Single(payload.Failures);
        Assert.Contains("root folder", failure.Target, StringComparison.Ordinal);
        Assert.True(failure.IsPermissionDenied);
    }

    [Fact]
    public async Task The_root_read_asks_the_root_folder_for_its_alarms_and_nothing_else()
    {
        var server = new RootAlarmServer();
        using var client = Client(server);

        await client.RetrieveInventoryAsync(CancellationToken.None);

        var body = Assert.Single(server.RootBodies);
        var paths = XDocument.Parse(body).Descendants()
            .Where(e => e.Name.LocalName == "pathSet").Select(e => e.Value).ToList();
        Assert.Equal(["triggeredAlarmState"], paths);
        Assert.Contains(">group-d1<", body, StringComparison.Ordinal);
    }

    private sealed class RootAlarmServer : HttpMessageHandler
    {
        public bool RefuseRoot { get; init; }

        public List<string> RootBodies { get; } = [];

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
                case "RetrievePropertiesEx" when body.Contains("type=\"Folder\"", StringComparison.Ordinal):
                    RootBodies.Add(body);
                    return RefuseRoot ? NoPermission() : Ok(RootPage);
                case "RetrievePropertiesEx" when body.Contains("type=\"Alarm\"", StringComparison.Ordinal):
                    return Ok(AlarmDefinitions);
                case "RetrievePropertiesEx":
                    return Ok(InventoryPage);
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private static string State(string key, string entityType, string entity, string alarm, string status) => $"""
            <AlarmState xsi:type="AlarmState">
              <key>{key}</key>
              <entity type="{entityType}">{entity}</entity>
              <alarm type="Alarm">{alarm}</alarm>
              <overallStatus>{status}</overallStatus>
              <time>2026-09-22T08:00:00Z</time>
              <acknowledged>false</acknowledged>
              <eventKey>1</eventKey>
            </AlarmState>
            """;

        private static readonly string HostAlarm = State("alarm-115.host-1", "HostSystem", "host-1", "alarm-115", "red");

        private static readonly string InventoryPage = $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">esx01</val></propSet>
                  <propSet><name>triggeredAlarmState</name><val xsi:type="ArrayOfAlarmState">{HostAlarm}</val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        private static readonly string RootPage = $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="Folder">group-d1</obj>
                  <propSet>
                    <name>triggeredAlarmState</name>
                    <val xsi:type="ArrayOfAlarmState">
                      {State("alarm-7.group-d1", "Folder", "group-d1", "alarm-7", "yellow")}
                      {HostAlarm}
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        private const string AlarmDefinitions = """
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="Alarm">alarm-7</obj>
                  <propSet><name>info.name</name><val xsi:type="xsd:string">License expiry</val></propSet>
                </objects>
                <objects>
                  <obj type="Alarm">alarm-115</obj>
                  <propSet><name>info.name</name><val xsi:type="xsd:string">Host memory status</val></propSet>
                </objects>
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
