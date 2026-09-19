using System.Globalization;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// Writes down what a real vCenter will actually measure, per entity kind.
/// </summary>
/// <remarks>
/// <para>
/// Built after four consecutive wrong fixes to the same problem. Each was a
/// reasonable guess — the counter name, the query's time range, the
/// availability probe, the interval — and each was wrong in a way that
/// produced no error, because an empty performance answer is not a failure.
/// The common cause was not any of those: it was that nobody had the map.
/// </para>
/// <para>
/// Three things this answers that documentation does not, because they are
/// properties of one installation rather than of vSphere:
/// </para>
/// <list type="number">
/// <item>Which <em>object</em> a measurement is collected on. Per-datastore
/// latency lives on the host, with the datastore as the instance — so the
/// datastore itself will never supply it.</item>
/// <item>Which counters have instances, and what an instance identifies: a
/// LUN, a datastore, a virtual disk, a physical NIC. A counter is not a
/// series.</item>
/// <item>What this vCenter's statistics levels leave out. The catalogue lists
/// counters by the level at which they <em>would</em> be collected; only the
/// server can say what it is keeping.</item>
/// </list>
/// </remarks>
internal static class CounterMap
{
    public static async Task WriteAsync(
        VsphereClient client,
        VsphereInventoryPayload payload,
        bool mask,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var catalogue = await client.GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("# vSphere counter map");
        Console.WriteLine();
        Console.WriteLine(Invariant($"Catalogue defines {catalogue.Count} counters on this vCenter."));
        Console.WriteLine();

        var representatives = Representatives(payload);

        foreach (var (entityType, moRef, name) in representatives)
        {
            if (moRef is null)
            {
                Console.WriteLine(Invariant($"## {entityType}"));
                Console.WriteLine();
                Console.WriteLine("  Nothing of this kind in the inventory; not probed.");
                Console.WriteLine();
                continue;
            }

            var interval = VsphereIntervals.IntervalSecondsFor(entityType);
            var realTime = VsphereIntervals.SupportsRealTime(entityType);

            IReadOnlyList<VsphereAvailableMetric> metrics;

            try
            {
                metrics = await client
                    .GetAvailableMetricsAsync(moRef, entityType, now, cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Justified: a map reports, it does not throw.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.WriteLine(Invariant($"## {entityType}"));
                Console.WriteLine();
                Console.WriteLine(Invariant($"  Could not be probed: {ex.Message}"));
                Console.WriteLine();
                continue;
            }

            Console.WriteLine(Invariant($"## {entityType}"));
            Console.WriteLine();
            Console.WriteLine(Invariant($"  probed          {Show(name, mask)}"));
            Console.WriteLine(Invariant(
                $"  interval        {interval}s ({(realTime ? "real-time feed" : "historical, from the vCenter database")})"));
            Console.WriteLine(Invariant($"  series offered  {metrics.Count}"));

            var byCounter = metrics
                .GroupBy(m => m.Counter.Key, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine(Invariant($"  distinct counters {byCounter.Count}"));
            Console.WriteLine();

            foreach (var group in byCounter)
            {
                var counter = group.First().Counter;

                var instances = group
                    .Select(m => m.Instance)
                    .Where(i => i.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var aggregate = group.Any(m => m.Instance.Length == 0);

                // The shape matters more than the count: a counter with
                // instances is several series, and reading only the aggregate
                // turns "one LUN is slow" into "storage is slightly slow".
                var shape = instances.Count switch
                {
                    0 => "aggregate only",
                    _ => Invariant($"{instances.Count} instance(s){(aggregate ? " + aggregate" : ", no aggregate")}"),
                };

                var rollup = VsphereCounter.RollupKey(counter.Rollup);

                Console.WriteLine(Invariant(
                    $"  {counter.Key,-52} {counter.Unit,-20} level={counter.Level} rollup={rollup,-10} stats={counter.StatsType,-9} {shape}"));

                foreach (var instance in instances.Take(3))
                {
                    Console.WriteLine(Invariant($"      instance: {Show(instance, mask)}"));
                }

                if (instances.Count > 3)
                {
                    Console.WriteLine(Invariant($"      ... and {instances.Count - 3} more"));
                }
            }

            Console.WriteLine();
        }

        await WriteCrossEntityAsync(client, payload, representatives, catalogue, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Counters defined for a group but supplied by some other entity kind.
    /// </summary>
    /// <remarks>
    /// The section that would have saved a day. A counter named
    /// <c>datastore.*</c> is not necessarily collected on a datastore, and the
    /// name is the only thing suggesting otherwise.
    /// </remarks>
    private static async Task WriteCrossEntityAsync(
        VsphereClient client,
        VsphereInventoryPayload payload,
        IReadOnlyList<(VsphereEntityType Type, string? MoRef, string Name)> representatives,
        IReadOnlyList<VsphereCounter> catalogue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("## Where each counter group is actually collected");
        Console.WriteLine();
        Console.WriteLine("  A counter's name says what it describes, not which object supplies it.");
        Console.WriteLine();

        var supplied = new Dictionary<VsphereEntityType, HashSet<string>>();

        foreach (var (entityType, moRef, _) in representatives)
        {
            if (moRef is null)
            {
                continue;
            }

            try
            {
                var metrics = await client
                    .GetAvailableMetricsAsync(moRef, entityType, now, cancellationToken)
                    .ConfigureAwait(false);

                supplied[entityType] =
                    [.. metrics.Select(m => m.Counter.Key).Distinct(StringComparer.Ordinal)];
            }
#pragma warning disable CA1031 // Justified: a map reports, it does not throw.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Already reported above; the cross-entity view simply omits it.
            }
        }

        var groups = catalogue
            .Select(c => c.Group)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(g => g, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var where = supplied
                .Where(pair => pair.Value.Any(k => k.StartsWith(group + ".", StringComparison.Ordinal)))
                .Select(pair => Invariant(
                    $"{pair.Key}({pair.Value.Count(k => k.StartsWith(group + ".", StringComparison.Ordinal))})"))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine(Invariant(
                $"  {group,-16} {(where.Count == 0 ? "nowhere — defined but not collected" : string.Join("  ", where))}"));
        }

        Console.WriteLine();
        _ = payload;
    }

    private static List<(VsphereEntityType Type, string? MoRef, string Name)> Representatives(
        VsphereInventoryPayload payload)
    {
        var host = payload.Hosts.Count > 0 ? payload.Hosts[0] : null;
        var datastore = payload.Datastores.Count > 0 ? payload.Datastores[0] : null;
        var cluster = payload.Clusters.Count > 0 ? payload.Clusters[0] : null;

        // A powered-off machine reports nothing, and a map built from one would
        // read as "virtual machines supply no counters" — a wrong answer that
        // looks exactly like a right one.
        var machine = PoweredOn(payload)
            ?? (payload.VirtualMachines.Count > 0 ? payload.VirtualMachines[0] : null);

        return
        [
            (VsphereEntityType.HostSystem, host?.MoRef, host?.Name ?? string.Empty),
            (VsphereEntityType.VirtualMachine, machine?.MoRef, machine?.Name ?? string.Empty),
            (VsphereEntityType.Datastore, datastore?.MoRef, datastore?.Name ?? string.Empty),
            (VsphereEntityType.ClusterComputeResource, cluster?.MoRef, cluster?.Name ?? string.Empty),
        ];
    }

    private static VsphereVirtualMachine? PoweredOn(VsphereInventoryPayload payload) =>
        payload.VirtualMachines.FirstOrDefault(v =>
            string.Equals(v.PowerState, "poweredOn", StringComparison.OrdinalIgnoreCase));

    private static string Show(string value, bool mask) =>
        mask && value.Length > 4
            ? string.Concat(value.AsSpan(0, 2), "***", value.AsSpan(value.Length - 2))
            : value;

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);
}
