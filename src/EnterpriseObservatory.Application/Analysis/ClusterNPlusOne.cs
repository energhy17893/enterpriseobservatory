using System.Collections.Concurrent;
using System.Globalization;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which resource a failover headroom verdict is about.
/// </summary>
public enum ClusterCapacityResource
{
    Cpu,
    Memory,
}

/// <summary>
/// The defaults this rule answers "does N+1 hold, and until when" with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this policy is built around.</b> The question asked is "if the
/// largest host fails, do the survivors still hold the running VMs' demand" —
/// which needs each host's CPU capacity in MHz and memory capacity in bytes to
/// find the largest one and to know what is left without it. Neither is
/// collected anywhere in this product today: <see cref="EntitySizing"/> only
/// carries a <em>virtual machine's</em> configured sizing, a host's
/// <c>Entity.Settings</c> only carries HA and advanced options
/// (<see cref="ClusterHighAvailabilityPolicy"/>, <c>AdvancedSettings</c>), and
/// the only host-level performance counters this product reads are
/// <see cref="CpuUsageCounter"/> and <see cref="MemoryUsageCounter"/> — both
/// already percentages of that host's own capacity, not a level in MHz or
/// bytes. This is a genuine collection gap, not something to be papered over
/// with an invented MHz figure.
/// </para>
/// <para>
/// <b>What is built with it instead.</b> A host's usage percentage, divided by
/// a hundred, is exactly "how many of that one host's capacity this host is
/// using" — a unit this policy calls a <em>host-equivalent</em>. Summed across
/// a cluster's hosts it is the cluster's demand in host-equivalents, and it
/// needs no MHz or byte figure to be correct arithmetic. What it does need,
/// and cannot get from the data collected, is host <em>size</em>: a host at
/// 50% of a 40-core box and a host at 50% of a 4-core box are not the same
/// amount of work, and this product cannot currently tell them apart. Losing
/// "the largest host" therefore stands for losing any one host, at one
/// host-equivalent of capacity, which is exact on a cluster built from
/// identical hosts and an approximation everywhere else. VMware's own vSphere
/// Availability guide already assumes this when it recommends building an HA
/// cluster from hosts of the same size and vendor, precisely so admission
/// control's arithmetic is predictable -- so the assumption this policy leans
/// on is the one the platform already asks an operator to make true. A
/// cluster that is not built that way gets a number that is honestly
/// approximate rather than one dressed up as exact, and the entity page says
/// so; see <see cref="ClusterNPlusOne"/>.
/// </para>
/// </remarks>
public sealed record ClusterNPlusOnePolicy
{
    public static ClusterNPlusOnePolicy Default { get; } = new();

    /// <summary>
    /// The counter naming a host's CPU demand as a percentage of its own
    /// capacity.
    /// </summary>
    /// <remarks>
    /// Held as policy, not compiled in, for the reason
    /// <see cref="CpuContentionPolicy.HostUsageCounter"/> already gives: the
    /// application layer may not know what a vim25 counter is called.
    /// </remarks>
    public string CpuUsageCounter { get; init; } = "cpu.usage.average";

    /// <summary>The counter naming a host's memory demand, the same way.</summary>
    public string MemoryUsageCounter { get; init; } = "mem.usage.average";

    /// <summary>
    /// The largest share of the surviving hosts' capacity that may be planned
    /// against after one host is lost.
    /// </summary>
    /// <remarks>
    /// Ninety percent — <b>this product's choice</b>, not a vendor's. Two
    /// vendor practices point the same direction without naming a figure:
    /// the vSphere Availability guide's percentage-based admission control
    /// reserves a slice of cluster capacity as failover headroom rather than
    /// spending all of it, and the vSphere Resource Management guide warns
    /// against running hosts at the edge of their capacity because it leaves
    /// nothing for the burst above average that real workloads produce. Ten
    /// percent of the survivors' capacity is kept back for exactly that
    /// burst — the same reason <see cref="CpuContentionPolicy.HostSaturationPercent"/>
    /// stops short of 100.
    /// </remarks>
    public double MaxUtilizationAfterFailover { get; init; } = 0.9;

    /// <summary>
    /// Warn when the date the guarantee is lost is this close.
    /// </summary>
    /// <remarks>
    /// Thirty days — <b>this product's choice</b>, the same window
    /// <see cref="DatastoreTimeToFullPolicy.WarningWithin"/> uses and for the
    /// same reason: long enough to add a host, rebalance, or right-size
    /// workloads before it is an outage waiting for a failure to happen.
    /// </remarks>
    public TimeSpan WarningWithin { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How much history the demand trend is fitted to.</summary>
    /// <remarks>Thirty days, for the reason <see cref="DatastoreTimeToFullPolicy.Lookback"/> gives.</remarks>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often a live host is expected to report a fresh sample.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not this rule's own number: it is <c>MonitoringPorts.ObservationInterval</c>'s
    /// default, held here as policy for the same reason every other
    /// cross-layer figure on this record is -- the application layer must
    /// not assume the host's own configured cadence.
    /// </para>
    /// <para>
    /// <see cref="ClusterNPlusOne.LatestPercent"/> uses it to decide when a
    /// host's "latest" sample is too old to still call current: the series
    /// store's raw tier keeps points for up to
    /// <see cref="SeriesRetentionPolicy.Raw"/> (two days by default), and
    /// without a recency check the newest point inside that whole window
    /// would be accepted as "now", silently counting a host that stopped
    /// reporting two days ago as present-and-idle capacity.
    /// </para>
    /// </remarks>
    public TimeSpan ObservationInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many missed <see cref="ObservationInterval"/>s a host's latest
    /// sample may be behind before it is treated as missing rather than
    /// current.
    /// </summary>
    /// <remarks>
    /// Three -- <b>this product's choice</b>: one missed interval is
    /// ordinary jitter, three in a row is a host that has actually stopped
    /// answering.
    /// </remarks>
    public int StaleAfterIntervals { get; init; } = 3;

    /// <summary>Most points read per host series.</summary>
    /// <remarks>Same default as <see cref="DatastoreTimeToFullPolicy.MaxPoints"/> and for the same reason.</remarks>
    public int MaxPoints { get; init; } = 720;

    /// <summary>The refusal thresholds of the date estimate itself.</summary>
    public TimeToFullPolicy Estimate { get; init; } = TimeToFullPolicy.Default;
}

/// <summary>
/// One cluster's hosts and their current demand, in host-equivalents.
/// </summary>
/// <param name="Cluster">The cluster entity.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Source">The collector instance that reported it.</param>
/// <param name="Hosts">
/// Every live host currently <c>PartOf</c> this cluster. At least two, or the
/// cluster is not carried at all — see <see cref="ClusterNPlusOne.HostsByLiveCluster"/>.
/// </param>
/// <param name="CpuDemandHosts">
/// The cluster's CPU demand, summed across hosts as a share of one host's
/// capacity each. Null when any host's current reading could not be read —
/// see the remarks on <see cref="ClusterNPlusOne.CurrentReadings"/>.
/// </param>
/// <param name="MemoryDemandHosts">The same, for memory.</param>
public sealed record ClusterFailoverState(
    EntityId Cluster,
    string Name,
    string Source,
    IReadOnlyList<EntityId> Hosts,
    double? CpuDemandHosts,
    double? MemoryDemandHosts)
{
    public int HostCount => Hosts.Count;
}

/// <summary>
/// If the largest host fails, do the survivors still hold the running VMs'
/// demand — and on what date is that guarantee lost.
/// </summary>
/// <remarks>
/// <para>
/// Rides the inventory rhythm, like <see cref="ClusterHighAvailabilityRule"/>
/// and <see cref="DatastoreTimeToFullRule"/>: cluster membership is read on it,
/// and the question is a capacity-planning one that does not need a thirty-
/// second answer. Current demand is read from the series store's latest raw
/// sample rather than from the cycle's own observation batch, the same way
/// <see cref="DatastoreTimeToFull.LatestCapacity"/> reads a datastore's
/// capacity — which is what lets this rule run on the slower rhythm without
/// depending on the metric cycle's batch, which the inventory rhythm does not
/// receive at all (see <see cref="RuleContext.Observations"/>).
/// </para>
/// <para>
/// See <see cref="ClusterNPlusOnePolicy"/> for the collection gap this model
/// is built around: no host's CPU or memory capacity in absolute units
/// (MHz, bytes) is collected today, so "the largest host" is approximated as
/// "any one host" at one host-equivalent, and the arithmetic works entirely
/// in percentages of a host's own capacity rather than invented absolute
/// figures.
/// </para>
/// </remarks>
public static class ClusterNPlusOne
{
    public const string RuleId = "cluster-n-plus-one";

    private const string Category = "Capacity";
    private const string Platform = "platform";
    private const double Epsilon = 1e-9;

    public const string AlreadyFailsTitle = "Cluster cannot absorb its largest host failing";
    public const string AtRiskTitle = "Cluster failover headroom running out";
    public const string HistoryUnreadableTitle = "Cluster failover history could not be read";

    /// <summary>
    /// How many host-equivalents of capacity are left once the largest host
    /// is gone.
    /// </summary>
    /// <remarks>
    /// <c>hostCount - 1</c>: the equal-size approximation
    /// <see cref="ClusterNPlusOnePolicy"/> documents. Never negative.
    /// </remarks>
    public static double SurvivingCapacityHosts(int hostCount) => Math.Max(0, hostCount - 1);

    /// <summary>
    /// How much of the survivors' capacity may be spent before the ninety
    /// percent (or configured) ceiling is reached.
    /// </summary>
    /// <remarks>
    /// The same figure for CPU and memory: both are expressed in
    /// host-equivalents of the same cluster, so the headroom policy applies
    /// identically to each resource's own demand.
    /// </remarks>
    public static double AvailableAfterFailoverHosts(int hostCount, ClusterNPlusOnePolicy policy) =>
        SurvivingCapacityHosts(hostCount) * policy.MaxUtilizationAfterFailover;

    /// <summary>
    /// Every live host's live cluster, from <c>PartOf</c> edges.
    /// </summary>
    /// <remarks>
    /// A cluster with fewer than two live hosts is left out entirely: N+1
    /// asks what survives losing one host, which is not a question a
    /// single-host cluster can be asked, and the honest answer for a
    /// zero-host cluster is not "yes" but "nothing to ask". Both are left
    /// silent here rather than reported as a false pass, the same choice
    /// <see cref="StoragePathRedundancy"/> makes about a single-path device.
    /// </remarks>
    public static Dictionary<EntityId, List<EntityId>> HostsByLiveCluster(EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var byCluster = new Dictionary<EntityId, List<EntityId>>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.PartOf))
        {
            if (!IsLiveConnectedHost(graph, edge.From) || !IsLive(graph, edge.To, EntityKind.Cluster))
            {
                continue;
            }

            if (!byCluster.TryGetValue(edge.To, out var hosts))
            {
                byCluster[edge.To] = hosts = [];
            }

            if (!hosts.Contains(edge.From))
            {
                hosts.Add(edge.From);
            }
        }

        return byCluster.Where(kv => kv.Value.Count >= 2)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static bool IsLive(EntityGraph graph, EntityId id, EntityKind kind) =>
        graph.Entities.TryGetValue(id, out var entity) &&
        entity.Kind == kind &&
        entity.ObservationState != ObservationState.Vanished;

    /// <summary>
    /// A host that actually stands as N+1 capacity right now: seen this
    /// cycle, not in maintenance, and reachable.
    /// </summary>
    /// <remarks>
    /// <see cref="IsLive"/> alone accepts <see cref="ObservationState.InMaintenance"/>,
    /// which is correct for membership questions but wrong here: a host
    /// deliberately pulled out of service, or one vCenter cannot currently
    /// reach, holds none of the failover headroom this rule is checking for.
    /// Only <see cref="ObservationState.Active"/> counts. Connectivity is not
    /// its own <c>ObservationState</c> today -- <c>VsphereInventorySource</c>
    /// reports an unreachable host as <see cref="HealthState.Unknown"/>
    /// rather than a distinct disconnected state (see its <c>AddHosts</c>),
    /// so <see cref="Entity.Health"/> is read as the next best signal: a
    /// connected host with a genuinely unclassified overall status also
    /// reads <c>Unknown</c>, so this is an approximation in the direction of
    /// undercounting capacity rather than overcounting it, which is the
    /// safer of the two errors for a failover guarantee.
    /// </remarks>
    private static bool IsLiveConnectedHost(EntityGraph graph, EntityId id) =>
        graph.Entities.TryGetValue(id, out var entity) &&
        entity.Kind == EntityKind.EsxiHost &&
        entity.ObservationState == ObservationState.Active &&
        entity.Health != HealthState.Unknown;

    /// <summary>
    /// Every eligible cluster's current demand, read from the series store's
    /// latest sample rather than a cycle's observation batch.
    /// </summary>
    /// <remarks>
    /// A resource's demand is null for a cluster where at least one of its
    /// hosts has no readable current sample: summing zero in for a host that
    /// was simply not read would understate demand and could report N+1
    /// holding when the truth is unknown, which is a worse answer than no
    /// answer. See <see cref="TimeToFullResult.Refusal"/>'s reasoning for the
    /// same trade, applied here to the "now" half instead of the date.
    /// </remarks>
    public static IReadOnlyList<ClusterFailoverState> CurrentReadings(
        ISeriesReader series,
        EntityGraph graph,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retention);

        var states = new List<ClusterFailoverState>();

        foreach (var (clusterId, hosts) in HostsByLiveCluster(graph))
        {
            if (!graph.Entities.TryGetValue(clusterId, out var cluster))
            {
                continue;
            }

            var cpu = SumLatestPercent(series, hosts, policy.CpuUsageCounter, nowUtc, policy, retention);
            var mem = SumLatestPercent(series, hosts, policy.MemoryUsageCounter, nowUtc, policy, retention);

            states.Add(new ClusterFailoverState(
                clusterId, cluster.DisplayName, Attribution(cluster), hosts, cpu, mem));
        }

        return states;
    }

    /// <summary>
    /// The sum, in host-equivalents, of every host's latest reading of one
    /// counter — or null when any host's reading is missing.
    /// </summary>
    private static double? SumLatestPercent(
        ISeriesReader series,
        IReadOnlyList<EntityId> hosts,
        string counter,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention)
    {
        var total = 0d;

        foreach (var host in hosts)
        {
            if (LatestPercent(series, host, counter, nowUtc, policy, retention) is not { } percent)
            {
                return null;
            }

            total += percent / 100d;
        }

        return total;
    }

    /// <summary>
    /// A host's latest reading of one percentage counter, the raw tier's
    /// newest point — the same pattern <see cref="DatastoreTimeToFull.LatestCapacity"/>
    /// uses for a datastore's capacity.
    /// </summary>
    /// <remarks>
    /// The raw tier keeps points for up to <see cref="SeriesRetentionPolicy.Raw"/>
    /// (two days by default), which is a retention window, not a claim that a
    /// two-day-old point is still "now". A point older than
    /// <see cref="ClusterNPlusOnePolicy.StaleAfterIntervals"/> worth of
    /// <see cref="ClusterNPlusOnePolicy.ObservationInterval"/> is treated the
    /// same as no point at all: a host that stopped reporting is missing
    /// capacity, not idle capacity.
    /// </remarks>
    private static double? LatestPercent(
        ISeriesReader series,
        EntityId host,
        string counter,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention)
    {
        var result = series.Query(new SeriesQuery
        {
            Key = new SeriesKey(host, counter, string.Empty),
            FromUtc = nowUtc - retention.Raw,
            ToUtc = nowUtc + TimeSpan.FromTicks(1),
            Resolution = SeriesResolution.Raw,
            MaxPoints = 1,
        });

        if (result.Points.Count == 0)
        {
            return null;
        }

        var latest = result.Points[^1];
        var staleAfter = policy.ObservationInterval * policy.StaleAfterIntervals;

        return nowUtc - latest.StartUtc <= staleAfter ? latest.Last : null;
    }

    /// <summary>One host's demand history query, aligned with the cluster's other hosts.</summary>
    public static SeriesQuery HostHistoryQuery(
        EntityId host,
        string counter,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retention);

        var resolution = retention.RetainedResolutionFor(policy.Lookback, policy.MaxPoints, policy.Lookback);

        return new SeriesQuery
        {
            Key = new SeriesKey(host, counter, string.Empty),
            FromUtc = nowUtc - policy.Lookback,
            ToUtc = nowUtc,
            Resolution = resolution,
            MaxPoints = policy.MaxPoints,
        };
    }

    /// <summary>
    /// The cluster's demand trend: at each bucket, the sum of every host's
    /// last reading in that bucket, in host-equivalents.
    /// </summary>
    /// <remarks>
    /// A host absent from a bucket (no reading yet, or it joined later) simply
    /// does not contribute to that bucket rather than being treated as zero —
    /// the same "absent is not zero" rule <see cref="SeriesResult.Points"/>
    /// itself documents. That understates history for a cluster whose
    /// membership changed inside the lookback window; it is not corrected
    /// here because doing so would need the membership history this product
    /// does not keep, and an approximate trend is preferable to inventing one.
    /// </remarks>
    public static IReadOnlyList<TrendPoint> AggregateDemandTrend(IReadOnlyList<SeriesResult> hostHistories)
    {
        ArgumentNullException.ThrowIfNull(hostHistories);

        var byBucket = new SortedDictionary<DateTimeOffset, double>();

        foreach (var history in hostHistories)
        {
            foreach (var point in history.Points)
            {
                byBucket[point.StartUtc] = byBucket.GetValueOrDefault(point.StartUtc) + point.Last / 100d;
            }
        }

        return [.. byBucket.Select(kv => new TrendPoint(kv.Key, kv.Value))];
    }

    /// <summary>
    /// Reads every host's history, aggregates it, and estimates the date the
    /// cluster's demand crosses <paramref name="availableAfterFailoverHosts"/>.
    /// </summary>
    /// <remarks>
    /// The queries run every time; the O(n²) estimate is cached, keyed off the
    /// aggregated trend's shape — the same split
    /// <see cref="DatastoreTimeToFull.Read"/> makes, for the same reason.
    /// </remarks>
    public static TimeToFullResult ReadDate(
        ISeriesReader series,
        EntityId cluster,
        IReadOnlyList<EntityId> hosts,
        string counter,
        double availableAfterFailoverHosts,
        ClusterCapacityResource resource,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention,
        ClusterNPlusOneCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retention);

        var histories = new List<SeriesResult>(hosts.Count);

        foreach (var host in hosts)
        {
            histories.Add(series.Query(HostHistoryQuery(host, counter, nowUtc, policy, retention)));
        }

        var trend = AggregateDemandTrend(histories);
        var key = ClusterNPlusOneCache.KeyOf(trend, availableAfterFailoverHosts, nowUtc, policy);

        return (cache ?? ClusterNPlusOneCache.Shared).GetOrAdd(
            cluster, resource, key, nowUtc,
            () => EstimateDate(trend, availableAfterFailoverHosts, nowUtc, policy));
    }

    /// <summary>Pure: the date the aggregated trend crosses the available headroom, or a refusal.</summary>
    public static TimeToFullResult EstimateDate(
        IReadOnlyList<TrendPoint> trend,
        double availableAfterFailoverHosts,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trend);
        ArgumentNullException.ThrowIfNull(policy);

        if (availableAfterFailoverHosts <= 0)
        {
            return new TimeToFullResult.Refusal
            {
                Reason = TimeToFullRefusalReason.AlreadyFull,
                Detail = "There is no surviving capacity to plan against: the cluster has too few hosts.",
                PointsUsed = trend.Count,
            };
        }

        return TimeToFull.Estimate(trend, availableAfterFailoverHosts, nowUtc, policy.Estimate);
    }

    /// <summary>
    /// The alerts for what was read and estimated. Pure.
    /// </summary>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<(ClusterFailoverState State, TimeToFullResult? CpuDate, TimeToFullResult? MemoryDate)> clusters,
        ClusterNPlusOnePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        var rules = policy ?? ClusterNPlusOnePolicy.Default;
        var alerts = new List<AlertDefinition>();

        foreach (var (state, cpuDate, memoryDate) in clusters)
        {
            if (state.CpuDemandHosts is { } cpuDemand &&
                Verdict(state, ClusterCapacityResource.Cpu, cpuDemand, cpuDate, rules) is { } cpu)
            {
                alerts.Add(cpu);
            }

            if (state.MemoryDemandHosts is { } memoryDemand &&
                Verdict(state, ClusterCapacityResource.Memory, memoryDemand, memoryDate, rules) is { } memory)
            {
                alerts.Add(memory);
            }
        }

        return alerts;
    }

    /// <summary>
    /// Estimates each cluster's date and returns the alerts, so that one
    /// cluster whose history cannot be read costs that cluster and not the
    /// others — the same guard <see cref="DatastoreTimeToFull.EvaluateEach"/> gives.
    /// </summary>
    public static IReadOnlyList<AlertDefinition> EvaluateEach(
        IReadOnlyList<ClusterFailoverState> states,
        ISeriesReader series,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy? policy,
        SeriesRetentionPolicy retention,
        ICollection<AlertFingerprint> unevaluated,
        ClusterNPlusOneCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(unevaluated);

        var rules = policy ?? ClusterNPlusOnePolicy.Default;

        var evaluated =
            new List<(ClusterFailoverState State, TimeToFullResult? CpuDate, TimeToFullResult? MemoryDate)>(states.Count);

        var failed = new List<(ClusterFailoverState Cluster, Exception Error)>();

        foreach (var state in states)
        {
            // "No answer" must not read as "no problem". A cluster with fewer
            // than two live hosts, or a resource whose current demand could
            // not be summed this cycle (see CurrentReadings), is not judged
            // below -- and without this, an alert this rule raised on an
            // earlier cycle would be silently resolved by reconciliation the
            // moment this cycle simply had nothing to say, the same failure
            // mode the catch block below already guards against for a
            // history read that throws outright.
            if (state.HostCount < 2)
            {
                foreach (var fingerprint in Fingerprints(state.Cluster))
                {
                    unevaluated.Add(fingerprint);
                }

                continue;
            }

            if (state.CpuDemandHosts is null)
            {
                foreach (var fingerprint in FingerprintsFor(state.Cluster, ClusterCapacityResource.Cpu))
                {
                    unevaluated.Add(fingerprint);
                }
            }

            if (state.MemoryDemandHosts is null)
            {
                foreach (var fingerprint in FingerprintsFor(state.Cluster, ClusterCapacityResource.Memory))
                {
                    unevaluated.Add(fingerprint);
                }
            }

            try
            {
                var available = AvailableAfterFailoverHosts(state.HostCount, rules);

                var cpuDate = state.CpuDemandHosts is not null
                    ? ReadDate(
                        series, state.Cluster, state.Hosts, rules.CpuUsageCounter, available,
                        ClusterCapacityResource.Cpu, nowUtc, rules, retention, cache)
                    : null;

                var memoryDate = state.MemoryDemandHosts is not null
                    ? ReadDate(
                        series, state.Cluster, state.Hosts, rules.MemoryUsageCounter, available,
                        ClusterCapacityResource.Memory, nowUtc, rules, retention, cache)
                    : null;

                evaluated.Add((state, cpuDate, memoryDate));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Justified as in DatastoreTimeToFull.EvaluateEach: the query
            // crosses a network to a database and may throw anything; one cluster's history must
            // not cost every other cluster its verdict.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed.Add((state, ex));

                foreach (var fingerprint in Fingerprints(state.Cluster))
                {
                    unevaluated.Add(fingerprint);
                }
            }
        }

        var alerts = Evaluate(evaluated, rules).ToList();

        if (failed.Count > 0)
        {
            alerts.Add(HistoryUnreadable(failed));
        }

        return alerts;
    }

    private static AlertDefinition? Verdict(
        ClusterFailoverState state,
        ClusterCapacityResource resource,
        double demandHosts,
        TimeToFullResult? date,
        ClusterNPlusOnePolicy policy)
    {
        var available = AvailableAfterFailoverHosts(state.HostCount, policy);

        if (demandHosts > available + Epsilon)
        {
            return Alert(
                state, resource, AlreadyFailsTitle, AlertSeverity.Critical,
                DescribeAlreadyFails(state, resource, demandHosts, available));
        }

        if (date is TimeToFullResult.Forecast forecast && forecast.Days <= policy.WarningWithin.TotalDays)
        {
            return Alert(
                state, resource, AtRiskTitle, AlertSeverity.Warning,
                DescribeAtRisk(state, resource, forecast, demandHosts, available));
        }

        return null;
    }

    private static AlertDefinition Alert(
        ClusterFailoverState state,
        ClusterCapacityResource resource,
        string title,
        AlertSeverity severity,
        string description) => new()
        {
            Fingerprint = FingerprintFor(state.Cluster, resource, title),
            Severity = severity,
            Title = title,
            Description = description,
            Category = Category,
            Source = Platform,
            Entity = state.Cluster,
            IsDerived = true,
        };

    private static AlertFingerprint FingerprintFor(EntityId cluster, ClusterCapacityResource resource, string title) =>
        AlertFingerprint.Create(Platform, title, Category, $"{cluster.Value}/{resource}", RuleId);

    /// <summary>Every fingerprint this rule can raise for one cluster.</summary>
    public static IReadOnlyList<AlertFingerprint> Fingerprints(EntityId cluster) =>
        [.. FingerprintsFor(cluster, ClusterCapacityResource.Cpu), .. FingerprintsFor(cluster, ClusterCapacityResource.Memory)];

    /// <summary>Every fingerprint this rule can raise for one cluster's one resource.</summary>
    private static IReadOnlyList<AlertFingerprint> FingerprintsFor(EntityId cluster, ClusterCapacityResource resource) =>
    [
        FingerprintFor(cluster, resource, AlreadyFailsTitle),
        FingerprintFor(cluster, resource, AtRiskTitle),
    ];

    private static AlertDefinition HistoryUnreadable(List<(ClusterFailoverState Cluster, Exception Error)> failed)
    {
        const int named = 5;
        var names = string.Join(", ", failed.Take(named).Select(f => $"'{f.Cluster.Name}'"));
        var more = failed.Count > named ? $" and {failed.Count - named} more" : string.Empty;
        var (_, first) = failed[0];

        return new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create(
                Platform, HistoryUnreadableTitle, Category, RuleId, "cluster-n-plus-one-history-unreadable"),
            Severity = AlertSeverity.Warning,
            Title = HistoryUnreadableTitle,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"The demand history of {failed.Count} cluster(s) could not be read this cycle " +
                $"({names}{more}); the first threw {first.GetType().Name}: {first.Message} " +
                $"Their N+1 verdicts are kept as they were, not rechecked, until the history can be " +
                $"read again. The other clusters were estimated normally."),
            Category = Category,
            Source = Platform,
            IsDerived = true,
        };
    }

    private static string DescribeAlreadyFails(
        ClusterFailoverState state, ClusterCapacityResource resource, double demand, double available) =>
        $"'{state.Name}' is already carrying more {Label(resource)} demand than its surviving hosts " +
        $"could absorb if the largest one failed: {demand:0.##} host-equivalents of demand against " +
        $"{available:0.##} available after losing one host at " +
        $"{Percent()} utilisation. \"Largest host\" is approximated as any one host, one " +
        "host-equivalent of capacity, because no collector this product runs reads a host's CPU or " +
        "memory capacity in absolute units -- see ClusterNPlusOnePolicy for why. If a host failed " +
        "right now, the survivors could not take on every VM's current demand without exceeding it.";

    private static string DescribeAtRisk(
        ClusterFailoverState state, ClusterCapacityResource resource, TimeToFullResult.Forecast forecast,
        double demand, double available) =>
        $"'{state.Name}' still holds N+1 for {Label(resource)} today ({demand:0.##} of {available:0.##} " +
        $"host-equivalents available after losing its largest host), but {Explain(forecast, resource)}. " +
        $"\"Largest host\" is approximated as any one host, one host-equivalent of capacity -- see " +
        "ClusterNPlusOnePolicy for why.";

    private static string Percent() =>
        (ClusterNPlusOnePolicy.Default.MaxUtilizationAfterFailover * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Label(ClusterCapacityResource resource) =>
        resource == ClusterCapacityResource.Cpu ? "CPU" : "memory";

    /// <summary>One sentence for a person: the date and window, or the refusal. Shared by the alert and the entity page.</summary>
    public static string Explain(TimeToFullResult estimate, ClusterCapacityResource resource)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        var label = Label(resource);

        return estimate switch
        {
            TimeToFullResult.Forecast f => string.Create(CultureInfo.InvariantCulture,
                $"{label} demand reaches the surviving hosts' headroom on {f.FullAtUtc:yyyy-MM-dd}, in " +
                $"{f.Days:0.#} days, growing {f.SlopePerDay:0.###} host-equivalents a day, based on " +
                $"{DatastoreTimeToFull.Describe(f.Window, f.PointsUsed)}"),
            TimeToFullResult.Refusal r => string.Create(CultureInfo.InvariantCulture,
                $"cannot say when {label} headroom is lost: {r.Detail}"),
            _ => throw new ArgumentOutOfRangeException(nameof(estimate)),
        };
    }

    private static string Attribution(Entity cluster) =>
        string.IsNullOrWhiteSpace(cluster.SourceInstanceId) ? Platform : cluster.SourceInstanceId;
}

/// <summary>
/// The last N+1 date estimate made for each cluster and resource, and what it
/// was made from.
/// </summary>
/// <remarks>
/// The same design as <see cref="TimeToFullCache"/>, kept as a separate class
/// rather than a generalisation of it: the key there is typed to
/// <see cref="DatastoreTimeToFullPolicy"/>, and one cluster carries two
/// independent estimates (CPU and memory) rather than one, so the cache key is
/// <c>(EntityId, ClusterCapacityResource)</c> instead of <see cref="EntityId"/>
/// alone. See <see cref="ClusterNPlusOne.ReadDate"/>.
/// </remarks>
public sealed class ClusterNPlusOneCache
{
    private readonly ConcurrentDictionary<(EntityId Cluster, ClusterCapacityResource Resource), Entry> _entries = new();

    private long _computations;

    public ClusterNPlusOneCache(int maxEntries = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        MaxEntries = maxEntries;
    }

    public static ClusterNPlusOneCache Shared { get; } = new();

    public int MaxEntries { get; }

    public int Count => _entries.Count;

    public long Computations => Interlocked.Read(ref _computations);

    public readonly record struct Key(
        int Points,
        DateTimeOffset FirstUtc,
        DateTimeOffset LastUtc,
        double LastValue,
        double AvailableAfterFailoverHosts,
        DateTimeOffset HourUtc,
        ClusterNPlusOnePolicy Policy);

    public static Key KeyOf(
        IReadOnlyList<TrendPoint> trend,
        double availableAfterFailoverHosts,
        DateTimeOffset nowUtc,
        ClusterNPlusOnePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trend);
        ArgumentNullException.ThrowIfNull(policy);

        return new Key(
            trend.Count,
            trend.Count > 0 ? trend[0].AtUtc : default,
            trend.Count > 0 ? trend[^1].AtUtc : default,
            trend.Count > 0 ? trend[^1].Value : 0d,
            availableAfterFailoverHosts,
            SeriesResolutions.BucketStart(nowUtc, SeriesResolution.OneHour),
            policy);
    }

    public TimeToFullResult GetOrAdd(
        EntityId cluster,
        ClusterCapacityResource resource,
        Key key,
        DateTimeOffset nowUtc,
        Func<TimeToFullResult> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var cacheKey = (cluster, resource);

        if (_entries.TryGetValue(cacheKey, out var cached) && cached.Key.Equals(key))
        {
            return AsOf(cached, nowUtc);
        }

        var result = compute();
        Interlocked.Increment(ref _computations);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(cacheKey))
        {
            _entries.Clear();
        }

        _entries[cacheKey] = new Entry(key, nowUtc, result);

        return result;
    }

    private static TimeToFullResult AsOf(Entry entry, DateTimeOffset nowUtc)
    {
        if (entry.ComputedAtUtc == nowUtc || entry.Result is not TimeToFullResult.Forecast forecast)
        {
            return entry.Result;
        }

        var days = Math.Max(0d, (forecast.FullAtUtc - nowUtc).TotalDays);

        return forecast with
        {
            Days = days,
            FullAtUtc = days == 0 ? nowUtc : forecast.FullAtUtc,
        };
    }

    private sealed record Entry(Key Key, DateTimeOffset ComputedAtUtc, TimeToFullResult Result);
}
