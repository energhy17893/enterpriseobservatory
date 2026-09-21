using EnterpriseObservatory.Application.Collection;

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

        public IReadOnlyList<SourceEvent> Recent(int limit, string? sourceInstanceId = null) => Events;

        public int Prune(DateTimeOffset createdBeforeUtc)
        {
            PrunedBefore = createdBeforeUtc;
            return 0;
        }

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
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

    [Fact]
    public async Task Events_older_than_the_retention_window_are_removed_every_pass()
    {
        var store = new RecordingStore();
        var pipeline = new EventCollectionPipeline(store, new FixedClock(Now));

        await pipeline.RunAsync([], CancellationToken.None);

        Assert.Equal(Now - EventCollectionPipeline.Retention, store.PrunedBefore);
    }
}
