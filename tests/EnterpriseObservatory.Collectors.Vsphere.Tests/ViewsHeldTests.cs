using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// <c>views_held</c> must tell "we could not look" from "we looked and found
/// none" apart -- see <see cref="VsphereClient.GetViewsHeldAsync(CancellationToken)"/>.
/// </summary>
public class ViewsHeldTests
{
    private static (VsphereClient Client, ScriptedServer Server) Connect(ScriptedServer server)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        var channel = new VsphereSessionChannel(server, options);
        return (new VsphereClient(channel, options), server);
    }

    [Fact]
    public async Task VCenter_naming_viewList_as_unreadable_reports_null_not_zero()
    {
        var (client, _) = Connect(new ScriptedServer { ViewListMissing = true });

        var held = await client.GetViewsHeldAsync(CancellationToken.None);

        Assert.Null(held);
    }

    [Fact]
    public async Task A_fault_reading_ViewManager_reports_null_not_zero()
    {
        var (client, _) = Connect(new ScriptedServer { FaultOnRetrieve = true });

        var held = await client.GetViewsHeldAsync(CancellationToken.None);

        Assert.Null(held);
    }

    [Fact]
    public async Task A_genuinely_empty_viewList_reports_zero()
    {
        // The property collector omits an empty array property entirely
        // rather than sending it with no elements -- the same convention
        // VsphereEventParser.ParseLatestPage relies on for an empty
        // EventHistoryCollector. This must read as zero, not as "unreadable".
        var (client, _) = Connect(new ScriptedServer());

        var held = await client.GetViewsHeldAsync(CancellationToken.None);

        Assert.Equal(0, held);
    }

    [Fact]
    public async Task Two_open_views_are_counted()
    {
        var (client, _) = Connect(new ScriptedServer { OpenViews = ["view-1", "view-2"] });

        var held = await client.GetViewsHeldAsync(CancellationToken.None);

        Assert.Equal(2, held);
    }

    /// <summary>A vCenter that knows only the calls a views_held read makes.</summary>
    internal sealed class ScriptedServer : HttpMessageHandler
    {
        /// <summary>The views this session's ViewManager currently holds, empty by default.</summary>
        public IReadOnlyList<string> OpenViews { get; init; } = [];

        /// <summary>vCenter names viewList in missingSet rather than sending it.</summary>
        public bool ViewListMissing { get; init; }

        /// <summary>The whole RetrievePropertiesEx call faults.</summary>
        public bool FaultOnRetrieve { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            return method switch
            {
                "RetrieveServiceContent" => Ok(ServiceContent),
                "Login" => Ok(
                    "<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>"),
                "RetrievePropertiesEx" => FaultOnRetrieve ? NoPermissionFault() : ViewListResponse(),
                _ => throw new InvalidOperationException($"Unscripted call {method}."),
            };
        }

        private HttpResponseMessage ViewListResponse()
        {
            if (ViewListMissing)
            {
                return Ok("""
                    <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                      <returnval>
                        <objects>
                          <obj type="ViewManager">ViewManager</obj>
                          <missingSet>
                            <path>viewList</path>
                            <fault><fault xsi:type="NoPermission"/><localizedMessage>Permission to perform this operation was denied.</localizedMessage></fault>
                          </missingSet>
                        </objects>
                      </returnval>
                    </RetrievePropertiesExResponse>
                    """);
            }

            if (OpenViews.Count == 0)
            {
                // An empty array property is omitted, not sent empty.
                return Ok("""
                    <RetrievePropertiesExResponse xmlns="urn:vim25">
                      <returnval>
                        <objects>
                          <obj type="ViewManager">ViewManager</obj>
                        </objects>
                      </returnval>
                    </RetrievePropertiesExResponse>
                    """);
            }

            var refs = string.Concat(OpenViews.Select(
                v => $"""<ManagedObjectReference type="ContainerView">{v}</ManagedObjectReference>"""));

            return Ok($"""
                <RetrievePropertiesExResponse xmlns="urn:vim25">
                  <returnval>
                    <objects>
                      <obj type="ViewManager">ViewManager</obj>
                      <propSet><name>viewList</name><val>{refs}</val></propSet>
                    </objects>
                  </returnval>
                </RetrievePropertiesExResponse>
                """);
        }

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

        private static HttpResponseMessage NoPermissionFault() => new(HttpStatusCode.InternalServerError)
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
