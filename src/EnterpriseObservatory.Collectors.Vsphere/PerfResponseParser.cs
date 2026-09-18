using System.Globalization;
using System.Xml.Linq;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>One entity's samples from a <c>QueryPerf</c> response.</summary>
public sealed record PerfEntitySamples
{
    /// <summary>The managed object reference, e.g. <c>host-123</c>.</summary>
    public required string EntityMoRef { get; init; }

    public IReadOnlyList<CounterValue> Values { get; init; } = [];
}

/// <summary>
/// Reads vim25 <c>PerformanceManager</c> responses.
/// </summary>
/// <remarks>
/// <para>
/// Element names are matched by local name so that namespace prefixes, which
/// differ between vSphere versions and between the SOAP and REST gateways, do
/// not break parsing.
/// </para>
/// <para>
/// Malformed XML yields an empty result rather than an exception: a vendor that
/// returns something unexpected is a collection failure to be reported, not a
/// reason to end the cycle.
/// </para>
/// </remarks>
public static class PerfResponseParser
{
    /// <summary>
    /// Reads counter metadata from a <c>QueryPerfCounter</c> response.
    /// </summary>
    /// <remarks>
    /// Counter ids differ per vCenter, so this mapping must be rebuilt per
    /// connection rather than cached across installations.
    /// </remarks>
    public static IReadOnlyList<VsphereCounter> ParseCounters(string xml)
    {
        if (!TryParse(xml, out var document))
        {
            return [];
        }

        var counters = new List<VsphereCounter>();

        foreach (var returnVal in Descendants(document, "returnval"))
        {
            var id = Child(returnVal, "key");
            var group = NestedKey(returnVal, "groupInfo");
            var name = NestedKey(returnVal, "nameInfo");
            var unit = NestedKey(returnVal, "unitInfo");
            var rollup = Child(returnVal, "rollupType");

            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counterId) ||
                string.IsNullOrWhiteSpace(group) ||
                string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            counters.Add(new VsphereCounter
            {
                Id = counterId,
                Group = group,
                Name = name,
                Rollup = VsphereCounter.ParseRollup(rollup),
                Unit = unit ?? string.Empty,
                Level = int.TryParse(Child(returnVal, "level"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var level) ? level : 0,
            });
        }

        return counters;
    }

    /// <summary>
    /// Reads samples from a <c>QueryPerf</c> response.
    /// </summary>
    /// <param name="xml">The SOAP body.</param>
    /// <param name="countersById">Counter metadata, from <see cref="ParseCounters"/>.</param>
    /// <param name="fallbackInterval">
    /// Used only when the response carries no <c>sampleInfo</c>, which should
    /// not happen but is not worth failing the whole read over.
    /// </param>
    /// <remarks>
    /// <para>
    /// The interval is read from <c>sampleInfo</c> rather than assumed from the
    /// request. A summation counter is a total over its interval, so the same
    /// raw number means a different thing over 20 seconds and over 300 — the
    /// previous parser discarded <c>sampleInfo</c> entirely and relied on always
    /// having asked for 20-second samples, which stops being true the moment
    /// anything queries a datastore.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PerfEntitySamples> ParseSamples(
        string xml,
        IReadOnlyDictionary<int, VsphereCounter> countersById,
        TimeSpan fallbackInterval)
    {
        ArgumentNullException.ThrowIfNull(countersById);

        if (!TryParse(xml, out var document))
        {
            return [];
        }

        var results = new List<PerfEntitySamples>();

        foreach (var returnVal in Descendants(document, "returnval"))
        {
            var entity = Child(returnVal, "entity");
            if (string.IsNullOrWhiteSpace(entity))
            {
                continue;
            }

            var interval = ReadInterval(returnVal) ?? fallbackInterval;
            var values = new List<CounterValue>();

            foreach (var series in Elements(returnVal, "value"))
            {
                var value = ReadSeries(series, countersById, interval);
                if (value is not null)
                {
                    values.Add(value.Value);
                }
            }

            results.Add(new PerfEntitySamples
            {
                EntityMoRef = entity.Trim(),
                Values = Aggregate(values),
            });
        }

        return results;
    }

    /// <summary>
    /// Collapses the per-device series of one counter into a single value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vCenter returns one series per instance — per HBA, per NIC, per disk —
    /// plus, for most counters, an aggregate series with an empty instance name.
    /// </para>
    /// <para>
    /// The aggregate wins when present, because that is vCenter's own answer.
    /// Otherwise a summation is summed, since a total across devices is still a
    /// total; anything else takes the maximum, because for latency and
    /// saturation the worst device is what the operator needs to know about and
    /// averaging it away is how a single sick path hides behind eleven healthy
    /// ones.
    /// </para>
    /// </remarks>
    private static List<CounterValue> Aggregate(List<CounterValue> values)
    {
        var byCounter = values
            .GroupBy(v => v.CounterName, StringComparer.Ordinal)
            .Select(group =>
            {
                var aggregate = group.FirstOrDefault(v => v.IsAggregateInstance);
                if (aggregate.CounterName is not null)
                {
                    return aggregate;
                }

                var first = group.First();
                var combined = first.Rollup == RollupType.Summation
                    ? group.Sum(v => v.Raw)
                    : group.Max(v => v.Raw);

                return first with { Raw = combined };
            });

        return [.. byCounter];
    }

    private static CounterValue? ReadSeries(
        XElement series,
        IReadOnlyDictionary<int, VsphereCounter> countersById,
        TimeSpan interval)
    {
        var id = Elements(series, "id").FirstOrDefault();
        if (id is null)
        {
            return null;
        }

        if (!int.TryParse(Child(id, "counterId"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var counterId) ||
            !countersById.TryGetValue(counterId, out var counter))
        {
            // A counter we did not ask for, or metadata we failed to read.
            // Silently ignoring it is right: guessing its meaning is not.
            return null;
        }

        var instance = Child(id, "instance") ?? string.Empty;

        // Sample points are <value> in normal format and <long> in csv format.
        var points = series.Elements()
            .Where(e => e.Name.LocalName is "value" or "long")
            .Select(e => double.TryParse(e.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? (double?)d
                : null)
            .OfType<double>()
            .ToList();

        if (points.Count == 0)
        {
            return null;
        }

        return new CounterValue
        {
            CounterName = counter.Key,
            // The most recent point. Earlier points in the window belong to a
            // trend store, not to the current-state view this feeds.
            Raw = points[^1],
            Rollup = counter.Rollup,
            Interval = interval,
            Unit = counter.Unit,
            Instance = instance.Trim(),
        };
    }

    /// <summary>
    /// Reads the sampling interval the server actually used.
    /// </summary>
    private static TimeSpan? ReadInterval(XElement returnVal)
    {
        foreach (var sample in Elements(returnVal, "sampleInfo"))
        {
            if (int.TryParse(Child(sample, "interval"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    private static bool TryParse(string xml, out XDocument document)
    {
        try
        {
            document = XDocument.Parse(xml);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            document = new XDocument();
            return false;
        }
    }

    private static IEnumerable<XElement> Descendants(XContainer container, string localName) =>
        container.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Elements(XElement element, string localName) =>
        element.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Child(XElement element, string localName) =>
        Elements(element, localName).FirstOrDefault()?.Value;

    /// <summary>Reads <c>&lt;wrapper&gt;&lt;key&gt;value&lt;/key&gt;&lt;/wrapper&gt;</c>.</summary>
    private static string? NestedKey(XElement element, string wrapperLocalName) =>
        Elements(element, wrapperLocalName).FirstOrDefault() is { } wrapper
            ? Child(wrapper, "key")
            : null;
}
