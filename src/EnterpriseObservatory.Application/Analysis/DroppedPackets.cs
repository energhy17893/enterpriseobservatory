using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How much of a machine's traffic may be dropped before it is worth saying.
/// </summary>
/// <remarks>
/// <para>
/// The counters are named here rather than compiled in, for the reason
/// <see cref="CpuContentionPolicy"/> gives: the application layer may not know
/// what a vim25 counter is called, and holding the names as policy keeps the
/// vendor's vocabulary out of the rule.
/// </para>
/// </remarks>
public sealed record DroppedPacketsPolicy
{
    /// <summary>
    /// The share of one direction's packets, as a percentage, that must be
    /// dropped before anything is said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One percent, and this one is cited rather than invented: it is the
    /// threshold Duncan Epping's esxtop reference (yellow-bricks.com/esxtop)
    /// gives for both <c>%DRPTX</c> and <c>%DRPRX</c> — "hardware overworked,
    /// possible cause very high network utilization". That is a blog by a
    /// VMware engineer and not vendor documentation, and it says of itself
    /// that its figures are a troubleshooting guideline; Broadcom publishes no
    /// dropped-packet threshold at all. It is used because it is the figure
    /// the field actually works to, and because esxtop's <c>%DRP</c> is the
    /// same quantity this rule computes — dropped as a share of the direction's
    /// packets — so the number transfers without reinterpretation.
    /// </para>
    /// <para>
    /// Not "any drop", which is what <see cref="FaultCounters"/> would have
    /// said and why these counters are deliberately not faults: a busy uplink
    /// drops the odd frame legitimately, and a rule with no line through it
    /// would stand open on a healthy estate. See the counter map §5e.
    /// </para>
    /// </remarks>
    public double DropPercent { get; init; } = 1d;

    /// <summary>
    /// The traffic, in packets per second in one direction, below which no
    /// verdict is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This product's own choice, with no source to cite: nobody publishes a
    /// traffic floor for a drop ratio. It is the gate that keeps an idle NIC
    /// quiet. A machine exchanging three packets in a window and losing one of
    /// them is at 33% and means nothing; a ratio over a handful of packets is
    /// arithmetic on noise, and an alert about it teaches an operator to
    /// ignore the next one — the same job
    /// <see cref="CpuContentionPolicy.ReadyPercent"/> does for a ratio of
    /// ready times.
    /// </para>
    /// <para>
    /// One hundred per second — two thousand in a twenty-second real-time
    /// window — because an idle guest's background chatter (ARP, heartbeats,
    /// name resolution, monitoring agents) sits well under ten, while any
    /// workload someone would file a ticket about clears a hundred easily. At
    /// the default <see cref="DropPercent"/> it also means nothing fires on
    /// fewer than twenty dropped packets in a window, which cannot be one
    /// burst's worth of bad luck. Expressed per second rather than per sample
    /// so that it keeps its meaning if the collection interval changes.
    /// </para>
    /// </remarks>
    public double MinimumPacketsPerSecond { get; init; } = 100d;

    /// <summary>The counter carrying received packets that were dropped.</summary>
    public string DroppedRxCounter { get; init; } = "net.droppedRx.summation";

    /// <summary>The counter carrying transmitted packets that were dropped.</summary>
    public string DroppedTxCounter { get; init; } = "net.droppedTx.summation";

    /// <summary>The counter carrying packets received.</summary>
    /// <remarks>
    /// The denominator, and the one whose absence changes the rule from
    /// "judged" to "not looked at". Without it there is no traffic to form a
    /// ratio against and the rule says nothing about that entity rather than
    /// falling back to "any drop is bad".
    /// </remarks>
    public string PacketsRxCounter { get; init; } = "net.packetsRx.summation";

    /// <summary>The counter carrying packets transmitted.</summary>
    public string PacketsTxCounter { get; init; } = "net.packetsTx.summation";

    public static DroppedPacketsPolicy Default { get; } = new();
}

/// <summary>
/// Says which machine or host is dropping a meaningful share of its traffic.
/// </summary>
/// <remarks>
/// <para>
/// A rate relative to traffic, not a count, and that is the whole design. The
/// drop counters were collected and deliberately marked as a level rather than
/// a fault (counter map §5e, architecture step 1e): a bus reset has no benign
/// cause, a dropped frame on a busy uplink does. So the question here is not
/// "did anything drop" but "what share of what was sent or received was lost,
/// and was there enough of it for the share to mean anything".
/// </para>
/// <para>
/// Judged per entity and per direction. The entity is whatever the sample is
/// attributed to — a virtual machine, for which the aggregate is across its
/// vNICs, or a host, for which it is across its physical uplinks — because
/// that is the grain the data has: <c>KeepPerDevice</c> deliberately does not
/// keep <c>net.*</c> per NIC, so "which vNIC" or "which uplink" is a question
/// this rule cannot answer and does not pretend to. Receive and transmit are
/// separate verdicts because they point at different work: receive drops on a
/// guest usually mean it is not draining its ring buffer (guest CPU, or a
/// small vmxnet3 ring), transmit drops usually mean the uplink or a traffic
/// shaping policy is the bottleneck.
/// </para>
/// <para>
/// The ratio is dropped over (packets plus dropped). Whether vSphere counts a
/// dropped packet inside <c>packetsRx</c> as well is not documented; adding
/// the dropped count to the denominator is the conservative reading — it can
/// only understate the ratio, never inflate it — and it keeps a direction that
/// dropped everything it saw from dividing by zero.
/// </para>
/// <para>
/// What it does not say. It says nothing about an entity whose packet counter
/// did not arrive, however many drops it reported: not looking and looking
/// and finding nothing are different answers. And it has no memory: one
/// sample per cycle, so a burst that clears the gates once opens and closes an
/// alert, and a recurring one is left to flap detection.
/// </para>
/// </remarks>
public static class DroppedPackets
{
    /// <summary>Shown beside a dropped-packet alert.</summary>
    public const string Category = "Network";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "dropped-packets";

    /// <summary>
    /// Who these alerts are attributed to: the platform, because the ratio is
    /// something the product concluded from two counters rather than
    /// something a vendor reported. Part of the fingerprint.
    /// </summary>
    private const string Platform = "platform";

    private const string ReceiveTitle = "Dropping received packets";
    private const string TransmitTitle = "Dropping transmitted packets";

    /// <summary>Every dropped-packet verdict this batch of observations supports.</summary>
    /// <param name="observations">One cycle's samples.</param>
    /// <param name="policy">Defaults, and why they are what they are.</param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        DroppedPacketsPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var rules = policy ?? DroppedPacketsPolicy.Default;

        return
        [
            .. Direction(observations, rules, rules.DroppedRxCounter, rules.PacketsRxCounter, ReceiveTitle, "received"),
            .. Direction(observations, rules, rules.DroppedTxCounter, rules.PacketsTxCounter, TransmitTitle, "transmitted"),
        ];
    }

    private static IEnumerable<AlertDefinition> Direction(
        IReadOnlyList<Observation> observations,
        DroppedPacketsPolicy rules,
        string droppedCounter,
        string packetsCounter,
        string title,
        string verb)
    {
        var dropped = CountsOf(observations, droppedCounter);
        var packets = CountsOf(observations, packetsCounter);

        foreach (var (entity, drop) in dropped.OrderBy(d => d.Key.Value, StringComparer.Ordinal))
        {
            // No denominator, no verdict. See the type's remarks.
            if (!packets.TryGetValue(entity, out var passed))
            {
                continue;
            }

            var total = passed.Count + drop.Count;
            var seconds = Math.Max(passed.Seconds, drop.Seconds);
            var perSecond = total / seconds;

            if (perSecond < rules.MinimumPacketsPerSecond)
            {
                continue;
            }

            // Multiplied before dividing, so that a share sitting exactly on
            // the line lands on it rather than a rounding error either side.
            var percent = drop.Count * 100d / total;

            if (percent < rules.DropPercent)
            {
                continue;
            }

            yield return new AlertDefinition
            {
                // The entity and the counter, not the ratio: the share moves
                // every cycle while the problem stays the same one, and a
                // fingerprint carrying it would open a fresh alert each time.
                Fingerprint = AlertFingerprint.Create(
                    Platform, title, Category, $"{entity.Value}/{droppedCounter}", "net-dropped-packets"),
                Severity = AlertSeverity.Warning,
                Title = title,
                Description =
                    $"{Readings.Number(drop.Count)} of {Readings.Number(total)} {verb} packets ({Readings.Number(percent)}%) " +
                    $"were dropped in the last {Readings.Number(seconds)} seconds, at {Readings.Number(perSecond)} " +
                    $"packets per second. The line is {Readings.Number(rules.DropPercent)}% of real traffic " +
                    $"(at least {Readings.Number(rules.MinimumPacketsPerSecond)} packets per second), not any " +
                    "drop at all: a busy link drops the odd frame legitimately. This is the aggregate " +
                    "across every NIC of this entity, so it does not say which one. On a virtual " +
                    "machine, receive drops usually mean the guest is not draining its ring buffer " +
                    "(guest CPU, or a small vmxnet3 ring); transmit drops usually mean the uplink or " +
                    "a traffic shaping policy is the bottleneck. On a host, check the physical " +
                    "uplinks and the switch ports they land on.",
                Category = Category,
                Source = Platform,
                Entity = entity,
                IsDerived = true,
            };
        }
    }

    /// <summary>
    /// A summed packet counter per entity, with the window it covers.
    /// </summary>
    /// <remarks>
    /// Aggregates only: a per-NIC series is not a small machine, and taking
    /// both would count the same traffic twice. A sample whose rollup is not a
    /// summation, or whose interval is not positive, is skipped rather than
    /// thrown over — a count without a window cannot be turned into a rate,
    /// and throwing would cost every other verdict in the cycle. If a cycle
    /// carries two samples for one entity, the larger is kept so the answer
    /// does not depend on collection order.
    /// </remarks>
    private static Dictionary<EntityId, (double Count, double Seconds)> CountsOf(
        IReadOnlyList<Observation> observations,
        string counter)
    {
        var values = new Dictionary<EntityId, (double Count, double Seconds)>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!Readings.IsCounter(value.CounterName, counter) ||
                !value.IsAggregateInstance ||
                value.Rollup != RollupType.Summation ||
                value.Interval <= TimeSpan.Zero ||
                value.Raw < 0)
            {
                continue;
            }

            if (!values.TryGetValue(observation.Entity, out var existing) ||
                value.Raw > existing.Count)
            {
                values[observation.Entity] = (value.Raw, value.Interval.TotalSeconds);
            }
        }

        return values;
    }
}
