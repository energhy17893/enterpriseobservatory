using System.Globalization;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Takes the demand snapshot the N+1 checks judge, outside the evaluation so
/// the evaluation stays pure (K1 decision 2).
/// </summary>
/// <remarks>
/// The same data the N+1 alarm read: each host's latest usage sample and the
/// cluster's aggregated history, from <see cref="ClusterNPlusOne"/>. One
/// cluster whose history cannot be read costs that cluster, not the others.
/// </remarks>
public static class ContinuityDemand
{
    public static DemandSnapshot Take(
        ISeriesReader series,
        EntityGraph graph,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention,
        ClusterNPlusOneCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retention);

        var clusters = new Dictionary<EntityId, ClusterDemand>();

        foreach (var state in ClusterNPlusOne.CurrentReadings(series, graph, nowUtc, policy, retention))
        {
            var available = ClusterNPlusOne.AvailableAfterFailoverHosts(state.HostCount, policy);

            try
            {
                clusters[state.Cluster] = new ClusterDemand
                {
                    HostCount = state.HostCount,
                    Cpu = Resource(series, state, ClusterCapacityResource.Cpu, state.CpuDemandHosts,
                        policy.CpuUsageCounter, available, nowUtc, policy, retention, cache),
                    Memory = Resource(series, state, ClusterCapacityResource.Memory, state.MemoryDemandHosts,
                        policy.MemoryUsageCounter, available, nowUtc, policy, retention, cache),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One cluster's unreadable history must not cost the others their verdict.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                clusters[state.Cluster] = new ClusterDemand
                {
                    HostCount = state.HostCount,
                    Cpu = new ResourceDemand { AvailableAfterFailoverHosts = available },
                    Memory = new ResourceDemand { AvailableAfterFailoverHosts = available },
                    Unreadable = $"{ex.GetType().Name}: {ex.Message}",
                };
            }
        }

        return new DemandSnapshot
        {
            TakenUtc = nowUtc,
            MaximumAge = policy.MaximumSnapshotAge,
            MinimumHistory = policy.MinimumHistory,
            WarningWithin = policy.WarningWithin,
            MaxUtilizationAfterFailover = policy.MaxUtilizationAfterFailover,
            Clusters = clusters,
        };
    }

    private static ResourceDemand Resource(
        ISeriesReader series,
        ClusterFailoverState state,
        ClusterCapacityResource resource,
        double? demand,
        string counter,
        double available,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention,
        ClusterNPlusOneCache? cache)
    {
        if (demand is null)
        {
            return new ResourceDemand { AvailableAfterFailoverHosts = available };
        }

        var trend = ClusterNPlusOne.ReadTrend(series, state.Hosts, counter, nowUtc, policy, retention);

        return new ResourceDemand
        {
            DemandHosts = demand,
            AvailableAfterFailoverHosts = available,
            HistoryCovered = trend.Count > 1 ? trend[^1].AtUtc - trend[0].AtUtc : TimeSpan.Zero,
            HistoryEndUtc = trend.Count > 0 ? trend[^1].AtUtc : null,
            Date = ClusterNPlusOne.EstimateCached(
                state.Cluster, trend, available, resource, nowUtc, policy, cache),
        };
    }
}

/// <summary>
/// If the largest host fails, do the survivors still hold the running VMs'
/// demand — and for how long (M8.2), judged as a finding.
/// </summary>
/// <remarks>
/// <para>
/// Moved from the <c>cluster-n-plus-one</c> alarm (ADR-0024): a cluster short
/// of failover headroom stays short until somebody adds capacity or accepts
/// the risk. The arithmetic is <see cref="ClusterNPlusOne"/>'s, unchanged —
/// host-equivalents, any one host lost, the policy's ceiling; only hosts
/// active, connected and not in maintenance count.
/// </para>
/// <para>
/// Pure: it reads the <see cref="DemandSnapshot"/> handed in through the
/// context. No snapshot, an old one, a cluster with fewer than two hosts in
/// service, an unread host or unreadable history are all <c>NotEvaluated</c>
/// with the reason. A cluster already over its headroom fails on its current
/// reading; "holds" is said only with at least the snapshot's minimum history
/// behind it, never from a few hours of numbers.
/// </para>
/// </remarks>
public sealed class NPlusOneCheck(ClusterCapacityResource resource) : IComplianceCheck
{
    public ClusterCapacityResource Resource { get; } = resource;

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        return [JudgeOne(entity, context)];
    }

    private CheckVerdict JudgeOne(Entity cluster, CheckContext context)
    {
        var label = Resource == ClusterCapacityResource.Cpu ? "CPU" : "memory";
        var snapshot = context.Demand;
        var ceiling = (snapshot?.MaxUtilizationAfterFailover ?? 0.9)
            .ToString("0.#%", CultureInfo.InvariantCulture);
        var expected = $"{label} demand fits the surviving hosts at {ceiling} if any one host fails";

        CheckVerdict NotEvaluated(string reason, string? observed = null) =>
            Verdict(ComplianceVerdict.NotEvaluated, expected, observed, reason);

        if (snapshot is null)
        {
            return NotEvaluated("Demand has not been computed yet.");
        }

        var age = context.NowUtc - snapshot.TakenUtc;

        if (age > snapshot.MaximumAge)
        {
            return NotEvaluated(string.Create(CultureInfo.InvariantCulture,
                $"The demand snapshot is {age.TotalMinutes:0} minutes old; it is not judged as current."));
        }

        if (!snapshot.Clusters.TryGetValue(cluster.Id, out var demand))
        {
            return NotEvaluated(
                "Fewer than two hosts of this cluster are in service (active, connected, not in " +
                "maintenance), so losing one cannot be asked about.");
        }

        if (demand.Unreadable is { } why)
        {
            return NotEvaluated($"The demand history could not be read: {why}");
        }

        var resource = Resource == ClusterCapacityResource.Cpu ? demand.Cpu : demand.Memory;

        if (resource.DemandHosts is not { } now)
        {
            return NotEvaluated($"Current {label} demand was not read from every host in service.");
        }

        var available = resource.AvailableAfterFailoverHosts;
        var observed = string.Create(CultureInfo.InvariantCulture,
            $"{now:0.##} host-equivalents of demand, {available:0.##} available after losing one of " +
            $"{demand.HostCount} hosts");

        if (now > available + 1e-9)
        {
            return Verdict(ComplianceVerdict.Failing, expected, observed + " — it does not fit today");
        }

        if (resource.HistoryCovered < snapshot.MinimumHistory)
        {
            return NotEvaluated(string.Create(CultureInfo.InvariantCulture,
                $"No {snapshot.MinimumHistory.TotalDays:0} days of demand history yet ({resource.HistoryCovered.TotalDays:0.#} days); it fits today, but that it holds is not said on so little."),
                observed);
        }

        if (resource.HistoryEndUtc is { } end && snapshot.TakenUtc - end > TimeSpan.FromDays(1))
        {
            return NotEvaluated(string.Create(CultureInfo.InvariantCulture,
                $"The demand history ends {(snapshot.TakenUtc - end).TotalDays:0.#} days ago."), observed);
        }

        if (resource.Date is TimeToFullResult.Forecast forecast &&
            forecast.Days <= snapshot.WarningWithin.TotalDays)
        {
            return Verdict(ComplianceVerdict.Failing, expected,
                observed + "; but " + ClusterNPlusOne.Explain(forecast, Resource));
        }

        return Verdict(ComplianceVerdict.Passing, expected,
            resource.Date is { } date ? observed + "; " + ClusterNPlusOne.Explain(date, Resource) : observed);
    }
}
