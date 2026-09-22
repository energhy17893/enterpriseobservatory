using System.Net;
using System.Text;
using System.Xml;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// What a hostile reply can do to the collector: nothing beyond failing the
/// read.
/// </summary>
/// <remarks>
/// The reply comes from vCenter or, with an untrusted certificate accepted,
/// from anyone in the path. Two things it must not be able to do are make the
/// host buffer an unbounded body and make the parser expand or fetch entities.
/// Both are refused as an ordinary collection failure — the same thing a
/// vCenter that is down produces — rather than as a crash or an empty answer
/// that reads as "nothing happened".
/// </remarks>
public class VsphereReplyHardeningTests
{
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
              <eventManager type="EventManager">EventManager</eventManager>
            </returnval></RetrieveServiceContentResponse>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    /// <summary>The classic entity-expansion reply, small on the wire.</summary>
    private const string Laughs = """
        <?xml version="1.0"?>
        <!DOCTYPE lolz [
          <!ENTITY lol "lol">
          <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
          <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
        ]>
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body><RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>&lol3;</returnval></RetrieveServiceContentResponse></soapenv:Body>
        </soapenv:Envelope>
        """;

    private static VsphereClient Connect(Func<HttpResponseMessage> reply)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        var channel = new VsphereSessionChannel(new OneReply(reply), options);

        return new VsphereClient(channel, options);
    }

    private static HttpResponseMessage Ok(HttpContent content) =>
        new(HttpStatusCode.OK) { Content = content };

    [Fact]
    public void The_parser_refuses_a_DTD()
    {
        Assert.Throws<XmlException>(() => VsphereXml.Parse(Laughs));
        Assert.True(VsphereXml.DeclaresDocumentType(Laughs));
    }

    [Fact]
    public void The_parser_still_reads_what_vCenter_actually_sends()
    {
        var document = VsphereXml.Parse(ServiceContent);

        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "rootFolder");
        Assert.False(VsphereXml.DeclaresDocumentType(ServiceContent));
    }

    [Fact]
    public async Task A_reply_with_a_DTD_is_a_collection_failure()
    {
        var client = Connect(() => Ok(new StringContent(Laughs, Encoding.UTF8, "text/xml")));

        var failure = await Assert.ThrowsAsync<VsphereApiException>(
            () => client.GetCounterCatalogAsync(CancellationToken.None));

        Assert.Equal(CollectionFailureKind.ProtocolError, ((ICollectionFault)failure).Kind);
        Assert.Contains("DTD", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_DTD_in_an_event_read_is_could_not_ask_never_an_empty_window()
    {
        var client = Connect(() => Ok(new StringContent(Laughs, Encoding.UTF8, "text/xml")));
        var source = new VsphereEventSource(client, new FixedClock());

        var read = await source.ReadAsync(null, CancellationToken.None);

        Assert.Null(read.Events);
        Assert.Contains("DTD", read.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_announced_as_too_large_is_refused_before_it_is_read()
    {
        var body = new EndlessStream();
        var content = new StreamContent(body);
        content.Headers.ContentLength = VsphereSessionChannel.MaxResponseBytes + 1;
        var client = Connect(() => Ok(content));

        var failure = await Assert.ThrowsAsync<VsphereApiException>(
            () => client.GetCounterCatalogAsync(CancellationToken.None));

        Assert.Equal(CollectionFailureKind.ProtocolError, ((ICollectionFault)failure).Kind);
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task A_reply_that_never_ends_is_abandoned_at_the_cap()
    {
        // No Content-Length: the size is only known by reading, so the cap
        // has to hold while reading rather than trust a header.
        var body = new EndlessStream();
        var client = Connect(() => Ok(new StreamContent(body)));

        var failure = await Assert.ThrowsAsync<VsphereApiException>(
            () => client.GetCounterCatalogAsync(CancellationToken.None));

        Assert.Contains("MB", failure.Message, StringComparison.Ordinal);
        Assert.InRange(
            body.BytesRead, VsphereSessionChannel.MaxResponseBytes, VsphereSessionChannel.MaxResponseBytes + (1024 * 1024));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class OneReply(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply());
    }

    /// <summary>A body that never ends and costs nothing to produce.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)'a');
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
