using System.Diagnostics;
using System.Globalization;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// Times real <c>QueryPerf</c> round trips (roadmap T1.1, package A).
/// </summary>
/// <remarks>
/// <para>
/// The read-budget measurement (docs/measurements/read-budget-2000vm.md) had
/// to assume a per-call latency because none had been timed. This is the
/// measurement: the same call the observation source makes, the same batch
/// shape (12 virtual machines, the VM counter set, maxSample 3), and one host
/// with the host counter set, each repeated and summarised as median and p95.
/// </para>
/// <para>
/// Read-only, like the rest of the probe: every call is a QueryPerf. Calls are
/// made one at a time, as the collector makes them, with a short pause between
/// so the probe is never the load it is measuring. The first call of each case
/// is reported separately and left out of the statistics — it pays for
/// connection warm-up the collector's steady state does not.
/// </para>
/// <para>
/// The time is the whole client call as the collector sees it: request,
/// server work, reply and parse.
/// </para>
/// </remarks>
internal static class QueryPerfTiming
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);

    public static async Task<int> RunAsync(
        VsphereClient client,
        VsphereInventoryPayload payload,
        IReadOnlyList<VsphereCounter> catalog,
        int calls,
        CancellationToken cancellationToken)
    {
        var byKey = VsphereCounterIndex.ByKey(catalog);
        var now = DateTimeOffset.UtcNow;

        var vms = payload.VirtualMachines
            .Where(v => string.Equals(v.PowerState, "poweredOn", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.MoRef)
            .Take(12)
            .ToList();

        Console.WriteLine($"  calls per case           {calls} timed (+1 warm-up, not counted)");
        Console.WriteLine($"  maxSample                3 (the collector's value)");

        if (vms.Count > 0)
        {
            var counters = await UsableAsync(
                client, vms[0], VsphereEntityType.VirtualMachine, VsphereCounters.VirtualMachine, byKey, now,
                cancellationToken);

            await CaseAsync(
                client, $"(a) {vms.Count} VMs x {counters.Count} counters", vms, VsphereEntityType.VirtualMachine,
                counters, calls, cancellationToken);

            await CaseAsync(
                client, $"(c) 1 VM x {counters.Count} counters", [vms[0]], VsphereEntityType.VirtualMachine,
                counters, calls, cancellationToken);
        }
        else
        {
            Console.WriteLine("  (a) skipped: no powered-on virtual machines");
        }

        var host = payload.Hosts[0].MoRef;
        var hostCounters = await UsableAsync(
            client, host, VsphereEntityType.HostSystem, VsphereCounters.Host, byKey, now, cancellationToken);

        await CaseAsync(
            client, $"(b) 1 host x {hostCounters.Count} counters", [host], VsphereEntityType.HostSystem,
            hostCounters, calls, cancellationToken);

        if (vms.Count > 0)
        {
            var vmCounters = await UsableAsync(
                client, vms[0], VsphereEntityType.VirtualMachine, VsphereCounters.VirtualMachine, byKey, now,
                cancellationToken);

            await WhichEndMaxSampleCutsAsync(client, vms[0], vmCounters, cancellationToken);
            await LongReadAsync(client, vms, vmCounters, cancellationToken);
        }

        return 0;
    }

    /// <summary>
    /// m1: with a window AND maxSample, which end of the window survives.
    /// </summary>
    /// <remarks>
    /// A ten-minute window holds about 30 real-time samples; asking for 3 of
    /// them shows whether vCenter keeps the newest three or the oldest three.
    /// The same window with maxSample 30 is the control that shows what the
    /// window held.
    /// </remarks>
    private static async Task WhichEndMaxSampleCutsAsync(
        VsphereClient client, string vm, List<VsphereCounter> counters, CancellationToken cancellationToken)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-10);

        Console.WriteLine();
        Console.WriteLine("  (m1) window + maxSample: which end is cut");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    window                 {from:HH:mm:ss}Z .. {to:HH:mm:ss}Z (10 min, real-time 20 s)"));

        foreach (var maxSample in new[] { 3, 30 })
        {
            try
            {
                var (body, _) = await client.QueryPerfForMeasurementAsync(
                    [vm], VsphereEntityType.VirtualMachine, counters, maxSample, (from, to), cancellationToken);
                var times = Timestamps(body);

                var span = times.Count == 0
                    ? "none"
                    : FormattableString.Invariant($"first {times[0]:HH:mm:ss}Z, last {times[^1]:HH:mm:ss}Z");
                Console.WriteLine(FormattableString.Invariant(
                    $"    maxSample {maxSample,-3}          {times.Count} samples, {span}"));
            }
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.WriteLine($"    maxSample {maxSample,-3}          FAULT {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(Pause, cancellationToken);
        }
    }

    /// <summary>
    /// m2: how large and how slow a long real-time read is — 180 samples
    /// (an hour at 20 s) for 12 VMs x the VM counter set.
    /// </summary>
    private static async Task LongReadAsync(
        VsphereClient client, List<string> vms, List<VsphereCounter> counters,
        CancellationToken cancellationToken)
    {
        const int Repeats = 5;

        Console.WriteLine();
        Console.WriteLine($"  (m2) {vms.Count} VMs x {counters.Count} counters x maxSample 180, {Repeats} calls");

        var times = new List<double>();
        foreach (var attempt in Enumerable.Range(1, Repeats))
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var (body, samples) = await client.QueryPerfForMeasurementAsync(
                    vms, VsphereEntityType.VirtualMachine, counters, 180, null, cancellationToken);
                watch.Stop();
                times.Add(watch.Elapsed.TotalMilliseconds);

                var perEntity = Timestamps(body);
                var span = perEntity.Count == 0
                    ? string.Empty
                    : FormattableString.Invariant($"({perEntity[0]:HH:mm:ss}Z .. {perEntity[^1]:HH:mm:ss}Z)");
                var kib = System.Text.Encoding.UTF8.GetByteCount(body) / 1024d;
                Console.WriteLine(FormattableString.Invariant(
                    $"    call {attempt}  {watch.Elapsed.TotalMilliseconds,6:0} ms  {kib,8:0.0} KiB  samples/entity {perEntity.Count} {span}  entities {samples.Count}"));
            }
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.WriteLine($"    call {attempt}  FAULT {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(Pause, cancellationToken);
        }

        if (times.Count > 0)
        {
            times.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    median {Percentile(times, 0.5):0} ms  max {times[^1]:0} ms"));
        }
    }

    /// <summary>The sample times of the first entity in a raw QueryPerf reply.</summary>
    private static List<DateTimeOffset> Timestamps(string body)
    {
        var document = System.Xml.Linq.XDocument.Parse(body);
        var first = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");

        return first is null
            ? []
            :
            [
                .. first.Elements()
                    .Where(e => e.Name.LocalName == "sampleInfo")
                    .SelectMany(e => e.Elements().Where(t => t.Name.LocalName == "timestamp"))
                    .Select(t => DateTimeOffset.Parse(t.Value, CultureInfo.InvariantCulture))
                    .Order(),
            ];
    }

    /// <summary>The wanted counters this vCenter defines and is keeping, as the source would query.</summary>
    private static async Task<List<VsphereCounter>> UsableAsync(
        VsphereClient client,
        string moRef,
        VsphereEntityType type,
        IReadOnlyList<string> wanted,
        IReadOnlyDictionary<string, VsphereCounter> byKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var available = new HashSet<string>(
            await client.GetAvailableCounterKeysAsync(moRef, type, now, cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        var usable = wanted
            .Where(k => byKey.ContainsKey(k) && available.Contains(k))
            .Select(k => byKey[k])
            .ToList();

        Console.WriteLine($"  {type} counters        wanted {wanted.Count}, defined+available {usable.Count}");
        return usable;
    }

    private static async Task CaseAsync(
        VsphereClient client,
        string label,
        IReadOnlyList<string> moRefs,
        VsphereEntityType type,
        List<VsphereCounter> counters,
        int calls,
        CancellationToken cancellationToken)
    {
        if (counters.Count == 0)
        {
            Console.WriteLine($"  {label}: skipped, no usable counters");
            return;
        }

        var times = new List<double>(calls);
        var values = 0;
        double warmUp = 0;

        for (var i = 0; i <= calls; i++)
        {
            var watch = Stopwatch.StartNew();
            var samples = await client.QueryPerfAsync(moRefs, type, counters, DateTimeOffset.UtcNow, cancellationToken);
            watch.Stop();

            if (i == 0)
            {
                warmUp = watch.Elapsed.TotalMilliseconds;
                values = samples.Sum(s => s.Values.Count);
            }
            else
            {
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            await Task.Delay(Pause, cancellationToken);
        }

        times.Sort();

        Console.WriteLine();
        Console.WriteLine($"  {label}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    n={times.Count}  median {Percentile(times, 0.50):0} ms  p95 {Percentile(times, 0.95):0} ms  " +
            $"min {times[0]:0}  max {times[^1]:0}  warm-up {warmUp:0} ms  values/reply {values}"));
    }

    /// <summary>Nearest-rank percentile of a sorted list.</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
