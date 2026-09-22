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
}
