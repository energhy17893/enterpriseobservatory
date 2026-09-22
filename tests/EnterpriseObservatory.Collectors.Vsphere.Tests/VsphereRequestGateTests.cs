using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// F2 end to end: the request gate a <see cref="VsphereClient"/> is given
/// bounds how many of <em>its</em> HTTP exchanges are in flight at once, and
/// nothing else's. docs/proposals/f-invert-collector-authority.md §3.2.
/// </summary>
public class VsphereRequestGateTests
{
    private static VsphereConnectionOptions Connection(string instanceId) => new()
    {
        BaseAddress = new Uri("https://vc.invalid"),
        Username = "svc-readonly@vsphere.local",
        Password = Secret.From("not-a-real-password"),
        InstanceId = instanceId,
    };

    [Fact]
    public async Task No_more_requests_than_the_gates_limit_are_in_flight_at_once()
    {
        var server = new CountingVcenter();
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://vc.invalid") };
        using var gate = new SourceRequestGate(2);
        using var client = new VsphereClient(http, Connection("vc-1"), gate);

        // Ten concurrent requests against a client whose gate allows two.
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetMaxQueryMetricsAsync(CancellationToken.None)));

        Assert.Equal(2, server.MaxObservedConcurrency);
    }

    [Fact]
    public async Task The_limit_is_per_source_not_a_shared_ceiling()
    {
        // Two clients, two gates, two vCenters. One being busy must never
        // borrow from or be limited by the other's ceiling.
        var serverA = new CountingVcenter();
        var serverB = new CountingVcenter();

        using var httpA = new HttpClient(serverA) { BaseAddress = new Uri("https://vc-a.invalid") };
        using var httpB = new HttpClient(serverB) { BaseAddress = new Uri("https://vc-b.invalid") };
        using var gateA = new SourceRequestGate(1);
        using var gateB = new SourceRequestGate(1);
        using var clientA = new VsphereClient(httpA, Connection("vc-a"), gateA);
        using var clientB = new VsphereClient(httpB, Connection("vc-b"), gateB);

        var callsA = Enumerable.Range(0, 5).Select(_ => clientA.GetMaxQueryMetricsAsync(CancellationToken.None));
        var callsB = Enumerable.Range(0, 5).Select(_ => clientB.GetMaxQueryMetricsAsync(CancellationToken.None));

        await Task.WhenAll(callsA.Concat(callsB));

        Assert.Equal(1, serverA.MaxObservedConcurrency);
        Assert.Equal(1, serverB.MaxObservedConcurrency);
    }

    [Fact]
    public async Task A_client_given_no_gate_is_not_bounded_here()
    {
        // The optional constructor argument: a test double or a read-only
        // probe that never runs concurrently with itself is not made to carry
        // a gate it has no use for.
        var server = new CountingVcenter();
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://vc.invalid") };
        using var client = new VsphereClient(http, Connection("vc-1"));

        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetMaxQueryMetricsAsync(CancellationToken.None)));

        Assert.True(server.MaxObservedConcurrency >= 1);
    }

    /// <summary>
    /// A scripted vCenter that answers <c>GetMaxQueryMetricsAsync</c>'s single
    /// call and counts how many requests were in flight on it at once.
    /// </summary>
    private sealed class CountingVcenter : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private int _inFlight;

        public int MaxObservedConcurrency { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _inFlight++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _inFlight);
            }

            try
            {
                var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
                var method = XDocument.Parse(body).Descendants()
                    .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

                // A little real delay so overlapping calls actually overlap
                // rather than each finishing before the next is dispatched.
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);

                switch (method)
                {
                    case "RetrieveServiceContent":
                        return Ok(ServiceContent);

                    case "Login":
                        return Ok(
                            "<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");

                    case "QueryOptions":
                        return Ok("""
                            <QueryOptionsResponse xmlns="urn:vim25">
                              <returnval><key>config.vpxd.stats.maxQueryMetrics</key><value>256</value></returnval>
                            </QueryOptionsResponse>
                            """);

                    default:
                        throw new InvalidOperationException($"Unscripted call {method}.");
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        private const string ServiceContent = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
                  <rootFolder type="Folder">group-d1</rootFolder>
                  <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
                  <viewManager type="ViewManager">ViewManager</viewManager>
                  <about><name>vc-test</name><apiVersion>8.0.3.0</apiVersion></about>
                  <setting type="OptionManager">VpxSettings</setting>
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
