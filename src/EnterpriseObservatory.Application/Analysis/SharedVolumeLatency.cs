using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How unanimous, how slow and how busy before a shared volume is the accused.
/// </summary>
/// <remarks>
/// This is the mirror of <see cref="PeerOutlierPolicy"/> and deliberately
/// borrows from it rather than restating it. The two rules divide one rung of
/// the ladder between them and the division has to stay exact, so the numbers
/// that decide which side a situation falls on live in one record and are
/// read by both.
/// </remarks>
public sealed record SharedVolumePolicy
{
    /// <summary>
    /// The peer policy this rule is the complement of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held rather than copied so the two rules cannot drift apart. The floor
    /// a host must clear is the same floor <see cref="PeerOutliers"/> uses,
    /// and the multiple that decides "one host stands out" is the same
    /// multiple — read here to decide the opposite. If an operator raises the
    /// floor for an estate on spinning disk, both halves of the rung move
    /// together, which is the only way they can both stay true.
    /// </para>
    /// <para>
    /// Wire the cycle's own <c>PeerOutlierPolicy</c> in here. Leaving the
    /// default while configuring the other is the one way to open a gap
    /// between the two rules, and it would open it silently.
    /// </para>
    /// </remarks>
    public PeerOutlierPolicy Peers { get; init; } = PeerOutlierPolicy.Default;

    /// <summary>
    /// How many hosts must mount the volume before "every host" means anything.
    /// </summary>
    /// <remarks>
    /// Three, matching <see cref="PeerOutlierPolicy.MinimumVantagePoints"/>,
    /// and for the mirrored reason. A volume mounted by one host offers no
    /// second opinion at all: its latency is equally well explained by that
    /// host's cable, and saying "every host that mounts this is slow" when
    /// there is one host is a true sentence used to imply a false one. Two is
    /// no better — agreement between two is the smallest agreement there is.
    /// </remarks>
    public int MinimumMountingHosts { get; init; } = 3;

    /// <summary>
    /// What fraction of the mounting hosts must be over the floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One, meaning all of them, and that is this rule's confidence gate.
    /// <see cref="PeerOutliers"/> needs one host of many over the floor before
    /// it names a path; this needs every one of them before it names the
    /// array. Dynatrace demands four samples of five to accuse physical
    /// storage and three of five to accuse the host queue, for exactly this
    /// reason — being wrong about the array sends someone to the storage team
    /// with a false accusation. A rule here sees one sample per series per
    /// cycle and cannot count samples, so the evidence is counted across hosts
    /// instead: one of N to blame a path, N of N to blame the array.
    /// </para>
    /// <para>
    /// Below one it stops being that claim. A quiet host is not a missing
    /// vote, it is the evidence that the array is serving somebody quickly —
    /// which is the whole case for the defence. Lower it only for the estate
    /// that has a host mounting the volume and running nothing on it, whose
    /// permanent zero would veto the rule forever, and know that what fires
    /// afterwards is a weaker claim than the title makes.
    /// </para>
    /// </remarks>
    public double MinimumElevatedShare { get; init; } = 1d;

    /// <summary>
    /// The load below which this volume's latency average means nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten operations a second, summed over read and write and over every host
    /// that mounts it. Over a twenty-second interval that is two hundred
    /// requests; below it, an average is not a picture of the volume but the
    /// fate of whichever handful of requests happened to go. An idle volume
    /// with one unlucky request reads exactly like a volume in trouble and
    /// there is no way to tell them apart from the number alone.
    /// </para>
    /// <para>
    /// A choice, not a citation. Nobody publishes one: the only numbers any
    /// vendor commits to in public are CPU readiness and datastore capacity,
    /// neither of which is this. What can be said is that the estate this was
    /// written against reports these counters as whole operations per second
    /// and most volumes read zero, so the gate does real work here rather than
    /// being decorative.
    /// </para>
    /// </remarks>
    public double MinimumOperationsPerSecond { get; init; } = 10d;

    /// <summary>
    /// How many times the estate's median load makes a volume busy rather than slow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The slow-versus-busy question, answered without a number to cite.
    /// Latency at high load may be a volume doing its job; the same latency at
    /// low load is a volume in trouble. An absolute IOPS line would have to be
    /// invented, and this estate's volumes run from zero to roughly twelve
    /// thousand operations a second, so any line drawn would be wrong for most
    /// of them.
    /// </para>
    /// <para>
    /// So the comparison is relative, which is the move this product already
    /// prefers to a threshold: a volume is busy when it is carrying several
    /// times what the estate's volumes are carrying right now. Four, the same
    /// multiple <see cref="PeerOutlierPolicy.Multiple"/> uses for "clearly
    /// stands out", and the estate median is floored at
    /// <see cref="MinimumOperationsPerSecond"/> before multiplying so that an
    /// estate doing nothing cannot elect one volume busy.
    /// </para>
    /// <para>
    /// The claim it supports is deliberately weak, and so is the action it
    /// takes: it does not reword the alert, it withholds it.
    /// </para>
    /// </remarks>
    public double BusyMultipleOfEstateMedian { get; init; } = 4d;

    public static SharedVolumePolicy Default { get; } = new();
}

/// <summary>
/// Finds a shared volume that is slow from every host that mounts it.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <see cref="PeerOutliers"/>' rung, and the half nothing
/// covered. That rule finds a volume slow from one host and not the rest,
/// which is that host's path; this finds a volume slow from all of them, which
/// is the array or the fabric. Between them the rung is complete, and neither
/// alone is: the product could say "check this host's cable" and could not say
/// "check the array".
/// </para>
/// <para>
/// The two must never both describe one situation, and the exclusion is
/// structural rather than remembered. <see cref="PeerOutliers"/> fires when the
/// worst host reaches a multiple of its peers' median; this fires only when it
/// does not, computed the same way from the same policy value. A volume where
/// one host stands out is that host's problem and this rule is silent by
/// construction, not by a gate somebody has to keep in step.
/// </para>
/// <para>
/// Its own tests record the case that made this necessary: peers all elevated
/// together is exactly what <see cref="PeerOutliers"/> deliberately stays quiet
/// about, and it called that silence "the most expensive kind of wrong answer
/// this rule can give". It was right to stay quiet and wrong to leave the
/// situation unreported. This is where it goes.
/// </para>
/// <para>
/// Milliseconds only, for the reason <see cref="PeerOutliers"/> gives: a
/// service time measured from several places onto one shared resource should
/// agree, while demand legitimately differs. Demand is read here, but never
/// alerted on — only used to decide whether the latency means anything.
/// </para>
/// </remarks>
public static class SharedVolumeLatency
{
    public const string Category = "Storage array";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "shared-volume-latency";

    private const string Title = "Slow from every host";

    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        SharedVolumePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var rules = policy ?? SharedVolumePolicy.Default;
        var alerts = new List<AlertDefinition>();

        var load = LoadPerVolume(observations);
        var busyAbove = Stats.FlooredMultiple(
            rules.BusyMultipleOfEstateMedian,
            Stats.Median([.. load.Values]),
            rules.MinimumOperationsPerSecond);

        var groups = observations
            .Where(o => o.Value.InstanceIsVantagePoint && Readings.IsMilliseconds(o.Value.Unit))
            .GroupBy(o => (o.Entity, o.Value.CounterName));

        foreach (var group in groups)
        {
            if (Unanimous(group, rules, load, busyAbove) is { } found)
            {
                alerts.Add(found);
            }
        }

        return alerts;
    }

    /// <summary>
    /// How much work each shared resource is being asked for, this cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named by shape rather than by counter: a rate, averaged, reported from
    /// a vantage point. That is what an operations-per-second counter looks
    /// like from here, and it keeps this rule free of any vendor's counter
    /// names — the constraint <c>FaultCounters</c> states and the one reason
    /// this rule needs no configured counter list while its sibling does.
    /// </para>
    /// <para>
    /// Read and write are summed rather than judged separately, unlike the
    /// latencies. The latencies are kept apart because they fail for different
    /// reasons; the loads are added because the question they answer is one
    /// question — how much is this volume being asked to do — and a volume
    /// saturated by writes is not made idle by reading nothing.
    /// </para>
    /// <para>
    /// Summed across hosts too. The volume's load is what reaches the array,
    /// and the array does not know which host sent what.
    /// </para>
    /// </remarks>
    private static Dictionary<EntityId, double> LoadPerVolume(
        IReadOnlyList<Observation> observations)
    {
        var load = new Dictionary<EntityId, double>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!value.InstanceIsVantagePoint ||
                value.Rollup != RollupType.Average ||
                value.IsFaultCount ||
                !string.Equals(value.Unit, "number", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            load[observation.Entity] = load.GetValueOrDefault(observation.Entity) + value.Raw;
        }

        return load;
    }

    private static AlertDefinition? Unanimous(
        IEnumerable<Observation> readings,
        SharedVolumePolicy rules,
        Dictionary<EntityId, double> load,
        double busyAbove)
    {
        var ordered = readings.OrderByDescending(o => o.Value.Raw).ToList();

        if (ordered.Count < rules.MinimumMountingHosts)
        {
            return null;
        }

        var floor = rules.Peers.MinimumMilliseconds;
        var elevated = ordered.Count(o => o.Value.Raw >= floor);

        if (elevated < rules.MinimumElevatedShare * ordered.Count)
        {
            return null;
        }

        // The exclusion, computed exactly as PeerOutliers computes it: the
        // worst against the median of everyone else, that median floored at
        // one. Where it would fire, this does not. Sharing the arithmetic and
        // the policy value is what makes "never both" a property of the code
        // rather than a promise in a comment.
        var worst = ordered[0];
        var peers = ordered.Skip(1).Select(o => o.Value.Raw).ToList();
        var peerMedian = Stats.Median(peers);

        if (worst.Value.Raw >= Stats.FlooredMultiple(rules.Peers.Multiple, peerMedian, 1d))
        {
            return null;
        }

        // Slow, or busy. The qualifier rather than a fourth alert: high
        // latency at high load is not a fault to report, it is an alert to
        // withhold, and only something that can subtract can do that.
        var measured = load.TryGetValue(worst.Entity, out var operations);

        if (measured && operations < rules.MinimumOperationsPerSecond)
        {
            return null;
        }

        if (measured && operations > busyAbove)
        {
            return null;
        }

        return new AlertDefinition
        {
            // The volume and the counter, and not the worst host — the same
            // choice PeerOutliers makes, arrived at from the opposite
            // direction. There the worst host may change while the volume
            // stays the sick one; here there is no worst host to speak of,
            // because the finding is that they all agree. Naming one would
            // invent a protagonist the alert is specifically denying.
            Fingerprint = AlertFingerprint.Create(
                worst.Source, Title, Category,
                $"{worst.Entity.Value}/{worst.Value.CounterName}", "shared-volume"),
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description = Describe(worst, ordered, measured ? operations : null),
            Category = Category,
            Source = worst.Source,
            Entity = worst.Entity,
        };
    }

    private static string Describe(
        Observation worst, List<Observation> all, double? operations)
    {
        var median = Stats.Median([.. all.Select(o => o.Value.Raw)]);

        // What the load was, or that it was not known. The counter map's own
        // lesson is that an unmeasured thing must not be printed as a measured
        // one, and the whole point of joining latency to demand is lost if the
        // alert does not say which of the two it had.
        var demand = operations is { } ops
            ? $"It is serving {Readings.Number(ops)} operations a second, which is not enough load to " +
              "explain this"
            : "Its load could not be read this cycle, so how much of this is demand is " +
              "unknown";

        return
            $"'{worst.Value.CounterName}' reads a median of {Readings.Number(median)} ms across all " +
            $"{all.Count} host(s) that mount this volume, from {Readings.Number(all.Min(o => o.Value.Raw))} " +
            $"to {Readings.Number(worst.Value.Raw)} ms, and no single host stands out. {demand}. A volume " +
            "that is slow from every host that mounts it is not one host's path: the hosts " +
            "have different adapters, different cables and different ports, and they agree. " +
            "Check the volume on the array and the fabric in front of it before looking at " +
            "any host.";
    }
}
