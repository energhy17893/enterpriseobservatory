using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The one shape every rule is run in, and the registry that lists them.
/// </summary>
/// <remarks>
/// The rules' own tests call their static <c>Evaluate</c> and do not see the
/// adapters. These pin what the adapters add: which scope each rule runs in,
/// the order, and the few inputs an adapter chooses rather than passes on.
/// </remarks>
public class AnalysisRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static RuleContext Context(
        IReadOnlyList<Observation>? observations = null,
        MonitoringOptions? options = null,
        Func<EntityGraph>? graph = null,
        IEventReader? events = null) => new()
        {
            Observations = observations ?? [],
            ReadGraph = graph ?? (() => EntityGraph.Empty),
            NowUtc = T0,
            Options = options ?? new MonitoringOptions(),
            Series = new NoSeries(),
            Events = events ?? new AskedEvents(),
        };

    [Fact]
    public void The_metric_rules_run_in_the_order_the_cycle_always_ran_them()
    {
        // Alert order is reconciliation's input order; it is kept, not chosen.
        Assert.Equal(
            [
                FaultCounters.RuleId,
                PeerOutliers.RuleId,
                CpuContention.RuleId,
                MemoryPressure.RuleId,
                StorageLayerSplit.RuleId,
                SharedVolumeLatency.RuleId,
                StorageLatencyBlindSpot.RuleId,
                DroppedPackets.RuleId,
                StorageNoisyNeighbour.RuleId,
            ],
            AnalysisRules.For(RuleScope.Metric).Select(r => r.RuleId));
    }

    [Fact]
    public void The_inventory_rules_run_in_order_with_coverage_last()
    {
        // Coverage goes last because it is the only thing that can say whether
        // the silence of everything above it was a verdict or a gap.
        Assert.Equal(
            [
                StoragePathRedundancy.RuleId,
                RemoteLogging.RuleId,
                ClusterHighAvailability.RuleId,
                EventAlerts.RuleId,
                DatastoreTimeToFull.RuleId,
                CollectionCoverage.RuleId,
            ],
            AnalysisRules.For(RuleScope.Inventory).Select(r => r.RuleId));
    }

    [Fact]
    public void Every_registered_rule_belongs_to_exactly_one_scope_and_has_its_own_id()
    {
        Assert.Equal(
            AnalysisRules.All.Count,
            AnalysisRules.For(RuleScope.Metric).Count + AnalysisRules.For(RuleScope.Inventory).Count);

        Assert.Equal(
            AnalysisRules.All.Count,
            AnalysisRules.All.Select(r => r.RuleId).Distinct(StringComparer.Ordinal).Count());
    }

    public static TheoryData<string> RuleIds() => [.. AnalysisRules.All.Select(r => r.RuleId)];

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rule_refuses_a_missing_context(string ruleId)
    {
        var rule = AnalysisRules.All.Single(r => r.RuleId == ruleId);

        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!));
    }

    [Fact]
    public void A_rule_that_needs_no_graph_does_not_read_one()
    {
        // The graph is read inside each rule's guard, as it was when the cycle
        // called the store inline: a store that throws costs the rules that
        // asked for a graph, not every rule of the scope.
        var context = Context(graph: () => throw new InvalidOperationException("store down"));

        Assert.Empty(new FaultCountersRule().Evaluate(context));
        Assert.Throws<InvalidOperationException>(() => new CpuContentionRule().Evaluate(context));
    }

    [Fact]
    public void The_shared_volume_rule_is_held_to_the_peer_rules_floor_not_its_own_copy()
    {
        // The two rules are exclusive by recomputing each other's test. The
        // adapter forces the peer policy so the copies cannot drift apart.
        List<Observation> unanimous =
        [
            .. Enumerable.Range(1, 5).Select(i => Latency($"esx0{i}", 20)),
            Load(100),
        ];

        var raised = new MonitoringOptions
        {
            PeerOutliers = PeerOutlierPolicy.Default with { MinimumMilliseconds = 50d },
        };

        Assert.Single(SharedVolumeLatency.Evaluate(unanimous, raised.SharedVolumes));
        Assert.Empty(new SharedVolumeLatencyRule().Evaluate(Context(unanimous, raised)));
    }

    [Fact]
    public void The_event_rule_reads_through_the_read_only_port_as_far_back_as_the_policy_reaches()
    {
        var events = new AskedEvents();

        Assert.Empty(new EventAlertsRule().Evaluate(Context(events: events)));

        var policy = EventAlertPolicy.Default;
        Assert.Equal(T0 - policy.Conditions.Max(c => c.TimeToLive), events.AskedSince);
    }

    [Fact]
    public void The_median_of_an_even_count_is_the_mean_of_the_middle_two()
    {
        Assert.Equal(2.5d, Stats.Median([4d, 1d, 3d, 2d]));
    }

    [Fact]
    public void The_median_of_an_odd_count_is_the_middle_value_whatever_the_input_order()
    {
        Assert.Equal(3d, Stats.Median([5d, 1d, 3d]));
    }

    [Fact]
    public void The_median_of_nothing_is_zero()
    {
        Assert.Equal(0d, Stats.Median([]));
    }

    [Fact]
    public void The_peer_threshold_floors_its_baseline_before_multiplying()
    {
        Assert.Equal(4d, Stats.FlooredMultiple(4d, 0d, 1d));
        Assert.Equal(12d, Stats.FlooredMultiple(4d, 3d, 1d));
    }

    private static Observation Latency(string host, double ms) =>
        Reading(host, "datastore.totalReadLatency.average", ms, "millisecond");

    private static Observation Load(double operations) =>
        Reading("esx01", "datastore.numberReadAveraged.average", operations, "number");

    private static Observation Reading(string host, string counter, double raw, string unit) => new()
    {
        Entity = new EntityId("vc-1:ds-prod"),
        Source = "vc-1",
        SampledAtUtc = T0,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = raw,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = unit,
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    private sealed class AskedEvents : IEventReader
    {
        public DateTimeOffset? AskedSince { get; private set; }

        public IReadOnlyList<SourceEvent> OfTypes(
            IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc)
        {
            AskedSince = createdSinceUtc;

            return [];
        }
    }

    private sealed class NoSeries : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query) => new()
        {
            Key = query.Key,
            Resolution = SeriesResolution.FiveMinutes,
        };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }
}
