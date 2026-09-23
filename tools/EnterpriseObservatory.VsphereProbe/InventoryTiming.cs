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
    double Ms,
    IReadOnlyList<PropertyPathBytes> PropertyBytes)
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
/// One <c>propSet</c> occurrence: the serialized size of one property path on
/// one object, attributed to that object's type.
/// </summary>
internal sealed record PropertyPathBytes(string Type, string Path, long Bytes);

/// <summary>One property-path row of <see cref="InventoryTiming.SummarizePropertyBytes"/>'s per-type table.</summary>
internal sealed record TypePropertyBytesRow(string Path, long Bytes, double SharePercent, double AvgBytesPerObject);

/// <summary>Bytes-per-property breakdown for one object type: top 15 paths by bytes, the rest folded into "other".</summary>
internal sealed record TypePropertyBytesSummary(
    string Type,
    int Objects,
    long TotalBytes,
    IReadOnlyList<TypePropertyBytesRow> Top,
    long OtherBytes,
    int OtherCount);

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

    /// <summary>Forgets what was recorded, so the next tier is measured on its own.</summary>
    public void Clear()
    {
        _calls.Clear();
        _nextPageIndex = 0;
    }

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
        var propertyBytes = isPage ? PropertyBytesOf(bytes) : [];

        _calls.Add(new InventoryCallTiming(
            callName, pageIndex, typeCounts, bytes.LongLength, watch.Elapsed.TotalMilliseconds, propertyBytes));

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

    /// <summary>
    /// Every <c>propSet</c> in one property-collector page, as the serialized
    /// size of its value attributed to its object's type and the property's
    /// path -- a minimal pass over the same reply, kept apart from
    /// <see cref="PropertyCollectorParser"/> because that parser flattens
    /// values to text and never carries their original byte size.
    /// </summary>
    internal static IReadOnlyList<PropertyPathBytes> PropertyBytesOf(byte[] bytes)
    {
        try
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            var document = XDocument.Parse(body);
            var returnVal = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");
            if (returnVal is null)
            {
                return [];
            }

            var entries = new List<PropertyPathBytes>();

            foreach (var objectContent in returnVal.Elements().Where(e => e.Name.LocalName == "objects"))
            {
                var type = objectContent.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "obj")
                    ?.Attribute("type")?.Value ?? string.Empty;

                foreach (var propSet in objectContent.Elements().Where(e => e.Name.LocalName == "propSet"))
                {
                    var name = propSet.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    entries.Add(new PropertyPathBytes(
                        type, name, System.Text.Encoding.UTF8.GetByteCount(propSet.ToString())));
                }
            }

            return entries;
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
        // Configuration first, so the fast read measured below carries it
        // exactly as the collector's does after its first slow pass.
        Console.WriteLine("== SLOW tier: configuration (Monitoring:ConfigurationIntervalSeconds) ==");
        await MeasureAsync(handler, () => client.RetrieveConfigurationAsync(cancellationToken)).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== FAST tier: topology and state (Monitoring:InventoryIntervalSeconds) ==");
        await MeasureAsync(handler, () => client.RetrieveInventoryAsync(cancellationToken)).ConfigureAwait(false);

        return 0;
    }

    private static async Task MeasureAsync(InventoryTimingHandler handler, Func<Task> read)
    {
        handler.Clear();
        handler.Enabled = true;
        try
        {
            await read().ConfigureAwait(false);
        }
        finally
        {
            handler.Enabled = false;
        }

        Print(handler.Calls);
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

    /// <summary>
    /// Per object type, every property path seen across all pages: total
    /// bytes, share of that type's bytes, and average bytes per object of
    /// that type -- sorted by bytes descending, top 15 with the rest folded
    /// into "other". This is what tells a planner which property to stop
    /// asking for (see InventoryTiming.cs remarks): the type with the most
    /// bytes and, inside it, the path carrying most of them.
    /// </summary>
    public static IReadOnlyList<TypePropertyBytesSummary> SummarizePropertyBytes(
        IReadOnlyList<InventoryCallTiming> calls)
    {
        var objectsByType = calls
            .SelectMany(c => c.TypeCounts)
            .GroupBy(t => t.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Count), StringComparer.Ordinal);

        var summaries = new List<TypePropertyBytesSummary>();

        foreach (var typeGroup in calls.SelectMany(c => c.PropertyBytes).GroupBy(p => p.Type, StringComparer.Ordinal))
        {
            var objects = objectsByType.GetValueOrDefault(typeGroup.Key);

            List<(string Path, long Bytes)> byPath =
            [
                .. typeGroup
                    .GroupBy(p => p.Path, StringComparer.Ordinal)
                    .Select(g => (Path: g.Key, Bytes: g.Sum(p => p.Bytes)))
                    .OrderByDescending(p => p.Bytes),
            ];

            var totalBytes = byPath.Sum(p => p.Bytes);
            var top = byPath
                .Take(15)
                .Select(p => new TypePropertyBytesRow(
                    p.Path,
                    p.Bytes,
                    totalBytes == 0 ? 0 : 100.0 * p.Bytes / totalBytes,
                    objects == 0 ? 0 : (double)p.Bytes / objects))
                .ToList();

            var other = byPath.Skip(15).ToList();

            summaries.Add(new TypePropertyBytesSummary(
                typeGroup.Key, objects, totalBytes, top, other.Sum(p => p.Bytes), other.Count));
        }

        return [.. summaries.OrderByDescending(s => s.TotalBytes)];
    }

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

        Console.WriteLine();
        Console.WriteLine("  -- bytes per property, by type --");

        foreach (var type in SummarizePropertyBytes(calls))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {type.Type,-24} objects {type.Objects,6}   bytes {type.TotalBytes,10}"));

            foreach (var row in type.Top)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      {row.Path,-40} {row.Bytes,10} bytes   {row.SharePercent,5:0.0}%   " +
                    $"{row.AvgBytesPerObject,8:0} avg/obj"));
            }

            if (type.OtherCount > 0)
            {
                var label = string.Create(CultureInfo.InvariantCulture, $"(other {type.OtherCount} properties)");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      {label,-40} {type.OtherBytes,10} bytes"));
            }
        }
    }
}
