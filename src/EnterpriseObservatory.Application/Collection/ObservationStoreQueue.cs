using System.Diagnostics.CodeAnalysis;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>How much the store queue may hold, and for how long (F5, ADR-0025 §6).</summary>
/// <remarks>
/// <para>
/// Bytes first, age second: the budget is what bounds the process's memory,
/// the age is what bounds how stale a row may be when it is finally written.
/// No spill to disk — that is a separate decision the ADR does not make.
/// </para>
/// <para>
/// The queue is a smoothing buffer, not the durable copy. The durable second
/// copy is the platform's own history — vCenter keeps about an hour of 20 s
/// samples — and a drop is recorded as a collection gap so that history is
/// read again (<see cref="ObservationStoreQueue"/>). That is why the default
/// budget can hold minutes rather than the full half hour: an outage shorter
/// than about an hour loses no data; beyond it, the loss is reported.
/// </para>
/// </remarks>
public sealed record StoreQueueLimits
{
    /// <summary>
    /// Bytes one queued row costs, measured (QueuedRowSizeMeasurementTests).
    /// </summary>
    /// <remarks>
    /// 220.2 bytes a row with one 20 s slot per series per read — every
    /// counter-name and instance string paid for by one row, the dearest
    /// shape — and 175.3 with two. The queue accounts its bytes as rows ×
    /// this, an estimate rather than a count of every allocation, which is
    /// what makes it cheap enough to do per batch. Set to 256, ~16% above the
    /// dearer measurement, not 221: at 0.8 bytes of headroom the measurement
    /// test failed on ordinary GC variance, and an accounting constant only
    /// has to be safe (never under-count), not tight.
    /// </remarks>
    public const int MeasuredBytesPerRow = 256;

    /// <summary>The default budget, in MiB (planner's decision, 23 September 2026).</summary>
    public const int DefaultBudgetMegabytes = 64;

    /// <summary>The default age limit, in minutes (#80 §7).</summary>
    public const int DefaultMaxAgeMinutes = 30;

    public const int MinimumBudgetMegabytes = 16;

    public const int MaximumBudgetMegabytes = 1024;

    public const int MinimumMaxAgeMinutes = 1;

    /// <summary>
    /// Held under the platform's one-hour real-time retention: a row older
    /// than that could not be read again if it were dropped anyway.
    /// </summary>
    public const int MaximumMaxAgeMinutes = 60;

    public const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>The default rows per store write (F5b), measured — see <see cref="MaxRowsPerWrite"/>.</summary>
    public const int DefaultMaxRowsPerWrite = 10_000;

    public const int MinimumMaxRowsPerWrite = 1_000;

    /// <summary>The largest chunk measured under 5 s on every run (see <see cref="MaxRowsPerWrite"/>).</summary>
    public const int MaximumMaxRowsPerWrite = 50_000;

    public long BudgetBytes { get; init; } = DefaultBudgetMegabytes * BytesPerMegabyte;

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(DefaultMaxAgeMinutes);

    /// <summary>
    /// The most rows one store write carries (F5b): a backlog is written in
    /// chunks of this many, each accepted on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured 23 September 2026 on the test PostgreSQL 18.6 (port 5433,
    /// shared_buffers 128 MB): <c>sample</c> with 33.3 million rows (9,000
    /// series × 3,700 slots, 2.7 GB with its primary key), each chunk new rows
    /// for every series, written as <c>Append</c> writes them — temp table,
    /// binary COPY, <c>INSERT … ON CONFLICT DO NOTHING RETURNING</c>, commit —
    /// three runs per size, two passes. Whole transaction, worst run:
    /// 5,000 rows 0.99 s; 10,000 1.07 s (0.19–0.21 s warm); 20,000 0.43 s;
    /// 50,000 3.0 s; 100,000 6.2 s; 260,000 5.4 s. So 100,000 and more miss
    /// the 5 s target even here, and 50,000 is the ceiling.
    /// </para>
    /// <para>
    /// 10,000 (~22 s of this estate's 26,660 rows a minute) for production,
    /// which is slower than this server: the incident's 260k rows did not
    /// finish in 30 s, so it merged under 8,700 rows/s against ~50,000 here.
    /// A 10,000-row chunk stays under 5 s down to 2,000 rows/s — a quarter of
    /// the incident's bound — and a 260k backlog is 26 writes.
    /// </para>
    /// </remarks>
    public int MaxRowsPerWrite { get; init; } = DefaultMaxRowsPerWrite;

    public static StoreQueueLimits Default { get; } = new();
}

/// <summary>The store queue's numbers, for package D's surface.</summary>
public sealed record StoreQueueSnapshot
{
    /// <summary>Batches waiting.</summary>
    public int Length { get; init; }

    /// <summary>Rows waiting.</summary>
    public long Rows { get; init; }

    /// <summary>What the waiting rows are accounted at (rows × <see cref="StoreQueueLimits.MeasuredBytesPerRow"/>).</summary>
    public long Bytes { get; init; }

    public long BudgetBytes { get; init; }

    public TimeSpan MaxAge { get; init; }

    /// <summary>How long the oldest waiting batch has waited; null when nothing is waiting.</summary>
    public TimeSpan? OldestAge { get; init; }

    /// <summary>Rows handed to the queue since the process started.</summary>
    public long ProducedRows { get; init; }

    /// <summary>Rows the store accepted since the process started.</summary>
    public long AcceptedRows { get; init; }

    /// <summary>Rows dropped, oldest first, because the byte budget was exceeded.</summary>
    public long DroppedOverBudgetRows { get; init; }

    /// <summary>Rows dropped because they waited longer than the age limit.</summary>
    public long DroppedTooOldRows { get; init; }

    /// <summary>
    /// Current-state rows (the inventory's capacity readings) dropped — for
    /// age, budget, or because a newer reading of the same source replaced
    /// them. Never a gap: the platform keeps no history of current state to
    /// read again, and the next inventory read is the newer value anyway.
    /// </summary>
    public long DroppedCurrentStateRows { get; init; }

    /// <summary>Dropped rows whose span was recorded as a gap the source will read again.</summary>
    public long RecordedAsGapRows { get; init; }

    /// <summary>
    /// Dropped rows that cannot be read again: past what the platform keeps
    /// by the time the store came back, or from a source that cannot be asked.
    /// </summary>
    public long CouldNotBeFilledRows { get; init; }

    /// <summary>Dropped rows whose gap is waiting for the store to come back to be recorded.</summary>
    public long PendingGapRows { get; init; }

    /// <summary>When the queue last dropped anything; null if it never has.</summary>
    public DateTimeOffset? LastDropUtc { get; init; }

    /// <summary>Why the last write attempt failed, if it did.</summary>
    public string? LastFailure { get; init; }

    public long DroppedRows => DroppedOverBudgetRows + DroppedTooOldRows + DroppedCurrentStateRows;
}

/// <summary>Where package D reads the store queue's numbers from.</summary>
public interface IStoreQueueMetrics
{
    StoreQueueSnapshot Snapshot();
}

/// <summary>What one drain of the queue did.</summary>
public sealed record StoreQueueDrain
{
    /// <summary>
    /// Batches the store accepted in this drain, oldest first, for the runner
    /// to accept in turn. A batch is here only once its last chunk is stored;
    /// one whose earlier chunks landed and a later one failed is not, and its
    /// marks stay where they were.
    /// </summary>
    public IReadOnlyList<ObservationBatch> Accepted { get; init; } = [];

    /// <summary>Whether a write was attempted at all (a gap record or rows).</summary>
    public bool Attempted { get; init; }

    /// <summary>Why the drain stopped short, if it did; the rest stays queued.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// The bounded queue in front of the observation store (F5, ADR-0025 §6).
/// </summary>
/// <remarks>
/// <para>
/// A store failure no longer costs the cycle's samples: they wait here and
/// are written, oldest first, on the next drain that reaches the store. The
/// wait is bounded by bytes first and age second. Past the budget the
/// <em>oldest</em> batch is dropped (Telegraf's <c>metric_buffer_limit</c>);
/// past the age limit likewise (Zabbix's proxy buffer). Both are counted,
/// separately, and a drop raises an alert through the ordinary path.
/// </para>
/// <para>
/// A drop is not the end of the rows. Every batch carries the span of history
/// its source read (<see cref="ObservationBatch.Refetchable"/>), and a dropped
/// span is recorded as a collection gap per source — H3's mechanism, no new
/// table — so the source reads it again. The gap store is in the same
/// database that is down, so the span is kept here, in memory, and written
/// <em>first</em> when the store is back: before any surviving batch is
/// accepted, since accepting one moves that source's marks past the dropped
/// span and nothing else would then ask for it. A restart mid-outage loses
/// the in-memory span and loses nothing by it: marks only ever moved to the
/// last accepted row, so the next process seeds from the store and records
/// the gap from there.
/// </para>
/// <para>
/// Written in chunks of at most <see cref="StoreQueueLimits.MaxRowsPerWrite"/>
/// rows (F5b; Telegraf's <c>metric_batch_size</c>, Prometheus remote-write's
/// <c>max_samples_per_send</c>). Each stored chunk is kept; a failed one is
/// retried from where it stopped, never as the whole backlog. The backlog
/// after an outage used to go as one statement (23 September 2026: ~260k
/// rows into a 33-million-row table outran the command timeout, rolled back,
/// and the next attempt carried more). A batch counts as accepted — its
/// source's marks move — only once its last chunk is stored.
/// </para>
/// <para>
/// Idempotent by construction: the store keeps one row per series and sample
/// time, so a chunk retried after a failure that in fact landed is not a
/// duplicate.
/// </para>
/// <para>
/// Current state — the inventory's capacity readings — waits here too
/// (<see cref="EnqueueCurrentState"/>), but is dropped differently: no gap,
/// since the platform keeps no history of it to read again, and the newest
/// reading of a source replaces an older one still waiting.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "ADR-0025 §6 names it the store queue, and it is one: first in, first out, bounded.")]
public sealed class ObservationStoreQueue : IStoreQueueMetrics
{
    private readonly Action<IReadOnlyList<Observation>> _append;
    private readonly IClock _clock;
    private readonly ICollectionGapStore? _gaps;

    private readonly Lock _gate = new();
    private readonly Lock _drainGate = new();
    private readonly LinkedList<Item> _items = new();

    /// <summary>Dropped spans not yet recorded as gaps, per source.</summary>
    private readonly Dictionary<string, List<(RefetchableSpan Span, long Rows)>> _pending =
        new(StringComparer.Ordinal);

    private long _rows;
    private long _produced;
    private long _accepted;
    private long _droppedOverBudget;
    private long _droppedTooOld;
    private long _droppedCurrentState;
    private long _recordedAsGap;
    private long _couldNotBeFilled;
    private DateTimeOffset? _lastDrop;
    private string? _lastFailure;

    public ObservationStoreQueue(
        Action<IReadOnlyList<Observation>> append,
        IClock clock,
        StoreQueueLimits? limits = null,
        ICollectionGapStore? gaps = null)
    {
        _append = append ?? throw new ArgumentNullException(nameof(append));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Limits = limits ?? StoreQueueLimits.Default;
        _gaps = gaps;
    }

    public StoreQueueLimits Limits { get; }

    /// <summary>Queues one batch's rows — its current values and its earlier samples, as one item.</summary>
    public void Enqueue(ObservationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        Add(new Item([.. batch.Observations, .. batch.Backfill], batch, batch.SourceInstanceId, _clock.UtcNow));
    }

    /// <summary>
    /// Queues one source's current-state rows — the capacity readings its
    /// inventory read carried — replacing any of that source's still waiting.
    /// </summary>
    /// <remarks>
    /// Newest wins: an older reading still queued describes a state that no
    /// longer holds, and writing both would only cost the store a row the
    /// newer one already says. The replaced rows are counted as
    /// <see cref="StoreQueueSnapshot.DroppedCurrentStateRows"/>.
    /// </remarks>
    public void EnqueueCurrentState(string sourceInstanceId, IReadOnlyList<Observation> rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceInstanceId);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var now = _clock.UtcNow;
            for (var node = _items.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Batch is null &&
                    string.Equals(node.Value.Source, sourceInstanceId, StringComparison.Ordinal))
                {
                    _items.Remove(node);
                    Drop(node.Value, now);
                }

                node = next;
            }
        }

        Add(new Item([.. rows], Batch: null, sourceInstanceId, _clock.UtcNow));
    }

    private void Add(Item item)
    {
        lock (_gate)
        {
            _items.AddLast(item);
            _rows += item.Rows.Length;
            _produced += item.Rows.Length;

            DropTooOld(item.EnqueuedAtUtc);
            DropOverBudget(item.EnqueuedAtUtc);
        }
    }

    /// <summary>
    /// Writes what can be written: first the gaps for what was dropped, then
    /// the waiting items, oldest first, in chunks of at most
    /// <see cref="StoreQueueLimits.MaxRowsPerWrite"/> rows, until one fails.
    /// </summary>
    /// <param name="currentStateOnly">
    /// Only the current-state rows (<see cref="EnqueueCurrentState"/>): the
    /// inventory cycle's drain, which must not accept metric batches whose
    /// bookkeeping belongs to the metric cycle. Current state moves no marks,
    /// so writing it ahead of older metric batches reorders nothing that
    /// matters.
    /// </param>
    public StoreQueueDrain Drain(bool currentStateOnly = false)
    {
        lock (_drainGate)
        {
            List<(string Source, List<(RefetchableSpan Span, long Rows)> Spans)> pending;
            lock (_gate)
            {
                DropTooOld(_clock.UtcNow);
                pending = [.. _pending.Select(p => (p.Key, p.Value.ToList()))];
            }

            var attempted = false;

            // The dropped spans first. Accepting any surviving batch below moves
            // its source's marks past what was dropped.
            foreach (var (source, spans) in pending)
            {
                attempted = true;
                if (RecordGap(source, spans) is { } failure)
                {
                    return Failed(failure, [], attempted);
                }
            }

            var accepted = new List<ObservationBatch>();
            var maxRows = Limits.MaxRowsPerWrite;

            LinkedListNode<Item>? node;
            lock (_gate)
            {
                node = _items.First;
            }

            while (node is not null)
            {
                var item = node.Value;

                if (currentStateOnly && item.Batch is not null)
                {
                    lock (_gate)
                    {
                        node = node.List is null ? _items.First : node.Next;
                    }

                    continue;
                }

                // One chunk. Written is read under the gate; only this drain
                // (under _drainGate) moves it.
                int from;
                lock (_gate)
                {
                    from = item.Written;
                }

                var count = Math.Min(maxRows, item.Rows.Length - from);

                if (count > 0)
                {
                    attempted = true;
                    try
                    {
                        _append(new ArraySegment<Observation>(item.Rows, from, count));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // Justified: the store may throw anything, and
                    // the one outcome that must not happen is losing the rows
                    // it could not take — they stay queued, and the chunks
                    // already stored stay stored.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        return Failed(ex.Message, accepted, attempted);
                    }
                }

                var complete = from + count == item.Rows.Length;

                lock (_gate)
                {
                    if (node.List is null)
                    {
                        // Dropped for age, budget or a newer reading while
                        // this chunk was being written: the drop was already
                        // counted (and its gap recorded), so the chunk is not
                        // also counted accepted — which only costs a refill
                        // the store keeps once.
                        node = _items.First;
                    }
                    else
                    {
                        item.Written += count;
                        _rows -= count;
                        _accepted += count;

                        if (!complete)
                        {
                            continue;
                        }

                        var next = node.Next;
                        _items.Remove(node);
                        node = next;
                    }
                }

                // The whole batch is stored, so its source's marks may move —
                // also when the last chunk landed on an item dropped meanwhile.
                if (complete && item.Batch is not null)
                {
                    accepted.Add(item.Batch);
                }
            }

            if (attempted)
            {
                lock (_gate)
                {
                    _lastFailure = null;
                }
            }

            return new StoreQueueDrain { Accepted = accepted, Attempted = attempted };
        }
    }

    public StoreQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            return new StoreQueueSnapshot
            {
                Length = _items.Count,
                Rows = _rows,
                Bytes = _rows * StoreQueueLimits.MeasuredBytesPerRow,
                BudgetBytes = Limits.BudgetBytes,
                MaxAge = Limits.MaxAge,
                OldestAge = _items.First is { } first ? now - first.Value.EnqueuedAtUtc : null,
                ProducedRows = _produced,
                AcceptedRows = _accepted,
                DroppedOverBudgetRows = _droppedOverBudget,
                DroppedTooOldRows = _droppedTooOld,
                DroppedCurrentStateRows = _droppedCurrentState,
                RecordedAsGapRows = _recordedAsGap,
                CouldNotBeFilledRows = _couldNotBeFilled,
                PendingGapRows = _pending.Values.Sum(spans => spans.Sum(s => s.Rows)),
                LastDropUtc = _lastDrop,
                LastFailure = _lastFailure,
            };
        }
    }

    private StoreQueueDrain Failed(string failure, List<ObservationBatch> accepted, bool attempted)
    {
        lock (_gate)
        {
            _lastFailure = failure;
        }

        return new StoreQueueDrain { Accepted = accepted, Attempted = attempted, Failure = failure };
    }

    /// <summary>
    /// Records one source's dropped spans as a single gap, or as closed and
    /// unrecoverable when the platform no longer keeps any of it.
    /// </summary>
    /// <returns>Why it could not be recorded, or null.</returns>
    private string? RecordGap(string source, List<(RefetchableSpan Span, long Rows)> spans)
    {
        var now = _clock.UtcNow;
        var from = spans.Min(s => s.Span.FromExclusiveUtc);
        var to = spans.Max(s => s.Span.ToInclusiveUtc);
        var fillable = spans.Any(s => now < s.Span.RecoverableUntilUtc);

        // A gap that can never fill is not recorded as one that might: it is
        // closed on the spot, with everything in it given up.
        var gap = fillable
            ? new CollectionGap
            {
                SourceInstanceId = source,
                FromUtc = from,
                ToUtc = to,
                FilledToUtc = from,
                State = CollectionGapState.Open,
                OpenedAtUtc = now,
            }
            : new CollectionGap
            {
                SourceInstanceId = source,
                FromUtc = from,
                ToUtc = to,
                FilledToUtc = to,
                State = CollectionGapState.Unrecoverable,
                LostBeforeUtc = to,
                OpenedAtUtc = now,
                ClosedAtUtc = now,
            };

        try
        {
            _gaps!.Open(gap);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Justified: see Drain; the span stays pending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }

        lock (_gate)
        {
            foreach (var (span, rows) in spans)
            {
                // Per span: part of a gap can be past the platform's horizon
                // while the rest is not, and the fill will give that part up.
                if (now < span.RecoverableUntilUtc)
                {
                    _recordedAsGap += rows;
                }
                else
                {
                    _couldNotBeFilled += rows;
                }
            }

            if (_pending.TryGetValue(source, out var list))
            {
                list.RemoveAll(spans.Contains);
                if (list.Count == 0)
                {
                    _pending.Remove(source);
                }
            }
        }

        return null;
    }

    // Under _gate.
    private void DropTooOld(DateTimeOffset now)
    {
        while (_items.First is { } first && now - first.Value.EnqueuedAtUtc > Limits.MaxAge)
        {
            _items.RemoveFirst();
            _droppedTooOld += Drop(first.Value, now);
        }
    }

    /// <remarks>
    /// Never the newest batch, so the queue can exceed its budget by at most
    /// the one read just handed in — which is in memory as that read's result
    /// anyway. Dropping it would make a refill larger than the budget
    /// unwritable forever: dropped, recorded as a gap, read again as the same
    /// large refill, dropped again. Under _gate.
    /// </remarks>
    private void DropOverBudget(DateTimeOffset now)
    {
        while (_items.First is { } first && _items.Count > 1 &&
               _rows * StoreQueueLimits.MeasuredBytesPerRow > Limits.BudgetBytes)
        {
            _items.RemoveFirst();
            _droppedOverBudget += Drop(first.Value, now);
        }
    }

    /// <summary>Accounts for an item already taken off the list: what of it was not yet stored is dropped.</summary>
    /// <returns>
    /// The metric rows dropped, for the caller to count under its reason;
    /// current-state rows are counted here, apart, and return zero.
    /// </returns>
    /// <remarks>Under _gate.</remarks>
    private long Drop(Item item, DateTimeOffset now)
    {
        long rows = item.Rows.Length - item.Written;
        _rows -= rows;

        if (rows == 0)
        {
            return 0;
        }

        _lastDrop = now;

        if (item.Batch is null)
        {
            // Current state: no gap — nothing the platform could be asked for.
            _droppedCurrentState += rows;
            return 0;
        }

        if (_gaps is not null && item.Batch.Refetchable is { } span)
        {
            if (!_pending.TryGetValue(item.Source, out var list))
            {
                _pending[item.Source] = list = [];
            }

            list.Add((span, rows));
        }
        else
        {
            // Nothing can ask for it again: no gap record, or a source whose
            // history cannot be re-read.
            _couldNotBeFilled += rows;
        }

        return rows;
    }

    /// <summary>One queued batch, or one source's current state (<see cref="Batch"/> null).</summary>
    private sealed record Item(Observation[] Rows, ObservationBatch? Batch, string Source, DateTimeOffset EnqueuedAtUtc)
    {
        /// <summary>Rows already stored, from the front. Under _gate.</summary>
        public int Written { get; set; }
    }
}
