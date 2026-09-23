using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>One SOAP round trip <see cref="InventoryTimingHandler"/> recorded.</summary>
internal sealed record InventoryCallTiming(
    string CallName,
    int PageIndex,
    IReadOnlyList<(string Type, int Count)> TypeCounts,
    long Bytes,
    double Ms)
{
    public int ObjectCount => TypeCounts.Sum(t => t.Count);
}

/// <summary>One row of <see cref="InventoryTiming.Summarize"/>: every call sharing a name, rolled up.</summary>
internal sealed record InventoryCallSummary(
    string CallName,
    int Calls,
    int Objects,
    long Bytes,
    double TotalMs,
    IReadOnlyList<(string Type, int Count)> TypeCounts);

/// <summary>
/// A <see cref="DelegatingHandler"/> the probe owns -- not a change to the
/// collector's transport -- that times every SOAP round trip while
/// <see cref="Enabled"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// For <c>--time-inventory</c> (23 Sep 2026 measurement: the KBVc01
/// <c>RetrieveInventoryAsync</c> call, SOAP only, no DB, took 21-35 s in the
/// evening vs ~7.4 s in the morning). This answers where inside that call the
/// time goes: page round trips, a particular object type, or a relationship
/// read (alarms, backup fields) -- without touching the collector, which
/// never learns this handler exists.
/// </para>
/// <para>
/// The SOAPAction header is always the same fixed string for every vim25
/// call, so it cannot tell one operation from another; the operation name is
/// the first element under the envelope's <c>Body</c>, read from the request
/// here rather than from the collector. A page's object-type breakdown comes
/// from parsing the *response* with <see cref="PropertyCollectorParser"/> --
/// the same reader the collector itself uses, so this never disagrees with it
/// about what a reply contains.
/// </para>
/// </remarks>
internal sealed class InventoryTimingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly List<InventoryCallTiming> _calls = [];
    private int _nextPageIndex;

    /// <summary>Only records while true, so login/logout and earlier probe sections are not measured.</summary>
    public bool Enabled { get; set; }

    public IReadOnlyList<InventoryCallTiming> Calls => _calls;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var callName = OperationName(requestBody);

        var watch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = response.Content is null
            ? []
            : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        watch.Stop();

        // The channel still has to read this reply itself once this returns,
        // so it gets a fresh, buffered copy rather than a stream this handler
        // already consumed.
        var contentType = response.Content?.Headers.ContentType;
        response.Content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = contentType;
        }

        var isPage = callName is "RetrievePropertiesEx" or "ContinueRetrievePropertiesEx";
        var pageIndex = isPage ? _nextPageIndex++ : -1;
        var typeCounts = isPage ? TypeCounts(bytes) : [];

        _calls.Add(new InventoryCallTiming(callName, pageIndex, typeCounts, bytes.LongLength, watch.Elapsed.TotalMilliseconds));

        return response;
    }

    private static string OperationName(string requestBody)
    {
        if (requestBody.Length == 0)
        {
            return "(no body)";
        }

        try
        {
            var body = XDocument.Parse(requestBody).Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
            return body?.Elements().FirstOrDefault()?.Name.LocalName ?? "(unknown)";
        }
        catch (System.Xml.XmlException)
        {
            return "(unparseable)";
        }
    }

    /// <summary>Per-object-type counts in one property-collector page, read the way the collector itself reads it.</summary>
    private static IReadOnlyList<(string Type, int Count)> TypeCounts(byte[] bytes)
    {
        try
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            return
            [
                .. PropertyCollectorParser.ParsePage(body).Objects
                    .GroupBy(o => o.Type, StringComparer.Ordinal)
                    .Select(g => (Type: g.Key, Count: g.Count()))
                    .OrderByDescending(t => t.Count),
            ];
        }
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw; an unparseable page just shows no breakdown.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }
}

/// <summary>Runs <c>--time-inventory</c> and prints what it recorded.</summary>
internal static class InventoryTiming
{
    public static async Task<int> RunAsync(
        VsphereClient client, InventoryTimingHandler handler, CancellationToken cancellationToken)
    {
        handler.Enabled = true;
        try
        {
            await client.RetrieveInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            handler.Enabled = false;
        }

        Print(handler.Calls);
        return 0;
    }

    /// <summary>Rolls every call up by name: pages/calls, objects, bytes, total ms, and the object types seen.</summary>
    public static IReadOnlyList<InventoryCallSummary> Summarize(IReadOnlyList<InventoryCallTiming> calls) =>
        [
            .. calls
                .GroupBy(c => c.CallName, StringComparer.Ordinal)
                .Select(group => new InventoryCallSummary(
                    CallName: group.Key,
                    Calls: group.Count(),
                    Objects: group.Sum(c => c.ObjectCount),
                    Bytes: group.Sum(c => c.Bytes),
                    TotalMs: group.Sum(c => c.Ms),
                    TypeCounts:
                    [
                        .. group.SelectMany(c => c.TypeCounts)
                            .GroupBy(t => t.Type, StringComparer.Ordinal)
                            .Select(g => (Type: g.Key, Count: g.Sum(t => t.Count)))
                            .OrderByDescending(t => t.Count),
                    ])),
        ];

    private static void Print(IReadOnlyList<InventoryCallTiming> calls)
    {
        foreach (var call in calls)
        {
            var page = call.PageIndex >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"page {call.PageIndex}")
                : "-";
            var breakdown = call.TypeCounts.Count == 0
                ? string.Empty
                : "  (" + string.Join(", ", call.TypeCounts.Select(t => $"{t.Type}={t.Count}")) + ")";

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {call.CallName,-28} {page,-8} {call.ObjectCount,5} objects{breakdown}   {call.Bytes,8} bytes   {call.Ms,7:0} ms"));
        }

        Console.WriteLine();
        Console.WriteLine("  -- summary by call --");

        foreach (var summary in Summarize(calls))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {summary.CallName,-28} calls {summary.Calls,3}   objects {summary.Objects,6}   " +
                $"bytes {summary.Bytes,10}   total {summary.TotalMs,8:0} ms"));

            foreach (var (type, count) in summary.TypeCounts)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {type,-24} {count,6} objects"));
            }
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  TOTAL                        calls {calls.Count,3}   bytes {calls.Sum(c => c.Bytes),10}   " +
            $"total {calls.Sum(c => c.Ms),8:0} ms"));
    }
}
