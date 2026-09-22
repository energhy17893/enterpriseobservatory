using EnterpriseObservatory.Application.Monitoring;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// Where a source's event stream was last read up to.
/// </summary>
/// <remarks>
/// Both halves, not just the key. The key is what makes "new" exact — vCenter
/// numbers its events in order — and the time is what bounds the next read, so
/// that the collector asks the server for a window rather than for its whole
/// history. Either alone is weaker: a time alone re-reads every event in the
/// same second, a key alone cannot be turned into a filter.
/// </remarks>
public sealed record EventMark
{
    public required long Key { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>An object an event names, as the source named it.</summary>
public sealed record EventObjectRef
{
    /// <summary>The source's own reference, e.g. <c>host-42</c>.</summary>
    public required string MoRef { get; init; }

    /// <summary>The name at the moment the event was written.</summary>
    /// <remarks>
    /// Kept as it was, not looked up again. A virtual machine renamed since is
    /// still the one the event is about, and the name it carried then is what
    /// the operator will find in vCenter's own history.
    /// </remarks>
    public required string Name { get; init; }
}

/// <summary>One event a source reported.</summary>
/// <remarks>
/// Close to what vCenter says, on purpose. Turning an event into an alert is a
/// later step with its own rules (roadmap M2.2); this is the record those rules
/// will read, and a record that had already decided what mattered would make
/// the decision unreviewable.
/// </remarks>
public sealed record SourceEvent
{
    /// <summary>The source that reported it. Stamped by the pipeline.</summary>
    public string SourceInstanceId { get; init; } = string.Empty;

    /// <summary>The source's own key for it, increasing over time.</summary>
    public required long Key { get; init; }

    /// <summary>The chain it belongs to — a task and its steps share one.</summary>
    public long? ChainId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// The class the event arrived as, e.g. <c>VmPoweredOffEvent</c> or
    /// <c>EventEx</c>.
    /// </summary>
    public required string EventClass { get; init; }

    /// <summary>
    /// What kind of event this really is.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="EventClass"/>, and conflating them is the
    /// mistake this field exists to prevent. Every ESXi problem — a lost
    /// storage path, an isolated host, a flapping uplink — arrives as the one
    /// class <c>EventEx</c>, and its real identity, say
    /// <c>esx.problem.storage.connectivity.lost</c>, is inside it. A rule that
    /// categorised on the class would file all of them under one heading and be
    /// confidently wrong about every one. For an ordinary event the two are
    /// equal.
    /// </remarks>
    public required string TypeId { get; init; }

    /// <summary>
    /// The source's severity, when it gave one: <c>info</c>, <c>warning</c>,
    /// <c>error</c> or <c>user</c>. Null for classes that carry none.
    /// </summary>
    public string? Severity { get; init; }

    /// <summary>The message as the source rendered it.</summary>
    public required string Message { get; init; }

    /// <summary>Who did it, when a person or account did.</summary>
    public string? UserName { get; init; }

    public string? DatacenterName { get; init; }

    /// <summary>The cluster or standalone compute resource.</summary>
    public EventObjectRef? ComputeResource { get; init; }

    public EventObjectRef? Host { get; init; }

    public EventObjectRef? VirtualMachine { get; init; }

    public EventObjectRef? Datastore { get; init; }
}

/// <summary>What one read of a source's event stream produced.</summary>
public sealed record EventRead
{
    /// <summary>
    /// The events newer than the mark, oldest first; empty when there were
    /// none; <strong>null when the source could not be asked</strong>.
    /// </summary>
    /// <remarks>
    /// Null and empty are different facts and must stay different. An empty
    /// list says "we looked and nothing happened", which moves the read time
    /// forward and is a claim the screen repeats. Null says "we did not see",
    /// and must neither advance the mark nor be shown as a quiet estate.
    /// </remarks>
    public IReadOnlyList<SourceEvent>? Events { get; init; }

    /// <summary>
    /// Whether the read reached the mark, so nothing between it and now is missing.
    /// </summary>
    /// <remarks>
    /// False when the per-read bound was hit first — an event storm, or a first
    /// contact with a busy vCenter. The newest events are kept either way; what
    /// is lost is the middle, and saying so is the difference between a gap
    /// and a history that looks complete.
    /// </remarks>
    public bool Complete { get; init; } = true;

    /// <summary>Why the source could not be asked, when it could not.</summary>
    public string? Detail { get; init; }

    public static EventRead CouldNotAsk(string detail) => new() { Events = null, Complete = false, Detail = detail };
}

/// <summary>Supplies a source's events, newer than a mark.</summary>
/// <remarks>
/// Separate from <see cref="IInventorySource"/> because an event stream has a
/// position and inventory does not: inventory is re-read whole every cycle,
/// while events are read from where the last read stopped. Folding the two
/// together would put the mark inside a collector that is otherwise stateless
/// between cycles.
/// </remarks>
public interface IEventSource
{
    string InstanceId { get; }

    /// <summary>Reads events newer than <paramref name="since"/>, or a bounded recent window when it is null.</summary>
    Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken);
}

/// <summary>Where one source's event stream stands.</summary>
/// <remarks>
/// Durable, and shown. The mark is what makes each read incremental; the
/// attempt and success times are what let the screen say "events last read
/// four minutes ago" or "events could not be read since Tuesday" instead of
/// presenting an old list as a current one.
/// </remarks>
public sealed record EventCursor
{
    public required string SourceInstanceId { get; init; }

    /// <summary>The newest event recorded, or null before the first one.</summary>
    public EventMark? Mark { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Why the last attempt could not read, or null when it could.</summary>
    public string? LastFailure { get; init; }

    /// <summary>When a read last had to stop short of the mark.</summary>
    public DateTimeOffset? LastGapUtc { get; init; }
}

/// <summary>Where collected events are kept.</summary>
public interface IEventStore : IEventReader
{
    /// <summary>Every source's position, including sources that have only ever failed.</summary>
    IReadOnlyList<EventCursor> Cursors { get; }

    /// <summary>
    /// Records a successful read: its events, and the position it reached.
    /// </summary>
    /// <remarks>
    /// Events already held are ignored rather than duplicated, because reads
    /// overlap on purpose — the window starts a little before the mark so that
    /// events written in the same second are not lost.
    /// </remarks>
    void Record(string sourceInstanceId, IReadOnlyList<SourceEvent> events, bool complete, DateTimeOffset readAtUtc);

    /// <summary>Records a read that could not ask. The mark does not move.</summary>
    void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc);

    /// <summary>The newest events, newest first, optionally for one source.</summary>
    IReadOnlyList<SourceEvent> Recent(int limit, string? sourceInstanceId = null);

    /// <summary>Removes events created before the cutoff; returns how many.</summary>
    int Prune(DateTimeOffset createdBeforeUtc);
}

/// <summary>
/// The read side of <see cref="IEventStore"/> that analysis rules are given.
/// </summary>
/// <remarks>
/// Separate so that a rule is handed something that cannot record, prune or
/// move a cursor. <see cref="IEventHistory"/> is the same idea for snapshot
/// attribution, and asks different questions.
/// </remarks>
public interface IEventReader
{
    /// <summary>
    /// Every source's events of the given types created at or after
    /// <paramref name="createdSinceUtc"/>, newest first, at most
    /// <see cref="EventCollectionPipeline.MaxMatching"/> of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For rules that turn events into alerts (roadmap M2.2). They cannot use
    /// <see cref="IEventStore.Recent"/>: that is a page for a screen, capped at
    /// <see cref="EventCollectionPipeline.MaxRecent"/>, and a busy vCenter
    /// writes that many task and login events in well under an hour — the one
    /// "host isolated" event a rule needs would fall off the page while it was
    /// still the most important thing in it.
    /// </para>
    /// <para>
    /// Type ids are matched without regard to case. vCenter itself is not
    /// consistent: its catalogue carries <c>com.vmware.vc.HA.*</c> beside
    /// <c>com.vmware.vc.ha.VmRestartedByHAEvent</c>.
    /// </para>
    /// <para>
    /// When more match than the cap, the cap is spent breadth first: the
    /// events are grouped by source, case-folded type and subject (host,
    /// virtual machine and compute resource), and every group's newest is kept
    /// before any group's second-newest. A storm of one kind therefore drops
    /// only its own older reports; it cannot push out the newest event of some
    /// other type or subject — the one that decides whether an alert is still
    /// open. See <see cref="EventCollectionPipeline.KeepNewestPerGroup"/>.
    /// </para>
    /// </remarks>
    IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc);
}

/// <summary>What one pass over the event sources did.</summary>
public sealed record EventCollectionResult
{
    public int Recorded { get; init; }

    public int Pruned { get; init; }

    /// <summary>Sources that could not be read, with why.</summary>
    public IReadOnlyList<(string Source, string Detail)> Failures { get; init; } = [];

    /// <summary>Sources whose read stopped before reaching the mark.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];
}

/// <summary>
/// Reads each source's new events and keeps them for a bounded time.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the inventory rhythm. Events are low-volume and the stream is read
/// incrementally from a mark, so a five-minute cadence costs one short
/// exchange per vCenter and loses nothing — a later read picks up whatever an
/// earlier one did not reach. What it does cost is latency, and that is the
/// right trade until something (M2.2) turns an event into an alert that has to
/// be fast.
/// </para>
/// <para>
/// One source failing never stops another, and never throws out of here: the
/// caller is the inventory loop, and an event read that could not reach a
/// vCenter must not be logged as the inventory cycle failing.
/// </para>
/// <para>
/// Each read goes through <see cref="SourceRunner"/> in the
/// <see cref="CollectorRole.Events"/> role (F1, ADR-0025 §1): the same breaker,
/// one-strike rule for a rejected login, hard timeout and health record as
/// inventory and observation. Before, it called the source directly with only
/// a cooperative deadline, and a read that ignored the token held the
/// inventory loop.
/// </para>
/// </remarks>
public sealed class EventCollectionPipeline
{
    /// <summary>
    /// How long events are kept. <strong>This product's choice</strong>, not a
    /// cited threshold.
    /// </summary>
    /// <remarks>
    /// Thirty days, matching vCenter's own default event retention, so that
    /// the product never claims to remember less than the server it reads
    /// from while not turning into a second event archive either. Long enough
    /// to answer "who took this snapshot last month" (M2.4); an installation
    /// that needs more should extend vCenter's retention first.
    /// </remarks>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>The most events one page of the listing may return.</summary>
    public const int MaxRecent = 500;

    /// <summary>
    /// The most events one <see cref="IEventReader.OfTypes"/> or
    /// <see cref="IEventHistory.Find"/> read may return.
    /// </summary>
    /// <remarks>
    /// A bound on a storm, not a page size: a host flapping a link for a day
    /// must not turn one rule's read into a million rows. What is kept is each
    /// kind's newest — see <see cref="KeepNewestPerGroup"/> — and those are the
    /// ones that decide whether something is open now.
    /// </remarks>
    public const int MaxMatching = 5000;

    /// <summary>
    /// The bounding rule of <see cref="IEventReader.OfTypes"/>, over events
    /// already filtered by type and time: at most <paramref name="cap"/> of
    /// them, every group's newest before any group's second, returned newest
    /// first.
    /// </summary>
    /// <remarks>
    /// A group is one source, one type id folded to upper case, and one
    /// subject — the host, virtual machine and compute resource the event
    /// names. The Postgres store says the same thing in SQL with
    /// <c>row_number() OVER (PARTITION BY ...)</c>; this is the statement of
    /// it that other stores, and the tests, share.
    /// </remarks>
    public static IReadOnlyList<SourceEvent> KeepNewestPerGroup(IEnumerable<SourceEvent> events, int cap = MaxMatching)
    {
        ArgumentNullException.ThrowIfNull(events);

        return
        [
            .. events
                .GroupBy(e => (
                    e.SourceInstanceId,
                    Type: e.TypeId.ToUpperInvariant(),
                    Host: e.Host?.MoRef,
                    Vm: e.VirtualMachine?.MoRef,
                    ComputeResource: e.ComputeResource?.MoRef))
                .SelectMany(g => g
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .ThenByDescending(e => e.Key)
                    .Select((e, rank) => (Event: e, Rank: rank)))
                .OrderBy(r => r.Rank)
                .ThenByDescending(r => r.Event.CreatedAtUtc)
                .ThenByDescending(r => r.Event.Key)
                .Take(cap)
                .Select(r => r.Event)
                .OrderByDescending(e => e.CreatedAtUtc)
                .ThenByDescending(e => e.Key),
        ];
    }

    private readonly IEventStore _store;
    private readonly IClock _clock;
    private readonly ICollectorHealthStore _health;
    private readonly CollectionPolicy _policy;
    private readonly TimeSpan _defaultDeadline;
    private readonly SourceRunner _runner;
    private readonly TimeProvider _time;

    /// <summary>
    /// A pipeline that keeps its sources' event health in memory, under the
    /// default collection policy.
    /// </summary>
    public EventCollectionPipeline(IEventStore store, IClock clock, TimeProvider? timeProvider = null)
        : this(store, clock, new LocalHealth(), MonitoringOptions.Default, timeProvider)
    {
    }

    /// <summary>
    /// A pipeline that carries each source's event health, one
    /// <see cref="CollectorRole.Events"/> row per source, in
    /// <paramref name="health"/> from one cycle to the next.
    /// </summary>
    public EventCollectionPipeline(
        IEventStore store,
        IClock clock,
        ICollectorHealthStore health,
        MonitoringOptions options,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        ArgumentNullException.ThrowIfNull(options);

        // Not retried within the cycle, as before F1: the event read never
        // was, and the next cycle asks again from the same mark.
        _policy = options.Collection with { MaxRetries = 0 };
        _defaultDeadline = options.EventReadDeadline;
        _time = timeProvider ?? TimeProvider.System;
        _runner = new SourceRunner(_clock, _time);
    }

    public Task<EventCollectionResult> RunAsync(
        IReadOnlyList<IEventSource> sources,
        CancellationToken cancellationToken) =>
        RunAsync(sources, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Reads every source, but stops asking once <paramref name="deadline"/>
    /// has passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is the inventory loop, and without a deadline one slow
    /// vCenter can hold it far past its interval: a read is dozens of calls,
    /// each with its own HTTP timeout, and nothing bounds their sum.
    /// </para>
    /// <para>
    /// A source cut off by the deadline, and any source not reached before it,
    /// is reported as a failure but <strong>not recorded</strong>: its cursor
    /// is left exactly as it was, so the next cycle asks for the same window
    /// again. Only <paramref name="cancellationToken"/> — shutdown — throws.
    /// </para>
    /// </remarks>
    public Task<EventCollectionResult> RunAsync(
        IReadOnlyList<IEventSource> sources,
        TimeSpan deadline,
        CancellationToken cancellationToken) =>
        RunAsync(sources, answered: null, deadline, cancellationToken);

    /// <summary>
    /// As above, asking only the sources named in <paramref name="answered"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before F1 this read did not go through <c>SourceRunner</c>, so a vCenter
    /// that rejected the password was held off for inventory after a single
    /// failure and then presented with the same password by this read, every
    /// cycle. The read now has its own breaker and one-strike rule.
    /// </para>
    /// <para>
    /// The inventory verdict is still borrowed on top of it (F note §7.1,
    /// decided in §8): events are read straight after inventory, from the same
    /// address, in the same session, with the same credential, so a vCenter
    /// whose inventory could not be read this cycle — refused, unreachable, or
    /// backed off from — is not asked here either. Its cursor does not move,
    /// so nothing is lost. The borrowing is removed in a follow-up once the
    /// events health row has been seen in production.
    /// </para>
    /// <para>
    /// Null asks everyone, for callers with no inventory read to go by.
    /// </para>
    /// </remarks>
    public async Task<EventCollectionResult> RunAsync(
        IReadOnlyList<IEventSource> sources,
        IReadOnlyCollection<string>? answered,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var asked = answered?.ToHashSet(StringComparer.Ordinal);
        const string notAsked =
            "not asked for events: this cycle's inventory read of the same vCenter did not " +
            "succeed; its position was kept and the window is read once it answers again";

        // One bound for the whole pass, as before; each source is given what
        // is left of it as its runner timeout. A pass without a deadline of
        // its own is still bounded: the runner has no "forever".
        var bound = deadline == Timeout.InfiniteTimeSpan ? _defaultDeadline : deadline;
        var passStart = _time.GetTimestamp();

        // One at a time, as before F1: the gate keeps the sequential order.
        using var gate = new SemaphoreSlim(1, 1);

        var prior = PriorHealth();
        var health = new List<CollectorHealth>();
        var cursors = _store.Cursors.ToDictionary(c => c.SourceInstanceId, StringComparer.Ordinal);
        var recorded = 0;
        var failures = new List<(string, string)>();
        var gaps = new List<string>();
        var outOfTime = $"the event read did not finish within {bound.TotalSeconds:0} s; " +
                        "its position was kept and the next cycle reads from it again";

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var left = bound - _time.GetElapsedTime(passStart);
            if (left <= TimeSpan.Zero)
            {
                failures.Add((source.InstanceId, outOfTime));
                continue;
            }

            if (asked is not null && !asked.Contains(source.InstanceId))
            {
                // Reported, never silent: "we did not look" must not be
                // readable as "nothing happened".
                failures.Add((source.InstanceId, notAsked));
                TryRecordFailure(source.InstanceId, notAsked);
                continue;
            }

            var since = cursors.TryGetValue(source.InstanceId, out var cursor) ? cursor.Mark : null;
            var attempt = new Attempt();

            var outcome = await _runner.RunAsync(
                source.InstanceId,
                CollectorRole.Events,
                token => ReadOnceAsync(source, since, attempt, token),
                static _ => [],
                SourceRunner.Existing(prior, source.InstanceId, CollectorRole.Events),
                _policy with { SourceTimeout = left },
                gate,
                cancellationToken).ConfigureAwait(false);

            health.Add(outcome.Health);

            if (outcome.Result is not { Events: { } events } read)
            {
                switch (attempt.Conclude())
                {
                    case AttemptState.NotStarted:
                        {
                            // The breaker is open. Said, and recorded against the
                            // cursor, so the screen does not show an old list as
                            // a current one.
                            var detail =
                                $"not asked for events: backing off after {outcome.Health.ConsecutiveFailures} " +
                                $"consecutive failures; last failure: {outcome.Health.LastFailureDetail}";
                            failures.Add((source.InstanceId, detail));
                            TryRecordFailure(source.InstanceId, detail);
                            break;
                        }

                    case AttemptState.Running or AttemptState.CutOff:
                        // Abandoned, or stopped when asked: cut off, not
                        // refused. The cursor is left alone like any other.
                        failures.Add((source.InstanceId, outOfTime));
                        break;

                    default:
                        {
                            var detail = outcome.Health.LastFailureDetail ?? "the source could not be asked for events";
                            failures.Add((source.InstanceId, detail));
                            TryRecordFailure(source.InstanceId, detail);
                            break;
                        }
                }

                continue;
            }

            var now = _clock.UtcNow;

            try
            {
                var stamped = events.Select(e => e with { SourceInstanceId = source.InstanceId }).ToList();
                _store.Record(source.InstanceId, stamped, read.Complete, now);
                recorded += stamped.Count;

                if (!read.Complete)
                {
                    gaps.Add(source.InstanceId);
                }
            }
#pragma warning disable CA1031 // Justified: a store failure is reported, not thrown
            // into the inventory loop; the mark did not move, so the next read
            // asks for the same window again and nothing is lost.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add((source.InstanceId, "events were read but could not be stored: " + ex.Message));
            }
        }

        if (health.Count > 0)
        {
            try
            {
                _health.Merge(health);
            }
#pragma warning disable CA1031 // Justified: as below — reported, not thrown into the loop.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(("health", "event read health could not be stored: " + ex.Message));
            }
        }
        var pruned = 0;
        try
        {
            pruned = _store.Prune(_clock.UtcNow - Retention);
        }
#pragma warning disable CA1031 // Justified: retention runs again next cycle.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(("retention", "old events could not be removed: " + ex.Message));
        }

        return new EventCollectionResult
        {
            Recorded = recorded,
            Pruned = pruned,
            Failures = failures,
            Gaps = gaps,
        };
    }

    /// <summary>Last cycle's event health, or none when the store cannot say.</summary>
    /// <remarks>
    /// None is the safe failure: every source is asked, as it would be on a
    /// fresh installation, rather than the pass throwing into the inventory
    /// loop.
    /// </remarks>
    private IReadOnlyList<CollectorHealth> PriorHealth()
    {
        try
        {
            return _health.Current;
        }
#pragma warning disable CA1031 // Justified: see remarks.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private void TryRecordFailure(string sourceInstanceId, string detail)
    {
        try
        {
            _store.RecordFailure(sourceInstanceId, detail, _clock.UtcNow);
        }
#pragma warning disable CA1031 // Justified: reported, not thrown into the loop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// One read, shaped for the runner: "could not ask" becomes a failure the
    /// runner counts, and a read cut off by the runner's deadline says so.
    /// </summary>
    /// <remarks>
    /// A returned <see cref="EventRead.CouldNotAsk"/> is thrown as a plain,
    /// retryable exception, so a vCenter whose event manager keeps failing is
    /// counted towards the breaker instead of passing as a successful read. A
    /// source's own <see cref="ICollectionFault"/> — a rejected login — is not
    /// touched, so the runner's one-strike rule sees it.
    /// </remarks>
    private static async Task<EventRead> ReadOnceAsync(
        IEventSource source, EventMark? since, Attempt attempt, CancellationToken cancellationToken)
    {
        attempt.Start();
        try
        {
            EventRead read;
            try
            {
                read = await source.ReadAsync(since, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                attempt.CutOff();
                throw;
            }

            if (read.Events is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // A source that turned the deadline into "could not ask"
                    // was cut off, not refused.
                    attempt.CutOff();
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new CouldNotAskException(read.Detail ?? "the source could not be asked for events");
            }

            return read;
        }
        finally
        {
            attempt.Finish();
        }
    }

    private enum AttemptState
    {
        NotStarted,
        Running,
        Finished,
        CutOff,
        Abandoned,
    }

    /// <summary>How far one read got, as seen when the runner returned.</summary>
    /// <remarks>
    /// Needed because the runner reports every failure as health, and the
    /// pipeline must still tell "backed off" (never started), "cut off by the
    /// deadline" (keep the cursor untouched) and "failed" (record why) apart.
    /// Interlocked because an abandoned read finishes on another thread.
    /// </remarks>
    private sealed class Attempt
    {
        private int _state;

        public void Start() => Interlocked.Exchange(ref _state, (int)AttemptState.Running);

        public void CutOff() => Interlocked.Exchange(ref _state, (int)AttemptState.CutOff);

        public void Finish() =>
            Interlocked.CompareExchange(ref _state, (int)AttemptState.Finished, (int)AttemptState.Running);

        /// <summary>The state now; a read still running from here on is abandoned.</summary>
        public AttemptState Conclude() =>
            (AttemptState)Interlocked.CompareExchange(
                ref _state, (int)AttemptState.Abandoned, (int)AttemptState.Running);
    }

    /// <summary>A source said it could not be asked.</summary>
    /// <remarks>Unclassified on purpose, so it stays retryable: see <see cref="ReadOnceAsync"/>.</remarks>
    private sealed class CouldNotAskException(string message) : Exception(message);

    /// <summary>Event health held in memory, for a pipeline given no store.</summary>
    private sealed class LocalHealth : ICollectorHealthStore
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<(string, CollectorRole), CollectorHealth> _rows = [];

        public IReadOnlyList<CollectorHealth> Current
        {
            get
            {
                lock (_lock)
                {
                    return [.. _rows.Values];
                }
            }
        }

        public void Merge(IReadOnlyList<CollectorHealth> health)
        {
            lock (_lock)
            {
                foreach (var h in health)
                {
                    _rows[(h.InstanceId, h.Role)] = h;
                }
            }
        }
    }
}
