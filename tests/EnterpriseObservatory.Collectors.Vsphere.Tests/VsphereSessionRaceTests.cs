using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// One expired session is replaced once, however many calls notice it.
/// </summary>
/// <remarks>
/// Inventory, metrics and events share one client and so one session, and they
/// run on separate loops: when a session expires, two calls routinely find out
/// at the same moment. Each used to clear the "signed in" flag on its own, from
/// outside the lock that guards signing in. The slower one could clear it
/// <em>after</em> the faster one had already signed in again, and so sign in a
/// second time — leaving the first new session on the vCenter with nobody
/// holding it. Harmless while nothing logged out; a leak of exactly the kind
/// T0.5 closed, once something did.
/// </remarks>
public class VsphereSessionRaceTests
{
    [Fact]
    public async Task Two_calls_that_find_the_session_expired_sign_in_once()
    {
        var server = new ScriptedVcenter();
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };
        using var channel = new VsphereSessionChannel(server, options);
        var client = new VsphereClient(channel, options);

        Assert.Equal(256, await client.GetMaxQueryMetricsAsync(CancellationToken.None));
        Assert.Equal(1, server.Logins);

        server.ExpireTheSession();

        var both = await Task.WhenAll(
            client.GetMaxQueryMetricsAsync(CancellationToken.None),
            client.GetMaxQueryMetricsAsync(CancellationToken.None));

        // Both calls were answered in the end...
        Assert.Equal([256, 256], both);

        // ...and the expired session was replaced by one new one, not two.
        Assert.Equal(2, server.Logins);
    }

    /// <summary>
    /// A vCenter whose session expires, scripted to produce the losing order.
    /// </summary>
    /// <remarks>
    /// Both calls are sent on the old session and both are refused. The first
    /// refusal is held until the second call has arrived, so both really are
    /// in flight on the expired session; the second refusal is held until a
    /// new login has been served, so it lands after the first call has already
    /// put things right. That is the order in which the old code signed in
    /// twice, and leaving it to the scheduler would make this test pass most
    /// days for the wrong reason.
    /// </remarks>
    private sealed class ScriptedVcenter : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _signedInAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

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
