using System.Globalization;
using System.Xml.Linq;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>One entity's samples from a <c>QueryPerf</c> response.</summary>
public sealed record PerfEntitySamples
{
    /// <summary>The managed object reference, e.g. <c>host-123</c>.</summary>
    public required string EntityMoRef { get; init; }

    /// <summary>The most recent sample.</summary>
    public IReadOnlyList<CounterValue> Values { get; init; } = [];

    /// <summary>
    /// When vCenter took <see cref="Values"/>, or null when the reply did not say.
    /// </summary>
    /// <remarks>
    /// vCenter's clock, verbatim, and deliberately not corrected toward ours.
    /// The same sample comes back in consecutive reads, and the store tells a
    /// repeat from a new reading by its time: a timestamp that is a function of
    /// when we happened to ask would file one sample under two moments, and a
    /// summation counted twice is worse than one counted late.
    /// </remarks>
    public DateTimeOffset? SampledAtUtc { get; init; }

    /// <summary>
    /// The samples before the latest that the same reply carried, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These used to be dropped. A thirty-second cycle meeting twenty-second
    /// samples then never reads about one in three — a thinner chart for a
    /// rate, and for a summation the event itself: a bus reset that fell in
    /// the unread slot did not happen as far as the product could tell.
    /// </para>
    /// <para>
    /// Kept apart from <see cref="Values"/> because they are for the store,
    /// not for the rules: a rule is handed one value per series and means the
    /// current one. Empty when the reply carried no sample times, since a
    /// number cannot be kept without saying when it was true.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PerfSampleSet> Earlier { get; init; } = [];
}

/// <summary>One entity's values at one sample time.</summary>
public sealed record PerfSampleSet
{
    public required DateTimeOffset SampledAtUtc { get; init; }

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
                StatsType = Child(returnVal, "statsType") ?? string.Empty,
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
            var times = ReadSampleTimes(returnVal);
            var allSeries = Elements(returnVal, "value")
                .Select(series => ReadSeries(series, countersById, interval))
                .OfType<SeriesPoints>()
                .ToList();

            results.Add(new PerfEntitySamples
            {
                EntityMoRef = entity.Trim(),
                Values = Aggregate([.. allSeries.Select(s => s.Latest()).OfType<CounterValue>()]),
                SampledAtUtc = times.Count > 0 ? times[^1] : null,
                Earlier = ReadEarlier(allSeries, times),
            });
        }

        return results;
    }

    /// <summary>
    /// Reduces one counter's series to a summary, keeping the devices where
    /// they are worth keeping.
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
    /// <para>
    /// A combined value carries no instance, and that is the correction rather
    /// than a detail. This kept the instance of whichever series happened to
    /// come back first, so a host's storage latency was stored as the maximum
    /// across thirty-two LUNs while labelled with one arbitrary LUN's name —
    /// a number that is wrong about what it describes, wearing an identifier
    /// precise enough to be believed. Worse, "first" is whatever order the
    /// server replied in, so the device a series claimed to be about could
    /// change between cycles without anything changing on screen.
    /// </para>
    /// <para>
    /// For storage, the devices are now kept <em>as well</em> rather than
    /// instead: the summary answers whether this host's storage is slow, and
    /// only the devices can say which one. See
    /// <see cref="VsphereCounters.KeepPerDevice"/> for why that does not extend
    /// to CPU, which reports per core.
    /// </para>
    /// </remarks>
    private static List<CounterValue> Aggregate(List<CounterValue> values)
    {
        var result = new List<CounterValue>();

        foreach (var group in values.GroupBy(v => v.CounterName, StringComparer.Ordinal))
        {
            // Some instances are not devices but other entities. A host's
            // datastore counters name a volume in the instance, and each of
            // those volumes is an object with its own page — so the per-device
            // reasoning below does not apply and collapsing them would produce
            // one number about thirty datastores with no way to say which is
            // slow. These are kept apart and attributed by the caller.
            if (VsphereCounters.InstanceNamesAnEntity(group.Key))
            {
                // The aggregate is dropped rather than kept: a figure covering
                // every volume this host can see belongs to none of them, and
                // there is no entity for it to be about.
                result.AddRange(group.Where(v => !v.IsAggregateInstance));
                continue;
            }

            // The devices themselves, kept beside the summary rather than
            // instead of it. Both are needed and they answer different
            // questions: the summary says whether this host's storage is slow,
            // and only the devices can say which one. See KeepPerDevice.
            if (VsphereCounters.KeepPerDevice(group.Key))
            {
                result.AddRange(group.Where(v => !v.IsAggregateInstance));
            }

            var aggregate = group.FirstOrDefault(v => v.IsAggregateInstance);
            if (aggregate.CounterName is not null)
            {
                result.Add(aggregate);
                continue;
            }

            var first = group.First();
            // A rate of requests adds up across disks the way a total does.
            // See VsphereCounters.IsAdditiveAcrossDevices.
            var combined = first.Rollup == RollupType.Summation ||
                           VsphereCounters.IsAdditiveAcrossDevices(group.Key)
                ? group.Sum(v => v.Raw)
                : group.Max(v => v.Raw);

            // Instance cleared: the sum across devices belongs to no device,
            // and neither does the worst of them.
            result.Add(first with { Raw = combined, Instance = string.Empty });
        }

        return result;
    }

    /// <summary>
    /// Every sample slot before the last, each reduced exactly as the latest is.
    /// </summary>
    /// <remarks>
    /// Only series whose points line up with the sample times take part: slot
    /// <c>i</c> of a series is sample <c>i</c> of the reply only when there is
    /// one point per time. A series that came back shorter cannot say which
    /// slots it skipped, so it contributes its latest value and nothing else.
    /// </remarks>
    private static List<PerfSampleSet> ReadEarlier(
        List<SeriesPoints> allSeries, List<DateTimeOffset> times)
    {
        var earlier = new List<PerfSampleSet>();

        for (var slot = 0; slot < times.Count - 1; slot++)
        {
            var values = allSeries
                .Where(s => s.Points.Count == times.Count)
                .Select(s => s.At(slot))
                .OfType<CounterValue>()
                .ToList();

            if (values.Count > 0)
            {
                earlier.Add(new PerfSampleSet { SampledAtUtc = times[slot], Values = Aggregate(values) });
            }
        }

        return earlier;
    }

    /// <summary>One series as it arrived: what it measures, and a point per slot.</summary>
    private sealed record SeriesPoints(CounterValue Shape, string WireUnit, IReadOnlyList<double?> Points)
    {
        /// <summary>The most recent point that parsed, as before.</summary>
        public CounterValue? Latest() => Reading(Points.LastOrDefault(p => p is not null));

        public CounterValue? At(int slot) => Reading(Points[slot]);

        // A reading that cannot exist is not a reading; see ReadSeries.
        private CounterValue? Reading(double? point) =>
            point is { } raw && raw >= 0
                ? Shape with { Raw = VsphereUnitNormalizer.Normalize(raw, WireUnit) }
                : null;
    }

    private static SeriesPoints? ReadSeries(
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
        // One entry per slot, unparseable ones kept as null rather than
        // skipped: a series is only lined up with the sample times when it has
        // a point for each, and dropping one would shift every later point
        // onto the wrong moment.
        var points = series.Elements()
            .Where(e => e.Name.LocalName is "value" or "long")
            .Select(e => double.TryParse(e.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? (double?)d
                : null)
            .ToList();

        if (!points.Any(p => p is not null))
        {
            return null;
        }

        // A reading that cannot exist is not a reading. None of the counters
        // this product collects — latency, IOPS, percentages, memory, counts —
        // can be negative, and yet a live vCenter returns exactly -1 for
        // between 0.15% and 0.52% of every one of them, plus at least one
        // value of -1.8e18 that is garbage under any reading.
        //
        // What -1 means in vim25 is not documented anywhere this was able to
        // check, and it is not needed: whatever it means, it is not a number
        // of milliseconds. Stored as one it dragged host averages below zero,
        // and a single -1.8e18 in disk.kernelLatency would scale that chart's
        // axis so that every real value sat on one flat line.
        //
        // Dropped rather than clamped to zero, because zero is a measurement
        // and this is the absence of one. The product already models that: a
        // gap stays a gap, never zero-filled, and a gap on a chart is visible
        // in a way a fabricated zero is not. Applied per slot, in
        // SeriesPoints.Reading, so an earlier sample is held to the same rule
        // as the latest.
        //
        // Revisit if a collector ever reports something genuinely signed — a
        // temperature from a BMC would be the obvious one — at which point
        // this belongs with the counter's metadata rather than here.
        return new SeriesPoints(
            new CounterValue
            {
                CounterName = counter.Key,
                // Filled per slot. Normalised because vSphere reports
                // percentages in hundredths — 26.69% arrives as 2669 — and
                // some latency counters in microseconds beside others in
                // milliseconds. The unit travels with the value so the two
                // stay consistent. See VsphereUnitNormalizer.
                Raw = 0,
                Rollup = counter.Rollup,
                Interval = interval,
                Unit = VsphereUnitNormalizer.NormalizedUnit(counter.Unit),
                IsFaultCount = VsphereCounters.IsFaultCounter(counter.Key),
                Instance = instance.Trim(),
            },
            counter.Unit,
            points);
    }

    /// <summary>
    /// When each sample slot was taken, by vCenter's clock, oldest first.
    /// </summary>
    /// <remarks>
    /// All or nothing. A reply in which any slot has no readable time yields
    /// none, because the slots are matched to the points by position and a
    /// partial list would match them to the wrong ones.
    /// </remarks>
    private static List<DateTimeOffset> ReadSampleTimes(XElement returnVal)
    {
        var times = new List<DateTimeOffset>();

        foreach (var sample in Elements(returnVal, "sampleInfo"))
        {
            if (!DateTimeOffset.TryParse(
                    Child(sample, "timestamp"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var at))
            {
                return [];
            }

            times.Add(at);
        }

        return times;
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
            document = VsphereXml.Parse(xml);
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
