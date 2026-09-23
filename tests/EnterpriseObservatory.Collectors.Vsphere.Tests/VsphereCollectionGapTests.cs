using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Handover 3 (T0.4 second half): the live read is always current, a service
/// outage is recorded at source level and filled oldest slice first with the
/// budget the live read leaves over, and what the host no longer keeps is
/// recorded as unrecoverable rather than silently dropped.
/// </summary>
public class VsphereCollectionGapTests
{
    /// <summary>On the 20-second grid, so sample times are exact.</summary>
    private static readonly DateTimeOffset S0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);

    private static readonly string[] Hosts = ["host-1", "host-2"];

    private static readonly string[] Vms = ["vm-1", "vm-2", "vm-3"];


    /// <remarks>
    /// F5 moved the gap record to the runner: the read reports the gap as
    /// data and the runner records it after the read, so the fill starts one
    /// cycle later than when the collector recorded it mid-read — 30 s against
    /// the host's one-hour real-time retention.
    /// </remarks>
    [Fact]
    public async Task After_a_30_minute_outage_the_first_cycle_is_current_and_records_the_gap_and_the_next_fill_it_oldest_first()
    {
        var fixture = new Fixture(lastStored: S0.AddMinutes(-30));

        // Cycle 1: budget for the live read (one query per type) and one slice.
        var first = await fixture.ReadAsync(S0, queriesAllowed: 4);

        // Current: every entity's newest sample is the server's now.
        foreach (var moRef in Hosts.Concat(Vms))
        {
            Assert.Equal(S0, first.Observations.Where(o => o.Entity == Id(moRef)).Max(o => o.SampledAtUtc));
        }

        // Recorded by the runner after the read; nothing filled yet.
        var gap = Assert.Single(fixture.Gaps.Gaps("vc-1"));
        Assert.Equal(S0.AddMinutes(-30), gap.FromUtc);
        Assert.Equal(S0.AddMinutes(-2), gap.ToUtc);
        Assert.Equal(S0.AddMinutes(-30), gap.FilledToUtc);
        Assert.Equal(CollectionGapState.Open, gap.State);

        // Cycle 2 fills the first slice, all of it, as backfill.
        var second = await fixture.ReadAsync(S0.AddSeconds(30), queriesAllowed: 4);

        Assert.Equal(S0.AddMinutes(-20), Assert.Single(fixture.Gaps.Gaps("vc-1")).FilledToUtc);
        var filled = second.Backfill.Where(o => o.Entity == Id("vm-1"))
            .Select(o => o.SampledAtUtc).ToHashSet();
        Assert.Equal(30, filled.Count(t => t > S0.AddMinutes(-30) && t <= S0.AddMinutes(-20)));

        // Cycles 3 and 4 fill the rest, oldest first, and open nothing new.
        await fixture.ReadAsync(S0.AddSeconds(60), queriesAllowed: 4);
        await fixture.ReadAsync(S0.AddSeconds(90), queriesAllowed: 4);

        gap = Assert.Single(fixture.Gaps.Gaps("vc-1"));
        Assert.Equal(CollectionGapState.Filled, gap.State);
        Assert.Equal(S0.AddMinutes(-2), gap.FilledToUtc);
        Assert.Null(gap.LostBeforeUtc);

        var fillStarts = fixture.Api.Windows
            .Where(w => w.Start < w.End - TimeSpan.FromMinutes(3))
            .Select(w => w.Start).Distinct().ToList();
        Assert.Equal(
            [S0.AddMinutes(-30), S0.AddMinutes(-20), S0.AddMinutes(-10)],
            fillStarts);

        // Never relying on maxSample: every fill window has both ends.
        Assert.All(fixture.Api.Windows, w => Assert.True(w.End > w.Start));
    }

    [Fact]
    public async Task The_live_read_starts_at_each_entity_mark_but_never_more_than_a_few_samples_back()
    {
        var fixture = new Fixture(lastStored: S0.AddMinutes(-1));
        fixture.Marks[Id("vm-1")] = S0.AddSeconds(-40);

        await fixture.ReadAsync(S0, queriesAllowed: 100);

        var live = fixture.Api.Targets.ToDictionary(t => t.MoRef);

        // vm-1's own mark, exclusive.
        Assert.Equal(S0.AddSeconds(-40), live["vm-1"].StartExclusiveUtc);

        // Everything else at most six samples back from the server's now.
        Assert.Equal(S0.AddMinutes(-1), live["host-1"].StartExclusiveUtc);
        Assert.All(live.Values, t => Assert.Equal(S0, t.EndInclusiveUtc));

        // Nothing older than the live window: no gap.
        Assert.Empty(fixture.Gaps.Gaps("vc-1"));
    }

    [Fact]
    public async Task An_outage_longer_than_host_retention_records_what_was_lost_and_fills_the_rest()
    {
        var fixture = new Fixture(lastStored: S0.AddMinutes(-90));

        var cycles = 0;
        while (fixture.Gaps.OpenGaps("vc-1").Count > 0 || cycles == 0)
        {
            await fixture.ReadAsync(S0.AddSeconds(30 * cycles), queriesAllowed: 4);
            Assert.True(++cycles < 20, "The gap never closed.");
        }

        var gap = Assert.Single(fixture.Gaps.Gaps("vc-1"));

        Assert.Equal(CollectionGapState.Unrecoverable, gap.State);
        Assert.Equal(S0.AddMinutes(-90), gap.FromUtc);
        Assert.Equal(S0.AddMinutes(-2), gap.ToUtc);
        Assert.Equal(S0.AddMinutes(-2), gap.FilledToUtc);

        // The host keeps an hour: the part before that is recorded, not dropped.
        // Judged by the second cycle, the first that fills (F5): the horizon
        // has moved on by that cycle's 30 s, the documented cost of recording
        // the gap after the read rather than during it.
        Assert.Equal(S0.AddSeconds(30).AddMinutes(-59), gap.LostBeforeUtc);

        // And nothing was asked for from before the horizon.
        Assert.All(fixture.Api.Windows, w => Assert.True(w.Start >= S0.AddMinutes(-59)));
    }

    [Fact]
    public async Task Marks_and_fill_progress_move_only_once_the_batch_is_stored()
    {
        var fixture = new Fixture(lastStored: S0.AddMinutes(-30));

        await fixture.ReadAsync(S0, queriesAllowed: 4);

        // The first slice is read, but the batch is never accepted.
        await fixture.ReadAsync(S0.AddSeconds(30), queriesAllowed: 4, store: false);

        // Not counted as filled.
        Assert.Equal(S0.AddMinutes(-30), Assert.Single(fixture.Gaps.Gaps("vc-1")).FilledToUtc);

        await fixture.ReadAsync(S0.AddSeconds(60), queriesAllowed: 4);

        // The same slice again, now kept.
        Assert.Equal(S0.AddMinutes(-20), Assert.Single(fixture.Gaps.Gaps("vc-1")).FilledToUtc);
        Assert.Equal(
            2,
            fixture.Api.Windows.Count(w => w.Start == S0.AddMinutes(-30) && w.End == S0.AddMinutes(-20)) /
                (Hosts.Length + Vms.Length));

        // And the marks did not move past the unkept live read: the next live
        // read starts where the last kept one ended.
        Assert.All(fixture.Api.Targets, t => Assert.Equal(S0, t.StartExclusiveUtc));
    }

    [Fact]
    public async Task Clock_skew_is_recorded_once_a_cycle_on_the_vcenter()
    {
        // F6: clock skew is the runner's self-metric now (ObservationBatch.
        // ClockSkewSeconds, filled by ObservationSourceSlot from the
        // channel's own server-time probe), not an Observation the collector
        // adds to Backfill under CollectorSelfMetrics.ClockSkewCounter -- see
        // that constant's remarks for why the name is still kept around.
        var fixture = new Fixture(lastStored: S0.AddMinutes(-1));

        var batch = await fixture.ReadAsync(S0, queriesAllowed: 100);

        Assert.Equal(Skew.TotalSeconds, batch.ClockSkewSeconds);
        Assert.DoesNotContain(
            batch.Backfill, o => o.Value.CounterName == CollectorSelfMetrics.ClockSkewCounter);
    }

    [Fact]
    public async Task A_fill_that_runs_out_of_budget_keeps_the_live_data()
    {
        var fixture = new Fixture(lastStored: S0.AddMinutes(-30));

        await fixture.ReadAsync(S0, queriesAllowed: 100);

        // Budget for the live read and half a slice.
        var batch = await fixture.ReadAsync(S0.AddSeconds(30), queriesAllowed: 3);

        Assert.Equal(Hosts.Length + Vms.Length, batch.Observations.Select(o => o.Entity).Distinct().Count());
        Assert.Equal(S0.AddMinutes(-30), Assert.Single(fixture.Gaps.Gaps("vc-1")).FilledToUtc);
    }

    private static EntityId Id(string moRef) => EntityId.For("vc-1", moRef);

    private sealed class Fixture
    {
        public Fixture(DateTimeOffset lastStored)
        {
            foreach (var moRef in Hosts.Concat(Vms))
            {
                Marks[Id(moRef)] = lastStored;
            }

            Gaps = new InMemoryGaps(Marks);
            Source = new VsphereObservationSource(Api, new Targets(), Clock);
            Slot = new ObservationSourceSlot("vc-1", Gaps, Clock);
        }

        /// <summary>The runner's slot: the source's learned state and its gap record (F5).</summary>
        public ObservationSourceSlot Slot { get; }

        public Dictionary<EntityId, DateTimeOffset> Marks { get; } = [];

        public TimelineVcenter Api { get; } = new();

        public MovableClock Clock { get; } = new();

        public InMemoryGaps Gaps { get; }

        public VsphereObservationSource Source { get; }

        public async Task<ObservationBatch> ReadAsync(DateTimeOffset serverNow, int queriesAllowed, bool store = true)
        {
            Api.ServerNow = serverNow;
            Clock.UtcNow = serverNow - Skew;

            using var budget = new CancellationTokenSource();
            Api.Allow(budget, queriesAllowed);

            var batch = await Slot.ReadAsync(Source, budget.Token);

            if (store)
            {
                Slot.Accept(batch);
            }

            return batch;
        }
    }

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Targets : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new() { Hosts = Hosts, VirtualMachines = Vms };

        public EntityId? ResolveEntity(string moRef) => Id(moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => null;
    }

    internal sealed class InMemoryGaps(Dictionary<EntityId, DateTimeOffset> marks) : ICollectionGapStore
    {
        private readonly List<CollectionGap> _gaps = [];

        public IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities) =>
            entities.Where(marks.ContainsKey).ToDictionary(e => e, e => marks[e]);

        public IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId) =>
            [.. Gaps(sourceInstanceId).Where(g => g.State == CollectionGapState.Open)];

        public IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId) =>
            [.. _gaps.Where(g => g.SourceInstanceId == sourceInstanceId).OrderBy(g => g.FromUtc).ThenBy(g => g.Id)];

        public CollectionGap Open(CollectionGap gap)
        {
            var opened = gap with { Id = _gaps.Count + 1 };
            _gaps.Add(opened);
            return opened;
        }

        public void Update(CollectionGap gap)
        {
            var i = _gaps.FindIndex(g => g.Id == gap.Id);
            if (_gaps[i].State == CollectionGapState.Open && gap.FilledToUtc >= _gaps[i].FilledToUtc)
            {
                _gaps[i] = gap;
            }
        }

        public IReadOnlyDictionary<CollectionGapState, int> CountsByState() =>
            _gaps.GroupBy(g => g.State).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>A vCenter whose hosts sample every 20 s and keep an hour.</summary>
    internal sealed class TimelineVcenter : IVsphereApi, IVsphereChannelSelfMetrics
    {
        // F6: VsphereObservationSource now reads GetServerTimeAsync through
        // this channel interface, not IVsphereApi.
        public int SessionsHeld => 1;

        public string InstanceId => "vc-1";

        public DateTimeOffset ServerNow { get; set; }

        private (CancellationTokenSource Source, int Allowed)? _budget;

        private int _queries;

        /// <summary>Cancels the read's token once this many queries have answered.</summary>
        public void Allow(CancellationTokenSource source, int queries)
        {
            _budget = (source, queries);
            _queries = 0;
        }

        public List<(DateTimeOffset Start, DateTimeOffset End)> Windows { get; } = [];

        /// <summary>The targets of the most recent read's first queries, per entity.</summary>
        public List<PerfQueryTarget> Targets { get; } = [];

        private static readonly VsphereCounter HostCpu = new()
        {
            Id = 1, Group = "cpu", Name = "usage", Rollup = RollupType.Average, Unit = "percent", Level = 1,
        };

        private static readonly VsphereCounter VmReady = new()
        {
            Id = 2, Group = "cpu", Name = "ready", Rollup = RollupType.Summation, Unit = "millisecond", Level = 1,
        };

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>([HostCpu, VmReady]);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) => Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([HostCpu.Key, VmReady.Key]);

        public Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(ServerNow);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, DateTimeOffset nowUtc, CancellationToken ct) =>
            throw new InvalidOperationException("Every read gives a window now.");

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
            IReadOnlyList<PerfQueryTarget> targets, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var target in targets)
            {
                Windows.Add((target.StartExclusiveUtc, target.EndInclusiveUtc));

                if (target.EndInclusiveUtc == ServerNow)
                {
                    Targets.RemoveAll(t => t.MoRef == target.MoRef);
                    Targets.Add(target);
                }
            }

            var horizon = ServerNow - TimeSpan.FromHours(1);

            IReadOnlyList<PerfEntitySamples> result =
            [
                .. targets.Select(target =>
                {
                    var times = new List<DateTimeOffset>();
                    for (var t = target.EndInclusiveUtc; t > target.StartExclusiveUtc && t > horizon; t -= TimeSpan.FromSeconds(20))
                    {
                        if (t <= ServerNow)
                        {
                            times.Add(t);
                        }
                    }

                    return new PerfEntitySamples
                    {
                        EntityMoRef = target.MoRef,
                        SampledAtUtc = times.Count > 0 ? times[0] : null,
                        Values = times.Count > 0 ? Values(counters) : [],
                        Earlier = [.. times.Skip(1).Select(t => new PerfSampleSet { SampledAtUtc = t, Values = Values(counters) })],
                    };
                }),
            ];

            if (_budget is { } budget && ++_queries >= budget.Allowed)
            {
                budget.Source.Cancel();
            }

            return Task.FromResult(result);
        }

        private static List<CounterValue> Values(IReadOnlyList<VsphereCounter> counters) =>
        [
            .. counters.Select(c => new CounterValue
            {
                CounterName = c.Key,
                Raw = 10,
                Rollup = c.Rollup,
                Interval = TimeSpan.FromSeconds(20),
                Unit = c.Unit,
            }),
        ];
    }
}
