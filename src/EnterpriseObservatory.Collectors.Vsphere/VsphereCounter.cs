using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// One counter as vCenter describes it.
/// </summary>
/// <remarks>
/// Counter ids are assigned per vCenter and differ between installations, so
/// they are discovered at runtime rather than hard-coded. The stable identifier
/// is the composed name.
/// </remarks>
public sealed record VsphereCounter
{
    /// <summary>The numeric id this vCenter uses. Not portable between servers.</summary>
    public required int Id { get; init; }

    /// <summary>Counter group, e.g. <c>cpu</c>.</summary>
    public required string Group { get; init; }

    /// <summary>Counter name within the group, e.g. <c>usage</c>.</summary>
    public required string Name { get; init; }

    public required RollupType Rollup { get; init; }

    /// <summary>Unit as vCenter names it, e.g. <c>percent</c>, <c>millisecond</c>.</summary>
    public required string Unit { get; init; }

    /// <summary>The statistics level at which this counter becomes available.</summary>
    public int Level { get; init; }

    /// <summary>The portable identifier, e.g. <c>cpu.usage.average</c>.</summary>
    public string Key => $"{Group}.{Name}.{RollupKey(Rollup)}";

    /// <summary>The token vSphere uses for a rollup type.</summary>
    public static string RollupKey(RollupType rollup) => rollup switch
    {
        RollupType.Average => "average",
        RollupType.Latest => "latest",
        RollupType.Summation => "summation",
        RollupType.Maximum => "maximum",
        RollupType.Minimum => "minimum",
        RollupType.None => "none",
        _ => "unknown",
    };

    /// <summary>
    /// Reads vSphere's rollup token. An unrecognised one becomes
    /// <see cref="RollupType.Unknown"/> rather than a guess, because guessing
    /// the wrong rollup silently changes what the number means.
    /// </summary>
    public static RollupType ParseRollup(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "average" => RollupType.Average,
        "latest" => RollupType.Latest,
        "summation" => RollupType.Summation,
        "maximum" => RollupType.Maximum,
        "minimum" => RollupType.Minimum,
        "none" => RollupType.None,
        _ => RollupType.Unknown,
    };
}

/// <summary>
/// The counters this collector asks for, and why.
/// </summary>
/// <remarks>
/// Kept in one place so the answer to "why are we collecting this" is never
/// more than one file away. Anything not listed here is not collected — see
/// docs/collectors/vsphere-metric-contract.md.
/// </remarks>
public static class VsphereCounters
{
    /// <summary>
    /// Counters for an ESXi host.
    /// </summary>
    /// <remarks>
    /// The three disk latency counters are the product's diagnostic core.
    /// <c>maxTotalLatency</c> is their sum and so cannot say <em>which</em>
    /// layer is slow — device points at the array or fabric, queue at host-side
    /// saturation, kernel at the VMkernel itself. They require statistics
    /// level 2; see the metric contract §6.1.
    /// </remarks>
    public static IReadOnlyList<string> Host { get; } =
    [
        "cpu.usage.average",
        "mem.usage.average",
        // Balloon and swap, not usage, are what actually indicate memory
        // pressure: high mem.usage on its own is normal and healthy.
        "mem.vmmemctl.average",
        "mem.swapused.average",
        "disk.deviceLatency.average",
        "disk.kernelLatency.average",
        "disk.queueLatency.average",
        "disk.maxTotalLatency.latest",
    ];

    /// <summary>Counters for a virtual machine.</summary>
    /// <remarks>
    /// <c>cpu.ready.summation</c> is the only reliable indicator of CPU
    /// overcommitment, and is a total in milliseconds — meaningless until
    /// divided by the interval it was accumulated over.
    /// </remarks>
    public static IReadOnlyList<string> VirtualMachine { get; } =
    [
        "cpu.ready.summation",
        "cpu.costop.summation",
        "mem.vmmemctl.average",
        "virtualDisk.totalLatency.average",
    ];

    /// <summary>Counters for a datastore.</summary>
    /// <remarks>
    /// Datastores have no real-time feed, so these come from the 5-minute
    /// historical interval. See the metric contract §5.
    /// </remarks>
    public static IReadOnlyList<string> Datastore { get; } =
    [
        "datastore.totalLatency.average",
    ];

    public static IReadOnlyList<string> For(VsphereEntityType entityType) => entityType switch
    {
        VsphereEntityType.HostSystem => Host,
        VsphereEntityType.VirtualMachine => VirtualMachine,
        VsphereEntityType.Datastore => Datastore,
        _ => [],
    };
}

/// <summary>Managed object types this collector reads.</summary>
public enum VsphereEntityType
{
    HostSystem,
    VirtualMachine,
    Datastore,
    ClusterComputeResource,
}

/// <summary>Which sampling interval applies to each entity type.</summary>
/// <remarks>
/// <para>
/// Real-time sampling bypasses the vCenter database and so costs it nothing,
/// which makes it the right default — but it does not exist for every type.
/// Datastores and clusters have no real-time feed at all; asking for one
/// returns nothing, silently.
/// </para>
/// <para>
/// The previous product requested <c>intervalId=20</c> for every type, so
/// datastore queries came back empty and nobody noticed. See the metric
/// contract §3.
/// </para>
/// </remarks>
public static class VsphereIntervals
{
    /// <summary>Real-time: 20-second samples read from the host.</summary>
    public const int RealTimeSeconds = 20;

    /// <summary>Historical interval 1: 5-minute samples from the vCenter database.</summary>
    public const int HistoricalLevel1Seconds = 300;

    public static bool SupportsRealTime(VsphereEntityType entityType) =>
        entityType is VsphereEntityType.HostSystem
            or VsphereEntityType.VirtualMachine;

    public static int IntervalSecondsFor(VsphereEntityType entityType) =>
        SupportsRealTime(entityType) ? RealTimeSeconds : HistoricalLevel1Seconds;
}
