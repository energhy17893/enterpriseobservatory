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
        new MultipathSinglePointOfFailureRule(),
        new DrsRuleViolationsRule(),
        new RemoteLoggingRule(),
        new ClusterHighAvailabilityRule(),
        new EventAlertsRule(),
        new DatastoreTimeToFullRule(),
        new ClusterNPlusOneRule(),
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return FaultCounters.Evaluate(context.Observations);
    }
}

/// <summary>Adapts <see cref="PeerOutliers"/>.</summary>
public sealed class PeerOutliersRule : IAnalysisRule
{
    public string RuleId => PeerOutliers.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return PeerOutliers.Evaluate(context.Observations, context.Options.PeerOutliers);
    }
}

/// <summary>Adapts <see cref="CpuContention"/>.</summary>
public sealed class CpuContentionRule : IAnalysisRule
{
    public string RuleId => CpuContention.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return CpuContention.Evaluate(
            context.Observations, context.Graph, context.Options.CpuContention);
    }
}

/// <summary>Adapts <see cref="MemoryPressure"/>.</summary>
public sealed class MemoryPressureRule : IAnalysisRule
{
    public string RuleId => MemoryPressure.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return MemoryPressure.Evaluate(
            context.Observations, context.Graph, context.Options.MemoryPressure);
    }
}

/// <summary>Adapts <see cref="StorageLayerSplit"/>.</summary>
public sealed class StorageLayerSplitRule : IAnalysisRule
{
    public string RuleId => StorageLayerSplit.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return StorageLayerSplit.Evaluate(context.Observations, context.Options.StorageLayers);
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;

        return SharedVolumeLatency.Evaluate(
            context.Observations, options.SharedVolumes with { Peers = options.PeerOutliers });
    }
}

/// <summary>Adapts <see cref="StorageLatencyBlindSpot"/>.</summary>
public sealed class StorageLatencyBlindSpotRule : IAnalysisRule
{
    public string RuleId => StorageLatencyBlindSpot.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return StorageLatencyBlindSpot.Evaluate(
            context.Observations, context.Options.StorageLatencyBlindSpot);
    }
}

/// <summary>Adapts <see cref="DroppedPackets"/>.</summary>
public sealed class DroppedPacketsRule : IAnalysisRule
{
    public string RuleId => DroppedPackets.RuleId;

    public RuleScope Scope => RuleScope.Metric;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return DroppedPackets.Evaluate(context.Observations, context.Options.DroppedPackets);
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;

        return StorageNoisyNeighbour.Evaluate(
            context.Observations,
            context.Graph,
            StorageNoisyNeighbour.TypicalRateFrom(
                context.Series, context.NowUtc, options.StorageNoisyNeighbour),
            options.StorageNoisyNeighbour with { Peers = options.PeerOutliers });
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return StoragePathRedundancy.Evaluate(
            [.. context.Graph.Active], context.Options.StoragePathRedundancy);
    }
}

/// <summary>Adapts <see cref="MultipathSinglePointOfFailure"/>.</summary>
/// <remarks>
/// Same rhythm and scope as <see cref="StoragePathRedundancyRule"/> and for
/// the same reason: both read the path table the inventory cycle just merged,
/// and running either on the metric rhythm would have it re-deciding a fact
/// nothing had re-read.
/// </remarks>
public sealed class MultipathSinglePointOfFailureRule : IAnalysisRule
{
    public string RuleId => MultipathSinglePointOfFailure.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return MultipathSinglePointOfFailure.Evaluate(
            [.. context.Graph.Active], context.Options.MultipathSinglePointOfFailure);
    }
}

/// <summary>Adapts <see cref="DrsRuleViolations"/>.</summary>
/// <remarks>
/// On the inventory rhythm beside <see cref="StoragePathRedundancyRule"/> and
/// for the same reason: DRS rules and the placement they are judged against
/// are both read on the inventory rhythm and change on it, and the graph
/// handed here is the one this cycle just merged rather than the store's
/// stale copy.
/// </remarks>
public sealed class DrsRuleViolationsRule : IAnalysisRule
{
    public string RuleId => DrsRuleViolations.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return DrsRuleViolations.Evaluate(context.Graph);
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return RemoteLogging.Evaluate([.. context.Graph.Active], context.Options.RemoteLogging);
    }
}

/// <summary>Adapts <see cref="ClusterHighAvailability"/>.</summary>
/// <remarks>
/// On the inventory rhythm because HA configuration is read on it and changes
/// on it, same as <see cref="RemoteLoggingRule"/> above -- a setting nobody
/// changes has nothing new to say every twenty seconds.
/// </remarks>
public sealed class ClusterHighAvailabilityRule : IAnalysisRule
{
    public string RuleId => ClusterHighAvailability.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ClusterHighAvailability.Evaluate(
            [.. context.Graph.Active], context.Options.ClusterHighAvailability);
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Options.EventAlerts;

        return EventAlerts.Evaluate(
            EventAlerts.Read(context.Events, context.NowUtc, policy), context.NowUtc, policy);
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
/// keeps the alerts it had (through <see cref="RuleContext.Unevaluated"/>),
/// the failure is raised as an alert, and the others are estimated as usual.
/// </para>
/// </remarks>
public sealed class DatastoreTimeToFullRule : IAnalysisRule
{
    public string RuleId => DatastoreTimeToFull.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Options.DatastoreTimeToFull;
        var readings = DatastoreTimeToFull.CurrentReadings(
            context.Snapshots.SelectMany(s => s.Observations), context.Graph);

        return DatastoreTimeToFull.EvaluateEach(
            readings,
            d => DatastoreTimeToFull.Read(
                context.Series,
                d.Datastore,
                d.CapacityBytes,
                context.NowUtc,
                policy,
                context.Options.Retention),
            context.Unevaluated,
            policy);
    }
}

/// <summary>Adapts <see cref="ClusterNPlusOne"/>.</summary>
/// <remarks>
/// <para>
/// On the inventory rhythm beside <see cref="DatastoreTimeToFullRule"/>, for
/// the parallel reason: cluster membership (<c>PartOf</c>) is read on it, and
/// the question this rule answers is a capacity-planning one rather than a
/// thirty-second one. Unlike <see cref="DatastoreTimeToFullRule"/> it does not
/// read <see cref="RuleContext.Observations"/> for current demand -- the
/// inventory rhythm carries none -- and instead reads each host's latest
/// sample from the series store, the way
/// <see cref="DatastoreTimeToFull.LatestCapacity"/> reads a datastore's
/// capacity from outside a cycle.
/// </para>
/// <para>
/// Guarded per cluster the same way <see cref="DatastoreTimeToFullRule"/> is:
/// a cluster whose history cannot be read keeps its alerts
/// (<see cref="RuleContext.Unevaluated"/>) rather than having them silently
/// resolved.
/// </para>
/// </remarks>
public sealed class ClusterNPlusOneRule : IAnalysisRule
{
    public string RuleId => ClusterNPlusOne.RuleId;

    public RuleScope Scope => RuleScope.Inventory;

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.Options.ClusterNPlusOne;

        var states = ClusterNPlusOne.CurrentReadings(
            context.Series, context.Graph, context.NowUtc, policy, context.Options.Retention);

        return ClusterNPlusOne.EvaluateEach(
            states, context.Series, context.NowUtc, policy, context.Options.Retention, context.Unevaluated);
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

    public IReadOnlyList<AlertDefinition> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return CollectionCoverage.Evaluate(
            context.Snapshots.ToDictionary(
                s => s.SourceInstanceId,
                s => s.Coverage,
                StringComparer.Ordinal));
    }
}
