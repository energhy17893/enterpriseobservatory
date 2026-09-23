using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// An outage shorter than about an hour loses no data; beyond it, the loss is
/// reported — the same rule for a store outage as for a source outage (F5).
/// </summary>
/// <remarks>
/// <para>
/// The history lives at the source: vCenter keeps about an hour of 20 s
/// samples, so it is the second copy, and the store queue is only a
/// smoothing buffer. A row the queue drops is recorded as a collection gap
/// (H3's mechanism) and read again. These drive the real observation source,
/// the runner's slot and the store queue together, cycle by cycle, against a
/// database whose rows and gap record go down together.
/// </para>
/// <para>
/// "Full" is five rows per 20 s slot: two hosts and three virtual machines,
/// one series each.
/// </para>
/// </remarks>
public class StoreOutageRecoveryTests
{
    /// <summary>On the 20-second grid.</summary>
    private static readonly DateTimeOffset S0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const int SeriesPerSlot = 5;

    /// <summary>
    /// One cycle per 20 s slot, so every read lands on the same grid as the
    /// fake vCenter's samples and "a slot" means one thing.
    /// </summary>
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(20);

    private const int Cycles = 60;

    private const int LastUp = 14;

    /// <summary>Cycles 15 to 44: ten minutes down.</summary>
    private static bool DownAt(int cycle) => cycle is > LastUp and < 45;

    /// <summary>About three of an outage's reads (each goes two minutes back): the rest is dropped.</summary>
    private static readonly StoreQueueLimits SmallQueue =
        new() { BudgetBytes = 100L * StoreQueueLimits.MeasuredBytesPerRow };

    [Fact]
    public async Task A_ten_minute_store_outage_drops_from_the_queue_records_the_dropped_span_and_refills_every_slot()
    {
        var db = new Database();
        var process = new Process(db);

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            // The store comes back between the read and the drain of cycle 45:
            // that read still found it down, so no read-side gap was recorded,
            // and the drain's surviving batches move the marks past the drop.
            // Only the recorded dropped span can bring that stretch back.
            db.Down = DownAt(cycle) || cycle == 45;
            await process.ReadAsync(At(cycle));
            db.Down = DownAt(cycle);
            process.Drain();
        }

        var queue = process.Queue.Snapshot();
        Assert.True(queue.DroppedOverBudgetRows > 0, "The outage never overflowed the queue.");
        Assert.Equal(queue.DroppedRows, queue.RecordedAsGapRows);
        Assert.Equal(0, queue.CouldNotBeFilledRows);
        Assert.Equal(0, queue.PendingGapRows);

        // The dropped span, and nothing else: every read-side attempt to record
        // a gap met the database down.
        var gap = Assert.Single(db.Gaps("vc-1"));
        Assert.Equal(CollectionGapState.Filled, gap.State);
        AssertEverySlotIsFull(db, from: S0, to: At(Cycles - 1));
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_a_store_outage_opens_the_gap_from_the_last_accepted_mark_and_refills_every_slot()
    {
        var db = new Database();
        var process = new Process(db);

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            if (cycle == 30)
            {
                // Mid-outage: the queue, its pending spans and every learned
                // mark are gone with the process. The marks had only ever
                // moved to the last accepted row, so the store still says
                // where the history stops.
                process = new Process(db);
            }

            db.Down = DownAt(cycle);
            await process.ReadAsync(At(cycle));
            process.Drain();
        }

        var gaps = db.Gaps("vc-1");
        Assert.Contains(gaps, g => g.FromUtc == At(LastUp) && g.State == CollectionGapState.Filled);
        AssertEverySlotIsFull(db, from: S0, to: At(Cycles - 1));
    }

    private static DateTimeOffset At(int cycle) => S0 + (Cycle * cycle);

    private static void AssertEverySlotIsFull(Database db, DateTimeOffset from, DateTimeOffset to)
    {
        var bySlot = db.Rows
            .Where(key => key.Counter != CollectorSelfMetrics.ClockSkewCounter)
            .GroupBy(key => key.At)
            .ToDictionary(g => g.Key, g => g.Count());

        var missing = new List<string>();
        for (var slot = from; slot <= to; slot += TimeSpan.FromSeconds(20))
        {
            var count = bySlot.GetValueOrDefault(slot);
            if (count != SeriesPerSlot)
            {
                missing.Add($"{slot:HH:mm:ss}={count}");
            }
        }

        Assert.True(missing.Count == 0, $"Slots not full: {string.Join(", ", missing)}");
    }

    /// <summary>One process: its source, its runner slot and its store queue.</summary>
    private sealed class Process
    {
        private readonly VsphereCollectionGapTests.TimelineVcenter _api = new();
        private readonly Clock _clock = new();
        private readonly VsphereObservationSource _source;
        private readonly ObservationSourceSlot _slot;

        public Process(Database db)
        {
            _source = new VsphereObservationSource(_api, new Targets(), _clock);
            _slot = new ObservationSourceSlot("vc-1", db);
            Queue = new ObservationStoreQueue(db.Append, _clock, SmallQueue, db);
        }

        public ObservationStoreQueue Queue { get; }

        public async Task ReadAsync(DateTimeOffset serverNow)
        {
            _api.ServerNow = serverNow;
            _clock.UtcNow = serverNow;

            var batch = await _slot.ReadAsync(_source, CancellationToken.None);
            Queue.Enqueue(batch);
        }

        public void Drain()
        {
            foreach (var accepted in Queue.Drain().Accepted)
            {
                _slot.Accept(accepted);
            }
        }
    }

    /// <summary>Rows, one per series and sample time, and the gap record — down together.</summary>
    private sealed class Database : ICollectionGapStore
    {
        private readonly List<CollectionGap> _gaps = [];

        public bool Down { get; set; }

        public HashSet<(EntityId Entity, string Counter, string Instance, DateTimeOffset At)> Rows { get; } = [];

        public void Append(IReadOnlyList<Observation> rows)
        {
            ThrowIfDown();
            foreach (var row in rows)
            {
                Rows.Add((row.Entity, row.Value.CounterName, row.Value.Instance, row.SampledAtUtc));
            }
        }

        public IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities)
        {
            ThrowIfDown();
            return Rows
                .Where(r => entities.Contains(r.Entity))
                .GroupBy(r => r.Entity)
                .ToDictionary(g => g.Key, g => g.Max(r => r.At));
        }

        public IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId)
        {
            ThrowIfDown();
            return [.. Gaps(sourceInstanceId).Where(g => g.State == CollectionGapState.Open)];
        }

        public IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId) =>
            [.. _gaps.Where(g => g.SourceInstanceId == sourceInstanceId).OrderBy(g => g.FromUtc).ThenBy(g => g.Id)];

        public CollectionGap Open(CollectionGap gap)
        {
            ThrowIfDown();
            var opened = gap with { Id = _gaps.Count + 1 };
            _gaps.Add(opened);
            return opened;
        }

        public void Update(CollectionGap gap)
        {
            ThrowIfDown();
            var i = _gaps.FindIndex(g => g.Id == gap.Id);
            if (_gaps[i].State == CollectionGapState.Open && gap.FilledToUtc >= _gaps[i].FilledToUtc)
            {
                _gaps[i] = gap;
            }
        }

        public IReadOnlyDictionary<CollectionGapState, int> CountsByState() =>
            _gaps.GroupBy(g => g.State).ToDictionary(g => g.Key, g => g.Count());

        private void ThrowIfDown()
        {
            if (Down)
            {
                throw new InvalidOperationException("The database is not there.");
            }
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Targets : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new()
        {
            Hosts = ["host-1", "host-2"],
            VirtualMachines = ["vm-1", "vm-2", "vm-3"],
        };

        public EntityId? ResolveEntity(string moRef) => EntityId.For("vc-1", moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => null;
    }
}
