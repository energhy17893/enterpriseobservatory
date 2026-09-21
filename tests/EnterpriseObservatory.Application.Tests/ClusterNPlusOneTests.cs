using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Roadmap M8.2: if the largest host fails, do the surviving hosts still hold
/// the running VMs' demand, and on what date is that guarantee lost.
/// </summary>
public class ClusterNPlusOneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly EntityId Cluster = new("vc-1:cluster-1");
    private static readonly EntityId Host1 = new("vc-1:host-1");
    private static readonly EntityId Host2 = new("vc-1:host-2");
    private static readonly EntityId Host3 = new("vc-1:host-3");

    // --- the arithmetic -----------------------------------------------------

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void Surviving_capacity_is_every_host_but_one_never_negative(int hostCount, double expected)
    {
        Assert.Equal(expected, ClusterNPlusOne.SurvivingCapacityHosts(hostCount));
    }

    [Fact]
    public void Available_headroom_applies_the_utilisation_ceiling_to_the_survivors()
    {
        Assert.Equal(
            2.7, ClusterNPlusOne.AvailableAfterFailoverHosts(4, ClusterNPlusOnePolicy.Default), 3);
    }

    // --- Evaluate: the verdict from precomputed demand and dates ------------

    private static ClusterFailoverState State(
        double? cpu, double? memory = 0d, IReadOnlyList<EntityId>? hosts = null) =>
        new(Cluster, "prod-cluster", "vc-1", hosts ?? [Host1, Host2, Host3], cpu, memory);

    private static TimeToFullResult.Forecast Forecast(double days) => new()
    {
        FullAtUtc = T0.AddDays(days),
        Days = days,
        SlopePerDay = 0.01,
        Window = new TrendWindow(T0.AddDays(-30), T0),
        PointsUsed = 720,
        PValue = 0.001,
    };

    private static TimeToFullResult.Refusal Refusal() => new()
    {
        Reason = TimeToFullRefusalReason.TooFewPoints,
        Detail = "3 points; at least 14 are needed.",
        PointsUsed = 3,
    };

    [Fact]
    public void Demand_already_past_the_survivors_headroom_is_critical_now()
    {
        // 3 hosts, 90% ceiling: available = 2 * 0.9 = 1.8 host-equivalents.
        var alert = Assert.Single(
            ClusterNPlusOne.Evaluate([(State(cpu: 1.9, memory: null), Refusal(), null)]));

        Assert.Equal(ClusterNPlusOne.AlreadyFailsTitle, alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(Cluster, alert.Entity);
    }

    [Fact]
    public void Demand_at_exactly_the_ceiling_still_holds()
    {
        Assert.Empty(ClusterNPlusOne.Evaluate([(State(cpu: 1.8, memory: null), Refusal(), null)]));
    }

    [Theory]
    [InlineData(3d, AlertSeverity.Warning)]
    [InlineData(29.9d, AlertSeverity.Warning)]
    [InlineData(30d, AlertSeverity.Warning)]
    public void A_date_inside_the_warning_window_is_an_alert_while_still_holding_now(
        double days, AlertSeverity expected)
    {
        var alert = Assert.Single(
            ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Forecast(days), null)]));

        Assert.Equal(ClusterNPlusOne.AtRiskTitle, alert.Title);
        Assert.Equal(expected, alert.Severity);
    }

    [Fact]
    public void A_date_beyond_thirty_days_is_not_an_alert()
    {
        Assert.Empty(ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Forecast(30.1), null)]));
    }

    [Fact]
    public void A_refusal_raises_nothing_of_its_own()
    {
        // "We cannot say" is not a problem with the cluster. It is shown on
        // the cluster's page instead.
        Assert.Empty(ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Refusal(), null)]));
    }

    [Fact]
    public void A_resource_with_no_demand_reading_raises_nothing_for_that_resource()
    {
        // At least one host's usage could not be read this cycle: silence
        // rather than a guess dressed up as a verdict.
        Assert.Empty(ClusterNPlusOne.Evaluate([(State(cpu: null, memory: null), null, null)]));
    }

    [Fact]
    public void Cpu_and_memory_are_independent_verdicts()
    {
        var alerts = ClusterNPlusOne.Evaluate([(State(cpu: 1.9, memory: 1.9), Refusal(), Refusal())]);

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(ClusterNPlusOne.AlreadyFailsTitle, a.Title));
        Assert.NotEqual(alerts[0].Fingerprint, alerts[1].Fingerprint);
    }

    [Fact]
    public void The_at_risk_fingerprint_stays_put_as_the_date_moves()
    {
        // "Already fails" and "at risk" are different facts with different
        // titles and different fingerprints, the same choice
        // StoragePathRedundancy makes between a lost path and a dead device.
        // Within one title the fingerprint must not move as only the date does.
        var soon = Assert.Single(
            ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Forecast(25), null)]));
        var sooner = Assert.Single(
            ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Forecast(5), null)]));

        Assert.Equal(soon.Fingerprint, sooner.Fingerprint);
        Assert.NotEqual(soon.Description, sooner.Description);
    }

    [Fact]
    public void Two_clusters_are_two_alerts()
    {
        var other = State(cpu: 1.9, memory: null) with { Cluster = new EntityId("vc-1:cluster-2") };

        var alerts = ClusterNPlusOne.Evaluate(
            [(State(cpu: 1.9, memory: null), Refusal(), null), (other, Refusal(), null)]);

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_thresholds_are_policy()
    {
        var policy = ClusterNPlusOnePolicy.Default with { WarningWithin = TimeSpan.FromDays(60) };

        Assert.Single(
            ClusterNPlusOne.Evaluate([(State(cpu: 1.0, memory: null), Forecast(45), null)], policy));
    }

    // --- what is read: membership -------------------------------------------

    private static Entity HostEntity(EntityId id, ObservationState state = ObservationState.Active) => new()
    {
        Id = id,
        Kind = EntityKind.EsxiHost,
        DisplayName = id.Value,
        LastSeenUtc = T0,
        ObservationState = state,
    };

    private static Entity ClusterEntity(EntityId id, string name = "prod-cluster") => new()
    {
        Id = id,
        Kind = EntityKind.Cluster,
        DisplayName = name,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
    };

    private static Relationship PartOf(EntityId host, EntityId cluster) => new()
    {
        From = host,
        To = cluster,
        Kind = RelationshipKind.PartOf,
        ObservedAtUtc = T0,
    };

    [Fact]
    public void A_cluster_with_two_or_more_live_hosts_is_carried()
    {
        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster),
                [Host1] = HostEntity(Host1),
                [Host2] = HostEntity(Host2),
            },
            Relationships = [PartOf(Host1, Cluster), PartOf(Host2, Cluster)],
        };

        var byCluster = ClusterNPlusOne.HostsByLiveCluster(graph);

        var hosts = Assert.Single(byCluster).Value;
        Assert.Equal([Host1, Host2], hosts.OrderBy(h => h.Value));
    }

    [Fact]
    public void A_single_host_cluster_is_not_carried()
    {
        // N+1 asks what survives losing one host, which a one-host cluster
        // cannot be asked. Left silent, not reported as a false pass.
        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster),
                [Host1] = HostEntity(Host1),
            },
            Relationships = [PartOf(Host1, Cluster)],
        };

        Assert.Empty(ClusterNPlusOne.HostsByLiveCluster(graph));
    }

    [Fact]
    public void A_vanished_host_does_not_count_toward_membership()
    {
        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster),
                [Host1] = HostEntity(Host1),
                [Host2] = HostEntity(Host2, ObservationState.Vanished),
            },
            Relationships = [PartOf(Host1, Cluster), PartOf(Host2, Cluster)],
        };

        Assert.Empty(ClusterNPlusOne.HostsByLiveCluster(graph));
    }

    // --- what is read: current demand ---------------------------------------

    private static EntityGraph TwoHostGraph() => EntityGraph.Empty with
    {
        Entities = new Dictionary<EntityId, Entity>
        {
            [Cluster] = ClusterEntity(Cluster),
            [Host1] = HostEntity(Host1),
            [Host2] = HostEntity(Host2),
        },
        Relationships = [PartOf(Host1, Cluster), PartOf(Host2, Cluster)],
    };

    [Fact]
    public void Current_demand_sums_each_hosts_latest_percent_as_host_equivalents()
    {
        var series = new LatestSeries(new Dictionary<EntityId, double>
        {
            [Host1] = 60,
            [Host2] = 40,
        });

        var state = Assert.Single(ClusterNPlusOne.CurrentReadings(
            series, TwoHostGraph(), T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default));

        Assert.Equal(1.0, state.CpuDemandHosts);
        Assert.Equal(1.0, state.MemoryDemandHosts);
    }

    [Fact]
    public void A_host_with_no_readable_sample_makes_that_resources_demand_unknown()
    {
        var series = new LatestSeries(new Dictionary<EntityId, double> { [Host1] = 60 });

        var state = Assert.Single(ClusterNPlusOne.CurrentReadings(
            series, TwoHostGraph(), T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default));

        Assert.Null(state.CpuDemandHosts);
        Assert.Null(state.MemoryDemandHosts);
    }

    // --- the demand trend ----------------------------------------------------

    [Fact]
    public void The_aggregate_trend_sums_each_hosts_last_reading_per_bucket()
    {
        SeriesResult HistoryOf(double bucket0, double bucket1) => new()
        {
            Key = default,
            Resolution = SeriesResolution.OneHour,
            Exists = true,
            Points =
            [
                new AggregatedSample { StartUtc = T0.AddHours(-1), Min = 0, Max = bucket0, Sum = bucket0, Count = 1, Last = bucket0 },
                new AggregatedSample { StartUtc = T0, Min = 0, Max = bucket1, Sum = bucket1, Count = 1, Last = bucket1 },
            ],
        };

        var trend = ClusterNPlusOne.AggregateDemandTrend([HistoryOf(50, 60), HistoryOf(30, 20)]);

        Assert.Equal(
            [new TrendPoint(T0.AddHours(-1), 0.8), new TrendPoint(T0, 0.8)],
            trend);
    }

    [Fact]
    public void A_bucket_only_one_host_reported_is_not_treated_as_the_missing_hosts_zero()
    {
        SeriesResult Single(DateTimeOffset at, double value) => new()
        {
            Key = default,
            Resolution = SeriesResolution.OneHour,
            Exists = true,
            Points = [new AggregatedSample { StartUtc = at, Min = 0, Max = value, Sum = value, Count = 1, Last = value }],
        };

        var trend = ClusterNPlusOne.AggregateDemandTrend([Single(T0, 90), Single(T0.AddHours(1), 10)]);

        // Each bucket carries only the host that actually reported into it —
        // not one host's reading averaged down by a phantom second host.
        Assert.Equal([new TrendPoint(T0, 0.9), new TrendPoint(T0.AddHours(1), 0.1)], trend);
    }

    [Fact]
    public void The_history_query_reads_the_hourly_tier_over_the_lookback()
    {
        var query = ClusterNPlusOne.HostHistoryQuery(
            Host1, "cpu.usage.average", T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default);

        Assert.Equal(new SeriesKey(Host1, "cpu.usage.average", string.Empty), query.Key);
        Assert.Equal(T0.AddDays(-30), query.FromUtc);
        Assert.Equal(T0, query.ToUtc);
        Assert.Equal(SeriesResolution.OneHour, query.Resolution);
        Assert.Equal(720, query.MaxPoints);
    }

    [Fact]
    public void Zero_available_headroom_refuses_rather_than_throws()
    {
        var estimate = ClusterNPlusOne.EstimateDate(
            [new TrendPoint(T0, 0.5)], availableAfterFailoverHosts: 0, T0, ClusterNPlusOnePolicy.Default);

        var refusal = Assert.IsType<TimeToFullResult.Refusal>(estimate);
        Assert.Equal(TimeToFullRefusalReason.AlreadyFull, refusal.Reason);
    }

    // --- the adapter: guarded per cluster -------------------------------------

    [Fact]
    public void The_rule_reads_one_history_per_host_of_a_live_cluster_and_nothing_else()
    {
        var series = new CountingSeries();

        var context = new RuleContext
        {
            ReadGraph = () => TwoHostGraph(),
            NowUtc = T0,
            Options = MonitoringOptions.Default,
            Series = series,
            Events = new NoEvents(),
        };

        new ClusterNPlusOneRule().Evaluate(context);

        // One current-demand query per host per counter, plus one history
        // query per host per counter: 2 hosts * 2 counters * 2 = 8.
        Assert.Equal(8, series.Queries.Count);
        Assert.All(series.Queries, q => Assert.True(q.Key.Entity == Host1 || q.Key.Entity == Host2));
    }

    [Fact]
    public void A_cluster_whose_history_cannot_be_read_costs_only_that_cluster()
    {
        var other = new EntityId("vc-1:cluster-2");
        var otherHost1 = new EntityId("vc-1:host-4");
        var otherHost2 = new EntityId("vc-1:host-5");

        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster, "flaky-cluster"),
                [Host1] = HostEntity(Host1),
                [Host2] = HostEntity(Host2),
                [other] = ClusterEntity(other, "healthy-cluster"),
                [otherHost1] = HostEntity(otherHost1),
                [otherHost2] = HostEntity(otherHost2),
            },
            Relationships =
            [
                PartOf(Host1, Cluster), PartOf(Host2, Cluster),
                PartOf(otherHost1, other), PartOf(otherHost2, other),
            ],
        };

        var series = new FailingHostSeries(Host1);
        var unevaluated = new List<AlertFingerprint>();

        var states = ClusterNPlusOne.CurrentReadings(
            series, graph, T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default);

        var alerts = ClusterNPlusOne.EvaluateEach(
            states, series, T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default, unevaluated);

        var failure = Assert.Single(alerts, a => a.Title == ClusterNPlusOne.HistoryUnreadableTitle);
        Assert.Contains("'flaky-cluster'", failure.Description, StringComparison.Ordinal);
        Assert.Contains("TimeoutException", failure.Description, StringComparison.Ordinal);

        Assert.Equal(ClusterNPlusOne.Fingerprints(Cluster), unevaluated);
    }

    // --- caching: the estimate is kept while its input stands still ---------

    [Fact]
    public void The_same_trend_and_hour_is_estimated_once_per_cluster_and_resource()
    {
        var cache = new ClusterNPlusOneCache();
        IReadOnlyList<TrendPoint> trend = [new TrendPoint(T0.AddDays(-1), 0.5), new TrendPoint(T0, 0.6)];
        var key = ClusterNPlusOneCache.KeyOf(trend, 1.8, T0, ClusterNPlusOnePolicy.Default);

        var computations = 0;
        TimeToFullResult Compute()
        {
            computations++;
            return ClusterNPlusOne.EstimateDate(trend, 1.8, T0, ClusterNPlusOnePolicy.Default);
        }

        cache.GetOrAdd(Cluster, ClusterCapacityResource.Cpu, key, T0, Compute);
        cache.GetOrAdd(Cluster, ClusterCapacityResource.Cpu, key, T0, Compute);

        Assert.Equal(1, computations);
        Assert.Equal(1, cache.Computations);

        // A different resource on the same cluster is a different entry.
        cache.GetOrAdd(Cluster, ClusterCapacityResource.Memory, key, T0, Compute);
        Assert.Equal(2, computations);
    }

    private sealed class LatestSeries(IReadOnlyDictionary<EntityId, double> readings) : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query)
        {
            if (!readings.TryGetValue(query.Key.Entity, out var value))
            {
                return new SeriesResult { Key = query.Key, Resolution = SeriesResolution.Raw };
            }

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.Raw,
                Exists = true,
                Points = [new AggregatedSample { StartUtc = T0, Min = value, Max = value, Sum = value, Count = 1, Last = value }],
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    private sealed class CountingSeries : ISeriesReader
    {
        public List<SeriesQuery> Queries { get; } = [];

        public SeriesResult Query(SeriesQuery query)
        {
            Queries.Add(query);

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.Raw,
                Exists = true,
                Points = [new AggregatedSample { StartUtc = T0, Min = 10, Max = 10, Sum = 10, Count = 1, Last = 10 }],
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    /// <summary>Throws only for history queries (a lookback range) against one host.</summary>
    private sealed class FailingHostSeries(EntityId failingHost) : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query)
        {
            if (query.Key.Entity == failingHost && query.MaxPoints > 1)
            {
                throw new TimeoutException("statement timeout");
            }

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.Raw,
                Exists = true,
                Points = [new AggregatedSample { StartUtc = T0, Min = 10, Max = 10, Sum = 10, Count = 1, Last = 10 }],
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    private sealed class NoEvents : IEventReader
    {
        public IReadOnlyList<SourceEvent> OfTypes(
            IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }
}
