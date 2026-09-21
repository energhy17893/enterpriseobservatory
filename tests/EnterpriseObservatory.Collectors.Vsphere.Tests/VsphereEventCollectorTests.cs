using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The event collector protocol, against a scripted vCenter.
/// </summary>
/// <remarks>
/// <para>
/// The script plays the documented behaviour of an EventHistoryCollector: the
/// newest page as <c>latestPage</c>, then older pages from
/// <c>ReadPreviousEvents</c> until there are none. It deliberately ignores the
/// time filter, so that stopping at the mark has to be the client's doing
/// rather than the server's.
/// </para>
/// <para>
/// It is a script, not a vCenter. The initial scroll position and the order of
/// events inside a page are the two behaviours most likely to differ live, and
/// the client merges by key and sorts precisely so that neither matters.
/// </para>
/// </remarks>
public class VsphereEventCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>One event per minute, key 1 the oldest.</summary>
    private static List<(long Key, DateTimeOffset At)> Stream(int count, long firstKey = 1) =>
        [.. Enumerable.Range(0, count).Select(i => (firstKey + i, Now.AddMinutes(-count + i)))];

    private static (VsphereClient Client, ScriptedVcenter Server) Connect(
        List<(long Key, DateTimeOffset At)> events,
        bool offersEventManager = true,
        bool failOlderPages = false)
    {
        var server = new ScriptedVcenter(events, offersEventManager, failOlderPages);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://vc.invalid") };

        var client = new VsphereClient(http, new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        });

        return (client, server);
    }

    [Fact]
    public async Task A_first_read_walks_back_through_the_whole_window_and_returns_it_oldest_first()
    {
        var (client, server) = Connect(Stream(450));

        var read = await client.ReadEventsAsync(since: null, Now, CancellationToken.None);

        Assert.NotNull(read.Events);
        Assert.True(read.Complete);
        Assert.Equal(Enumerable.Range(1, 450).Select(i => (long)i), read.Events.Select(e => e.Key));

        // Latest page, then 51-250, 1-50, and an empty page that ends it.
        Assert.Equal(3, server.Calls.Count(c => c == "ReadPreviousEvents"));
        Assert.Contains(
            (Now - VsphereClient.FirstEventLookBack).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            server.CreateBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_read_stops_at_the_mark_and_returns_only_what_is_newer()
    {
        var stream = Stream(450);
        var (client, server) = Connect(stream);
        var mark = new EventMark { Key = 100, CreatedAtUtc = stream[99].At };

        var read = await client.ReadEventsAsync(mark, Now, CancellationToken.None);

        Assert.True(read.Complete);
        Assert.Equal(Enumerable.Range(101, 350).Select(i => (long)i), read.Events!.Select(e => e.Key));

        // 251-450 does not reach key 100; 51-250 does. No third page.
        Assert.Equal(1, server.Calls.Count(c => c == "ReadPreviousEvents"));

        // The window starts a little before the mark, never at it.
        Assert.Contains(
            (mark.CreatedAtUtc - VsphereClient.EventWindowOverlap).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            server.CreateBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mark_inside_the_latest_page_needs_no_further_pages()
    {
        var stream = Stream(450);
        var (client, server) = Connect(stream);

        var read = await client.ReadEventsAsync(
            new EventMark { Key = 440, CreatedAtUtc = stream[439].At }, Now, CancellationToken.None);

        Assert.Equal(10, read.Events!.Count);
        Assert.DoesNotContain("ReadPreviousEvents", server.Calls);
    }

    [Fact]
    public async Task The_collector_is_destroyed_after_every_read()
    {
        // vCenter bounds collectors per session. One leaked per cycle stops
        // every read on the session some hours later, far from the cause.
        var (client, server) = Connect(Stream(450));

        await client.ReadEventsAsync(null, Now, CancellationToken.None);
        await client.ReadEventsAsync(null, Now, CancellationToken.None);

        Assert.Equal(2, server.Calls.Count(c => c == "CreateCollectorForEvents"));
        Assert.Equal(2, server.Calls.Count(c => c == "DestroyCollector"));
    }

    [Fact]
    public async Task The_collector_is_destroyed_even_when_a_page_fails()
    {
        var (client, server) = Connect(Stream(450), failOlderPages: true);

        await Assert.ThrowsAsync<VsphereApiException>(
            () => client.ReadEventsAsync(null, Now, CancellationToken.None));

        Assert.Equal(1, server.Calls.Count(c => c == "DestroyCollector"));
    }

    [Fact]
    public async Task A_failed_read_is_could_not_ask_and_never_an_empty_window()
    {
        var (client, _) = Connect(Stream(450), failOlderPages: true);
        var source = new VsphereEventSource(client, new FixedClock());

        var read = await source.ReadAsync(null, CancellationToken.None);

        Assert.Null(read.Events);
        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
    }

    [Fact]
    public async Task A_quiet_window_is_an_empty_list_not_a_failure()
    {
        var (client, _) = Connect([]);

        var read = await client.ReadEventsAsync(null, Now, CancellationToken.None);

        Assert.NotNull(read.Events);
        Assert.Empty(read.Events);
        Assert.True(read.Complete);
    }

    [Fact]
    public async Task A_vCenter_without_an_event_manager_cannot_be_asked()
    {
        var (client, server) = Connect(Stream(10), offersEventManager: false);

        var read = await client.ReadEventsAsync(null, Now, CancellationToken.None);

        Assert.Null(read.Events);
        Assert.DoesNotContain("CreateCollectorForEvents", server.Calls);
    }

    [Fact]
    public async Task An_event_storm_keeps_the_newest_events_and_says_it_stopped_short()
    {
        // The opposite of QueryEvents, which under the same load keeps the
        // oldest and says nothing.
        var total = (VsphereClient.MaxEventPages * VsphereClient.EventPageSize) + 500;
        var (client, _) = Connect(Stream(total));

        var read = await client.ReadEventsAsync(null, Now, CancellationToken.None);

        Assert.False(read.Complete);
        Assert.Equal(VsphereClient.MaxEventPages * VsphereClient.EventPageSize, read.Events!.Count);
        Assert.Equal(total, read.Events[^1].Key);
    }

    [Fact]
    public async Task A_vCenter_whose_keys_went_backwards_is_still_read()
    {
        // A rebuilt vCenter counts from one again. Filtering on key alone
        // would ignore every event it ever writes after that.
        var mark = new EventMark { Key = 5_000, CreatedAtUtc = Now.AddHours(-1) };
        var (client, _) = Connect(Stream(10));

        var read = await client.ReadEventsAsync(mark, Now, CancellationToken.None);

        Assert.Equal(10, read.Events!.Count);
    }

    /// <summary>A vCenter that knows only the calls event collection makes.</summary>
    private sealed class ScriptedVcenter(
        List<(long Key, DateTimeOffset At)> events,
        bool offersEventManager,
        bool failOlderPages) : HttpMessageHandler
    {
        private int _position;

        public List<string> Calls { get; } = [];

        public string CreateBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            Calls.Add(method);

            return method switch
            {
                "RetrieveServiceContent" => Ok(ServiceContent()),
                "Login" => Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>"),
                "CreateCollectorForEvents" => Create(body),
                "SetCollectorPageSize" => Ok("<SetCollectorPageSizeResponse xmlns=\"urn:vim25\" />"),
                "RetrievePropertiesEx" => Ok(LatestPage()),
                "ReadPreviousEvents" when failOlderPages => Fault(),
                "ReadPreviousEvents" => Ok(Previous()),
                "DestroyCollector" => Ok("<DestroyCollectorResponse xmlns=\"urn:vim25\" />"),
                _ => throw new InvalidOperationException($"Unscripted call {method}."),
            };
        }

        private HttpResponseMessage Create(string body)
        {
            CreateBody = body;

            // The scroll position sits just before the latest page.
            _position = Math.Max(0, events.Count - VsphereClient.EventPageSize);

            return Ok("<CreateCollectorForEventsResponse xmlns=\"urn:vim25\"><returnval type=\"EventHistoryCollector\">session[1]c1</returnval></CreateCollectorForEventsResponse>");
        }

        private string LatestPage()
        {
            var page = events.Skip(Math.Max(0, events.Count - VsphereClient.EventPageSize));

            return $"""
                <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <returnval><objects>
                    <obj type="EventHistoryCollector">session[1]c1</obj>
                    <propSet><name>latestPage</name><val xsi:type="ArrayOfEvent">{string.Concat(page.Select(e => Event("Event", e)))}</val></propSet>
                  </objects></returnval>
                </RetrievePropertiesExResponse>
                """;
        }

        private string Previous()
        {
            var from = Math.Max(0, _position - VsphereClient.EventPageSize);
            var page = events.Skip(from).Take(_position - from).ToList();
            _position = from;

            return $"""
                <ReadPreviousEventsResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  {string.Concat(page.Select(e => Event("returnval", e)))}
                </ReadPreviousEventsResponse>
                """;
        }

        private static string Event(string element, (long Key, DateTimeOffset At) e) =>
            $"<{element} xsi:type=\"VmPoweredOnEvent\"><key>{e.Key}</key>" +
            $"<createdTime>{e.At.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</createdTime>" +
            $"<fullFormattedMessage>event {e.Key}</fullFormattedMessage></{element}>";

        private string ServiceContent() => $"""
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
                  <rootFolder type="Folder">group-d1</rootFolder>
                  <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
                  <viewManager type="ViewManager">ViewManager</viewManager>
                  <about><name>vc-test</name><apiVersion>8.0.3.0</apiVersion></about>
                  <sessionManager type="SessionManager">SessionManager</sessionManager>
                  <perfManager type="PerformanceManager">PerfMgr</perfManager>
                  {(offersEventManager ? "<eventManager type=\"EventManager\">EventManager</eventManager>" : string.Empty)}
                </returnval></RetrieveServiceContentResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        private static HttpResponseMessage Ok(string xml) =>
            new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

        private static HttpResponseMessage Fault() => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <soapenv:Body>
                    <soapenv:Fault>
                      <faultcode>ServerFaultCode</faultcode>
                      <faultstring>The object 'vim.event.EventHistoryCollector:session[1]c1' has already been deleted or has not been completely created</faultstring>
                      <detail><ManagedObjectNotFoundFault xmlns="urn:vim25" xsi:type="ManagedObjectNotFound" /></detail>
                    </soapenv:Fault>
                  </soapenv:Body>
                </soapenv:Envelope>
                """, Encoding.UTF8, "text/xml"),
        };
    }
}
