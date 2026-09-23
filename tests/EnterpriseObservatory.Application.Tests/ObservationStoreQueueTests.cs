using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The bounded store queue (F5, ADR-0025 §6): bytes first, age second, the
/// oldest dropped, drops counted by reason and recorded as gaps the source
/// reads again.
/// </summary>
public class ObservationStoreQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    /// <summary>The database: rows and the gap record, down together, with one log of what was written in which order.</summary>
    private sealed class Database : ICollectionGapStore
    {
        public bool Down { get; set; }

        public List<string> Log { get; } = [];

        public List<Observation> Rows { get; } = [];

        public List<CollectionGap> Recorded { get; } = [];

        public void Append(IReadOnlyList<Observation> rows)
        {
            ThrowIfDown();
            Rows.AddRange(rows);
            Log.Add($"rows:{rows[0].Source}@{rows[0].SampledAtUtc:HH:mm:ss}");
        }

        public CollectionGap Open(CollectionGap gap)
        {
            ThrowIfDown();
            var opened = gap with { Id = Recorded.Count + 1 };
            Recorded.Add(opened);
            Log.Add($"gap:{gap.SourceInstanceId}");
            return opened;
        }

        public IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities) =>
            throw new NotSupportedException();

        public IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId) => throw new NotSupportedException();

        public IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId) => throw new NotSupportedException();

        public void Update(CollectionGap gap) => throw new NotSupportedException();

        public IReadOnlyDictionary<CollectionGapState, int> CountsByState() => throw new NotSupportedException();

        private void ThrowIfDown()
        {
            if (Down)
            {
                throw new InvalidOperationException("The database is not there.");
            }
        }
    }

    /// <summary>A batch of <paramref name="rows"/> rows read at <paramref name="at"/>, re-readable for an hour.</summary>
    private static ObservationBatch Batch(string source, DateTimeOffset at, int rows, DateTimeOffset? recoverableUntil = null) => new()
    {
        SourceInstanceId = source,
        ReadAtUtc = at,
        Observations =
        [
            .. Enumerable.Range(0, rows).Select(i => new Observation
            {
                Entity = EntityId.For(source, $"host-{i}"),
                Value = new CounterValue
                {
                    CounterName = "cpu.usage.average",
                    Raw = 1,
                    Rollup = RollupType.Average,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = "percent",
                },
                SampledAtUtc = at,
                Source = source,
            }),
        ],
        Refetchable = new RefetchableSpan
        {
            FromExclusiveUtc = at.AddMinutes(-2),
            ToInclusiveUtc = at,
            RecoverableUntilUtc = recoverableUntil ?? at.AddMinutes(59),
        },
    };

    private static StoreQueueLimits RowsBudget(int rows) =>
        new() { BudgetBytes = (long)rows * StoreQueueLimits.MeasuredBytesPerRow };

    [Fact]
    public void A_failed_write_keeps_the_rows_and_writes_them_first_next_time()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, gaps: db);

        queue.Enqueue(Batch("vc-1", T0, 10));
        var failed = queue.Drain();

        Assert.NotNull(failed.Failure);
        Assert.Empty(failed.Accepted);
        Assert.Equal(10, queue.Snapshot().Rows);

        db.Down = false;
        clock.UtcNow = T0.AddSeconds(30);
        queue.Enqueue(Batch("vc-1", clock.UtcNow, 10));
        var drained = queue.Drain();

        Assert.Null(drained.Failure);
        Assert.Equal([T0, T0.AddSeconds(30)], drained.Accepted.Select(b => b.ReadAtUtc));
        Assert.Equal(["rows:vc-1@12:00:00", "rows:vc-1@12:00:30"], db.Log);
        Assert.Equal(0, queue.Snapshot().Rows);
    }

    [Fact]
    public void Over_the_byte_budget_the_oldest_batch_is_dropped_and_counted()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, RowsBudget(25));

        for (var i = 0; i < 4; i++)
        {
            clock.UtcNow = T0.AddSeconds(30 * i);
            queue.Enqueue(Batch("vc-1", clock.UtcNow, 10));
        }

        var snapshot = queue.Snapshot();

        Assert.Equal(2, snapshot.Length);
        Assert.Equal(20, snapshot.Rows);
        Assert.Equal(20 * StoreQueueLimits.MeasuredBytesPerRow, snapshot.Bytes);
        Assert.Equal(20, snapshot.DroppedOverBudgetRows);
        Assert.Equal(0, snapshot.DroppedTooOldRows);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.OldestAge);
        Assert.Equal(snapshot.ProducedRows, snapshot.AcceptedRows + snapshot.DroppedRows + snapshot.Rows);
    }

    /// <summary>
    /// A refill after an outage can be larger than the whole budget. Dropped,
    /// it would be recorded as a gap, read again as the same refill and
    /// dropped again, for ever; kept, it is written as soon as the store is up.
    /// </summary>
    [Fact]
    public void A_batch_larger_than_the_budget_is_kept_alone_rather_than_dropped()
    {
        var clock = new Clock();
        var db = new Database();
        var queue = new ObservationStoreQueue(db.Append, clock, RowsBudget(25), db);

        queue.Enqueue(Batch("vc-1", T0, 10));
        queue.Enqueue(Batch("vc-1", T0.AddSeconds(30), 100));

        var snapshot = queue.Snapshot();
        Assert.Equal(1, snapshot.Length);
        Assert.Equal(100, snapshot.Rows);
        Assert.Equal(10, snapshot.DroppedOverBudgetRows);

        var drained = queue.Drain();
        Assert.Equal(T0.AddSeconds(30), Assert.Single(drained.Accepted).ReadAtUtc);
    }

    [Fact]
    public void Past_the_age_limit_a_batch_is_dropped_and_counted_apart_from_the_budget()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, new StoreQueueLimits { MaxAge = TimeSpan.FromMinutes(30) });

        queue.Enqueue(Batch("vc-1", T0, 10));
        clock.UtcNow = T0.AddMinutes(31);
        queue.Drain();

        var snapshot = queue.Snapshot();
        Assert.Equal(10, snapshot.DroppedTooOldRows);
        Assert.Equal(0, snapshot.DroppedOverBudgetRows);
        Assert.Equal(T0.AddMinutes(31), snapshot.LastDropUtc);
    }

    /// <summary>
    /// The order that matters: accepting a surviving batch moves its source's
    /// marks past what was dropped, so the dropped span must be on record
    /// first — and the gap store is in the same database that was down.
    /// </summary>
    [Fact]
    public void A_dropped_span_is_recorded_as_a_gap_before_any_surviving_row_is_written()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, RowsBudget(25), db);

        for (var i = 0; i < 4; i++)
        {
            clock.UtcNow = T0.AddSeconds(30 * i);
            queue.Enqueue(Batch("vc-1", clock.UtcNow, 10));
        }

        // Down: the span waits in memory.
        Assert.NotNull(queue.Drain().Failure);
        Assert.Equal(20, queue.Snapshot().PendingGapRows);

        db.Down = false;
        var drained = queue.Drain();

        Assert.Equal("gap:vc-1", db.Log[0]);
        Assert.Equal(["gap:vc-1", "rows:vc-1@12:01:00", "rows:vc-1@12:01:30"], db.Log);
        Assert.Equal(2, drained.Accepted.Count);

        // The two dropped batches, one span: from the first one's start to the second one's end.
        var gap = Assert.Single(db.Recorded);
        Assert.Equal(CollectionGapState.Open, gap.State);
        Assert.Equal(T0.AddMinutes(-2), gap.FromUtc);
        Assert.Equal(T0.AddSeconds(30), gap.ToUtc);
        Assert.Equal(gap.FromUtc, gap.FilledToUtc);

        var snapshot = queue.Snapshot();
        Assert.Equal(20, snapshot.RecordedAsGapRows);
        Assert.Equal(0, snapshot.PendingGapRows);
        Assert.Equal(0, snapshot.CouldNotBeFilledRows);
    }

    [Fact]
    public void A_drop_the_source_no_longer_keeps_is_reported_as_could_not_be_filled_not_recorded_as_fillable()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(
            db.Append, clock, RowsBudget(15) with { MaxAge = TimeSpan.FromHours(2) }, db);

        // Re-readable for ten minutes only, and the store is back after an hour.
        queue.Enqueue(Batch("vc-1", T0, 10, recoverableUntil: T0.AddMinutes(10)));
        clock.UtcNow = T0.AddSeconds(30);
        queue.Enqueue(Batch("vc-1", clock.UtcNow, 10));

        clock.UtcNow = T0.AddHours(1);
        db.Down = false;
        queue.Drain();

        var gap = Assert.Single(db.Recorded);
        Assert.Equal(CollectionGapState.Unrecoverable, gap.State);
        Assert.Equal(gap.ToUtc, gap.LostBeforeUtc);
        Assert.Equal(gap.ToUtc, gap.FilledToUtc);
        Assert.NotNull(gap.ClosedAtUtc);

        var snapshot = queue.Snapshot();
        Assert.Equal(10, snapshot.CouldNotBeFilledRows);
        Assert.Equal(0, snapshot.RecordedAsGapRows);
    }

    [Fact]
    public void Dropped_spans_are_recorded_per_source()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, RowsBudget(10), db);

        queue.Enqueue(Batch("vc-1", T0, 10));
        queue.Enqueue(Batch("vc-2", T0, 10));
        queue.Enqueue(Batch("vc-1", T0.AddSeconds(30), 10));

        db.Down = false;
        queue.Drain();

        Assert.Equal(["vc-1", "vc-2"], db.Recorded.Select(g => g.SourceInstanceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_a_gap_record_a_drop_is_counted_as_could_not_be_filled()
    {
        var clock = new Clock();
        var db = new Database { Down = true };
        var queue = new ObservationStoreQueue(db.Append, clock, RowsBudget(10));

        queue.Enqueue(Batch("vc-1", T0, 10));
        queue.Enqueue(Batch("vc-1", T0.AddSeconds(30), 10));

        Assert.Equal(10, queue.Snapshot().CouldNotBeFilledRows);
    }
}
