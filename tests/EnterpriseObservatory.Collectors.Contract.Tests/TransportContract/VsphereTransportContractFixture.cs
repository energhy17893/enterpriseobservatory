using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives today's vSphere collector's transport (<see cref="VsphereClient"/>)
/// through a scripted SOAP server instead of a real vCenter.
/// </summary>
/// <remarks>
/// The scripted servers here follow the same pattern already proven in
/// <c>VsphereSessionCleanupTests</c> and <c>VsphereSessionRaceTests</c>
/// (EnterpriseObservatory.Collectors.Vsphere.Tests): an <see cref="HttpMessageHandler"/>
/// that answers vim25 SOAP calls by their method element name. Each scenario
/// here gets its own minimal server rather than one do-everything server, so
/// each stays as easy to read as the tests it was modelled on.
/// </remarks>
public sealed class VsphereTransportContractFixture : ITransportContractFixture
{
    private static (VsphereClient Client, VsphereSessionChannel Channel) Connect(HttpMessageHandler handler)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-contract",
        };

        var channel = new VsphereSessionChannel(handler, options);

        return (new VsphereClient(channel, options), channel);
    }

    public async Task<bool> DisposingWithoutAnyCallIsSilentAsync()
    {
        var server = new RefusingServer();
        var (_, channel) = Connect(server);

        channel.Dispose();

        return server.RequestCount == 0;
    }

    public async Task<int> ReadHealthyInventoryEntityCountAsync(int targetCount)
    {
        var server = new HealthyServer(targetCount);
        var (client, channel) = Connect(server);
        using var _ = channel;

        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        return payload.Hosts.Count;
    }

    public async Task<bool> CancelledReadLeavesNoServerObjectAsync()
    {
        using var cutOff = new CancellationTokenSource();
        var server = new CutOffServer(onFirstPage: cutOff.Cancel);
        var (client, channel) = Connect(server);
        using var _ = channel;

        var threw = false;
        try
        {
            await client.RetrieveInventoryAsync(cutOff.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        return threw && server.Calls.Contains("DestroyView");
    }

    public async Task<bool> SingleInvalidFieldFailsWholeReadAsync()
    {
        var server = new InvalidPropertyServer();
        var (client, channel) = Connect(server);
        using var _ = channel;

        try
        {
            var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

            // Reaching here without throwing is only honest if the source
            // named the problem — an empty, silently "successful" payload is
            // exactly the defect this case exists to catch.
            return payload.Hosts.Count == 0 && payload.Failures.Count > 0;
        }
        catch (VsphereApiException)
        {
            // The whole retrieval failed loudly. That is the contract: one
            // invalid property path must not become a partial, silently
            // truncated inventory.
            return true;
        }
    }

    public async Task<bool> MalformedReplyNeverBecomesEmptySuccessAsync()
    {
        var server = new MalformedReplyServer();
        var (client, channel) = Connect(server);
        using var _ = channel;

        try
        {
            await client.RetrieveInventoryAsync(CancellationToken.None);

            // A reply that could not possibly be parsed must never read as an
            // empty, successful inventory.
            return false;
        }
        catch (Exception ex) when (ex is VsphereApiException or System.Xml.XmlException)
        {
            return true;
        }
    }

    public async Task<int> ReauthenticationsWhenTwoConcurrentCallsNoticeExpiredSessionAsync()
    {
        var server = new ExpiringSessionServer();
        var (client, channel) = Connect(server);
        using var _ = channel;

        // Establish the first session.
        await client.GetMaxQueryMetricsAsync(CancellationToken.None);
        var loginsBeforeExpiry = server.Logins;

        server.ExpireTheSession();

        await Task.WhenAll(
            client.GetMaxQueryMetricsAsync(CancellationToken.None),
            client.GetMaxQueryMetricsAsync(CancellationToken.None));

        return server.Logins - loginsBeforeExpiry;
    }

    public async Task<int?> ViewsHeldAfterAFullCycleAsync(bool cancelDuringInventory)
    {
        var server = new FullCycleServer();
        var (client, channel) = Connect(server);
        using var _ = channel;

        if (cancelDuringInventory)
        {
            using var cutOff = new CancellationTokenSource();
            server.OnInventoryFirstPage = cutOff.Cancel;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.RetrieveInventoryAsync(cutOff.Token));
        }
        else
        {
            await client.RetrieveInventoryAsync(CancellationToken.None);
        }

        // A metrics call and an event read, the other two legs of a cycle
        // (F4's brief: "inventory + perf + events"). Neither touches a view,
        // but the event collector is the same kind of server-side object and
        // belongs in the same proof.
        await client.GetMaxQueryMetricsAsync(CancellationToken.None);
        await client.ReadEventsAsync(since: null, DateTimeOffset.UtcNow, CancellationToken.None);

        return await client.GetViewsHeldAsync(CancellationToken.None);
    }

    // --- shared wire fixtures -----------------------------------------

    private const string ServiceContent = """
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body>
            <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
              <rootFolder type="Folder">group-d1</rootFolder>
              <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
              <viewManager type="ViewManager">ViewManager</viewManager>
              <about><name>vc-contract</name><apiVersion>8.0.3.0</apiVersion></about>
              <setting type="OptionManager">VpxSettings</setting>
              <sessionManager type="SessionManager">SessionManager</sessionManager>
              <perfManager type="PerformanceManager">PerfMgr</perfManager>
            </returnval></RetrieveServiceContentResponse>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    private static HttpResponseMessage Ok(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

    private static HttpResponseMessage Fault(string faultTypeElementName) => new(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent($"""
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <soapenv:Body>
                <soapenv:Fault>
                  <faultcode>ServerFaultCode</faultcode>
                  <faultstring>A server fault occurred.</faultstring>
                  <detail><{faultTypeElementName} xmlns="urn:vim25" xsi:type="{faultTypeElementName}" /></detail>
                </soapenv:Fault>
              </soapenv:Body>
            </soapenv:Envelope>
            """, Encoding.UTF8, "text/xml"),
    };

    private static async Task<string> MethodOf(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
        return XDocument.Parse(body).Descendants()
            .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;
    }

    /// <summary>A server that must never be asked anything.</summary>
    private sealed class RefusingServer : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException(
                "A transport that was never read from must never send a request.");
        }
    }

    /// <summary>A vCenter with `targetCount` hosts and nothing wrong with any of them.</summary>
    private sealed class HealthyServer(int targetCount) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = await MethodOf(request);

            return method switch
            {
                "RetrieveServiceContent" => Ok(ServiceContent),
                "Login" => Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>"),
                "CreateContainerView" => Ok(
                    "<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>"),
                "RetrievePropertiesEx" => Ok(Page()),
                "DestroyView" => Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />"),
                _ => throw new InvalidOperationException($"Unscripted call {method}."),
            };
        }

        private string Page() => $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                {string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, targetCount)
                        .Select(i => $"<objects><obj type=\"HostSystem\">host-{i}</obj></objects>"))}
              </returnval>
            </RetrievePropertiesExResponse>
            """;
    }

    /// <summary>A vCenter whose first page is served exactly once cancellation fires.</summary>
    private sealed class CutOffServer(Action onFirstPage) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = await MethodOf(request);
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(method);

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContent);
                case "Login":
                    return Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");
                case "CreateContainerView":
                    return Ok("<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>");
                case "RetrievePropertiesEx":
                    var page = Ok("""
                        <RetrievePropertiesExResponse xmlns="urn:vim25">
                          <returnval>
                            <objects><obj type="HostSystem">host-1</obj></objects>
                          </returnval>
                        </RetrievePropertiesExResponse>
                        """);
                    onFirstPage();
                    return page;
                case "DestroyView":
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }
    }

    /// <summary>A vCenter that refuses the property retrieval as InvalidProperty.</summary>
    private sealed class InvalidPropertyServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = await MethodOf(request);

            return method switch
            {
                "RetrieveServiceContent" => Ok(ServiceContent),
                "Login" => Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>"),
                "CreateContainerView" => Ok(
                    "<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>"),
                // Measured on a live vCenter (docs/reference-approaches.md §10.3):
                // one invalid path fails the entire RetrievePropertiesEx call.
                "RetrievePropertiesEx" => Fault("InvalidPropertyFault"),
                "DestroyView" => Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />"),
                _ => throw new InvalidOperationException($"Unscripted call {method}."),
            };
        }
    }

    /// <summary>A vCenter whose property retrieval reply is not valid XML at all.</summary>
    private sealed class MalformedReplyServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = await MethodOf(request);

            return method switch
            {
                "RetrieveServiceContent" => Ok(ServiceContent),
                "Login" => Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>"),
                "CreateContainerView" => Ok(
                    "<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>"),
                "RetrievePropertiesEx" => Ok("this is not xml, and no parser will make it inventory"),
                "DestroyView" => Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />"),
                _ => throw new InvalidOperationException($"Unscripted call {method}."),
            };
        }
    }

    /// <summary>
    /// A vCenter whose session expires, scripted to produce the losing order.
    /// </summary>
    /// <remarks>
    /// Adapted from <c>VsphereSessionRaceTests.ScriptedVcenter</c>. Both calls
    /// are sent on the old session and both are refused; the first refusal is
    /// held until the second call has arrived, and the second is held until a
    /// new login has been served, so the calls really do race on one expired
    /// session rather than passing by scheduler luck.
    /// </remarks>
    private sealed class ExpiringSessionServer : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _secondArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _signedInAgain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refusalsOwed;

        public int Logins { get; private set; }

        public void ExpireTheSession()
        {
            lock (_gate)
            {
                _refusalsOwed = 2;
                _signedInAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = await MethodOf(request);

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContent);

                case "Login":
                    lock (_gate)
                    {
                        Logins++;
                    }

                    _signedInAgain.TrySetResult();
                    return Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");

                case "QueryOptions":
                    int owed;
                    lock (_gate)
                    {
                        owed = _refusalsOwed;
                        if (owed > 0)
                        {
                            _refusalsOwed--;
                        }
                    }

                    if (owed == 2)
                    {
                        await _secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        return NotAuthenticated();
                    }

                    if (owed == 1)
                    {
                        _secondArrived.TrySetResult();
                        await _signedInAgain.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        return NotAuthenticated();
                    }

                    return Ok("""
                        <QueryOptionsResponse xmlns="urn:vim25">
                          <returnval><key>config.vpxd.stats.maxQueryMetrics</key><value>256</value></returnval>
                        </QueryOptionsResponse>
                        """);

                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private static HttpResponseMessage NotAuthenticated() => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <soapenv:Body>
                    <soapenv:Fault>
                      <faultcode>ServerFaultCode</faultcode>
                      <faultstring>The session is not authenticated.</faultstring>
                      <detail><NotAuthenticatedFault xmlns="urn:vim25" xsi:type="NotAuthenticated" /></detail>
                    </soapenv:Fault>
                  </soapenv:Body>
                </soapenv:Envelope>
                """, Encoding.UTF8, "text/xml"),
        };
    }

    /// <summary>
    /// A vCenter that answers a whole cycle -- inventory, a metrics call and
    /// an event read -- and tracks every view and event collector it has
    /// handed out, the way a real <c>ViewManager.viewList</c> and
    /// <c>EventManager</c> would. <c>RetrievePropertiesEx</c> is dispatched by
    /// which property was asked for, since the same method name carries the
    /// inventory read, the <c>views_held</c> self-metric's own read and the
    /// event collector's <c>latestPage</c> alike.
    /// </summary>
    private sealed class FullCycleServer : HttpMessageHandler
    {
        private readonly HashSet<string> _openViews = [];
        private readonly HashSet<string> _openEventCollectors = [];
        private int _nextView;
        private int _nextCollector;

        /// <summary>Runs once the inventory read's only page has been served.</summary>
        public Action? OnInventoryFirstPage { get; set; }

        private const string ServiceContentWithEventManager = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
                  <rootFolder type="Folder">group-d1</rootFolder>
                  <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
                  <viewManager type="ViewManager">ViewManager</viewManager>
                  <about><name>vc-contract</name><apiVersion>8.0.3.0</apiVersion></about>
                  <sessionManager type="SessionManager">SessionManager</sessionManager>
                  <perfManager type="PerformanceManager">PerfMgr</perfManager>
                  <eventManager type="EventManager">EventManager</eventManager>
                </returnval></RetrieveServiceContentResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var document = XDocument.Parse(body);
            var method = await MethodOf(request);

            cancellationToken.ThrowIfCancellationRequested();

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContentWithEventManager);

                case "Login":
                    return Ok(
                        "<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");

                case "QueryOptions":
                    return Ok("""
                        <QueryOptionsResponse xmlns="urn:vim25">
                          <returnval><key>config.vpxd.stats.maxQueryMetrics</key><value>256</value></returnval>
                        </QueryOptionsResponse>
                        """);

                case "CreateContainerView":
                {
                    var view = $"view-{++_nextView}";
                    _openViews.Add(view);
                    return Ok(
                        $"<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">{view}</returnval></CreateContainerViewResponse>");
                }

                case "DestroyView":
                    _openViews.Remove(ThisMoRef(document));
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");

                case "CreateCollectorForEvents":
                {
                    var collector = $"event-collector-{++_nextCollector}";
                    _openEventCollectors.Add(collector);
                    return Ok(
                        $"<CreateCollectorForEventsResponse xmlns=\"urn:vim25\"><returnval type=\"EventHistoryCollector\">{collector}</returnval></CreateCollectorForEventsResponse>");
                }

                case "SetCollectorPageSize":
                    return Ok("<SetCollectorPageSizeResponse xmlns=\"urn:vim25\" />");

                case "DestroyCollector":
                    _openEventCollectors.Remove(ThisMoRef(document));
                    return Ok("<DestroyCollectorResponse xmlns=\"urn:vim25\" />");

                case "RetrievePropertiesEx":
                    return Dispatch(document, cancellationToken);

                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        /// <summary>
        /// One method name, four different questions: the inventory read
        /// itself, <c>views_held</c>'s own <c>ViewManager.viewList</c> read,
        /// <c>EventManager.maxCollector</c> and the event collector's
        /// <c>latestPage</c>. Told apart by which property was asked for.
        /// </summary>
        private HttpResponseMessage Dispatch(XDocument document, CancellationToken cancellationToken)
        {
            var pathSets = document.Descendants()
                .Where(e => e.Name.LocalName == "pathSet")
                .Select(e => e.Value)
                .ToHashSet(StringComparer.Ordinal);

            if (pathSets.Contains("viewList"))
            {
                var refs = string.Concat(_openViews.Select(
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

            if (pathSets.Contains("maxCollector"))
            {
                return Ok("""
                    <RetrievePropertiesExResponse xmlns="urn:vim25">
                      <returnval>
                        <objects>
                          <obj type="EventManager">EventManager</obj>
                          <propSet><name>maxCollector</name><val>10</val></propSet>
                        </objects>
                      </returnval>
                    </RetrievePropertiesExResponse>
                    """);
            }

            if (pathSets.Contains("latestPage"))
            {
                // No propSet at all is how an empty ArrayOfEvent property
                // comes back (VsphereEventParser.ParseLatestPage's remarks):
                // this collector has no events.
                return Ok("""
                    <RetrievePropertiesExResponse xmlns="urn:vim25">
                      <returnval />
                    </RetrievePropertiesExResponse>
                    """);
            }

            // The inventory read itself: one HostSystem, the same shape
            // CutOffServer above uses.
            var page = Ok("""
                <RetrievePropertiesExResponse xmlns="urn:vim25">
                  <returnval>
                    <objects><obj type="HostSystem">host-1</obj></objects>
                  </returnval>
                </RetrievePropertiesExResponse>
                """);
            OnInventoryFirstPage?.Invoke();
            return page;
        }

        private static string ThisMoRef(XDocument document) =>
            document.Descendants().First(e => e.Name.LocalName == "_this").Value.Trim();
    }
}
