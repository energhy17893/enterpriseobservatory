using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// What this client creates on a vCenter, it gives back — on the paths where a
/// read did not finish as much as on the one where it did.
/// </summary>
/// <remarks>
/// Views, paging tokens and sessions are all server-side and all bounded per
/// session or per vCenter. Each leak is invisible where it happens and shows
/// up hours later as a vCenter that refuses something unrelated.
/// </remarks>
public class VsphereSessionCleanupTests
{
    private static (VsphereClient Client, VsphereSessionChannel Channel, ScriptedVcenter Server) Connect(
        ScriptedVcenter server)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        var channel = new VsphereSessionChannel(server, options);
        var client = new VsphereClient(channel, options);

        return (client, channel, server);
    }

    [Fact]
    public async Task The_view_is_destroyed_when_the_read_is_cut_off()
    {
        // The runner cancels a read at its timeout. The cleanup used to run on
        // that same, already-cancelled token, so it threw before it sent
        // anything and the view stayed on a session the metric loop keeps
        // alive — one more for every inventory read that timed out.
        using var cutOff = new CancellationTokenSource();
        var (client, _, server) = Connect(new ScriptedVcenter { OnFirstPage = cutOff.Cancel });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RetrieveInventoryAsync(cutOff.Token));

        Assert.Contains("DestroyView", server.Calls);
    }

    [Fact]
    public async Task A_paging_token_left_open_is_cancelled()
    {
        // A retrieval abandoned between pages leaves its results held on the
        // server until the token is released or the session ends. Between
        // pages, because a read cut off during the first one never learned a
        // token and has nothing to give back.
        using var cutOff = new CancellationTokenSource();
        var (client, _, server) = Connect(new ScriptedVcenter
        {
            FirstPageHasMore = true,
            OnNextPage = cutOff.Cancel,
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RetrieveInventoryAsync(cutOff.Token));

        Assert.Contains("CancelRetrievePropertiesEx", server.Calls);
        Assert.Contains("session[1]token-1", server.CancelBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retrieval_that_ran_to_its_last_page_has_no_token_to_cancel()
    {
        var (client, _, server) = Connect(new ScriptedVcenter());

        await client.RetrieveInventoryAsync(CancellationToken.None);

        Assert.DoesNotContain("CancelRetrievePropertiesEx", server.Calls);
        Assert.Contains("DestroyView", server.Calls);
    }

    [Fact]
    public async Task Logging_out_ends_the_session_on_the_vcenter()
    {
        // Nothing called Logout. Every restart, every edited or removed
        // connection and every press of Test left a session behind until
        // vCenter's idle timeout collected it.
        var (client, channel, server) = Connect(new ScriptedVcenter());
        await client.RetrieveInventoryAsync(CancellationToken.None);

        await channel.LogoutAsync(CancellationToken.None);

        Assert.Equal(1, server.Calls.Count(c => c == "Logout"));
    }

    [Fact]
    public async Task A_client_that_never_logged_in_has_nothing_to_log_out_of()
    {
        // Logging out must not be what opens the connection.
        var (_, channel, server) = Connect(new ScriptedVcenter());

        await channel.LogoutAsync(CancellationToken.None);

        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task Logging_out_twice_asks_once()
    {
        var (client, channel, server) = Connect(new ScriptedVcenter());
        await client.RetrieveInventoryAsync(CancellationToken.None);

        await channel.LogoutAsync(CancellationToken.None);
        await channel.LogoutAsync(CancellationToken.None);

        Assert.Equal(1, server.Calls.Count(c => c == "Logout"));
    }

    [Fact]
    public async Task A_vcenter_that_cannot_be_reached_does_not_make_logging_out_throw()
    {
        // This runs while a connection is being taken down. There is nobody
        // left to tell, and the idle timeout still collects the session.
        var (client, channel, server) = Connect(new ScriptedVcenter());
        await client.RetrieveInventoryAsync(CancellationToken.None);
        server.Unreachable = true;

        await channel.LogoutAsync(CancellationToken.None);
    }

    /// <summary>A vCenter that knows only the calls an inventory read makes.</summary>
    internal sealed class ScriptedVcenter : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        public string CancelBody { get; private set; } = string.Empty;

        /// <summary>Runs as the first page is served — where a read gets cut off.</summary>
        public Action? OnFirstPage { get; init; }

        /// <summary>Runs as a later page is asked for, which then never arrives.</summary>
        public Action? OnNextPage { get; init; }

        public bool FirstPageHasMore { get; init; }

        public bool Unreachable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }

            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            // A cancelled call never reaches a real server either.
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
                case "RetrievePropertiesEx":
                    var page = Ok(Page(FirstPageHasMore ? "<token>session[1]token-1</token>" : string.Empty));
                    OnFirstPage?.Invoke();
                    return page;
                case "ContinueRetrievePropertiesEx":
                    OnNextPage?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Ok(Page(string.Empty));
                case "CancelRetrievePropertiesEx":
                    CancelBody = body;
                    return Ok("<CancelRetrievePropertiesExResponse xmlns=\"urn:vim25\" />");
                case "DestroyView":
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");
                case "Logout":
                    return Ok("<LogoutResponse xmlns=\"urn:vim25\" />");
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private static string Page(string token) => $"""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                {token}
                <objects>
                  <obj type="Datastore">datastore-1</obj>
                  <propSet><name>name</name><val>ds-1</val></propSet>
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
    }
}
