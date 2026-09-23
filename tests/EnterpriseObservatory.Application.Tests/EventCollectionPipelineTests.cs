using EnterpriseObservatory.Application.Collection;
using Microsoft.Extensions.Time.Testing;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Reading each source's new events, and what a read that could not ask does.
/// </summary>
public class EventCollectionPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static SourceEvent Event(long key, DateTimeOffset? at = null) => new()
    {
        Key = key,
        CreatedAtUtc = at ?? Now.AddMinutes(-key),
        EventClass = "VmPoweredOnEvent",
        TypeId = "VmPoweredOnEvent",
        Message = $"event {key}",
    };

    private sealed class ScriptedSource(string id, Func<EventMark?, EventRead> read) : IEventSource
    {
        public List<EventMark?> AskedSince { get; } = [];

        public string InstanceId => id;

        public Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
        {
            AskedSince.Add(since);
            return Task.FromResult(read(since));
        }
    }

    /// <summary>The store's rules that decide pipeline behaviour, and nothing more.</summary>
    private sealed class RecordingStore : IEventStore
    {
        private readonly Dictionary<string, EventCursor> _cursors = new(StringComparer.Ordinal);

        public List<SourceEvent> Events { get; } = [];

        public DateTimeOffset? PrunedBefore { get; private set; }

        public IReadOnlyList<EventCursor> Cursors => [.. _cursors.Values];

        public void Record(string sourceInstanceId, IReadOnlyList<SourceEvent> events, bool complete, DateTimeOffset readAtUtc)
        {
            Events.AddRange(events);
            var previous = _cursors.GetValueOrDefault(sourceInstanceId);
            var newest = events.Count == 0 ? null : events.MaxBy(e => e.Key);

            _cursors[sourceInstanceId] = new EventCursor
            {
                SourceInstanceId = sourceInstanceId,
                Mark = newest is null ? previous?.Mark : new EventMark { Key = newest.Key, CreatedAtUtc = newest.CreatedAtUtc },
                LastAttemptUtc = readAtUtc,
                LastSuccessUtc = readAtUtc,
                LastGapUtc = complete ? previous?.LastGapUtc : readAtUtc,
            };
        }

        public void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc) =>
            _cursors[sourceInstanceId] =
                (_cursors.GetValueOrDefault(sourceInstanceId) ?? new EventCursor { SourceInstanceId = sourceInstanceId })
                with { LastAttemptUtc = attemptedAtUtc, LastFailure = detail };

        public EventPage Recent(int offset, int limit, string? sourceInstanceId = null, string? search = null) => new(Events, Events.Count);

        public int Prune(DateTimeOffset createdBeforeUtc)
        {
            PrunedBefore = createdBeforeUtc;
            return 0;
        }

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }

    [Fact]
    public async Task A_source_whose_inventory_did_not_answer_is_not_asked_for_events()
    {
        // The event read shares the inventory read's session and credential but
        // never went through the runner, so nothing held it off: with a rejected
        // password the inventory breaker opened after one strike and this read
        // went on presenting the same password every cycle regardless. The
        // inventory read has already answered "is this vCenter worth asking
        // right now", so its answer is used rather than asked again.
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));
        var reachable = new ScriptedSource("vc-ok", _ => new EventRead { Events = [Event(1)] });
        var refused = new ScriptedSource("vc-refused", _ => new EventRead { Events = [Event(2)] });

        var result = await pipeline.RunAsync(
            [reachable, refused], answered: ["vc-ok"], Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.Single(reachable.AskedSince);
        Assert.Empty(refused.AskedSince);

        // Said out loud: not asking must never read as "nothing happened".
        var (source, detail) = Assert.Single(result.Failures);
        Assert.Equal("vc-refused", source);
        Assert.Contains("not asked", detail, StringComparison.Ordinal);

        // And its position is kept, so the window is read once it answers again.
        var cursor = Assert.Single(store.Cursors, c => c.SourceInstanceId == "vc-refused");
        Assert.Null(cursor.Mark);
        Assert.Null(cursor.LastSuccessUtc);
    }

    [Fact]
    public async Task Each_read_starts_from_where_the_last_one_stopped()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));
        var source = new ScriptedSource("vc-1", since => new EventRead
        {
            Events = since is null ? [Event(1), Event(2)] : [Event(3)],
        });

        await pipeline.RunAsync([source], CancellationToken.None);
        await pipeline.RunAsync([source], CancellationToken.None);

        Assert.Null(source.AskedSince[0]);
        Assert.Equal(2, source.AskedSince[1]?.Key);
        Assert.Equal(3, Assert.Single(store.Cursors).Mark?.Key);
    }

    [Fact]
    public async Task Events_are_attributed_to_the_source_that_read_them()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));

        await pipeline.RunAsync(
            [new ScriptedSource("vc-1", _ => new EventRead { Events = [Event(1)] })],
            CancellationToken.None);

        Assert.Equal("vc-1", Assert.Single(store.Events).SourceInstanceId);
    }

    [Fact]
    public async Task A_source_that_could_not_be_asked_keeps_its_mark_and_says_why()
    {
        // Null must never be read as "nothing happened": the mark stays where
        // it was, so the next read asks for the same window again, and the
        // failure is recorded for the screen rather than swallowed.
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));
        var fails = false;
        var source = new ScriptedSource("vc-1", _ => fails
            ? EventRead.CouldNotAsk("vCenter did not answer")
            : new EventRead { Events = [Event(5)] });

        await pipeline.RunAsync([source], CancellationToken.None);
        fails = true;
        var result = await pipeline.RunAsync([source], CancellationToken.None);

        var cursor = Assert.Single(store.Cursors);
        Assert.Equal(5, cursor.Mark?.Key);
        Assert.Equal("vCenter did not answer", cursor.LastFailure);
        Assert.Equal("vc-1", Assert.Single(result.Failures).Source);
    }

    [Fact]
    public async Task A_quiet_window_is_a_success_and_clears_an_earlier_failure()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));
        var quiet = false;
        var source = new ScriptedSource("vc-1", _ => quiet
            ? new EventRead { Events = [] }
            : EventRead.CouldNotAsk("down"));

        await pipeline.RunAsync([source], CancellationToken.None);
        quiet = true;
        var result = await pipeline.RunAsync([source], CancellationToken.None);

        var cursor = Assert.Single(store.Cursors);
        Assert.Null(cursor.LastFailure);
        Assert.Equal(Now, cursor.LastSuccessUtc);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task One_source_throwing_does_not_stop_the_next()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));

        var result = await pipeline.RunAsync(
            [
                new ScriptedSource("vc-broken", _ => throw new InvalidOperationException("boom")),
                new ScriptedSource("vc-fine", _ => new EventRead { Events = [Event(1)] }),
            ],
            CancellationToken.None);

        Assert.Equal(1, result.Recorded);
        Assert.Equal("vc-broken", Assert.Single(result.Failures).Source);
        Assert.Equal("boom", store.Cursors.Single(c => c.SourceInstanceId == "vc-broken").LastFailure);
    }

    [Fact]
    public async Task A_read_that_stopped_short_is_reported_as_a_gap()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));

        var result = await pipeline.RunAsync(
            [new ScriptedSource("vc-1", _ => new EventRead { Events = [Event(1)], Complete = false })],
            CancellationToken.None);

        Assert.Equal("vc-1", Assert.Single(result.Gaps));
        Assert.Equal(Now, Assert.Single(store.Cursors).LastGapUtc);
    }

    /// <summary>A source that never answers until it is cancelled.</summary>
    private sealed class HangingSource(string id) : IEventSource
    {
        public string InstanceId => id;

        public async Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new EventRead { Events = [] };
        }
    }

    /// <summary>
    /// Pumps <paramref name="time"/> forward in generous jumps until
    /// <paramref name="task"/> completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every timeout the pipeline and <c>SourceRunner</c> reason about — the
    /// per-source grace, the hard timeout, the pass deadline — comes from
    /// <paramref name="time"/> here, not the wall clock, so what decides this
    /// test is <c>Advance</c>, not how fast this machine happens to be. Before,
    /// with the real clock, a source cut off right at the deadline left a
    /// grace-sized sliver (10% of the source timeout) that a fast or lightly
    /// loaded run did not spend before the next source's own turn, letting it
    /// slip in and succeed — on a slower or busier run the same sliver was
    /// gone by the time the loop got there. Jumping in whole seconds, far past
    /// every threshold in one step, removes that sliver instead of hoping it
    /// is never there: every source after the stuck one sees the deadline
    /// already spent, on every run.
    /// </para>
    /// <para>
    /// A due FakeTimeProvider callback still has to be dispatched and, under
    /// the test host, that dispatch is posted rather than guaranteed to run
    /// inline within <c>Advance</c> — so the loop also yields real
    /// (negligible) time between jumps to let it land. That yield decides
    /// nothing about the test's outcome, only how promptly this loop notices a
    /// virtual-time transition has already happened; the loop is bounded
    /// generously so it cannot hang.
    /// </para>
    /// </remarks>
    private static async Task<T> RunToCompletionAsync<T>(FakeTimeProvider time, Task<T> task, TimeSpan step)
    {
        var deadline = Environment.TickCount64 + 10_000;

        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            time.Advance(step);
            await Task.Delay(1).ConfigureAwait(false);
        }

        Assert.True(task.IsCompleted, "Pipeline did not complete after pumping the fake clock forward.");
        return await task.ConfigureAwait(false);
    }

    [Fact]
    public async Task A_read_past_the_deadline_is_cut_off_and_keeps_its_cursor()
    {
        // One stalled vCenter must not hold the inventory loop: the pass ends
        // at the deadline, does not throw, and leaves the stalled source's
        // cursor untouched so the next cycle asks for the same window again.
        // A FakeTimeProvider drives every timeout here — see
        // RunToCompletionAsync — so the second source's fate does not depend
        // on real elapsed time either.
        var store = new RecordingStore();
        store.Record("vc-slow", [Event(7)], complete: true, Now.AddMinutes(-5));
        var before = Assert.Single(store.Cursors);
        var time = new FakeTimeProvider();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now), time);

        var run = pipeline.RunAsync(
            [new HangingSource("vc-slow"), new ScriptedSource("vc-later", _ => new EventRead { Events = [Event(1)] })],
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        var result = await RunToCompletionAsync(time, run, TimeSpan.FromSeconds(1));

        Assert.Equal(["vc-slow", "vc-later"], result.Failures.Select(f => f.Source));
        Assert.Equal(before, store.Cursors.Single(c => c.SourceInstanceId == "vc-slow"));
        Assert.DoesNotContain(store.Cursors, c => c.SourceInstanceId == "vc-later");
    }

    [Fact]
    public async Task Shutdown_during_a_deadline_bounded_read_still_throws()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(
            [new HangingSource("vc-slow")], TimeSpan.FromMinutes(5), shutdown.Token));
    }

    [Fact]
    public void The_default_event_deadline_is_well_inside_the_inventory_interval()
    {
        var options = Monitoring.MonitoringOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(90), options.EventReadDeadline);
        Assert.True(options.EventReadDeadline < options.InventoryInterval);
    }

    private static SourceEvent Typed(long key, string type, string host, DateTimeOffset at) => new()
    {
        SourceInstanceId = "vc-1",
        Key = key,
        CreatedAtUtc = at,
        EventClass = "EventEx",
        TypeId = type,
        Message = type,
        Host = new EventObjectRef { MoRef = host, Name = host },
    };

    [Fact]
    public void A_storm_of_one_kind_cannot_push_out_the_newest_of_another()
    {
        // Twenty hours ago a host was isolated; since then another host's
        // uplink has flapped far more often than the cap. The isolation is the
        // one event that keeps its alert open and must survive the cap.
        var isolation = Typed(1, "com.vmware.vc.HA.HostIsolatedEvent", "host-1", Now.AddHours(-20));
        var storm = Enumerable.Range(0, 50)
            .Select(i => Typed(100 + i, "esx.problem.net.redundancy.lost", "host-2", Now.AddMinutes(-i)));

        var kept = EventCollectionPipeline.KeepNewestPerGroup([isolation, .. storm], cap: 10);

        Assert.Equal(10, kept.Count);
        Assert.Contains(isolation, kept);
        Assert.Equal(100, kept[0].Key);
        Assert.Equal(kept.OrderByDescending(e => e.CreatedAtUtc), kept);
    }

    [Fact]
    public void Groups_fold_type_case_and_split_by_subject()
    {
        var at = Now.AddHours(-1);
        var events = new[]
        {
            Typed(1, "com.vmware.vc.HA.X", "host-1", at),
            Typed(2, "com.vmware.vc.ha.x", "host-1", at.AddMinutes(1)),
            Typed(3, "com.vmware.vc.ha.x", "host-2", at),
        };

        // Keys 1 and 2 are one group; its older member is the one dropped.
        var kept = EventCollectionPipeline.KeepNewestPerGroup(events, cap: 2);

        Assert.Equal([2L, 3L], kept.Select(e => e.Key));
    }

    [Fact]
    public async Task Events_older_than_the_retention_window_are_removed_every_pass()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));

        await pipeline.RunAsync([], CancellationToken.None);

        Assert.Equal(Now - EventCollectionPipeline.Retention, store.PrunedBefore);
    }

    // --- F1: the event read goes through SourceRunner ----------------------

    /// <summary>A fault the source classified, as a vendor client would throw it.</summary>
    private sealed class ClassifiedFault(CollectionFailureKind kind, string message)
        : Exception(message), ICollectionFault
    {
        public CollectionFailureKind Kind => kind;
    }

    private sealed class CountingSource(string id, Func<EventRead> read) : IEventSource
    {
        public int Calls { get; private set; }

        public string InstanceId => id;

        public Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(read());
        }
    }

    /// <summary>A read that ignores the token: only a hard timeout can walk away from it.</summary>
    private sealed class DeafSource(string id) : IEventSource
    {
        public string InstanceId => id;

        public async Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return new EventRead { Events = [] };
        }
    }

    private sealed class HealthStore : Monitoring.ICollectorHealthStore
    {
        private readonly Dictionary<(string, CollectorRole), CollectorHealth> _rows = [];

        public IReadOnlyList<CollectorHealth> Current => [.. _rows.Values];

        public void Merge(IReadOnlyList<CollectorHealth> health)
        {
            foreach (var h in health)
            {
                _rows[(h.InstanceId, h.Role)] = h;
            }
        }
    }

    [Fact]
    public async Task A_rejected_login_on_the_event_read_is_one_strike_and_not_asked_again()
    {
        var store = new RecordingStore();
        var health = new HealthStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now), health, Monitoring.MonitoringOptions.Default);
        var source = new CountingSource(
            "vc-1", () => throw new ClassifiedFault(CollectionFailureKind.AuthenticationRejected, "Cannot complete login"));

        var first = await pipeline.RunAsync([source], CancellationToken.None);
        var second = await pipeline.RunAsync([source], CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Equal("vc-1", Assert.Single(first.Failures).Source);
        Assert.Equal("vc-1", Assert.Single(second.Failures).Source);

        var row = Assert.Single(health.Current);
        Assert.Equal(CollectorRole.Events, row.Role);
        Assert.Equal(CollectionFailureKind.AuthenticationRejected, row.LastFailureKind);
        Assert.True(row.IsBackingOff);
    }

    [Fact]
    public async Task A_transient_failure_on_the_event_read_is_not_retried_within_the_cycle()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now), new HealthStore(), Monitoring.MonitoringOptions.Default);
        var source = new CountingSource("vc-1", () => throw new InvalidOperationException("reset"));

        await pipeline.RunAsync([source], CancellationToken.None);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_read_that_ignores_the_token_is_abandoned_at_the_deadline_and_counted()
    {
        var store = new RecordingStore();
        store.Record("vc-deaf", [Event(7)], complete: true, Now.AddMinutes(-5));
        var before = Assert.Single(store.Cursors);
        var health = new HealthStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now), health, Monitoring.MonitoringOptions.Default);

        var run = pipeline.RunAsync(
            [new DeafSource("vc-deaf")], answered: null, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(run, finished);

        var result = await run;
        Assert.Equal("vc-deaf", Assert.Single(result.Failures).Source);
        Assert.Equal(before, Assert.Single(store.Cursors));

        var row = Assert.Single(health.Current);
        Assert.Equal(CollectorRole.Events, row.Role);
        Assert.Equal(1, row.ConsecutiveFailures);
        Assert.NotNull(row.LastAttemptUtc);
    }

    [Fact]
    public async Task A_successful_event_read_writes_a_healthy_events_row()
    {
        var health = new HealthStore();
        var pipeline = new EventCollectionPipeline(new RecordingStore(), new FixedClock(Now), health, Monitoring.MonitoringOptions.Default);

        await pipeline.RunAsync([new ScriptedSource("vc-1", _ => new EventRead { Events = [Event(1)] })], CancellationToken.None);

        var row = Assert.Single(health.Current);
        Assert.Equal(CollectorRole.Events, row.Role);
        Assert.Equal(Domain.HealthState.Healthy, row.Health);
        Assert.Equal(Now, row.LastSuccessUtc);
    }

    [Fact]
    public async Task A_source_whose_inventory_did_not_answer_gets_no_events_health_row()
    {
        var health = new HealthStore();
        var pipeline = new EventCollectionPipeline(new RecordingStore(), new FixedClock(Now), health, Monitoring.MonitoringOptions.Default);
        var source = new CountingSource("vc-1", () => new EventRead { Events = [] });

        await pipeline.RunAsync([source], answered: [], Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.Equal(0, source.Calls);
        Assert.Empty(health.Current);
    }
}
