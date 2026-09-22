using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>Supplies which entities to sample this cycle.</summary>
/// <remarks>
/// The observation source does not discover inventory — that is the inventory
/// source's job, on its own rhythm (ADR-0005). It is told what exists and
/// samples it.
/// </remarks>
public interface IVsphereSampleTargetProvider
{
    VsphereSampleTargets Current { get; }

    /// <summary>Maps a managed object reference to the entity it resolved to.</summary>
    EntityId? ResolveEntity(string moRef);

    /// <summary>
    /// Maps a storage volume identifier to the datastore entity that owns it.
    /// </summary>
    /// <remarks>
    /// A host reports datastore latency with the volume's identifier as the
    /// counter instance, and nothing else in the sample says which datastore
    /// that is. Null means the volume is not one this source has inventoried —
    /// a datastore mounted on a host outside the collected estate, or one
    /// added since the last inventory cycle — and the measurement is dropped
    /// rather than attached to a guess.
    /// </remarks>
    EntityId? ResolveVolume(string volumeIdentifier);

    /// <summary>The name to show for a managed object, or null when unknown.</summary>
    /// <remarks>
    /// Used to label a datastore's series with the host that measured it, so
    /// "slow from one host" and "slow from all of them" can be told apart on
    /// screen. A managed object reference would technically serve and would be
    /// unreadable — nobody diagnoses storage by recognising host-3615.
    /// </remarks>
    string? DisplayNameOf(string moRef);
}

/// <summary>
/// Reads vSphere performance counters for one vCenter.
/// </summary>
/// <remarks>
/// <para>
/// Everything hard about this is in the orchestration rather than the
/// transport: sizing queries so the server accepts them, telling "the platform
/// is not configured to give us this" apart from "the platform is broken", and
/// never returning a number whose meaning we cannot vouch for.
/// </para>
/// <para>
/// Entity types fail one at a time. A read walks hosts, then virtual machines,
/// then datastores, and a fault on one of them is reported as a
/// <c>CollectionFailure</c> against that type rather than thrown — partial
/// success being a first-class outcome here, per ADR-0005. It used to escape,
/// and the common deployment where a monitoring account may read hosts and VMs
/// but not datastore counters therefore collected the host and VM samples and
/// then discarded them, every cycle, reporting the entire vCenter as
/// unreachable. See <see cref="EndsTheSession"/> for the two faults that are
/// still allowed out, and why.
/// </para>
/// <para>
/// See docs/collectors/vsphere-metric-contract.md.
/// </para>
/// </remarks>
public sealed class VsphereObservationSource(
    IVsphereApi api,
    IVsphereSampleTargetProvider targets,
    IClock clock,
    ICollectionGapStore? gaps = null) : IObservationSource
{
    private readonly IVsphereApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IVsphereSampleTargetProvider _targets =
        targets ?? throw new ArgumentNullException(nameof(targets));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Where the source-level gap record is kept, or null to keep none — the
    /// live read then still starts at each entity's mark, but a restart forgets
    /// the marks and an outage is neither recorded nor filled.
    /// </summary>
    private readonly ICollectionGapStore? _gaps = gaps;

    /// <summary>How long a "nothing is available" answer is taken at its word.</summary>
    /// <remarks>
    /// Long enough that the re-check costs nothing measurable, short enough
    /// that a statistics level someone raised this morning starts producing
    /// data the same day rather than at the next restart.
    /// </remarks>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    /// <summary>What this session has learnt about the probe, per entity type.</summary>
    /// <remarks>
    /// In memory rather than stored, deliberately. It is an observation about
    /// this session's conversation with one server, and a restart re-reads the
    /// connection anyway — persisting it would mean a vCenter that was fixed
    /// yesterday is still being second-guessed today on the strength of a
    /// disagreement nobody can see.
    /// </remarks>
    private readonly ProbeMemory _probe = new();

    /// <summary>Batch sizes this session has learnt the server accepts (T1.2).</summary>
    private readonly LearnedBatchSizes _learned = new();

    /// <summary>
    /// The probe's reputation, per entity type, guarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This instance is shared by every read — the registry hands back the same
    /// source and the host holds it as a singleton — and
    /// <see cref="IObservationSource.ReadAsync"/> is documented as
    /// non-re-entrant on that basis. It is not, however, enough. A read the
    /// runner abandoned at its timeout is still running in here when the next
    /// cycle starts thirty seconds later, and nothing can stop it: a .NET task
    /// cannot be aborted. So two reads can be in this object at once, rarely,
    /// and a bare <c>Dictionary</c> and <c>HashSet</c> written from both is a
    /// lost entry at best and a torn bucket array at worst — a lookup that
    /// throws, or one that never returns, inside the collector that is supposed
    /// to be watching everything else.
    /// </para>
    /// <para>
    /// The lock is held only around the dictionary work, never across the
    /// vCenter call. A source that hangs must cost its own read and nothing
    /// else; holding this while waiting on a network would let one slow vCenter
    /// stop the next cycle from starting, which is the whole failure the hard
    /// timeout exists to prevent.
    /// </para>
    /// <para>
    /// <see cref="DueForRecheck"/> is one operation rather than a read and a
    /// write for the same reason it is locked at all: split in two, the
    /// overlapping read would pass a check the first read was about to
    /// invalidate, and both would query — turning the hourly re-check this
    /// exists to ration back into two.
    /// </para>
    /// </remarks>
    private sealed class ProbeMemory
    {
        private readonly Lock _padlock = new();

        /// <summary>When each type the probe dismissed may be tried again.</summary>
        private readonly Dictionary<VsphereEntityType, DateTimeOffset> _nextRecheck = [];

        /// <summary>Types where the probe said "nothing" and the data disagreed.</summary>
        private readonly HashSet<VsphereEntityType> _probeProvedWrong = [];

        public bool HasBeenProvedWrongAbout(VsphereEntityType entityType)
        {
            lock (_padlock)
            {
                return _probeProvedWrong.Contains(entityType);
            }
        }

        /// <summary>
        /// Whether the hourly re-check for this type is due, claiming it if so.
        /// </summary>
        public bool DueForRecheck(VsphereEntityType entityType, DateTimeOffset now, TimeSpan interval)
        {
            lock (_padlock)
            {
                if (_nextRecheck.TryGetValue(entityType, out var due) && now < due)
                {
                    return false;
                }

                _nextRecheck[entityType] = now + interval;
                return true;
            }
        }

        /// <summary>Records that data arrived for a type the probe dismissed.</summary>
        public void RecordProbeWasWrong(VsphereEntityType entityType)
        {
            lock (_padlock)
            {
                _probeProvedWrong.Add(entityType);
                _nextRecheck.Remove(entityType);
            }
        }
    }

    public string InstanceId => _api.InstanceId;

    public async Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var observations = new List<Observation>();
        var backfill = new List<Observation>();
        var failures = new List<CollectionFailure>();

        var catalog = await _api.GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);

        // Not ToDictionary: a counter key is not unique, and building one
        // directly throws on a real catalogue. See VsphereCounterIndex.
        var byKey = VsphereCounterIndex.ByKey(catalog);

        var maxQueryMetrics = await _api.GetMaxQueryMetricsAsync(cancellationToken).ConfigureAwait(false);

        // The server's clock ends every window: sample times are its, so the
        // local clock being out must not decide what is asked for (§10.3).
        var serverTime = await _api.GetServerTimeAsync(cancellationToken).ConfigureAwait(false);
        var serverNow = serverTime ?? now;

        var types = _targets.Current.ByType().ToList();

        SeedMarks(types, failures);
        OpenGapIfBehind(types, serverNow, now, failures);

        var live = new LiveRead(serverNow);

        for (var i = 0; i < types.Count; i++)
        {
            var (entityType, moRefs) = types[i];
            try
            {
                await ReadTypeAsync(
                    entityType, moRefs, byKey, maxQueryMetrics, now, live,
                    observations, backfill, failures, cancellationToken).ConfigureAwait(false);
            }
            catch (VsphereApiException ex) when (!EndsTheSession(ex.Kind))
            {
                // One type, not the vCenter. See CouldNotRead.
                failures.Add(CouldNotRead(entityType, ex));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested && observations.Count > 0)
            {
                // The budget ran out with something in hand. See OutOfTime.
                foreach (var (untouched, untouchedMoRefs) in types.Skip(i + 1))
                {
                    failures.Add(OutOfTime(untouched, read: 0, untouchedMoRefs.Count));
                }

                break;
            }
        }

        ReportUnmeasurableLatency(observations, failures);

        // Only with what the live read left over, and never instead of it: the
        // live samples are already in hand, and running out of budget here
        // costs the fill a cycle, not the batch its current values.
        var filled = new Dictionary<long, CollectionGap>();
        if (_gaps is not null && live.Fillable.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FillAsync(live, maxQueryMetrics, now, backfill, filled, failures, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The slice in flight is asked again next cycle.
            }
        }

        if (serverTime is { } server)
        {
            backfill.Add(ClockSkew(server, now));
        }

        var seen = live.Seen;

        return new ObservationBatch
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Observations = observations,
            Backfill = backfill,
            Failures = failures,
            Stored = () => Stored(seen, filled),
        };
    }

    // --- high-water marks, the gap record and the fill (T0.4, handover 3) ---

    /// <summary>How far back the live read reaches for a real-time entity.</summary>
    /// <remarks>
    /// Six samples, two minutes: about four cycles, so an entity whose mark is
    /// older than this is behind by more than scheduling jitter — the steady
    /// state has a mark 20-70 s old at read time (the newest sample is up to one
    /// interval old when read, plus the cycle). Also the threshold for opening
    /// a source-level gap: a literal "two intervals" would open one on a normal
    /// cycle. Anything an entity misses beyond this, alone, is lost — the gap
    /// record covers the whole source only.
    /// </remarks>
    private const int LiveLookbackSamples = 6;

    private static readonly TimeSpan RealTimeLookback =
        TimeSpan.FromSeconds(VsphereIntervals.RealTimeSeconds * LiveLookbackSamples);

    /// <summary>How long a host keeps real-time samples.</summary>
    /// <remarks>
    /// A policy constant, not read from the platform: vim25 publishes the
    /// historical intervals' lengths (<c>PerformanceManager.historicalInterval</c>)
    /// but not the real-time one, which ESXi fixes at an hour (the m2
    /// measurement returned exactly 180 samples). Datastores, read from the
    /// 300 s historical interval, are kept longer; the gap record uses the
    /// real-time horizon for the whole source, so it can under-claim for them
    /// but never over-claim.
    /// </remarks>
    private static readonly TimeSpan RealTimeRetention = TimeSpan.FromHours(1);

    /// <summary>Kept clear of the retention edge, so a slice does not expire while it is read.</summary>
    private static readonly TimeSpan RetentionMargin = TimeSpan.FromMinutes(1);

    private readonly HighWaterMarks _marks = new();

    /// <summary>
    /// The newest sample time of each real-time entity, and how far the
    /// source as a whole has been accounted for.
    /// </summary>
    /// <remarks>
    /// Seeded once per process from the store's newest timestamp per entity —
    /// the mark survives a restart because the store is where it lives — then
    /// moved from what each read returned, and only once that read was stored.
    /// Locked for the reason <see cref="ProbeMemory"/> is.
    /// </remarks>
    private sealed class HighWaterMarks
    {
        private readonly Lock _padlock = new();
        private readonly Dictionary<string, DateTimeOffset> _byMoRef = new(StringComparer.Ordinal);
        private DateTimeOffset? _accountedTo;
        private bool _seeded;

        public bool IsSeeded()
        {
            lock (_padlock)
            {
                return _seeded;
            }
        }

        public DateTimeOffset? For(string moRef)
        {
            lock (_padlock)
            {
                return _byMoRef.TryGetValue(moRef, out var mark) ? mark : null;
            }
        }

        /// <summary>The newest thing the source has either stored or recorded as a gap.</summary>
        public DateTimeOffset? SourceMark()
        {
            lock (_padlock)
            {
                DateTimeOffset? newest = _accountedTo;
                foreach (var mark in _byMoRef.Values)
                {
                    if (newest is not { } n || mark > n)
                    {
                        newest = mark;
                    }
                }

                return newest;
            }
        }

        public void Seed(IEnumerable<KeyValuePair<string, DateTimeOffset>> marks)
        {
            lock (_padlock)
            {
                foreach (var (moRef, mark) in marks)
                {
                    Raise(moRef, mark);
                }

                _seeded = true;
            }
        }

        public void Advance(IReadOnlyDictionary<string, DateTimeOffset> seen)
        {
            lock (_padlock)
            {
                foreach (var (moRef, mark) in seen)
                {
                    Raise(moRef, mark);
                }
            }
        }

        public void Account(DateTimeOffset to)
        {
            lock (_padlock)
            {
                if (_accountedTo is not { } current || to > current)
                {
                    _accountedTo = to;
                }
            }
        }

        private void Raise(string moRef, DateTimeOffset mark)
        {
            if (!_byMoRef.TryGetValue(moRef, out var current) || mark > current)
            {
                _byMoRef[moRef] = mark;
            }
        }
    }

    /// <summary>What one read's live pass learnt, for the fill and for the marks.</summary>
    private sealed class LiveRead(DateTimeOffset serverNow)
    {
        public DateTimeOffset ServerNow { get; } = serverNow;

        /// <summary>The types the live pass could read, with the counters it used.</summary>
        public List<(VsphereEntityType Type, IReadOnlyList<string> MoRefs, IReadOnlyList<VsphereCounter> Usable)> Fillable { get; } = [];

        /// <summary>The newest sample time returned per real-time entity.</summary>
        public Dictionary<string, DateTimeOffset> Seen { get; } = new(StringComparer.Ordinal);

        public void Saw(VsphereEntityType entityType, IReadOnlyList<PerfEntitySamples> samples)
        {
            if (!VsphereIntervals.SupportsRealTime(entityType))
            {
                return;
            }

            foreach (var entity in samples)
            {
                if (entity.SampledAtUtc is { } at &&
                    (!Seen.TryGetValue(entity.EntityMoRef, out var seen) || at > seen))
                {
                    Seen[entity.EntityMoRef] = at;
                }
            }
        }
    }

    /// <summary>The window one entity is read over this cycle.</summary>
    /// <remarks>
    /// Real-time: from the entity's own mark (exclusive), but never more than
    /// <see cref="LiveLookbackSamples"/> back, to the server's now (inclusive).
    /// Historical (datastores): the rollup-lag window as before — a five-minute
    /// bucket can be finished after a later one has been read, so a mark would
    /// skip it.
    /// </remarks>
    private PerfQueryTarget LiveWindow(string moRef, VsphereEntityType entityType, DateTimeOffset serverNow)
    {
        if (!VsphereIntervals.SupportsRealTime(entityType))
        {
            return new PerfQueryTarget(moRef, serverNow - VsphereIntervals.HistoricalWindow, serverNow);
        }

        var floor = serverNow - RealTimeLookback;
        var start = _marks.For(moRef) is { } mark && mark > floor ? mark : floor;

        // Read twice within one sample: ask for the last one again rather than
        // for an empty window.
        if (start >= serverNow)
        {
            start = serverNow - TimeSpan.FromSeconds(VsphereIntervals.RealTimeSeconds);
        }

        return new PerfQueryTarget(moRef, start, serverNow);
    }

    /// <summary>Seeds the marks from the store, once per process.</summary>
    private void SeedMarks(
        List<(VsphereEntityType Type, IReadOnlyList<string> MoRefs)> types, List<CollectionFailure> failures)
    {
        if (_gaps is null || _marks.IsSeeded())
        {
            return;
        }

        var moRefsOf = new Dictionary<EntityId, List<string>>();
        foreach (var (entityType, moRefs) in types)
        {
            if (!VsphereIntervals.SupportsRealTime(entityType))
            {
                continue;
            }

            foreach (var moRef in moRefs)
            {
                if (_targets.ResolveEntity(moRef) is { } entity)
                {
                    if (!moRefsOf.TryGetValue(entity, out var list))
                    {
                        moRefsOf[entity] = list = [];
                    }

                    list.Add(moRef);
                }
            }
        }

        if (moRefsOf.Count == 0)
        {
            // Nothing to seed yet; asked again once inventory names something.
            return;
        }

        try
        {
            var latest = _gaps.LatestSampleTimes([.. moRefsOf.Keys]);

            _marks.Seed(latest.SelectMany(pair => moRefsOf[pair.Key]
                .Select(moRef => new KeyValuePair<string, DateTimeOffset>(moRef, pair.Value))));
        }
#pragma warning disable CA1031 // Justified: the gap record is bookkeeping; a
        // store that cannot answer must cost the record, not the read.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(GapRecordFailed("read the stored high-water marks", ex));
        }
    }

    /// <summary>
    /// Records a source-level gap when the source's mark is older than the live read reaches.
    /// </summary>
    private void OpenGapIfBehind(
        List<(VsphereEntityType Type, IReadOnlyList<string> MoRefs)> types,
        DateTimeOffset serverNow,
        DateTimeOffset now,
        List<CollectionFailure> failures)
    {
        if (_gaps is null || !types.Any(t => VsphereIntervals.SupportsRealTime(t.Type)))
        {
            return;
        }

        var liveStart = serverNow - RealTimeLookback;

        if (CollectionGaps.ToOpen(_marks.SourceMark(), liveStart) is not { } gap)
        {
            return;
        }

        try
        {
            _gaps.Open(new CollectionGap
            {
                SourceInstanceId = InstanceId,
                FromUtc = gap.From,
                ToUtc = gap.To,
                FilledToUtc = gap.From,
                State = CollectionGapState.Open,
                OpenedAtUtc = now,
            });

            // Recorded, so not recorded again: the next cycle's mark is at
            // least here even if this cycle's samples are never stored.
            _marks.Account(liveStart);
        }
#pragma warning disable CA1031 // Justified: see SeedMarks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(GapRecordFailed("record a collection gap", ex));
        }
    }

    /// <summary>
    /// Reads the oldest open gaps again, ten minutes at a time, until they
    /// close or the budget runs out.
    /// </summary>
    /// <remarks>
    /// Oldest gap first, oldest slice first, so a gap that cannot be finished
    /// before the host forgets it loses its newest end last. Progress is kept
    /// in <paramref name="filled"/> and written only once the batch is stored;
    /// what has passed the retention horizon is written at once, since no
    /// append can change that.
    /// </remarks>
    private async Task FillAsync(
        LiveRead live,
        int? maxQueryMetrics,
        DateTimeOffset now,
        List<Observation> backfill,
        Dictionary<long, CollectionGap> filled,
        List<CollectionFailure> failures,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CollectionGap> open;
        try
        {
            open = _gaps!.OpenGaps(InstanceId);
        }
#pragma warning disable CA1031 // Justified: see SeedMarks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(GapRecordFailed("read the open collection gaps", ex));
            return;
        }

        var recoverableAfter = live.ServerNow - RealTimeRetention + RetentionMargin;

        foreach (var recorded in open)
        {
            var gap = CollectionGaps.Expire(recorded, recoverableAfter, now);
            if (!ReferenceEquals(gap, recorded))
            {
                try
                {
                    _gaps.Update(gap);
                }
#pragma warning disable CA1031 // Justified: see SeedMarks.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failures.Add(GapRecordFailed("record what was past retention", ex));
                    return;
                }
            }

            while (gap.State == CollectionGapState.Open)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (start, end) = CollectionGaps.NextSlice(gap);

                foreach (var (entityType, moRefs, usable) in live.Fillable)
                {
                    await FillSliceAsync(
                        entityType, moRefs, usable, maxQueryMetrics, start, end, backfill, cancellationToken)
                        .ConfigureAwait(false);
                }

                gap = CollectionGaps.Advance(gap, end, now);
                filled[gap.Id] = gap;
            }
        }
    }

    /// <summary>Reads one slice of one entity type, every entity over the same window.</summary>
    /// <remarks>
    /// Sized like the live read and shrunk the same way when refused. A batch
    /// that faults is asked again one entity at a time, and an entity that
    /// faults alone is skipped: the gap record is about the source, and one
    /// entity the server will not answer for must not hold the whole fill.
    /// </remarks>
    private async Task FillSliceAsync(
        VsphereEntityType entityType,
        IReadOnlyList<string> moRefs,
        IReadOnlyList<VsphereCounter> usable,
        int? maxQueryMetrics,
        DateTimeOffset start,
        DateTimeOffset end,
        List<Observation> backfill,
        CancellationToken cancellationToken)
    {
        var sizer = new AdaptiveBatchSizer(maxQueryMetrics, usable.Count, _learned.For(entityType, usable.Count));
        var fallbackInterval = TimeSpan.FromSeconds(VsphereIntervals.IntervalSecondsFor(entityType));
        var remaining = new Queue<string>(moRefs);
        var singles = new Stack<List<string>>();

        while (remaining.Count > 0 || singles.Count > 0)
        {
            var batch = singles.Count > 0 ? singles.Pop() : Dequeue(remaining, sizer.Current);

            try
            {
                var samples = await _api
                    .QueryPerfWindowsAsync(
                        [.. batch.Select(moRef => new PerfQueryTarget(moRef, start, end))],
                        entityType, usable, cancellationToken)
                    .ConfigureAwait(false);

                // All of it is history: nothing a fill returns is "current".
                var timed = samples.Where(s => s.SampledAtUtc is not null).ToList();
                backfill.AddRange(ToObservations(timed, fallbackInterval, end));
                backfill.AddRange(ToBackfill(timed, fallbackInterval));
            }
            catch (VsphereQuerySizeRefusedException)
            {
                if (!sizer.Reduce())
                {
                    return;
                }

                Requeue(remaining, batch);
            }
            catch (VsphereApiException ex) when (!EndsTheSession(ex.Kind))
            {
                if (batch.Count == 1)
                {
                    continue;
                }

                for (var i = batch.Count - 1; i >= 0; i--)
                {
                    singles.Push([batch[i]]);
                }
            }
        }
    }

    /// <summary>Moves the marks and the fill points once the batch is in the store.</summary>
    private void Stored(IReadOnlyDictionary<string, DateTimeOffset> seen, Dictionary<long, CollectionGap> filled)
    {
        _marks.Advance(seen);

        if (_gaps is null)
        {
            return;
        }

        foreach (var gap in filled.Values)
        {
            _gaps.Update(gap);
        }
    }

    /// <summary>The server's clock minus ours, as a series on the vCenter.</summary>
    private Observation ClockSkew(DateTimeOffset serverNow, DateTimeOffset now) => new()
    {
        Entity = EntityId.For(InstanceId, "vcenter"),
        Value = new CounterValue
        {
            CounterName = CollectorSelfMetrics.ClockSkewCounter,
            Raw = (serverNow - now).TotalSeconds,
            Rollup = RollupType.Latest,
            Interval = TimeSpan.FromSeconds(VsphereIntervals.RealTimeSeconds),
            Unit = "second",
        },
        SampledAtUtc = now,
        Source = InstanceId,
    };

    private static CollectionFailure GapRecordFailed(string what, Exception error) => new()
    {
        Kind = CollectionFailureKind.ProtocolError,
        Target = "collection gap record",
        Detail = $"Could not {what}: {error.Message} The live read is unaffected; an outage in this period " +
            "may be neither recorded nor filled until the record can be reached again.",
    };

    /// <summary>
    /// Whether a fault ends the conversation rather than just this question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line the per-type guard is drawn on, and the only judgement in it.
    /// A vCenter that answers with a vim25 fault has, by answering, proved the
    /// connection and the session are alive — so the next entity type is worth
    /// asking about, and the fault describes what was asked for rather than who
    /// is asking. The two exceptions are the faults that are about the asking
    /// itself: a rejected login and an expired session. Neither improves by
    /// moving on to datastores, and both need the whole read started again on a
    /// fresh connection, which only <c>SourceRunner</c> can do.
    /// </para>
    /// <para>
    /// Letting those two escape is also what keeps the one-strike rule working.
    /// <c>CollectionFailures.IsWorthRetrying</c> says a rejected login must not
    /// be asked again, and the runner holds the source off after a single such
    /// failure — vSphere SSO locks an account at a handful. Demoted to a
    /// <c>CollectionFailure</c> the runner would never see it, would count the
    /// read a success, and would present the operator three fresh rejected
    /// logins every cycle under a heading that says it does not do that.
    /// </para>
    /// <para>
    /// Note that <c>IsWorthRetrying</c> cannot itself be the split. It answers
    /// "can asking again help", which is orthogonal: <c>NoPermission</c> is not
    /// worth retrying and must be demoted — it is the whole defect — while a
    /// generic runtime fault is worth retrying and must be demoted too. The
    /// vocabulary is still reused, in the other direction: what the demoted
    /// fault becomes is <c>ICollectionFault.Kind</c>, unchanged.
    /// </para>
    /// <para>
    /// Anything that is not a <see cref="VsphereApiException"/> at all —
    /// a dead socket, a mid-read timeout, a cancelled cycle — escapes as
    /// before, and deliberately. Those arrive without a fault body, which means
    /// the server never answered, which means there is nothing to say about one
    /// entity type in particular. Guessing that the session survived would have
    /// the product report "datastore metrics unavailable" while the truth is
    /// that the vCenter is gone.
    /// </para>
    /// </remarks>
    private static bool EndsTheSession(VsphereFaultKind kind) =>
        kind is VsphereFaultKind.InvalidLogin or VsphereFaultKind.NotAuthenticated;

    /// <summary>
    /// Records one entity type as unread, so the rest of the cycle survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kind is the exception's own <c>ICollectionFault.Kind</c> rather than
    /// a new one invented here: <c>NoPermission</c> is already
    /// <c>AuthorizationDenied</c> and everything else is already
    /// <c>ProtocolError</c>, and a second spelling of that mapping is a second
    /// chance for it to drift.
    /// </para>
    /// <para>
    /// The target is the entity type, because that is the unit the failure
    /// actually has. Naming a counter would be narrower than the truth — none
    /// of them were read — and naming the vCenter would be wider than it, since
    /// the other types were read normally and their samples are in this batch.
    /// </para>
    /// <para>
    /// Nothing is substituted for what is missing. The series gets a gap, and a
    /// gap is the honest shape of this: a zero would look like a measurement.
    /// </para>
    /// </remarks>
    private static CollectionFailure CouldNotRead(
        VsphereEntityType entityType, VsphereApiException error)
    {
        var advice = error.Kind == VsphereFaultKind.NoPermission
            ? $" The account is authenticated but not permitted to read {entityType} " +
              "performance data. That is a permission granted on those objects, not " +
              "something a retry can change."
            : string.Empty;

        return new CollectionFailure
        {
            Kind = ((ICollectionFault)error).Kind,
            Target = entityType.ToString(),
            Detail =
                $"Reading {entityType} performance data from this vCenter failed with " +
                $"{error.Kind}, so nothing of that kind was measured this cycle: " +
                $"{error.Message}{advice} The other entity types in the same read were " +
                "unaffected and their samples are in this batch. No value is substituted " +
                "for the missing ones.",
        };
    }

    /// <summary>
    /// Says so when the platform's configuration makes a measurement impossible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The product telling an operator about its own blindness, which is the
    /// first thing it owes them. <c>datastore.datastoreVMObservedLatency</c> is
    /// the one storage counter with sub-millisecond resolution — it is reported
    /// in microseconds precisely for that — and it only reports while Storage
    /// I/O Control is active. With SIOC off it reads zero forever, and the
    /// millisecond counters beside it truncate anything faster than 1ms to zero
    /// as well. An operator looking at a chart of zeroes concludes the array is
    /// fast and moves on; the truth is that nothing was measured.
    /// </para>
    /// <para>
    /// Reported as a collection failure rather than an alert because that is
    /// what it is: NotConfigured means the platform can supply this and is not
    /// set up to. It is also where it belongs — no graph, no history and no
    /// second object are involved, and only this collector knows that one
    /// vSphere feature gates one vSphere counter.
    /// </para>
    /// <para>
    /// Gated on there being I/O. SIOC reads zero on an idle datastore too, and
    /// telling somebody to enable a feature on a volume nobody uses is the kind
    /// of advice that teaches people to ignore advice.
    /// </para>
    /// <para>
    /// One failure for the estate rather than one per volume. Forty-one
    /// identical messages would be a wall, and the fix is a single decision
    /// about the platform rather than forty-one decisions about volumes.
    /// </para>
    /// </remarks>
    private static void ReportUnmeasurableLatency(
        List<Observation> observations,
        List<CollectionFailure> failures)
    {
        var busyVolumes = new HashSet<string>(StringComparer.Ordinal);
        var siocActive = false;
        var sawSioc = false;

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (string.Equals(value.CounterName, SiocCounter, StringComparison.OrdinalIgnoreCase))
            {
                sawSioc = true;
                siocActive |= value.Raw > 0;
            }
            else if (IopsCounters.Contains(value.CounterName, StringComparer.OrdinalIgnoreCase) &&
                     value.Raw > 0)
            {
                // Counted by entity, not by instance. These observations have
                // already been re-attributed: the datastore is the entity and
                // the instance has become the host that measured it, so
                // counting instances would count hosts. Found by a surviving
                // mutation — the test could not tell one report per estate
                // from one per volume, because with a single host both were
                // one.
                busyVolumes.Add(observation.Entity.Value);
            }
        }

        // Nothing to say when the counter was never collected — that is a
        // different problem with its own report — or when it is working, or
        // when nothing is doing any I/O to be blind about.
        if (!sawSioc || siocActive || busyVolumes.Count == 0)
        {
            return;
        }

        failures.Add(new CollectionFailure
        {
            Kind = CollectionFailureKind.NotConfigured,
            Target = "datastore.datastoreVMObservedLatency.latest",
            Detail =
                "Storage I/O Control is inactive everywhere on this vCenter, so storage latency " +
                $"cannot be measured below one millisecond — including on {busyVolumes.Count} " +
                "datastore(s) currently serving I/O. The millisecond counters truncate anything " +
                "faster to zero, and the microsecond counter that exists for this reports only " +
                "while SIOC is active. A latency chart reading zero here means 'not measured', " +
                "not 'fast'. Enable Storage I/O Control on the datastores that matter to get " +
                "sub-millisecond visibility.",
        });
    }

    private const string SiocCounter = "datastore.siocActiveTimePercentage.average";

    private static readonly string[] IopsCounters =
    [
        "datastore.numberReadAveraged.average",
        "datastore.numberWriteAveraged.average",
    ];

    /// <summary>How far one read has got through one entity type.</summary>
    private sealed class TypeProgress
    {
        /// <summary>Entities whose query came back.</summary>
        public int Read { get; set; }

        /// <summary>Entities vCenter says no longer exist.</summary>
        public List<string> Gone { get; } = [];

        /// <summary>The first entity not yet read, where the next cycle should start.</summary>
        public string? ResumeFrom { get; set; }

        /// <summary>Entities whose own query faulted, by fault kind, with the first fault seen.</summary>
        public Dictionary<VsphereFaultKind, (VsphereApiException First, List<string> MoRefs)> Faults { get; } = [];

        public void Faulted(string moRef, VsphereApiException error)
        {
            if (!Faults.TryGetValue(error.Kind, out var entry))
            {
                entry = (error, []);
                Faults[error.Kind] = entry;
            }

            entry.MoRefs.Add(moRef);
        }
    }

    /// <summary>
    /// Says which entities of a type could not be read, one report per fault kind (T1.4).
    /// </summary>
    private static CollectionFailure EntitiesFaulted(
        VsphereEntityType entityType, VsphereFaultKind kind, VsphereApiException first, List<string> moRefs)
    {
        var advice = kind == VsphereFaultKind.NoPermission
            ? $" The account is authenticated but not permitted to read these {entityType} objects' " +
              "performance data — a permission on those objects, not something a retry can change."
            : string.Empty;

        return new CollectionFailure
        {
            Kind = ((ICollectionFault)first).Kind,
            Target = $"{entityType} entities ({kind})",
            Detail =
                $"{moRefs.Count} {entityType} entities could not be read with {kind} " +
                $"({string.Join(", ", moRefs.Take(5))}{(moRefs.Count > 5 ? ", …" : string.Empty)}): " +
                $"{first.Message}{advice} The batch each was in was asked again one entity at a time, " +
                "so the others in it, and every other batch, were read. No value is substituted for the missing ones.",
        };
    }

    /// <summary>
    /// Where each type's read stopped when the budget ran out, so the next
    /// cycle starts there (T1.1).
    /// </summary>
    /// <remarks>
    /// Without it, a read cut off by the budget kept the same first entities
    /// every cycle: the synthetic 2000-VM read kept VMs 1–996 every time and
    /// the other 1004 never had a sample. Rotating the start spreads the gap
    /// across the estate, so every entity is sampled every few cycles instead
    /// of half of them never. Locked for the same reason as
    /// <see cref="ProbeMemory"/>.
    /// </remarks>
    private sealed class ReadCursor
    {
        private readonly Lock _padlock = new();
        private readonly Dictionary<VsphereEntityType, string> _resumeFrom = [];

        public string? For(VsphereEntityType entityType)
        {
            lock (_padlock)
            {
                return _resumeFrom.GetValueOrDefault(entityType);
            }
        }

        public void Set(VsphereEntityType entityType, string? moRef)
        {
            lock (_padlock)
            {
                if (moRef is null)
                {
                    _resumeFrom.Remove(entityType);
                }
                else
                {
                    _resumeFrom[entityType] = moRef;
                }
            }
        }
    }

    private readonly ReadCursor _cursor = new();

    /// <summary>The targets in reading order: from the cursor, wrapping round.</summary>
    private static List<string> StartingAt(IReadOnlyList<string> moRefs, string? resumeFrom)
    {
        var start = 0;
        if (resumeFrom is not null)
        {
            for (var i = 0; i < moRefs.Count; i++)
            {
                if (string.Equals(moRefs[i], resumeFrom, StringComparison.Ordinal))
                {
                    start = i;
                    break;
                }
            }
        }

        return [.. moRefs.Skip(start), .. moRefs.Take(start)];
    }

    /// <summary>
    /// Says how much of a type the read budget covered, and that the rest was not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// T1.1. A read was all or nothing: the runner abandoned it at the
    /// timeout, and everything it had already collected went with it. The
    /// synthetic 2000-VM measurement (docs/measurements/read-budget-2000vm.md)
    /// needs 42 s at 250 ms a query against a 25 s budget, which made that
    /// "nothing", every cycle, for the whole estate.
    /// </para>
    /// <para>
    /// Now the runner cancels the token a little before it gives up, the
    /// query in flight is lost, and what was read before it is returned. The
    /// rest is named here rather than left as a gap for someone to notice —
    /// the operator is told the estate is larger than one cycle can read, and
    /// by how much. Timeout is the kind because that is what happened, and it
    /// stays worth retrying. The next cycle starts where this one stopped; see
    /// <see cref="ReadCursor"/>.
    /// </para>
    /// </remarks>
    private static CollectionFailure OutOfTime(VsphereEntityType entityType, int read, int total) => new()
    {
        Kind = CollectionFailureKind.Timeout,
        Target = entityType.ToString(),
        Detail = $"The read budget ran out with {read} of {total} {entityType} entities read; the other " +
            $"{total - read} were not read this cycle and have no sample for it. What was read is kept. " +
            "An estate this size does not fit one collection interval at this vCenter's query speed.",
    };

    /// <summary>How many entities the probe tries before calling the target list stale.</summary>
    /// <remarks>
    /// One deleted entity at the head of the list is ordinary churn between
    /// inventory reads. Five in a row is a list that no longer describes the
    /// vCenter, and asking every entity in turn would be a query storm aimed at
    /// finding that out.
    /// </remarks>
    private const int MaxProbeAttempts = 5;

    private async Task ReadTypeAsync(
        VsphereEntityType entityType,
        IReadOnlyList<string> moRefs,
        IReadOnlyDictionary<string, VsphereCounter> catalog,
        int? maxQueryMetrics,
        DateTimeOffset now,
        LiveRead live,
        List<Observation> observations,
        List<Observation> backfill,
        List<CollectionFailure> failures,
        CancellationToken cancellationToken)
    {
        var progress = new TypeProgress();
        try
        {
            await ReadTypeCoreAsync(
                entityType, StartingAt(moRefs, _cursor.For(entityType)), catalog, maxQueryMetrics, now, live,
                observations, backfill, failures, progress, cancellationToken).ConfigureAwait(false);

            // Read to the end: the next cycle starts from the top.
            _cursor.Set(entityType, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _cursor.Set(entityType, progress.ResumeFrom);
            failures.Add(OutOfTime(entityType, progress.Read, moRefs.Count - progress.Gone.Count));
            throw;
        }
        finally
        {
            if (progress.Gone.Count > 0)
            {
                failures.Add(NoLongerExist(entityType, progress.Gone));
            }

            foreach (var (kind, (first, faulted)) in progress.Faults)
            {
                failures.Add(EntitiesFaulted(entityType, kind, first, faulted));
            }
        }
    }

    /// <summary>
    /// Says which entities were skipped because vCenter no longer has them.
    /// </summary>
    /// <remarks>
    /// T1.4. The target list comes from the last inventory read, minutes old,
    /// and a VM deleted since then is ordinary. vim25 answers a query naming
    /// one with <c>ManagedObjectNotFound</c> for the whole query, and that used
    /// to end the type: when it was <c>moRefs[0]</c> the probe failed and all
    /// 2000 virtual machines went unread; mid-list, everything after its batch
    /// did. The entity is now isolated and skipped, and named here so the gap
    /// is explained. It leaves the list at the next inventory read.
    /// </remarks>
    private static CollectionFailure NoLongerExist(VsphereEntityType entityType, List<string> gone) => new()
    {
        Kind = CollectionFailureKind.ProtocolError,
        Target = $"{entityType} no longer on vCenter",
        Detail = $"{gone.Count} {entityType} entities listed by the last inventory read no longer exist on this " +
            $"vCenter ({string.Join(", ", gone.Take(5))}{(gone.Count > 5 ? ", …" : string.Empty)}), so they " +
            "were skipped; the rest of the type was read. They drop out at the next inventory read.",
    };

    private async Task ReadTypeCoreAsync(
        VsphereEntityType entityType,
        List<string> moRefs,
        IReadOnlyDictionary<string, VsphereCounter> catalog,
        int? maxQueryMetrics,
        DateTimeOffset now,
        LiveRead live,
        List<Observation> observations,
        List<Observation> backfill,
        List<CollectionFailure> failures,
        TypeProgress progress,
        CancellationToken cancellationToken)
    {
        var wanted = VsphereCounters.For(entityType);
        if (wanted.Count == 0 || moRefs.Count == 0)
        {
            return;
        }

        // Ask one representative entity what it can actually supply. Counter
        // availability is a property of the platform's statistics level, not of
        // the individual object, so one probe answers for the type — and one
        // probe per cycle is affordable where one per entity would not be.
        //
        // Not always moRefs[0]: an entity deleted since the inventory read
        // answers ManagedObjectNotFound, and that used to fail the whole type.
        // It is skipped — here and in the queries below — and the next one is
        // asked. See NoLongerExist and MaxProbeAttempts.
        IReadOnlyList<string>? available = null;
        foreach (var candidate in moRefs.Take(MaxProbeAttempts))
        {
            try
            {
                available = await _api
                    .GetAvailableCounterKeysAsync(candidate, entityType, live.ServerNow, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.ManagedObjectNotFound)
            {
                progress.Gone.Add(candidate);
            }
        }

        if (available is null)
        {
            failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.ProtocolError,
                Target = entityType.ToString(),
                Detail = $"The first {progress.Gone.Count} {entityType} entities in the target list no longer exist " +
                    "on this vCenter, so the list is stale and the type was not read this cycle. It is " +
                    "rebuilt at the next inventory read.",
            });
            return;
        }

        var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        // The source supplied nothing at all for this entity type. Reported as
        // one fact rather than as one complaint per counter, because it is one
        // fact: against a live vCenter this produced two separate messages both
        // claiming a counter "requires statistics level 1", which is not a
        // thing that can be true — level 1 is the floor every installation
        // collects at. Two impossible sentences in place of one true one.
        // The probe is trusted, but not indefinitely and not on its own word.
        //
        // When it reports nothing for a whole entity type we stop querying —
        // asking every thirty seconds for an answer that will not change is a
        // monitoring tool making work for the system it monitors. But the probe
        // is also the thing that has been wrong all day, and a plain skip makes
        // that self-confirming: never ask, never learn it was available. So the
        // skip expires. Once an hour the query goes out regardless, and if data
        // comes back the probe has been proven wrong for this type and is not
        // consulted for it again.
        var trustTheProbe = availableSet.Count == 0 && !_probe.HasBeenProvedWrongAbout(entityType);

        if (trustTheProbe)
        {
            failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.NotConfigured,
                Target = entityType.ToString(),
                Detail =
                    $"This vCenter reports no performance data at all for {entityType} at the " +
                    $"{VsphereIntervals.IntervalSecondsFor(entityType)}s interval, so nothing of " +
                    "that kind can be measured. This is about what the platform keeps, not about " +
                    "which counters the product asked for. The product re-checks hourly rather " +
                    "than taking the answer as permanent.",
            });

            if (!_probe.DueForRecheck(entityType, now, RecheckInterval))
            {
                return;
            }
        }

        // A type the probe has been caught out on, or an hourly re-check: use
        // every counter this vCenter actually defines and let the data answer.
        var ignoreTheProbe = availableSet.Count == 0;

        var usable = new List<VsphereCounter>();
        foreach (var key in wanted)
        {
            if (!catalog.TryGetValue(key, out var counter))
            {
                // The counter does not exist on this vCenter at all. Worth
                // saying whose problem that is: raising a statistics level
                // cannot conjure a counter the server has never heard of, so
                // an operator reading this should not go looking in settings.
                // It is either a version difference or — as it was for
                // datastore and virtual disk latency — a name this product got
                // wrong and never checked against a real catalogue.
                failures.Add(new CollectionFailure
                {
                    Kind = CollectionFailureKind.ProtocolError,
                    Target = key,
                    Detail =
                        $"This vCenter has no counter called '{key}' for {entityType}, so it is " +
                        "not being measured. Changing the statistics level will not help: either " +
                        "this vCenter version does not provide it, or the product is asking for " +
                        "the wrong name.",
                });
                continue;
            }

            if (!ignoreTheProbe && !availableSet.Contains(key))
            {
                // Defined but not being collected. Usually the statistics
                // level — but only when the counter needs a level above the
                // floor. A level-1 counter that is unavailable cannot be
                // explained by the statistics level, and saying so anyway
                // sends an operator to raise a setting that is already high
                // enough. Reported either way: the affected metrics are
                // Unknown, and a product that hides what it cannot see is
                // worse than one that is absent.
                var raisingTheLevelCouldHelp = counter.Level > 1;

                failures.Add(new CollectionFailure
                {
                    Kind = raisingTheLevelCouldHelp
                        ? CollectionFailureKind.InsufficientDetailLevel
                        : CollectionFailureKind.NotConfigured,
                    Target = key,
                    Detail = raisingTheLevelCouldHelp
                        ? $"Counter requires statistics level {counter.Level} for {entityType}; " +
                          "the platform is collecting at a lower level, so this is not measured."
                        : $"This vCenter defines '{key}' but is not keeping it for {entityType}. " +
                          "It needs no statistics level above the default, so raising the level " +
                          "will not help — the platform simply holds no data of this kind.",
                });
                continue;
            }

            usable.Add(counter);
        }

        if (usable.Count == 0)
        {
            return;
        }

        // What a gap fill may ask this cycle: the types the live read could
        // ask, with the counters it asked for.
        live.Fillable.Add((entityType, [.. moRefs.Except(progress.Gone, StringComparer.Ordinal)], usable));

        // Started from what this session already learnt, if anything (T1.2).
        var sizer = new AdaptiveBatchSizer(
            maxQueryMetrics, usable.Count, _learned.For(entityType, usable.Count));
        var fallbackInterval = TimeSpan.FromSeconds(VsphereIntervals.IntervalSecondsFor(entityType));

        var remaining = new Queue<string>(moRefs.Except(progress.Gone, StringComparer.Ordinal));

        // Halves of a batch that named an entity vCenter no longer has, asked
        // before anything new. See the ManagedObjectNotFound catch below.
        var isolating = new Stack<List<string>>();

        while (remaining.Count > 0 || isolating.Count > 0)
        {
            var batch = isolating.Count > 0 ? isolating.Pop() : Dequeue(remaining, sizer.Current);

            // If the budget runs out during this query it is lost, so the
            // next cycle starts with it.
            progress.ResumeFrom = batch[0];

            try
            {
                var samples = await _api
                    .QueryPerfWindowsAsync(
                        [.. batch.Select(moRef => LiveWindow(moRef, entityType, live.ServerNow))],
                        entityType, usable, cancellationToken)
                    .ConfigureAwait(false);

                live.Saw(entityType, samples);

                progress.Read += batch.Count;

                // The size the server just accepted, kept for the session so
                // the next cycle does not walk down to it again (ADR-0005 §2).
                if (sizer.WasReduced)
                {
                    _learned.Remember(entityType, usable.Count, sizer.Current);
                }

                var read = ToObservations(samples, fallbackInterval, now).ToList();

                // Data arrived for a type the probe said had none. The probe is
                // wrong here — recorded so the hourly re-check becomes a
                // permanent one, rather than this measuring an hour apart
                // forever while the data was there all along.
                if (ignoreTheProbe && read.Count > 0)
                {
                    _probe.RecordProbeWasWrong(entityType);
                }

                observations.AddRange(read);
                backfill.AddRange(ToBackfill(samples, fallbackInterval));
            }
            catch (VsphereQuerySizeRefusedException ex)
            {
                if (!sizer.Reduce())
                {
                    // Already down to one entity: the size is not the problem,
                    // so retrying forever would be a loop rather than a fix.
                    failures.Add(new CollectionFailure
                    {
                        Kind = CollectionFailureKind.ProtocolError,
                        Target = $"{entityType} performance query",
                        Detail = $"Refused even for a single entity: {ex.Message}",
                    });
                    return;
                }

                // Put the batch back and try again smaller. The server told us
                // what it will accept; believing it is cheaper than guessing.
                Requeue(remaining, batch);
            }
            catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.ManagedObjectNotFound)
            {
                // One entity in the batch is gone and vim25 refused the whole
                // query for it (T1.4). Halve until it is alone and skip it:
                // a few extra queries per deleted entity, where giving up cost
                // the rest of the type. Bisected rather than read out of the
                // fault message, because the message is the server's wording
                // and the halving works whatever it says.
                //
                // Only this fault. A permission fault or a runtime fault is not
                // known to be about one entity, and bisecting 2000 VMs down to
                // singles against a fault that applies to all of them would be
                // thousands of queries to learn nothing.
                if (batch.Count == 1)
                {
                    progress.Gone.Add(batch[0]);
                    continue;
                }

                var half = batch.Count / 2;
                isolating.Push(batch.GetRange(half, batch.Count - half));
                isolating.Push(batch.GetRange(0, half));
            }
            catch (VsphereApiException ex) when (!EndsTheSession(ex.Kind))
            {
                // Any other fault (T1.4, the OTel vcenterreceiver rule): this
                // batch — only this one — is asked again one entity at a time,
                // so the fault lands on the entity it is about and the others
                // in the batch are read. It used to end the type: one VM the
                // account may not read cost every VM. A fault that really does
                // cover the whole type costs one query per entity, which the
                // read budget bounds, and is reported once per kind.
                if (batch.Count == 1)
                {
                    progress.Faulted(batch[0], ex);
                    continue;
                }

                for (var i = batch.Count - 1; i >= 0; i--)
                {
                    isolating.Push([batch[i]]);
                }
            }
        }

        if (sizer.IsBelowPlan)
        {
            failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.ProtocolError,
                Target = $"{entityType} performance query",
                Detail = $"The server refused queries of {sizer.Planned} entities, the size its stated limit " +
                    $"allows; reduced to {sizer.Current} entities per query, a size this session " +
                    "remembers. Samples were collected, but more slowly than planned. Raising " +
                    "config.vpxd.stats.maxQueryMetrics on the vCenter, or making it readable to this " +
                    "account, restores the planned size.",
            });
        }
    }

    /// <summary>The current value of each series, under the time vCenter took it.</summary>
    /// <remarks>
    /// vCenter's sample time, not the moment this read started. The two differ
    /// by up to a sampling interval for a host and by minutes for a datastore,
    /// whose samples are five minutes apart — stamped "now", a five-minute-old
    /// figure claimed to be current. It is also what lets the store recognise
    /// a sample it has already been given: the same sample comes back on the
    /// next read, and under one time it is one row. The local clock is the
    /// fallback only for a reply that carried no sample times.
    /// </remarks>
    private IEnumerable<Observation> ToObservations(
        IReadOnlyList<PerfEntitySamples> samples,
        TimeSpan fallbackInterval,
        DateTimeOffset now) =>
        samples.SelectMany(entity => ToObservations(
            entity.EntityMoRef, entity.Values, fallbackInterval, entity.SampledAtUtc ?? now));

    /// <summary>The samples before the current one; see <see cref="ObservationBatch.Backfill"/>.</summary>
    private IEnumerable<Observation> ToBackfill(
        IReadOnlyList<PerfEntitySamples> samples,
        TimeSpan fallbackInterval) =>
        samples.SelectMany(entity => entity.Earlier.SelectMany(earlier => ToObservations(
            entity.EntityMoRef, earlier.Values, fallbackInterval, earlier.SampledAtUtc)));

    private IEnumerable<Observation> ToObservations(
        string entityMoRef,
        IReadOnlyList<CounterValue> values,
        TimeSpan fallbackInterval,
        DateTimeOffset now)
    {
        // A sample we cannot attribute to an entity is dropped rather than
        // attached to a guess. An unattributed number is worse than no
        // number: it looks like knowledge.
        if (_targets.ResolveEntity(entityMoRef) is not { } entityId)
        {
            yield break;
        }

        var measuredBy = _targets.DisplayNameOf(entityMoRef);

        foreach (var value in values)
        {
            var withInterval = value.Interval > TimeSpan.Zero
                ? value
                : value with { Interval = fallbackInterval };

            // Measured on this object, about a different one. The sample
            // arrived under a host because that is where vSphere keeps
            // datastore counters; the instance says which datastore, and
            // filing it under the host would put storage latency on the
            // wrong page entirely.
            if (VsphereCounters.InstanceNamesAnEntity(value.CounterName))
            {
                if (Reattribute(withInterval, entityId, measuredBy) is { } moved)
                {
                    yield return moved;
                }

                continue;
            }

            yield return new Observation
            {
                Entity = entityId,
                Value = withInterval,
                SampledAtUtc = now,
                Source = InstanceId,
            };
        }

        Observation? Reattribute(CounterValue value, EntityId measuredOn, string? measuredBy)
        {
            // No instance, nothing to attribute it to. vCenter's aggregate
            // across every volume a host can see is already dropped by the
            // parser; anything else reaching here is a shape we did not expect,
            // and inventing a subject for it would be the worst of the options.
            if (value.Instance.Length == 0 ||
                _targets.ResolveVolume(value.Instance) is not { } datastore)
            {
                return null;
            }

            return new Observation
            {
                Entity = datastore,

                // The instance stops being the volume — that is now the entity
                // — and becomes the host that took the reading. One datastore
                // therefore carries one series per host, which is what lets the
                // product distinguish a volume that is slow from everywhere
                // from one that is slow from a single host. The first is the
                // array or the fabric; the second is that host's HBA, cable or
                // path.
                // The instance stops naming a device and starts naming an
                // observer, which is exactly what makes these comparable with
                // each other. Said explicitly so a rule never has to infer it.
                Value = value with
                {
                    Instance = measuredBy ?? measuredOn.Value,
                    InstanceIsVantagePoint = true,
                },
                SampledAtUtc = now,
                Source = InstanceId,
            };
        }
    }

    private static List<string> Dequeue(Queue<string> queue, int count)
    {
        var batch = new List<string>(Math.Min(count, queue.Count));
        while (batch.Count < count && queue.Count > 0)
        {
            batch.Add(queue.Dequeue());
        }

        return batch;
    }

    private static void Requeue(Queue<string> queue, List<string> batch)
    {
        var rest = queue.ToList();
        queue.Clear();

        foreach (var item in batch.Concat(rest))
        {
            queue.Enqueue(item);
        }
    }
}
