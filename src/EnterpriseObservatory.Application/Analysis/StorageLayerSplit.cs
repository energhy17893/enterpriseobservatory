using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which of a device's three latency layers has to dominate before it is named.
/// </summary>
/// <remarks>
/// <para>
/// The counter names are policy rather than constants, and that is a
/// concession rather than a design. The rule needs to know which of three
/// numbers is the array side and which two are the host side, and nothing on
/// <see cref="CounterValue"/> says so — there is a flag for "this is a fault"
/// and a flag for "this instance is an observer", and no flag for "this is the
/// layer of the stack this counter measures". Naming vim25 counters in a
/// literal here would be the first time the application layer referenced a
/// collector's vocabulary. Putting them in configuration keeps the rule itself
/// about roles: an iLO or Redfish collector with an equivalent triple is
/// configured in rather than requiring this file to change.
/// </para>
/// <para>
/// The right answer is a role declared by the collector, beside
/// <see cref="CounterValue.IsFaultCount"/>. That is a domain change and is
/// recorded as follow-up, not smuggled in here.
/// </para>
/// </remarks>
public sealed record StorageLayerPolicy
{
    /// <summary>The counter measuring time spent below the host: array and fabric.</summary>
    public string ArrayCounter { get; init; } = "disk.deviceLatency.average";

    /// <summary>The counter measuring time spent inside the hypervisor's storage stack.</summary>
    public string KernelCounter { get; init; } = "disk.kernelLatency.average";

    /// <summary>The counter measuring time a request waited to be issued at all.</summary>
    public string QueueCounter { get; init; } = "disk.queueLatency.average";

    /// <summary>
    /// What the winning layer must reach on its own before anybody is told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stops the whole rule on a quiet estate. Five milliseconds, and it is
    /// borrowed rather than measured: it is the floor
    /// <see cref="PeerOutlierPolicy.MinimumMilliseconds"/> already argued for
    /// and shipped, on the grounds that something must actually be slow before
    /// a diagnosis is worth printing.
    /// </para>
    /// <para>
    /// Borrowed is the honest word. The counter map's §5b measured the
    /// sub-millisecond truncation on the <c>datastore.*</c> counters and has
    /// never been run against the <c>disk.*</c> triple this rule reads. Those
    /// are reported in whole milliseconds too, so the same truncation is very
    /// likely and the same floor is very likely right — but "likely" is what
    /// this number rests on, and the probe should be pointed at these three
    /// before anybody treats it as measured.
    /// </para>
    /// </remarks>
    public double MinimumMilliseconds { get; init; } = 5d;

    /// <summary>
    /// How many times the host-side layers the array layer must reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four, the multiple <see cref="PeerOutlierPolicy.Multiple"/> already uses
    /// for "this one clearly stands out", reused rather than invented because
    /// no vendor publishes a storage latency number to copy: vROps carries two
    /// numeric thresholds in its whole vCenter solution and neither is about
    /// storage, and Dynatrace's are configurable with no published defaults.
    /// </para>
    /// <para>
    /// Deliberately larger than <see cref="HostMultiple"/>. Blaming the array
    /// sends somebody to the storage team with an accusation; blaming the host
    /// sends somebody to a setting they own. Dynatrace encodes the same
    /// asymmetry by demanding four samples of five before accusing physical
    /// storage and only three of five for queue latency. This product cannot
    /// spend that currency — a rule here sees one sample per series per cycle —
    /// so the asymmetry is spent in the currency it does have: how far ahead
    /// the accused layer must be, and which verdicts are tried first.
    /// </para>
    /// </remarks>
    public double ArrayMultiple { get; init; } = 4d;

    /// <summary>
    /// How many times the array layer a host-side layer must reach.
    /// </summary>
    /// <remarks>
    /// Two: the weakest dominance that is not a coin toss. Stops the case
    /// where the layers are within noise of each other and the rule would pick
    /// a winner by rounding — which is the confident wrong answer this
    /// codebase treats as worse than silence.
    /// </remarks>
    public double HostMultiple { get; init; } = 2d;

    /// <summary>
    /// Whether a host reporting a fault this cycle silences this rule for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bus reset or an aborted command explains latency better than any
    /// share of a total does, and <c>FaultCounters</c> is already reporting it
    /// with no threshold to argue about. Two alerts describing one fault is
    /// the thing this rule was told not to produce, so the weaker one stands
    /// down.
    /// </para>
    /// <para>
    /// Per host and not per device, because the join does not exist: the
    /// surviving path counters name a path by its runtime name
    /// (<c>vmhba0:C0:T0:L1</c>), which carries no LUN identity, while these
    /// three name a device by its NAA. Counter map §5c states the cost
    /// plainly. Host-level deferral therefore silences more than it strictly
    /// must — thirty-one healthy devices go quiet because a thirty-second path
    /// reset — and that is the direction to be wrong in.
    /// </para>
    /// </remarks>
    public bool DeferToFaults { get; init; } = true;

    public static StorageLayerPolicy Default { get; } = new();
}

/// <summary>
/// Says which layer of one device's storage path is the slow one.
/// </summary>
/// <remarks>
/// <para>
/// The rung the product was built for and had never climbed. Three numbers
/// arrive for every <c>naa.*</c> device on every host — the array, the
/// VMkernel, the queue — plus a fourth that is their total and therefore
/// cannot say which. Everything before this read the total or read nothing.
/// </para>
/// <para>
/// One device produces at most one verdict, and that is the point rather than
/// a convenience. "The array is slow and so is the queue" is not a diagnosis,
/// it is two people being sent to two consoles over one fault. The layers are
/// tried host-side first — queue, then kernel, then array — so the array is
/// named only after both explanations the host owns have been tried and
/// failed.
/// </para>
/// <para>
/// Three titles rather than one title with a layer field, following the split
/// Dynatrace ships: its <c>undersizedStorageDetection</c>,
/// <c>slowPhysicalStorageDetection</c> and <c>overloadedStorageDetection</c>
/// are this same split under different words, and what their names do is tell
/// the operator which console to open. A single title reading "storage latency"
/// with the answer buried in a field is a title that names no fix.
/// </para>
/// <para>
/// Devices and not vantage points. A host's LUNs are different things rather
/// than several views of one thing, so nothing here compares them with each
/// other; that comparison is <see cref="PeerOutliers"/>' and
/// <see cref="SharedVolumeLatency"/>' job and they work on the datastore
/// series, where the instance is an observer. The two rungs never meet.
/// </para>
/// </remarks>
public static class StorageLayerSplit
{
    public const string Category = "Storage layer";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "storage-layer-split";

    private const string QueueTitle = "Host queue depth is the bottleneck";
    private const string KernelTitle = "VMkernel storage stack is the bottleneck";
    private const string ArrayTitle = "Array or fabric is the bottleneck";

    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        StorageLayerPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var rules = policy ?? StorageLayerPolicy.Default;
        var alerts = new List<AlertDefinition>();

        // Hosts whose fault counters already explain themselves. Gathered once
        // rather than per device: the list is short and the join to a device
        // does not exist anyway.
        var explained = rules.DeferToFaults
            ? observations
                .Where(o => o.Value.IsFaultCount && o.Value.Raw > 0)
                .Select(o => o.Entity)
                .ToHashSet()
            : [];

        var devices = observations
            .Where(o => IsLayerReading(o.Value, rules))
            .Where(o => !explained.Contains(o.Entity))
            .GroupBy(o => (o.Entity, o.Value.Instance));

        foreach (var device in devices)
        {
            if (Verdict(device, rules) is { } found)
            {
                alerts.Add(found);
            }
        }

        return alerts;
    }

    /// <summary>
    /// A reading this rule is entitled to judge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aggregates are skipped, as they are in <c>FaultCounters</c> and for the
    /// same reason: the triple is stored once per device and once as a
    /// computed maximum across them, and judging both would raise a nameless
    /// second alert for every real one. Worse here than there — a maximum
    /// across devices tells you a host has a slow LUN and refuses to say
    /// which, which is the sentence the per-device series exist to avoid.
    /// </para>
    /// <para>
    /// Vantage points are skipped because they are the other rung. Those
    /// series name an observer, not a device, and comparing an observer's
    /// three layers would be reading a datastore's latency as though it were a
    /// LUN's.
    /// </para>
    /// </remarks>
    private static bool IsLayerReading(CounterValue value, StorageLayerPolicy rules) =>
        !value.IsAggregateInstance &&
        !value.InstanceIsVantagePoint &&
        string.Equals(value.Unit, "millisecond", StringComparison.OrdinalIgnoreCase) &&
        (Is(value, rules.ArrayCounter) || Is(value, rules.KernelCounter) ||
         Is(value, rules.QueueCounter));

    private static bool Is(CounterValue value, string counter) =>
        string.Equals(value.CounterName, counter, StringComparison.OrdinalIgnoreCase);

    private static AlertDefinition? Verdict(
        IEnumerable<Observation> readings, StorageLayerPolicy rules)
    {
        var device = readings.ToList();

        var array = Layer(device, rules.ArrayCounter);
        var kernel = Layer(device, rules.KernelCounter);
        var queue = Layer(device, rules.QueueCounter);

        // All three or nothing. Two of them cannot split three layers, and the
        // ladder's third answer — could not look — is silence here rather than
        // a guess with one term missing. This is also what stops a device that
        // only reports the total: the total is a different counter and is not
        // one of the three.
        if (array is null || kernel is null || queue is null)
        {
            return null;
        }

        var a = array.Value.Raw;
        var k = kernel.Value.Raw;
        var q = queue.Value.Raw;

        // Host-side first, and the order is the argument. Queue saturation is
        // the most specific and most cheaply fixed of the three, the kernel is
        // next, and the array is reached only when neither host-side
        // explanation held. Being tried last and needing the larger multiple
        // are the same decision said twice.
        if (Dominates(q, rules.HostMultiple, a) && Over(q, rules))
        {
            return Alert(queue, QueueTitle, DescribeQueue(a, k, q));
        }

        if (Dominates(k, rules.HostMultiple, a) && Over(k, rules))
        {
            return Alert(kernel, KernelTitle, DescribeKernel(a, k, q));
        }

        if (Dominates(a, rules.ArrayMultiple, Math.Max(k, q)) && Over(a, rules))
        {
            return Alert(array, ArrayTitle, DescribeArray(a, k, q));
        }

        return null;
    }

    private static bool Over(double value, StorageLayerPolicy rules) =>
        value >= rules.MinimumMilliseconds;

    /// <summary>
    /// Whether one layer is far enough ahead of the others to be named.
    /// </summary>
    /// <remarks>
    /// The other side is floored at one before multiplying, exactly as
    /// <see cref="PeerOutliers"/> floors its peer median and for the same
    /// reason: on this estate the quiet layers read zero, and every number is
    /// an infinite multiple of zero. Without the floor the rule would name a
    /// layer at 1 ms because the other two truncated.
    /// </remarks>
    private static bool Dominates(double layer, double multiple, double others) =>
        layer >= multiple * Math.Max(others, 1d);

    private static Observation? Layer(List<Observation> device, string counter)
    {
        foreach (var reading in device)
        {
            if (Is(reading.Value, counter))
            {
                return reading;
            }
        }

        return null;
    }

    private static AlertDefinition Alert(Observation reading, string title, string description) =>
        new()
        {
            // The device, not the layer. The layer is in the title and the
            // title is in the fingerprint, so a fault that moves from the
            // queue to the array raises a new alert rather than quietly
            // relabelling the old one — which is right: that is a different
            // fault, a different fix and a different team, and an alert
            // history claiming the array was blamed all along would be a lie
            // told by an optimisation.
            Fingerprint = AlertFingerprint.Create(
                reading.Source, title, Category,
                $"{reading.Entity.Value}/{reading.Value.Instance}", "storage-layer"),
            Severity = AlertSeverity.Warning,
            Title = title,
            Description = description,
            Category = Category,
            Source = reading.Source,
            Entity = reading.Entity,
        };

    private static string Ms(double v) =>
        v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Layers(double a, double k, double q) =>
        $"device {Ms(a)} ms, kernel {Ms(k)} ms, queue {Ms(q)} ms";

    private static string DescribeQueue(double a, double k, double q) =>
        $"This device waits in the host's own queue longer than it waits for the storage " +
        $"({Layers(a, k, q)}). The request had not been issued yet, so nothing below this " +
        "host can be the cause. Check the queue depth of this device and its adapter, and " +
        "how many outstanding I/Os the virtual machines on this host are asking for. The " +
        "array and the fabric are cleared by the same numbers.";

    private static string DescribeKernel(double a, double k, double q) =>
        $"This device spends its time inside the hypervisor's storage stack rather than " +
        $"below it ({Layers(a, k, q)}). The time is being spent between the virtual machine " +
        "and the adapter: check this host's multipathing policy and path state, its storage " +
        "stack configuration and its version. The array and the fabric are cleared by the " +
        "same numbers.";

    private static string DescribeArray(double a, double k, double q) =>
        $"This device spends its time below the host ({Layers(a, k, q)}). The host issued " +
        "the request promptly and waited: the queue and the kernel are both quiet, so the " +
        "time was spent in the fabric or on the array. Check this LUN on the array and the " +
        "fabric path to it. This host's queue depth and storage stack are cleared by the " +
        "same numbers.";
}
