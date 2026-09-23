using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which rule owns an alert, read from its fingerprint's check id — the
/// fingerprint's last segment, which every rule sets (ADR-0026 design note §4).
/// </summary>
/// <remarks>
/// <para>
/// A new alert learns its rule from the verdict that raised it; this map is
/// for the alerts that were raised before the rule id was stored. Migration 14
/// fills <c>alert_instance.rule_id</c> from the same table, spelled out in SQL,
/// and a test holds the two together. Another test holds this map to every
/// check id a registered rule can emit.
/// </para>
/// <para>
/// A check id that is not here — a collection alert, a store failure, a rule
/// failure — belongs to a direct producer and stays two-valued at N = 1, which
/// is what every alert was before. So an unmapped row cannot close because of
/// the deploy.
/// </para>
/// </remarks>
public static class RuleCheckIds
{
    /// <summary>Check id → rule id, matched exactly.</summary>
    public static IReadOnlyDictionary<string, string> Exact { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fault-counter"] = FaultCounters.RuleId,
            ["peer-outlier"] = PeerOutliers.RuleId,
            ["cpu-host-saturated"] = CpuContention.RuleId,
            ["cpu-limit-reached"] = CpuContention.RuleId,
            ["cpu-ready-outlier"] = CpuContention.RuleId,
            ["cpu-costop-oversized"] = CpuContention.RuleId,
            ["cpu-width-unreadable"] = CpuContention.RuleId,
            ["memory-host-pressure"] = MemoryPressure.RuleId,
            ["memory-guest-pressure"] = MemoryPressure.RuleId,
            ["memory-limit-pressure"] = MemoryPressure.RuleId,
            ["storage-layer"] = StorageLayerSplit.RuleId,
            ["shared-volume"] = SharedVolumeLatency.RuleId,
            ["storage-latency-blind-spot"] = StorageLatencyBlindSpot.RuleId,
            ["net-dropped-packets"] = DroppedPackets.RuleId,
            ["storage-noisy-neighbour"] = StorageNoisyNeighbour.RuleId,
            ["storage-path-redundancy"] = StoragePathRedundancy.RuleId,
            ["datastore-time-to-full"] = DatastoreTimeToFull.RuleId,
            [DatastoreTimeToFull.OvercommitCheckId] = DatastoreTimeToFull.RuleId,
            [DatastoreTimeToFull.HistoryUnreadableCheckId] = DatastoreTimeToFull.RuleId,
            ["collection-coverage"] = CollectionCoverage.RuleId,
        };

    /// <summary>
    /// Check-id prefixes: <c>vcenter-events:&lt;condition&gt;</c>, one check id per
    /// condition of the event table.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Prefixes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventAlerts.RuleId + ":"] = EventAlerts.RuleId,
        };

    /// <summary>The rule that owns an alert with this fingerprint, or null for a direct producer.</summary>
    /// <remarks>
    /// The four M8 rules K2 retired are included (<see cref="MovedContinuityRules"/>):
    /// their open alarms keep their owner, which keeps them open until the
    /// continuity evaluation resolves them as moved.
    /// </remarks>
    public static string? RuleOf(AlertFingerprint fingerprint)
    {
        var value = fingerprint.Value;
        var checkId = value[(value.LastIndexOf('|') + 1)..];

        if (Exact.TryGetValue(checkId, out var rule))
        {
            return rule;
        }

        foreach (var (prefix, owner) in Prefixes)
        {
            if (checkId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return owner;
            }
        }

        return MovedContinuityRules.RuleOf(value);
    }
}
