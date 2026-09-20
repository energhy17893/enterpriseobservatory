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

    /// <summary>
    /// How the value relates to time: <c>absolute</c>, <c>delta</c> or <c>rate</c>.
    /// </summary>
    /// <remarks>
    /// Captured because it is what distinguishes two counters that otherwise
    /// share a <see cref="Key"/> — a live vCenter defines
    /// <c>disk.scsiReservationCnflctsPct.average</c> more than once.
    /// </remarks>
    public string StatsType { get; init; } = string.Empty;

    /// <summary>
    /// The portable identifier, e.g. <c>cpu.usage.average</c>.
    /// </summary>
    /// <remarks>
    /// How humans and the metric contract name a counter — but <em>not</em>
    /// unique: see <see cref="VsphereCounterIndex"/>.
    /// </remarks>
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
    /// Counters a host reports per datastore rather than about itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance names the volume, so these are not measurements about the
    /// host at all — they are measurements about each datastore, taken from
    /// this host's point of view. Both halves of that matter. Attributing them
    /// to the host would file storage latency under the wrong object; throwing
    /// away which host saw them would discard the most useful storage question
    /// there is, which is whether a volume is slow from every host or only
    /// from one. The first points at the array or the fabric, the second at
    /// that host's HBA, cable or path.
    /// </para>
    /// <para>
    /// Read and write latency are the two the product already wanted. VM
    /// observed latency is what a guest actually experiences, queueing
    /// included: when it greatly exceeds total latency the queue is the
    /// problem and the array is not. The two IOPS counters are there so that
    /// "slow" can be told apart from "busy" — high latency at high IOPS is a
    /// volume doing its job, and at low IOPS it is a volume in trouble.
    /// </para>
    /// <para>
    /// Declared before <see cref="Host"/> because <see cref="Host"/> includes
    /// it. Static initialisers run in source order, so the other way round
    /// gives every host an empty counter list and no indication why.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PerDatastore { get; } =
    [
        "datastore.totalReadLatency.average",
        "datastore.totalWriteLatency.average",
        "datastore.datastoreVMObservedLatency.latest",
        "datastore.numberReadAveraged.average",
        "datastore.numberWriteAveraged.average",

        // Not a performance number. It is how the product knows whether the
        // two above it mean anything: VM observed latency only reports while
        // Storage I/O Control is active, and on the estate this was measured
        // against SIOC was inactive on all 302 host-volume pairs, so that
        // counter read exactly zero everywhere while the volumes served
        // thousands of IOPS. Collected so the evidence for "we cannot see
        // below a millisecond here" lives in the database rather than in
        // somebody's memory of an afternoon.
        "datastore.siocActiveTimePercentage.average",
    ];

    /// <summary>
    /// Faults a host reports per storage path — the bottom of the ladder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A storage path is one route to one LUN: an HBA, a target and a unit,
    /// spelled <c>vmhba2:C0:T3:L7</c>. It is the finest grain vSphere offers
    /// and the only one that can distinguish a bad cable, a failing SFP or a
    /// zoning mistake from a slow array — every coarser number averages the
    /// broken path in with the working ones and reports something mild.
    /// </para>
    /// <para>
    /// Faults only, and that is a decision rather than an oversight. Both are
    /// level 2, so they arrive on any estate already meeting the product's
    /// stated prerequisite, and neither is a threshold to tune: SCSI does not
    /// reset a bus because the array is busy, and a command is not aborted
    /// because a volume is popular. One of either is a fault, and a count of
    /// zero is a real answer rather than a truncated one — unlike the latency
    /// counters, whose zeros say nothing at all below a millisecond.
    /// </para>
    /// <para>
    /// The level-3 latency pair was collected and then deliberately dropped.
    /// It was 2 536 of this estate's 8 620 series — about 8 GB of the
    /// projected steady state — to answer "which path is slow" on an estate
    /// where, per the counter map §5b, path latency cannot be resolved below
    /// a millisecond anyway. The faults answer "which path is broken", which
    /// is the question the bottom of the ladder exists for, at a fifth of the
    /// cost.
    /// </para>
    /// <para>
    /// It costs something real and the map records it: the fault counters name
    /// a path by its runtime name, which carries no LUN identifier, while the
    /// latency counters named it by initiator and target WWPN plus the LUN's
    /// NAA. So a reset is now attributable to a host and an HBA but not
    /// automatically to a datastore. That is the right granularity for a cable
    /// or an SFP, and recovering the rest means reading the host's multipath
    /// map rather than paying for it in series.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PerStoragePath { get; } =
    [
        "storagePath.busResets.summation",
        "storagePath.commandsAborted.summation",
    ];

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

        // Datastore counters, asked of the host, because that is where vSphere
        // keeps them. A live vCenter offers 24 of these on HostSystem and none
        // at all on Datastore — so the names below were never wrong, the entity
        // was. See PerDatastore.
        .. PerDatastore,

        // And the paths beneath them. See PerStoragePath.
        .. PerStoragePath,
    ];

    /// <summary>
    /// Whether a counter's instance names another entity rather than a device.
    /// </summary>
    /// <remarks>
    /// The distinction decides whether the per-instance series may be collapsed.
    /// For a host's HBAs the worst of them is a fair summary of the host; for
    /// a host's datastores it is not a summary of anything, because each
    /// instance belongs to a different object that has its own page and its own
    /// alerts. Collapsing those would produce one number about thirty volumes
    /// and no way to say which one is slow.
    /// </remarks>
    public static bool InstanceNamesAnEntity(string? counterKey) =>
        counterKey?.StartsWith("datastore.", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether each device's own series is worth keeping beside the summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storage only, and deliberately. A live host reports the three disk
    /// latency counters across 32 devices and supplies no aggregate of its
    /// own, so the product computes one — the worst device, which is the right
    /// summary for "is this host's storage slow" and structurally incapable of
    /// answering "which LUN". The lower half of the diagnostic ladder is that
    /// second question, and it cannot be asked of a number that has already
    /// been collapsed.
    /// </para>
    /// <para>
    /// Not applied to CPU, which reports per core: the same host offers 96
    /// instances of <c>cpu.usage.average</c> beside a perfectly good aggregate,
    /// and nobody diagnoses anything by reading core 57. The summary is the
    /// answer there, and keeping the detail would be a tenfold cost for it.
    /// </para>
    /// </remarks>
    public static bool KeepPerDevice(string? counterKey) =>
        counterKey is not null &&
        (counterKey.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) ||
         counterKey.StartsWith("storagePath.", StringComparison.OrdinalIgnoreCase));

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

        // Read and write separately, because vSphere has no combined virtual
        // disk latency counter and because the two mean different things: read
        // latency points at the array or the cache, write latency at the write
        // path — mirroring, replication, a full cache. Both are level 1, so
        // they arrive without anyone changing a statistics level.
        "virtualDisk.totalReadLatency.average",
        "virtualDisk.totalWriteLatency.average",
    ];

    /// <summary>
    /// Nothing. A datastore is measured through its hosts — see <see cref="PerDatastore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty on evidence rather than on principle. This list asked for
    /// <c>datastore.totalLatency.average</c>, which does not exist; then for
    /// the correct <c>totalReadLatency</c> and <c>totalWriteLatency</c>, at the
    /// correct 300-second interval, with the correct time range — and every
    /// datastore still came back unmeasured. Four fixes, all of them real
    /// defects, none of them the cause.
    /// </para>
    /// <para>
    /// The cause was that a counter's entity in vim25 is not always the object
    /// it describes. Asked directly, a live Datastore supplies no performance
    /// data at all; the same vCenter offers 24 <c>datastore.*</c> counters on
    /// every HostSystem, with the volume named in the instance. So asking the
    /// datastore could never have worked, however correct the request.
    /// </para>
    /// <para>
    /// Kept as an empty list rather than deleted, because the emptiness is the
    /// finding. If a vCenter is ever measured that does answer here, this is
    /// where the evidence goes.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Datastore { get; } = [];

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

    /// <summary>
    /// How far back a historical query asks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Historical queries need an explicit time range and real-time ones do
    /// not, which is the whole reason this exists. vim25 honours
    /// <c>maxSample</c> only for real-time series; for a historical interval it
    /// is ignored, and a query with no range comes back empty. Not an error, not
    /// a fault — an empty answer, indistinguishable from an idle datastore.
    /// </para>
    /// <para>
    /// Wide enough to survive the rollup lag. vCenter finishes a five-minute
    /// bucket some minutes after the fact, so a window of one or two intervals
    /// would intermittently return nothing and the series would have holes
    /// nobody could explain.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan HistoricalWindow = TimeSpan.FromMinutes(20);
}
