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

    // --- what is read: membership -------------------------------------------

    private static Entity HostEntity(
        EntityId id,
        ObservationState state = ObservationState.Active,
        HealthState health = HealthState.Healthy) => new()
    {
        Id = id,
        Kind = EntityKind.EsxiHost,
        DisplayName = id.Value,
        LastSeenUtc = T0,
        ObservationState = state,
        Health = health,
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

    [Fact]
    public void A_host_in_maintenance_does_not_count_toward_membership()
    {
        // In maintenance is deliberately out of service. It is not the
        // failover headroom a surviving-hosts calculation may spend.
        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster),
                [Host1] = HostEntity(Host1),
                [Host2] = HostEntity(Host2, ObservationState.InMaintenance),
            },
            Relationships = [PartOf(Host1, Cluster), PartOf(Host2, Cluster)],
        };

        Assert.Empty(ClusterNPlusOne.HostsByLiveCluster(graph));
    }

    [Fact]
    public void A_disconnected_host_does_not_count_toward_membership()
    {
        // VsphereInventorySource reports a host vCenter cannot reach as
        // Health.Unknown rather than a distinct ObservationState -- see
        // IsLiveConnectedHost's remarks. A host in that state is not
        // capacity either.
        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Cluster] = ClusterEntity(Cluster),
                [Host1] = HostEntity(Host1),
                [Host2] = HostEntity(Host2, health: HealthState.Unknown),
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

    [Fact]
    public void A_sample_older_than_the_staleness_window_is_treated_as_missing()
    {
        // Default policy: 30s observation interval, stale after 3 -- 90s.
        // A host silent for ten minutes is not idle capacity, it is a host
        // that stopped reporting.
        var series = new AgedSeries(new Dictionary<EntityId, (double Value, TimeSpan Age)>
        {
            [Host1] = (60, TimeSpan.Zero),
            [Host2] = (40, TimeSpan.FromMinutes(10)),
        });

        var state = Assert.Single(ClusterNPlusOne.CurrentReadings(
            series, TwoHostGraph(), T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default));

        Assert.Null(state.CpuDemandHosts);
        Assert.Null(state.MemoryDemandHosts);
    }

    [Fact]
    public void A_sample_inside_the_staleness_window_still_counts()
    {
        var series = new AgedSeries(new Dictionary<EntityId, (double Value, TimeSpan Age)>
        {
            [Host1] = (60, TimeSpan.FromSeconds(45)),
            [Host2] = (40, TimeSpan.FromSeconds(60)),
        });

        var state = Assert.Single(ClusterNPlusOne.CurrentReadings(
            series, TwoHostGraph(), T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default));

        Assert.Equal(1.0, state.CpuDemandHosts);
        Assert.Equal(1.0, state.MemoryDemandHosts);
    }

    [Fact]
    public void The_staleness_window_is_policy()
    {
        var policy = ClusterNPlusOnePolicy.Default with { StaleAfterIntervals = 100 };
        var series = new AgedSeries(new Dictionary<EntityId, (double Value, TimeSpan Age)>
        {
            [Host1] = (60, TimeSpan.FromMinutes(10)),
            [Host2] = (40, TimeSpan.FromMinutes(10)),
        });

        var state = Assert.Single(ClusterNPlusOne.CurrentReadings(
            series, TwoHostGraph(), T0, policy, SeriesRetentionPolicy.Default));

        Assert.Equal(1.0, state.CpuDemandHosts);
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

    /// <summary>A reading each host answers with, aged by a fixed amount behind whatever "now" the query asks for.</summary>
    private sealed class AgedSeries(IReadOnlyDictionary<EntityId, (double Value, TimeSpan Age)> readings) : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query)
        {
            if (!readings.TryGetValue(query.Key.Entity, out var reading))
            {
                return new SeriesResult { Key = query.Key, Resolution = SeriesResolution.Raw };
            }

            var nowUtc = query.ToUtc - TimeSpan.FromTicks(1);

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.Raw,
                Exists = true,
                Points =
                [
                    new AggregatedSample
                    {
                        StartUtc = nowUtc - reading.Age,
                        Min = reading.Value,
                        Max = reading.Value,
                        Sum = reading.Value,
                        Count = 1,
                        Last = reading.Value,
                    },
                ],
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
