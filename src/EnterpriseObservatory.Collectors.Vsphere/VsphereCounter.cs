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
    /// SCSI transport faults a host reports per storage device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three things that can go wrong on a path, counted against the
    /// device instead — instance is the LUN's <c>naa.*</c>. That difference is
    /// the entire reason these are worth having beside
    /// <see cref="PerStoragePath"/>, which already counts two of them: a path's
    /// runtime name (<c>vmhba0:C0:T0:L1</c>) carries no LUN identity, so a bus
    /// reset is attributable to a host and an HBA but not to a volume. A
    /// <c>naa.*</c> is, and the map's §5c closed that chain end to end —
    /// datastore to LUN to path. A reset counted here lands on "PRODVOL10, the
    /// fourteen VMs on it", which is the sentence an operator is actually
    /// looking for.
    /// </para>
    /// <para>
    /// <c>scsiReservationConflicts</c> has no path-level twin at all and is the
    /// one of the three that earns its place on its own. A reservation conflict
    /// is one host being refused a lock another host holds; it is not slowness
    /// and no latency counter explains it, yet it stalls every host contending
    /// for that volume at once. It is the classic estate-wide storage stall
    /// whose cause is invisible in every number this product currently keeps.
    /// </para>
    /// <para>
    /// Faults only, for the same reason the paths are faults only: SCSI does
    /// not reset a bus because the array is busy, so there is no threshold to
    /// invent, and a summation's zero is a real answer rather than the
    /// truncated kind in §5b.
    /// </para>
    /// <para>
    /// This is the most expensive addition in this batch by a wide margin and
    /// that is not obvious from the counter count. <see cref="KeepPerDevice"/>
    /// already matches <c>disk.*</c>, so each of these is kept once per LUN:
    /// on the measured estate, 32 devices plus an aggregate, ten hosts, three
    /// counters — about 990 series and 2.2 GB of the projected steady state.
    /// Judged worth it because it buys the volume attribution the path
    /// counters structurally cannot give, and because the estate already pays
    /// roughly 2 500 series for the path faults. If it ever has to be cut, the
    /// two that duplicate <see cref="PerStoragePath"/> are what to cut;
    /// <c>scsiReservationConflicts</c> is the one with no substitute.
    /// </para>
    /// <para>
    /// Declared before <see cref="Host"/> for the same reason
    /// <see cref="PerDatastore"/> is: static initialisers run in source order,
    /// and a list spliced into <see cref="Host"/> before it has been built
    /// splices in nothing, silently.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PerDeviceScsiFaults { get; } =
    [
        "disk.busResets.summation",
        "disk.commandsAborted.summation",
        "disk.scsiReservationConflicts.summation",
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

        // The host's own verdict on its memory, and the only counter in this
        // file whose meaning comes with the platform: a published enum —
        // high, soft, hard, low — rather than a number somebody has to pick a
        // line through. That matters more than it sounds. Broadcom publishes
        // almost no thresholds anywhere, so every other memory rule this
        // product will ever write is an invention; this one is not. "hard" or
        // "low" means the host is reclaiming in earnest and every VM on it is
        // degrading at the same moment.
        //
        // Not marked as a fault, and it is worth saying why rather than
        // leaving it to be rediscovered: zero here means *healthy*, which is
        // the exact opposite of what IsFaultCount promises, and a state of
        // "soft" is normal operation rather than something that happened.
        // Expressing it to a rule needs a breakpoint, not a flag — see the
        // counter map §5d.
        "mem.state.latest",

        // Memory pressure as a rate rather than a level, which is the whole
        // point. mem.swapused above is a level: it stays high for weeks after
        // a single event, so an alert on it fires long after there is anything
        // to do and is one nobody ends up believing. A rate tells "swapped
        // once in March" apart from "swapping now". Kept beside the level
        // rather than instead of it — the level still answers how much is
        // parked out of memory, which the rate cannot.
        //
        // Compression is the step before swap and is what makes this a ladder:
        // a host compressing is under pressure and still coping, a host
        // swapping in has already lost. Ballooning and mem.usage are
        // deliberately not part of this group — a balloon is the mechanism
        // working, and high mem.usage on a consolidated host is normal.
        "mem.swapinRate.average",
        "mem.swapoutRate.average",
        "mem.compressionRate.average",
        "mem.decompressionRate.average",

        // The first network data this product collects at all. Until now a
        // dropped-packet storm presented as "the application is slow" with
        // every storage and CPU number clean — the blind spot that sends an
        // operator down the storage ladder for a problem that was never on it.
        //
        // Summations, so a zero is a real answer, exactly as for the path
        // faults. But NOT declared as faults: see IsFaultCounter. And not kept
        // per NIC: see KeepPerDevice.
        "net.droppedRx.summation",
        "net.droppedTx.summation",

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

        // And the same faults counted against the LUN rather than the route
        // to it, which is what makes them attributable to a datastore. See
        // PerDeviceScsiFaults.
        .. PerDeviceScsiFaults,

        // The denominator for the dropped-packet counters above. A drop count
        // alone cannot tell a busy uplink losing the odd frame from a broken
        // one; a share of traffic can. Summations like the drop counters, same
        // interval, same aggregate — and not kept per NIC, for the same reason.
        // Appended rather than placed beside the drop counters so that the
        // order of everything already collected is unchanged.
        "net.packetsRx.summation",
        "net.packetsTx.summation",
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
    /// Whether any non-zero reading of this counter is a fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of these are SCSI transport errors, reported per storage path or
    /// per storage device. A bus reset, an aborted command or a reservation
    /// conflict is not a level to threshold — the array being busy does not
    /// cause any of them — so one is enough to be worth saying, and a count of
    /// zero genuinely means it did not happen. That is the opposite of the
    /// latency counters in the map's §5b, whose zeros are a truncation and say
    /// nothing at all.
    /// </para>
    /// <para>
    /// A list rather than a prefix, deliberately. <c>disk.*</c> and
    /// <c>storagePath.*</c> each hold far more latencies and throughputs than
    /// faults, and a prefix that caught them would turn every busy LUN into a
    /// reported error — the failure mode the classifier's own test names. What
    /// is enumerated here is enumerated because somebody decided it.
    /// </para>
    /// <para>
    /// Dropped packets are consciously absent. <c>net.dropped{Rx,Tx}</c> are
    /// summations and their zeros are real, so they look like they belong —
    /// but the rule this flag feeds has no threshold and no memory, and a
    /// single dropped frame on a busy uplink would open a warning. A bus reset
    /// has no benign cause; a dropped packet does. They need a rate and a line
    /// through it, which is a different rule, so calling them faults would
    /// produce a standing alert on a healthy estate and bury the resets it
    /// exists to surface. See the counter map §5e.
    /// </para>
    /// <para>
    /// Declared here because only this collector knows what a vim25 counter
    /// means; it travels to the rule on <c>CounterValue.IsFaultCount</c> so
    /// that the rule never has to name a vSphere counter.
    /// </para>
    /// </remarks>
    public static bool IsFaultCounter(string? counterKey) =>
        counterKey is not null &&
        (PerStoragePath.Contains(counterKey, StringComparer.OrdinalIgnoreCase) ||
         PerDeviceScsiFaults.Contains(counterKey, StringComparer.OrdinalIgnoreCase));

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
    /// <para>
    /// This is also what makes the <c>disk.*</c> faults the expensive half of
    /// this file: the prefix that keeps a slow LUN visible keeps a quiet one
    /// too, so each fault counter costs a series per device on every host.
    /// </para>
    /// <para>
    /// <c>net.*</c> is left out on the same reasoning that leaves out CPU. A
    /// host has around sixteen NICs reporting and vCenter
    /// supplies an aggregate; the question the dropped-packet counters exist to
    /// answer is "is this host dropping frames at all", which the aggregate
    /// answers and which nothing currently answers. Which uplink is a second
    /// question, and on this estate buying it costs roughly 320 series for two
    /// counters that read zero on a healthy fabric. It can be bought later,
    /// by adding <c>net.</c> here, once something has been seen to drop.
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

        // The counter that turns a confident wrong answer into a different and
        // more useful one. cpu.ready above cannot say WHY a machine was ready
        // to run and did not: a VM held under its own configured CPU limit
        // accrues the same waiting as one starved by a busy host, so the
        // contention rule called the limited machine a victim and sent an
        // operator to look at a host that was fine. Dynatrace separates the two
        // with guestCpuLimitReached; this is the cheap half of it.
        //
        // A summation in milliseconds like the two above it, and the same
        // interval, so it needs no new machinery in the rule — it converts to a
        // percentage of the window exactly as ready and co-stop do. What makes
        // it decisive is that it is zero by construction on a machine with no
        // limit set: vSphere only accumulates here when a configured ceiling
        // actually held the vCPU back. So a non-zero reading is not a level to
        // interpret, it is the presence of a limit that is biting right now.
        //
        // Not a fault, and worth stating rather than leaving to be rediscovered
        // (see IsFaultCounter): a CPU limit is a configuration somebody chose,
        // and on a test or licence-bound VM it is chosen on purpose and bites
        // every cycle. Non-zero is an explanation, not an error. And not kept
        // per device — see KeepPerDevice; vSphere reports this per vCPU too,
        // and a per-vCPU series is not a small virtual machine.
        //
        // Safe here as a plain literal: unlike PerDatastore and
        // PerStoragePath, nothing splices this list into another, so the
        // source-order hazard those two carry does not apply. Adding it as a
        // spliced list ABOVE Host would be the thing to get wrong.
        "cpu.maxlimited.summation",

        "mem.vmmemctl.average",

        // The counter that stops the ballooning alert being wrong, and the
        // reason vmmemctl above is not enough on its own. Datadog's guidance
        // is to alert on any positive vmmemctl; Dynatrace ignores ballooning
        // entirely. Both are reacting to the same fact from opposite ends: a
        // balloon reclaiming pages from a VM whose active memory is far below
        // what it was granted is the mechanism working exactly as designed,
        // and on any healthy consolidated estate that is most VMs most of the
        // time. Alert on vmmemctl alone and the product ships a standing
        // false alarm; ignore ballooning and it cannot see a genuinely starved
        // guest at all.
        //
        // mem.active is the gate between the two: a balloon is worth saying
        // something about when the guest is touching the memory being taken
        // away from it. Active memory is an estimate from sampled pages and
        // understates a guest with a large warm cache, which is a real limit —
        // but it is the difference between "reclaiming" and "starving", and
        // the product has no other way to tell them apart.
        "mem.active.average",

        // The VM end of the network blind spot. On the host these say the
        // uplink is dropping; here they say which guest is losing frames,
        // which is what turns "the application is slow" into a ticket
        // somebody can act on. Not faults — see IsFaultCounter.
        "net.droppedRx.summation",
        "net.droppedTx.summation",

        // Read and write separately, because vSphere has no combined virtual
        // disk latency counter and because the two mean different things: read
        // latency points at the array or the cache, write latency at the write
        // path — mirroring, replication, a full cache. Both are level 1, so
        // they arrive without anyone changing a statistics level.
        "virtualDisk.totalReadLatency.average",
        "virtualDisk.totalWriteLatency.average",

        // The guest's traffic, as the denominator for its drop counters. See
        // the same pair on Host.
        "net.packetsRx.summation",
        "net.packetsTx.summation",

        // The same four memory rates the host collects, asked of each guest,
        // and for a reason the host list cannot answer: ESXi only ever swaps
        // and compresses virtual machines' memory, so a host's rate is the
        // sum of its guests'. One machine held under its own memory limit
        // swaps on a host with memory to spare and the host counter reads
        // exactly as it would for a host that is short. Without these the
        // memory pressure rule could only blame the host, and would be wrong
        // in precisely the case the CPU rule already learned to separate.
        //
        // Not verified against a live vCenter (see the counter map §5g); the
        // rule treats a missing reading as "not measured", never as zero.
        // About 145 VMs x 4 = 580 series, roughly 1.3 GB at steady state.
        "mem.swapinRate.average",
        "mem.swapoutRate.average",
        "mem.compressionRate.average",
        "mem.decompressionRate.average",
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
