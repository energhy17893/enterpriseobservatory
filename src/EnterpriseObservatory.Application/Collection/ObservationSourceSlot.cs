using System.Collections.Concurrent;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>What the runner hands one observation read (F5, ADR-0025 §4).</summary>
/// <remarks>
/// Everything in here was read or decided by the runner before the read
/// started. The collector reads its source and nothing else: it never holds
/// the product's store (ADR-0005 §3), and what it wants recorded comes back
/// as data on the batch.
/// </remarks>
public sealed class ObservationReadContext
{
    public ObservationReadContext(SourceState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>The source's learned state, held by the runner.</summary>
    public SourceState State { get; }

    /// <summary>Whether the runner keeps a source-level gap record for this source at all.</summary>
    /// <remarks>
    /// False without a gap store: the live read still starts at each entity's
    /// mark, but an outage is neither recorded nor filled.
    /// </remarks>
    public bool KeepsGapRecord { get; init; }

    /// <summary>
    /// The store's newest sample time per entity, for seeding the marks — set
    /// on the one read after which the runner considers the source seeded.
    /// </summary>
    /// <remarks>
    /// Read by the runner from the entities the source named
    /// (<see cref="IObservationSource.EntitiesWithMarks"/>), once per source
    /// state: the store's newest timestamp is the mark, so a restart does not
    /// forget where the history stops. Null on every other read.
    /// </remarks>
    public IReadOnlyDictionary<EntityId, DateTimeOffset>? StoredMarks { get; init; }

    /// <summary>The source's open gaps, oldest first, as the runner read them before this read.</summary>
    public IReadOnlyList<CollectionGap> OpenGaps { get; init; } = [];

    /// <summary>
    /// How far a recorded gap already accounts for the source, if one does.
    /// </summary>
    /// <remarks>
    /// Moved when the runner records a gap the read asked for
    /// (<see cref="ObservationBatch.GapToOpen"/>), so the next read does not
    /// ask for the same gap again even if the samples of the read that found
    /// it are never stored.
    /// </remarks>
    public DateTimeOffset? AccountedTo { get; init; }
}

/// <summary>
/// The runner's slot for one observation source: its learned state, its gap
/// record, and the order in which both change.
/// </summary>
/// <remarks>
/// <para>
/// F5 (ADR-0025 §4 and §6). Every store call the vSphere collector used to
/// make on its own — seeding the high-water marks, reading and recording
/// gaps, writing fill progress — is made here instead, before or after the
/// read, never from inside it:
/// </para>
/// <list type="bullet">
/// <item>Before: pending advances are applied, the marks are seeded once, and
/// the open gaps are read.</item>
/// <item>After: a gap the read found is recorded.</item>
/// <item>On acceptance (<see cref="Accept"/>): the read's fill progress is
/// written and its advance queued for the next read.</item>
/// </list>
/// <para>
/// A gap recorded after a read is filled from the next read on — one cycle
/// later than when the collector recorded it mid-read. Against the host's
/// one-hour real-time retention that is 30 seconds of a sixty-minute budget.
/// </para>
/// <para>
/// A store failure here costs the gap record and not the read: it becomes a
/// <see cref="CollectionFailure"/> on the batch, which the health record
/// shows, exactly as it did when the collector made the calls.
/// </para>
/// </remarks>
public sealed class ObservationSourceSlot
{
    private readonly string _instanceId;
    private readonly ICollectionGapStore? _gaps;

    /// <summary>
    /// Advances of accepted batches, waiting for the source's next read slot.
    /// </summary>
    /// <remarks>
    /// Concurrent because acceptance happens on the cycle's thread after the
    /// store write, while an abandoned read may still hold the slot. The
    /// advance itself is applied only at the start of a read, where nothing
    /// else can be touching the state.
    /// </remarks>
    private readonly ConcurrentQueue<IStateAdvance> _accepted = new();

    // Touched only inside the slot (see SourceState's remarks).
    private bool _seeded;
    private DateTimeOffset? _accountedTo;

    public ObservationSourceSlot(string instanceId, ICollectionGapStore? gaps = null)
    {
        _instanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
        _gaps = gaps;
    }

    /// <summary>The source's learned state.</summary>
    public SourceState State { get; } = new();

    /// <summary>Reads the source once, doing the runner's half of the bookkeeping around it.</summary>
    public async Task<ObservationBatch> ReadAsync(IObservationSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (_accepted.TryDequeue(out var advance))
        {
            advance.Apply(State);
        }

        var failures = new List<CollectionFailure>();
        var context = new ObservationReadContext(State)
        {
            KeepsGapRecord = _gaps is not null,
            StoredMarks = _gaps is null ? null : Seed(source, failures),
            OpenGaps = _gaps is null ? [] : OpenGaps(failures),
            AccountedTo = _accountedTo,
        };

        var batch = await source.ReadAsync(context, cancellationToken).ConfigureAwait(false);

        if (_gaps is not null && batch.GapToOpen is { } gap)
        {
            try
            {
                var opened = _gaps.Open(gap);

                // Recorded, so not recorded again: the next read's source mark
                // is at least here even if this read's samples are never stored.
                if (_accountedTo is not { } current || opened.ToUtc > current)
                {
                    _accountedTo = opened.ToUtc;
                }
            }
#pragma warning disable CA1031 // Justified: the gap record is bookkeeping; a
            // store that cannot answer must cost the record, not the read.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(GapRecordFailed("record a collection gap", ex));
            }
        }

        return batch with
        {
            Failures = failures.Count == 0 ? batch.Failures : [.. failures, .. batch.Failures],
            Slot = this,
        };
    }

    /// <summary>
    /// What happens once the batch this slot's read produced is safely in the
    /// store: the fill progress is written, and the read's advance waits for
    /// the next read slot.
    /// </summary>
    /// <remarks>
    /// Called by the cycle when the store queue accepts the batch, never
    /// otherwise — a batch the queue dropped leaves the marks where they were,
    /// so the next read asks for its window again or a gap records it. Throws
    /// if the gap record cannot be written; the caller guards it. The advance
    /// is queued first, so a gap-record failure does not also cost the marks.
    /// </remarks>
    public void Accept(ObservationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Advance is { } advance)
        {
            _accepted.Enqueue(advance);
        }

        if (_gaps is null)
        {
            return;
        }

        foreach (var gap in batch.GapProgress)
        {
            _gaps.Update(gap);
        }
    }

    /// <summary>Reads the stored marks once per source state, as soon as the source names entities.</summary>
    private IReadOnlyDictionary<EntityId, DateTimeOffset>? Seed(
        IObservationSource source, List<CollectionFailure> failures)
    {
        if (_seeded)
        {
            return null;
        }

        var entities = source.EntitiesWithMarks();
        if (entities.Count == 0)
        {
            // Nothing to seed yet; asked again once inventory names something.
            return null;
        }

        try
        {
            var marks = _gaps!.LatestSampleTimes(entities);
            _seeded = true;
            return marks;
        }
#pragma warning disable CA1031 // Justified: see ReadAsync.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(GapRecordFailed("read the stored high-water marks", ex));
            return null;
        }
    }

    private IReadOnlyList<CollectionGap> OpenGaps(List<CollectionFailure> failures)
    {
        try
        {
            return _gaps!.OpenGaps(_instanceId);
        }
#pragma warning disable CA1031 // Justified: see ReadAsync.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(GapRecordFailed("read the open collection gaps", ex));
            return [];
        }
    }

    private static CollectionFailure GapRecordFailed(string what, Exception error) => new()
    {
        Kind = CollectionFailureKind.ProtocolError,
        Target = "collection gap record",
        Detail = $"Could not {what}: {error.Message} The live read is unaffected; an outage in this period " +
            "may be neither recorded nor filled until the record can be reached again.",
    };
}
