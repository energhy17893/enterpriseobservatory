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

    public long BudgetBytes { get; init; } = DefaultBudgetMegabytes * BytesPerMegabyte;

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(DefaultMaxAgeMinutes);

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

    public long DroppedRows => DroppedOverBudgetRows + DroppedTooOldRows;
}

/// <summary>Where package D reads the store queue's numbers from.</summary>
public interface IStoreQueueMetrics
{
    StoreQueueSnapshot Snapshot();
}

/// <summary>What one drain of the queue did.</summary>
public sealed record StoreQueueDrain
{
    /// <summary>Batches the store accepted in this drain, oldest first, for the runner to accept in turn.</summary>
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
/// Idempotent by construction: a write that failed is retried as a whole,
/// and the store keeps one row per series and sample time, so a partial
/// write retried is not a duplicate.
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

    /// <summary>Queues one batch's rows — its current values and its earlier samples, kept or lost together.</summary>
    public void Enqueue(ObservationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        IReadOnlyList<Observation> rows = [.. batch.Observations, .. batch.Backfill];

        lock (_gate)
        {
            var now = _clock.UtcNow;
            _items.AddLast(new Item(rows, batch, now));
            _rows += rows.Count;
            _produced += rows.Count;

            DropTooOld(now);
            DropOverBudget(now);
        }
    }

    /// <summary>
    /// Writes what can be written: first the gaps for what was dropped, then
    /// the waiting batches, oldest first, until one fails.
    /// </summary>
    public StoreQueueDrain Drain()
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

            while (true)
            {
                Item item;
                lock (_gate)
                {
                    if (_items.First is not { } first)
                    {
                        break;
                    }

                    item = first.Value;
                }

                if (item.Rows.Count > 0)
                {
                    attempted = true;
                    try
                    {
                        _append(item.Rows);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // Justified: the store may throw anything, and
                    // the one outcome that must not happen is losing the rows
                    // it could not take — they stay queued.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        return Failed(ex.Message, accepted, attempted);
                    }
                }

                lock (_gate)
                {
                    // Dropped for age or budget while it was being written:
                    // the write landed, so it is not also a drop — but the
                    // drop was already counted and its gap recorded, which
                    // only costs a refill the store keeps once.
                    if (ReferenceEquals(_items.First?.Value, item))
                    {
                        _items.RemoveFirst();
                        _rows -= item.Rows.Count;
                        _accepted += item.Rows.Count;
                    }
                }

                accepted.Add(item.Batch);
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
            _droppedTooOld += first.Value.Rows.Count;
            Drop(first.Value, now);
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
            _droppedOverBudget += first.Value.Rows.Count;
            Drop(first.Value, now);
        }
    }

    // Under _gate.
    private void Drop(Item item, DateTimeOffset now)
    {
        _items.RemoveFirst();
        _rows -= item.Rows.Count;

        if (item.Rows.Count == 0)
        {
            return;
        }

        _lastDrop = now;

        if (_gaps is not null && item.Batch.Refetchable is { } span)
        {
            if (!_pending.TryGetValue(item.Batch.SourceInstanceId, out var list))
            {
                _pending[item.Batch.SourceInstanceId] = list = [];
            }

            list.Add((span, item.Rows.Count));
        }
        else
        {
            // Nothing can ask for it again: no gap record, or a source whose
            // history cannot be re-read.
            _couldNotBeFilled += item.Rows.Count;
        }
    }

    private sealed record Item(IReadOnlyList<Observation> Rows, ObservationBatch Batch, DateTimeOffset EnqueuedAtUtc);
}
