using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How slow, how loud and how sure before a virtual machine is named as the
/// load on a slow volume.
/// </summary>
/// <remarks>
/// <para>
/// Every number below is either borrowed from a rule that already made the same
/// judgement, or marked as this product's own choice with the reasoning beside
/// it. None of them is cited from a vendor, because none is published: vROps'
/// Storage Heavy Hitters ranks machines by IOPS and draws no line at all, and
/// HPE InfoSight's VMVision reports per-VM latency contribution without stating
/// how it decides. What they establish is that storage load is attributable to
/// a machine; where to draw the line is left to whoever builds it.
/// </para>
/// <para>
/// The two counters in <see cref="VirtualMachineOperationCounters"/> are named
/// here rather than found by shape, following
/// <see cref="StorageLatencyBlindSpotPolicy.SiocCounter"/>. The volume side is
/// found by shape exactly as <see cref="SharedVolumeLatency"/> finds it; the
/// machine side cannot be, because nothing on <c>CounterValue</c> says "this is
/// a rate of requests made by this machine" as opposed to any other average
/// that happens to be a plain number.
/// </para>
/// </remarks>
public sealed record StorageNoisyNeighbourPolicy
{
    /// <summary>
    /// The peer policy whose latency floor decides that a volume is slow.
    /// </summary>
    /// <remarks>
    /// Held rather than copied, for the reason <see cref="SharedVolumePolicy.Peers"/>
    /// gives: the cycle wires <see cref="PeerOutliers"/>' own policy in here, so
    /// "slow enough to talk about" means the same number in every storage rule
    /// and an operator raising it for an estate on spinning disk moves all of
    /// them at once. Only <see cref="PeerOutlierPolicy.MinimumMilliseconds"/> is
    /// read; the peer multiple answers a different question and is not borrowed.
    /// </remarks>
    public PeerOutlierPolicy Peers { get; init; } = PeerOutlierPolicy.Default;

    /// <summary>
    /// The counters that together are one machine's requests per second.
    /// </summary>
    /// <remarks>
    /// Read and write, summed. The collector sums each across the machine's
    /// virtual disks before it gets here — see
    /// <c>VsphereCounters.IsAdditiveAcrossDevices</c> — so each arrives as one
    /// aggregate reading per machine per cycle.
    /// </remarks>
    public IReadOnlyList<string> VirtualMachineOperationCounters { get; init; } =
    [
        "virtualDisk.numberReadAveraged.average",
        "virtualDisk.numberWriteAveraged.average",
    ];

    /// <summary>
    /// How many other measured machines on the volume a candidate is compared with.
    /// </summary>
    /// <remarks>
    /// Three, and the argument is <see cref="PeerOutlierPolicy.MinimumVantagePoints"/>'
    /// applied to machines instead of hosts: a median of one is that machine,
    /// a median of two is a disagreement with no majority. With three
    /// neighbours the median is a machine that is neither the loudest nor the
    /// quietest, which is what "typical on this volume" has to mean. Product's
    /// own choice, not a citation.
    /// </remarks>
    public int MinimumPeers { get; init; } = 3;

    /// <summary>How many times its neighbours' median a machine must reach.</summary>
    /// <remarks>
    /// <para>
    /// Four, the same "clearly stands out" multiple
    /// <see cref="PeerOutlierPolicy.Multiple"/> and
    /// <see cref="SharedVolumePolicy.BusyMultipleOfEstateMedian"/> use. Product's
    /// own choice: consistent within the product, not taken from anyone.
    /// </para>
    /// <para>
    /// The median is floored at one operation a second before multiplying, for
    /// the reason <see cref="PeerOutliers"/> floors at one millisecond — on a
    /// volume of idle machines the median is zero and every reading is an
    /// infinite multiple of it. <see cref="MinimumOperationsPerSecond"/> is the
    /// gate that actually carries that case.
    /// </para>
    /// </remarks>
    public double Multiple { get; init; } = 4d;

    /// <summary>
    /// The load a machine must carry on its own before it can be the problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One hundred operations a second, read and write together. Product's own
    /// choice. The claim this rule makes is that a machine is a material part
    /// of what a shared array is being asked to do, and on the estate this was
    /// written against a busy volume serves thousands of operations a second
    /// (the 20 September probe saw per-host peaks of 1 638 reads) while most
    /// machines idle at single figures. A machine at forty operations can be
    /// four times a median of ten and still be nobody's problem.
    /// </para>
    /// <para>
    /// Ten times <see cref="SharedVolumePolicy.MinimumOperationsPerSecond"/>,
    /// deliberately: that floor asks whether a volume's latency average means
    /// anything at all, this one asks whether one machine could plausibly be
    /// why the volume is slow. The second is a far stronger claim.
    /// </para>
    /// </remarks>
    public double MinimumOperationsPerSecond { get; init; } = 100d;

    /// <summary>
    /// How much the volume's load must exceed its usual level to have risen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One and a half: half again what the volume usually carries. Product's
    /// own choice. This is the suppressor, and the whole of the rule's honesty
    /// rests on it. A volume that is slow while carrying its ordinary load is
    /// an array degrading underneath a constant workload, and naming the
    /// busiest machine on it is the confident wrong answer — somebody throttles
    /// a database that has done the same thing every day for a year while the
    /// controller keeps failing.
    /// </para>
    /// <para>
    /// Set low rather than high on purpose. The other gates already demand
    /// that the volume is slow and that one machine is four times its
    /// neighbours; this one only has to rule out "nothing changed". A higher
    /// bar would silence the rule on large volumes where one machine's burst
    /// is a real but modest fraction of the total.
    /// </para>
    /// </remarks>
    public double RiseMultiple { get; init; } = 1.5d;

    /// <summary>How far back "usual" reaches.</summary>
    /// <remarks>
    /// A day, so the baseline covers one of every hour: a volume busy every
    /// night at two is judged against a figure that includes two in the
    /// morning. Product's own choice. The cost is that a machine which has
    /// hammered a volume for more than a day has become the baseline and the
    /// rule goes quiet about it — acceptable, because by then it is a capacity
    /// question rather than an incident.
    /// </remarks>
    public TimeSpan BaselineLookback { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How much of the recent past is left out of "usual".</summary>
    /// <remarks>
    /// The last hour, so the episode being judged does not dilute its own
    /// baseline, and so the current cycle's samples — already written by the
    /// time rules run — are never compared with themselves. Product's own
    /// choice.
    /// </remarks>
    public TimeSpan BaselineExclusion { get; init; } = TimeSpan.FromHours(1);

    public static StorageNoisyNeighbourPolicy Default { get; } = new();
}

/// <summary>
/// Names the virtual machines generating an outsized share of a slow volume's I/O.
/// </summary>
/// <remarks>
/// <para>
/// The achievable half of the noisy-neighbour claim, architecture §10 item 1b.
/// CPU culprit attribution cannot be done from vSphere's counters — a host
/// reports saturation and each guest reports waiting, and nothing says which
/// guest's demand caused another's wait. Storage is different: every machine
/// reports its own requests per second, and <c>VM BackedBy Datastore</c> says
/// where they go. vROps (Storage Heavy Hitters) and HPE InfoSight (VMVision)
/// both attribute storage load per machine for exactly this reason.
/// </para>
/// <para>
/// Three conditions and a suppressor, cheapest first. The volume is slow from
/// every host that reads it. A machine stored on it carries at least
/// <see cref="StorageNoisyNeighbourPolicy.MinimumOperationsPerSecond"/> and at
/// least <see cref="StorageNoisyNeighbourPolicy.Multiple"/> times the median of
/// its measured neighbours, of which there are enough to have a median. And
/// the volume's own load has risen above its usual level — if it has not, the
/// array is degrading under constant load and the rule stays silent. Unknown
/// history is treated the same as "did not rise": the suppressor cannot be
/// cleared by the absence of the evidence it needs.
/// </para>
/// <para>
/// Machines with disks on more than one volume are left out entirely — neither
/// named nor counted as a neighbour. The platform reports a machine's requests
/// per virtual disk, instanced <c>scsi0:0</c>, and nothing this product collects
/// maps a disk to the volume its backing file lives on. The collector therefore
/// sums the disks into one figure per machine, and that figure can only be
/// attributed to a volume when the machine lives on exactly one. A machine with
/// a mounted ISO on a second datastore is excluded by this too. Closing that
/// needs each disk's backing datastore from inventory, which is future work.
/// </para>
/// <para>
/// The vMotion window the architecture names as a second suppressor is not
/// implemented: there is no event stream until M2.1. A machine migrating onto
/// a volume legitimately produces this signature and will be named.
/// </para>
/// <para>
/// On the estate this was written against, this rule is expected to be silent
/// and that is correct. Datastore latency reads zero almost everywhere —
/// whole-millisecond truncation with Storage I/O Control off, see
/// <see cref="StorageLatencyBlindSpot"/> — so no volume is ever "slow" by the
/// only measure available, and nothing is worked around to make it otherwise.
/// </para>
/// </remarks>
public static class StorageNoisyNeighbour
{
    /// <summary>
    /// Its own category, because it sends the operator somewhere new.
    /// </summary>
    /// <remarks>
    /// "Storage path" names a host's cable, "Storage array" the array, "Storage
    /// layer" a layer of a host's stack. This names the workload — the place
    /// to look is what those machines are doing, not the storage.
    /// </remarks>
    public const string Category = "Storage demand";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "storage-noisy-neighbour";

    private const string Title = "Virtual machines are loading a slow volume";

    /// <summary>
    /// Every slow volume with a machine on it that stands out.
    /// </summary>
    /// <param name="observations">This cycle's samples.</param>
    /// <param name="graph">Where each machine is stored.</param>
    /// <param name="typicalRate">
    /// The usual level of one load series, or <c>null</c> when it is not
    /// known. Consulted only for volumes that have already passed every other
    /// gate, so on a healthy estate it is never called. See
    /// <see cref="TypicalRateFrom"/>.
    /// </param>
    /// <param name="policy">Defaults, and why they are what they are.</param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        EntityGraph graph,
        Func<SeriesKey, double?> typicalRate,
        StorageNoisyNeighbourPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(typicalRate);

        var rules = policy ?? StorageNoisyNeighbourPolicy.Default;
        var alerts = new List<AlertDefinition>();

        var rates = MachineRates(observations, graph, rules);
        var (residents, spanning) = Residents(graph);

        foreach (var volume in observations.GroupBy(o => o.Entity))
        {
            if (Judge(volume, graph, rates, residents, spanning, typicalRate, rules) is { } found)
            {
                alerts.Add(found);
            }
        }

        return alerts;
    }

    /// <summary>
    /// The usual level of a series, read from what the product has recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mean of the five-minute buckets over
    /// <see cref="StorageNoisyNeighbourPolicy.BaselineLookback"/>, stopping
    /// <see cref="StorageNoisyNeighbourPolicy.BaselineExclusion"/> short of now.
    /// Summed from each bucket's sum and count rather than averaged from the
    /// bucket averages, so a bucket with a missing sample does not weigh as
    /// much as a full one.
    /// </para>
    /// <para>
    /// <c>null</c> for a series never recorded or recorded with nothing in the
    /// window. That is not zero, and the rule does not treat it as zero: an
    /// unknown baseline cannot show that load rose, so the rule stays silent.
    /// A fresh installation is therefore silent for its first hour or so by
    /// construction.
    /// </para>
    /// </remarks>
    public static Func<SeriesKey, double?> TypicalRateFrom(
        ISeriesReader store,
        DateTimeOffset nowUtc,
        StorageNoisyNeighbourPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var rules = policy ?? StorageNoisyNeighbourPolicy.Default;
        var to = nowUtc - rules.BaselineExclusion;
        var from = nowUtc - rules.BaselineLookback;

        return key =>
        {
            if (to <= from)
            {
                return null;
            }

            var result = store.Query(new SeriesQuery
            {
                Key = key,
                FromUtc = from,
                ToUtc = to,
                Resolution = SeriesResolution.FiveMinutes,
                MaxPoints = (int)Math.Ceiling((to - from) / TimeSpan.FromMinutes(5)) + 1,
            });

            var count = result.Points.Sum(p => p.Count);

            return result.Exists && count > 0
                ? result.Points.Sum(p => p.Sum) / count
                : null;
        };
    }

    private static AlertDefinition? Judge(
        IGrouping<EntityId, Observation> volume,
        EntityGraph graph,
        Dictionary<EntityId, double> rates,
        Dictionary<EntityId, List<EntityId>> residents,
        Dictionary<EntityId, int> spanning,
        Func<SeriesKey, double?> typicalRate,
        StorageNoisyNeighbourPolicy rules)
    {
        // Slow from every host that reads it, on at least one counter. One
        // host alone being slow is that host's path and PeerOutliers' finding,
        // and no machine's workload explains a cable.
        var slowest = SlowCounter(volume, rules);

        if (slowest is null)
        {
            return null;
        }

        // Only machines we actually measured. A powered-off machine reports
        // nothing, and counting it as a quiet neighbour would drag the median
        // down and make every running machine look exceptional.
        var measured = residents.TryGetValue(volume.Key, out var stored)
            ? stored.Where(rates.ContainsKey).ToList()
            : [];

        if (measured.Count < rules.MinimumPeers + 1)
        {
            return null;
        }

        var culprits = new List<(EntityId Machine, double Rate, double PeerMedian)>();

        foreach (var machine in measured)
        {
            var rate = rates[machine];

            if (rate < rules.MinimumOperationsPerSecond)
            {
                continue;
            }

            var peerMedian = Stats.Median([.. measured.Where(m => m != machine).Select(m => rates[m])]);

            if (rate >= Stats.FlooredMultiple(rules.Multiple, peerMedian, 1d))
            {
                culprits.Add((machine, rate, peerMedian));
            }
        }

        if (culprits.Count == 0)
        {
            return null;
        }

        // The suppressor, last because it is the only step that reads
        // history. The volume's load now, against its usual level.
        var load = volume.Where(o => IsLoad(o.Value)).ToList();

        if (load.Count == 0)
        {
            return null;
        }

        double typical = 0;

        foreach (var reading in load)
        {
            var key = new SeriesKey(reading.Entity, reading.Value.CounterName, reading.Value.Instance);

            // One unknown series makes the whole baseline unknown. Summing
            // the known ones would understate "usual" — a host added this
            // morning has no history — and an understated baseline is exactly
            // what makes load look as though it rose.
            if (typicalRate(key) is not { } usual)
            {
                return null;
            }

            typical += usual;
        }

        var current = load.Sum(o => o.Value.Raw);

        if (current < Stats.FlooredMultiple(rules.RiseMultiple, typical, 1d))
        {
            return null;
        }

        var witness = slowest;

        return new AlertDefinition
        {
            // The volume and nothing else. Which machines are loudest may
            // change from one cycle to the next while the volume stays the
            // slow one; a fingerprint carrying their names would close one
            // alert and open another for what the operator sees as a single
            // incident. The names are in the description, which is updated.
            Fingerprint = AlertFingerprint.Create(
                witness.Source, Title, Category, volume.Key.Value, "storage-noisy-neighbour"),
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description = Describe(
                volume.Key, graph, witness, culprits, measured.Count,
                spanning.GetValueOrDefault(volume.Key), current, typical),
            Category = Category,
            Source = witness.Source,
            Entity = volume.Key,
        };
    }

    /// <summary>
    /// The latency counter every host agrees is slow, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Found by shape, a duration from a vantage point, exactly as
    /// <see cref="SharedVolumeLatency"/> finds it. Returns the worst reading of
    /// the worst such counter, taken for its source and value.
    /// </remarks>
    private static Observation? SlowCounter(
        IEnumerable<Observation> volume, StorageNoisyNeighbourPolicy rules)
    {
        var floor = rules.Peers.MinimumMilliseconds;

        return volume
            .Where(o => o.Value.InstanceIsVantagePoint &&
                        Readings.IsMilliseconds(o.Value.Unit))
            .GroupBy(o => o.Value.CounterName)
            .Where(g => g.All(o => o.Value.Raw >= floor))
            .Select(g => g.MaxBy(o => o.Value.Raw)!)
            .MaxBy(o => o.Value.Raw);
    }

    /// <summary>
    /// A volume's demand, seen from one host. The shape <see cref="SharedVolumeLatency"/> uses.
    /// </summary>
    private static bool IsLoad(CounterValue value) =>
        value.InstanceIsVantagePoint &&
        value.Rollup == RollupType.Average &&
        !value.IsFaultCount &&
        string.Equals(value.Unit, "number", StringComparison.OrdinalIgnoreCase);

    /// <summary>Each live machine's requests per second, read and write together.</summary>
    /// <remarks>
    /// The aggregate instance only. The collector reports one summed figure per
    /// machine; if per-disk series ever arrive beside it, adding them in would
    /// count every request twice.
    /// </remarks>
    private static Dictionary<EntityId, double> MachineRates(
        IReadOnlyList<Observation> observations,
        EntityGraph graph,
        StorageNoisyNeighbourPolicy rules)
    {
        var rates = new Dictionary<EntityId, double>();

        foreach (var observation in observations)
        {
            if (!observation.Value.IsAggregateInstance ||
                !rules.VirtualMachineOperationCounters.Contains(
                    observation.Value.CounterName, StringComparer.OrdinalIgnoreCase) ||
                !IsLive(graph, observation.Entity, EntityKind.VirtualMachine))
            {
                continue;
            }

            rates[observation.Entity] = rates.GetValueOrDefault(observation.Entity) + observation.Value.Raw;
        }

        return rates;
    }

    /// <summary>
    /// The machines stored on exactly one volume, by volume, and how many
    /// machines on each volume were left out for spanning several.
    /// </summary>
    private static (Dictionary<EntityId, List<EntityId>> Residents, Dictionary<EntityId, int> Spanning)
        Residents(EntityGraph graph)
    {
        var homes = new Dictionary<EntityId, HashSet<EntityId>>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.BackedBy))
        {
            if (!IsLive(graph, edge.From, EntityKind.VirtualMachine) ||
                !IsLive(graph, edge.To, EntityKind.Datastore))
            {
                continue;
            }

            if (!homes.TryGetValue(edge.From, out var volumes))
            {
                homes[edge.From] = volumes = [];
            }

            volumes.Add(edge.To);
        }

        var residents = new Dictionary<EntityId, List<EntityId>>();
        var spanning = new Dictionary<EntityId, int>();

        foreach (var (machine, volumes) in homes)
        {
            if (volumes.Count == 1)
            {
                var only = volumes.First();

                if (!residents.TryGetValue(only, out var list))
                {
                    residents[only] = list = [];
                }

                list.Add(machine);
                continue;
            }

            foreach (var volume in volumes)
            {
                spanning[volume] = spanning.GetValueOrDefault(volume) + 1;
            }
        }

        return (residents, spanning);
    }

    private static bool IsLive(EntityGraph graph, EntityId id, EntityKind kind) =>
        graph.Entities.TryGetValue(id, out var entity) &&
        entity.Kind == kind &&
        entity.ObservationState != ObservationState.Vanished;

    private static string Describe(
        EntityId volume,
        EntityGraph graph,
        Observation witness,
        List<(EntityId Machine, double Rate, double PeerMedian)> culprits,
        int measured,
        int spanning,
        double current,
        double typical)
    {
        string Name(EntityId id) =>
            graph.Entities.TryGetValue(id, out var e) && !string.IsNullOrWhiteSpace(e.DisplayName)
                ? e.DisplayName
                : id.Value;

        var named = string.Join(
            "; ",
            culprits
                .OrderByDescending(c => c.Rate)
                .Select(c =>
                    $"'{Name(c.Machine)}' at {Readings.Number(c.Rate)} operations a second, " +
                    $"{Readings.Number(c.Rate / Math.Max(c.PeerMedian, 1d))}x the median of its neighbours " +
                    $"({Readings.Number(c.PeerMedian)}), {Readings.Number(current > 0 ? c.Rate / current * 100d : 0d)}% of " +
                    "the volume's load"));

        // Said, not hidden: which machines could not be judged at all, so an
        // operator does not read the list as complete.
        var unjudged = spanning > 0
            ? $" {spanning} machine(s) with disks on this and another volume were not judged, " +
              "because the platform reports a machine's I/O as one total that cannot be split " +
              "between volumes."
            : string.Empty;

        return
            $"'{Name(volume)}' is slow from every host that reads it — '{witness.Value.CounterName}' " +
            $"up to {Readings.Number(witness.Value.Raw)} ms — and is carrying {Readings.Number(current)} operations a second " +
            $"against a usual {Readings.Number(typical)} over the past day. Of the {measured} measured machine(s) " +
            $"stored only on this volume, these are carrying an outsized share: {named}.{unjudged} " +
            "Look at what these machines are doing — a backup, a batch job, a scan — before " +
            "looking at the array. This is a correlation in time, not proof: a machine that has " +
            "just moved onto this volume produces the same picture.";
    }
}
