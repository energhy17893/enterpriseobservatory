using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Handover 3 end to end: a 30-minute service outage, the collector filling it
/// over a few cycles, and the store's late-sample marker (#54) refolding the
/// five-minute and hourly tiers the filled samples land in.
/// </summary>
public class GapFillRefoldTests : IDisposable
{
    /// <summary>12:10, so the gap (11:40, 12:08] crosses the hour.</summary>
    private static readonly DateTimeOffset S0 = new(2026, 9, 22, 12, 10, 0, TimeSpan.Zero);

    private static readonly EntityId Host = EntityId.For("vc-1", "host-1");

    private const string Counter = "cpu.usage.average";

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    [SkippableFact]
    public async Task A_30_minute_outage_is_filled_over_a_few_cycles_and_the_tiers_refold()
    {
        RequireDatabase();

        var samples = new PostgresObservationStore(_live.Database);
        var gaps = new PostgresCollectionGapStore(_live.Database);

        // History up to 11:40, when the service stopped: 11:00:00 .. 11:40:00.
        samples.Append(
        [
            .. Enumerable.Range(0, 121).Select(i => new Observation
            {
                Entity = Host,
                Value = OneTimeline.Value(),
                SampledAtUtc = S0.AddMinutes(-70).AddSeconds(20 * i),
                Source = "vc-1",
            }),
        ]);

        var clock = new MovableClock();
        var api = new OneTimeline();
        var source = new VsphereObservationSource(api, new OneHost(), clock);

        // The runner's half (F5): the slot keeps the gap record, and the
        // store queue writes the samples and says which batches were kept.
        var slot = new ObservationSourceSlot("vc-1", gaps);
        var queue = new ObservationStoreQueue(samples.Append, clock, gaps: gaps);

        async Task CycleAsync(DateTimeOffset at)
        {
            api.ServerNow = at;
            clock.UtcNow = at;

            // The live read and one ten-minute slice.
            using var budget = new CancellationTokenSource();
            api.Allow(budget, queries: 2);

            var batch = await slot.ReadAsync(source, budget.Token);

            queue.Enqueue(batch);
            foreach (var accepted in queue.Drain().Accepted)
            {
                slot.Accept(accepted);
            }

            samples.Compact(at, SeriesRetentionPolicy.Default);
        }

        // First cycle after the restart: current, and the gap recorded by the
        // runner after the read — filled from the next cycle on (F5).
        await CycleAsync(S0);

        var gap = Assert.Single(gaps.Gaps("vc-1"));
        Assert.Equal(S0.AddMinutes(-30), gap.FromUtc);
        Assert.Equal(S0.AddMinutes(-2), gap.ToUtc);
        Assert.Equal(S0, Raw(samples, S0.AddMinutes(-1), S0.AddSeconds(1)).Max(p => p.StartUtc));

        await CycleAsync(S0.AddSeconds(30));

        // The hour 11:00 was folded with what was there: history to 11:40:00 (121)
        // and the first slice (30).
        Assert.Equal(151, Hourly(samples, S0.AddMinutes(-70)).Count);

        await CycleAsync(S0.AddSeconds(60));
        await CycleAsync(S0.AddSeconds(90));

        gap = Assert.Single(gaps.Gaps("vc-1"));
        Assert.Equal(CollectionGapState.Filled, gap.State);

        // Every raw sample of the gap is there.
        Assert.Equal(84, Raw(samples, S0.AddMinutes(-30).AddSeconds(1), S0.AddMinutes(-2).AddSeconds(1)).Count);

        // The five-minute buckets the later slices landed in were built after
        // the watermark had passed them: refolded, whole.
        var fiveMinutes = samples.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, Counter, string.Empty),
            FromUtc = S0.AddMinutes(-20),
            ToUtc = S0.AddMinutes(-10),
            Resolution = SeriesResolution.FiveMinutes,
        }).Points;
        Assert.Equal([15, 15], fiveMinutes.Select(p => p.Count));

        // And the hour they belong to now counts all of it.
        Assert.Equal(180, Hourly(samples, S0.AddMinutes(-70)).Count);
    }

    private static IReadOnlyList<AggregatedSample> Raw(
        PostgresObservationStore store, DateTimeOffset from, DateTimeOffset to) =>
        store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, Counter, string.Empty),
            FromUtc = from,
            ToUtc = to,
            Resolution = SeriesResolution.Raw,
            MaxPoints = 5_000,
        }).Points;

    private static AggregatedSample Hourly(PostgresObservationStore store, DateTimeOffset hour) =>
        Assert.Single(store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, Counter, string.Empty),
            FromUtc = hour,
            ToUtc = hour.AddHours(1),
            Resolution = SeriesResolution.OneHour,
        }).Points);

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class OneHost : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new() { Hosts = ["host-1"] };

        public EntityId? ResolveEntity(string moRef) => EntityId.For("vc-1", moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => null;
    }

    /// <summary>One host sampling every 20 s and keeping an hour.</summary>
    private sealed class OneTimeline : IVsphereApi
    {
        private static readonly VsphereCounter Cpu = new()
        {
            Id = 1, Group = "cpu", Name = "usage", Rollup = RollupType.Average, Unit = "percent", Level = 1,
        };

        private (CancellationTokenSource Source, int Allowed)? _budget;
        private int _queries;

        public string InstanceId => "vc-1";

        public DateTimeOffset ServerNow { get; set; }

        public void Allow(CancellationTokenSource source, int queries)
        {
            _budget = (source, queries);
            _queries = 0;
        }

        public static CounterValue Value() => new()
        {
            CounterName = Counter,
            Raw = 10,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
        };

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>([Cpu]);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([Counter]);

        public Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(ServerNow);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Every read gives a window now.");

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
            IReadOnlyList<PerfQueryTarget> targets, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var horizon = ServerNow - TimeSpan.FromHours(1);

            IReadOnlyList<PerfEntitySamples> result =
            [
                .. targets.Select(target =>
                {
                    var times = new List<DateTimeOffset>();
                    for (var t = target.EndInclusiveUtc;
                         t > target.StartExclusiveUtc && t > horizon;
                         t -= TimeSpan.FromSeconds(20))
                    {
                        times.Add(t);
                    }

                    return new PerfEntitySamples
                    {
                        EntityMoRef = target.MoRef,
                        SampledAtUtc = times.Count > 0 ? times[0] : null,
                        Values = times.Count > 0 ? [Value()] : [],
                        Earlier = [.. times.Skip(1).Select(t => new PerfSampleSet { SampledAtUtc = t, Values = [Value()] })],
                    };
                }),
            ];

            if (_budget is { } budget && ++_queries >= budget.Allowed)
            {
                budget.Source.Cancel();
            }

            return Task.FromResult(result);
        }
    }
}
