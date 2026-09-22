using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Every analysis rule, in the order the cycle runs them.
/// </summary>
/// <remarks>
/// <para>
/// One list, and the order in it is the order of the alerts each cycle hands
/// to reconciliation. Adding a rule is adding a line here; nothing in
/// <c>MonitoringCycle</c> changes.
/// </para>
/// <para>
/// Within each scope the order is the order the cycle used when these were
/// still written inline, and is kept on purpose. In the inventory scope it
/// also carries meaning: <see cref="CollectionCoverageRule"/> goes last
/// because everything above it is entitled to be silent, and it is the only
/// thing that can tell an operator whether a silence was a verdict or a gap.
/// </para>
/// </remarks>
public static class AnalysisRules
{
    public static IReadOnlyList<IAnalysisRule> All { get; } =
    [
        new FaultCountersRule(),
        new PeerOutliersRule(),
        new CpuContentionRule(),
        new MemoryPressureRule(),
        new StorageLayerSplitRule(),
        new SharedVolumeLatencyRule(),
        new StorageLatencyBlindSpotRule(),
        new DroppedPacketsRule(),
        new StorageNoisyNeighbourRule(),

        new StoragePathRedundancyRule(),
        new RemoteLoggingRule(),
        new EventAlertsRule(),
        new DatastoreTimeToFullRule(),
        new CollectionCoverageRule(),
    ];

    /// <summary>The rules of one scope, in registration order.</summary>
    public static IReadOnlyList<IAnalysisRule> For(RuleScope scope) =>
        [.. All.Where(rule => rule.Scope == scope)];
}

/// <summary>Adapts <see cref="FaultCounters"/>.</summary>
public sealed class FaultCountersRule : IAnalysisRule
{
    public string RuleId => FaultCounters.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Chosen, not measured: no fault-counter alert in the 15 h window of
    // design note §3.1. Six cycles (3 min), because faults come in bursts and
    // one clean 30 s window is weak evidence.
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 6 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, FaultCounters.Evaluate(context.Observations));
    }
}

/// <summary>Adapts <see cref="PeerOutliers"/>.</summary>
public sealed class PeerOutliersRule : IAnalysisRule
{
    public string RuleId => PeerOutliers.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Measured: 23 cessations over 21 fingerprints, 2 recurrences after 81 s
    // and 111 s, so p95 <= 2 absences (n too small for a p95); proposed 3 kept
    // (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, PeerOutliers.Evaluate(context.Observations, context.Options.PeerOutliers));
    }
}

/// <summary>Adapts <see cref="CpuContention"/>.</summary>
public sealed class CpuContentionRule : IAnalysisRule
{
    public string RuleId => CpuContention.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Chosen: no alert in the measured window (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, CpuContention.Evaluate(
            context.Observations, context.Graph, context.Options.CpuContention));
    }
}

/// <summary>Adapts <see cref="MemoryPressure"/>.</summary>
public sealed class MemoryPressureRule : IAnalysisRule
{
    public string RuleId => MemoryPressure.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Chosen: no alert in the measured window; four because swapping comes in
    // bursts (design note §3).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 4 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, MemoryPressure.Evaluate(
            context.Observations, context.Graph, context.Options.MemoryPressure));
    }
}

/// <summary>Adapts <see cref="StorageLayerSplit"/>.</summary>
public sealed class StorageLayerSplitRule : IAnalysisRule
{
    public string RuleId => StorageLayerSplit.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Measured: 2 cessations, no recurrence -- no data; proposed 3 kept
    // (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, StorageLayerSplit.Evaluate(context.Observations, context.Options.StorageLayers));
    }
}

/// <summary>Adapts <see cref="SharedVolumeLatency"/>.</summary>
/// <remarks>
/// Its peer policy is forced to the one <see cref="PeerOutliers"/> was given,
/// not merely defaulted to the same value. The two rules are mutually
/// exclusive by recomputing each other's test, and two copies that drifted
/// apart would open a band where both fire, or neither does, with nothing to
/// say so.
/// </remarks>
public sealed class SharedVolumeLatencyRule : IAnalysisRule
{
    public string RuleId => SharedVolumeLatency.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Chosen: no alert in the measured window (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;

        return TwoValuedVerdicts.From(context, RuleId, SharedVolumeLatency.Evaluate(
            context.Observations, options.SharedVolumes with { Peers = options.PeerOutliers }));
    }
}

/// <summary>Adapts <see cref="StorageLatencyBlindSpot"/>.</summary>
public sealed class StorageLatencyBlindSpotRule : IAnalysisRule
{
    public string RuleId => StorageLatencyBlindSpot.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Measured: 399 Cleared->Returned pairs over 30 fingerprints, p50 = p95 =
    // 1 absence (30 s), max 26 610 s; chosen 2, above the p95 (design note
    // §3.1). Every one of those flaps is a quiet cycle, which the rule's own
    // conversion will call NotJudgeable -- N was never the fix for them.
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 2 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, StorageLatencyBlindSpot.Evaluate(
            context.Observations, context.Options.StorageLatencyBlindSpot));
    }
}

/// <summary>Adapts <see cref="DroppedPackets"/>.</summary>
public sealed class DroppedPacketsRule : IAnalysisRule
{
    public string RuleId => DroppedPackets.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Measured: 1 cessation, never returned -- no data; proposed 3 kept
    // (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, DroppedPackets.Evaluate(context.Observations, context.Options.DroppedPackets));
    }
}

/// <summary>Adapts <see cref="StorageNoisyNeighbour"/>.</summary>
/// <remarks>
/// Given the graph for VM BackedBy Datastore, and the sample history for the
/// one question a single cycle cannot answer: whether the volume's load rose.
/// The history is read only for a volume that has already passed every other
/// gate. Its peer policy is forced to <see cref="PeerOutliers"/>' for the
/// reason <see cref="SharedVolumeLatencyRule"/> gives.
/// </remarks>
public sealed class StorageNoisyNeighbourRule : IAnalysisRule
{
    public string RuleId => StorageNoisyNeighbour.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    // Chosen: no alert in the measured window (design note §3.1).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;

        return TwoValuedVerdicts.From(context, RuleId, StorageNoisyNeighbour.Evaluate(
            context.Observations,
            context.Graph,
            StorageNoisyNeighbour.TypicalRateFrom(
                context.Series, context.NowUtc, options.StorageNoisyNeighbour),
            options.StorageNoisyNeighbour with { Peers = options.PeerOutliers }));
    }
}

/// <summary>Adapts <see cref="StoragePathRedundancy"/>.</summary>
/// <remarks>
/// <para>
/// On the inventory rhythm, and the scope is the argument. It judges the path
/// table, which is read on the inventory rhythm and changes on it; running it
/// beside the counters would have the faster cycle re-deciding a fact nothing
/// had re-read, and — because reconciliation treats what it is given as the
/// whole truth — each cycle resolving the other's findings.
/// </para>
/// <para>
/// Given the graph as the cycle merged it rather than the store's copy, so
/// that a failed write leaves the rule reasoning about what was actually just
/// read.
/// </para>
/// </remarks>
public sealed class StoragePathRedundancyRule : IAnalysisRule
{
    public string RuleId => StoragePathRedundancy.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    // Chosen: no alert in the measured window; two inventory cycles (10 min)
    // (design note §3).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 2 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, StoragePathRedundancy.Evaluate(
            [.. context.Graph.Active], context.Options.StoragePathRedundancy));
    }
}

/// <summary>Adapts <see cref="RemoteLogging"/>.</summary>
/// <remarks>
/// <para>
/// The first rule that reads configuration rather than measurement. It rides
/// the inventory rhythm because that is when the setting is read: a value that
/// changes when somebody changes it has nothing to say every twenty seconds.
/// </para>
/// <para>
/// The compliance engine now judges the same setting as a catalogue control
/// (<c>esx-9.log-forwarding</c>, <c>esxi-8.logs-remote</c>) with a finding's
/// lifecycle, which is where it belongs. This alert is kept until that screen
/// has been verified on the live estate (roadmap M3.3); retiring it before then
/// would trade a proven signal for an unproven one. When it goes, it goes from
/// this list and its tests together.
/// </para>
/// </remarks>
public sealed class RemoteLoggingRule : IAnalysisRule
{
    public string RuleId => RemoteLogging.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    // Chosen: 9 alerts, none cleared in the measured window. A configuration
    // value read fresh is definitive, so one (design note §3).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 1 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, RemoteLogging.Evaluate([.. context.Graph.Active], context.Options.RemoteLogging));
    }
}

/// <summary>Adapts <see cref="EventAlerts"/>.</summary>
/// <remarks>
/// vCenter's own announcements — HA, storage connectivity, uplinks. On the
/// inventory rhythm because events are collected on it, and in that scope
/// because the rule re-derives every open condition from the stored events
/// each time: reconciliation then holds one alert per fingerprint instead of
/// one per cycle. The worker collects events after the inventory cycle, so
/// what is read here is the previous read's — one inventory interval of
/// latency, until the worker reads events before inventory rather than after
/// it.
/// </remarks>
public sealed class EventAlertsRule : IAnalysisRule
{
    public string RuleId => EventAlerts.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    // Chosen: the clear event is vCenter's own statement, so one (design note §3).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 1 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Options.EventAlerts;

        return TwoValuedVerdicts.From(context, RuleId, EventAlerts.Evaluate(
            EventAlerts.Read(context.Events, context.NowUtc, policy), context.NowUtc, policy));
    }
}

/// <summary>Adapts <see cref="DatastoreTimeToFull"/>.</summary>
/// <remarks>
/// <para>
/// On the inventory rhythm because capacity is read on it: the cycle has just
/// written this read's figures, and the fill date cannot move between reads.
/// The datastores are the ones this cycle carried a capacity reading for, and
/// the capacity is this read's, not a stored copy.
/// </para>
/// <para>
/// One history query per datastore, and only for those. The series reader
/// answers one series per call, so there is nothing to batch into.
/// </para>
/// <para>
/// Each query is guarded on its own: a datastore whose history cannot be read
/// keeps the alerts it had (an <see cref="UnknownReason.RuleFailed"/> verdict),
/// the failure is raised as an alert, and the others are estimated as usual.
/// </para>
/// </remarks>
public sealed class DatastoreTimeToFullRule : IAnalysisRule
{
    public string RuleId => DatastoreTimeToFull.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    // Filling: 3 (chosen). Over-commit: 2 (chosen; its 11 cessations and 4
    // "returns" in the measured window span a product outage, not a flap),
    // given per verdict. Design note §3.1.
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 3 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Options.DatastoreTimeToFull;
        var readings = DatastoreTimeToFull.CurrentReadings(
            context.Snapshots.SelectMany(s => s.Observations), context.Graph);

        return DatastoreTimeToFull.Judge(
            readings,
            d => DatastoreTimeToFull.Read(
                context.Series,
                d.Datastore,
                d.CapacityBytes,
                context.NowUtc,
                policy,
                context.Options.Retention),
            context.HeldBy(RuleId),
            context.NowUtc,
            policy);
    }
}

/// <summary>Adapts <see cref="CollectionCoverage"/>.</summary>
/// <remarks>
/// Not a rule about the estate but a rule about this product: what it managed
/// to read. Registered last; see <see cref="AnalysisRules"/>.
/// </remarks>
public sealed class CollectionCoverageRule : IAnalysisRule
{
    public string RuleId => CollectionCoverage.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    // Chosen: the snapshot is the evidence, so one (design note §3).
    public ResolutionPolicy Resolution { get; } = new() { ConsecutiveAbsent = 1 };

    public IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TwoValuedVerdicts.From(context, RuleId, CollectionCoverage.Evaluate(
            context.Snapshots.ToDictionary(
                s => s.SourceInstanceId,
                s => s.Coverage,
                StringComparer.Ordinal)));
    }
}
