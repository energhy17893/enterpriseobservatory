using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// How far each source's events are known to have been read, and what the
/// event rule does with that (ADR-0026 §5.7, F note §6).
/// </summary>
/// <remarks>
/// The rule re-derives every open condition from stored events. "No clear and
/// no new report in the store" means "absent" only if the store holds
/// everything up to now; for a source whose read failed, was abandoned or
/// stopped short, it means "not looked", and must not resolve anything.
/// </remarks>
public class EventReadWatermarkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Interval = new MonitoringOptions().InventoryInterval;

    // --- the watermark, as the pipeline moves it -----------------------------

    private sealed class SteppingClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class ScriptedSource(string id) : IEventSource
    {
        public Func<EventRead> Next { get; set; } = () => new EventRead { Events = [] };

        public string InstanceId => id;

        public Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken) =>
            Task.FromResult(Next());
    }

    /// <summary>A read that ignores the token: only the hard timeout walks away from it.</summary>
    private sealed class DeafSource(string id) : IEventSource
    {
        public string InstanceId => id;

        public async Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return new EventRead { Events = [] };
        }
    }

    /// <summary>The cursor rules every store follows, and nothing more.</summary>
    private sealed class CursorStore : IEventStore
    {
        private readonly Dictionary<string, EventCursor> _cursors = new(StringComparer.Ordinal);

        public IReadOnlyList<EventCursor> Cursors => [.. _cursors.Values];

        public void Record(string sourceInstanceId, IReadOnlyList<SourceEvent> events, bool complete, DateTimeOffset readAtUtc)
        {
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

        public EventPage Recent(int offset, int limit, string? sourceInstanceId = null, string? search = null) => EventPage.Empty;

        public int Prune(DateTimeOffset createdBeforeUtc) => 0;

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }

    private static DateTimeOffset? WatermarkOf(IEventReader store, string source) =>
        store.ReadWatermarks.SingleOrDefault(w => w.SourceInstanceId == source)?.ReadThroughUtc;

    private static SourceEvent Event(long key, DateTimeOffset at) => new()
    {
        Key = key,
        CreatedAtUtc = at,
        EventClass = "VmPoweredOnEvent",
        TypeId = "VmPoweredOnEvent",
        Message = $"event {key}",
    };

    [Fact]
    public async Task A_complete_read_moves_the_watermark_to_the_time_of_the_read()
    {
        var store = new CursorStore();
        var clock = new SteppingClock(T0);
        var pipeline = new EventCollectionPipeline(store, clock);
        var source = new ScriptedSource("vc-1") { Next = () => new EventRead { Events = [Event(1, T0.AddMinutes(-1))] } };

        Assert.Null(WatermarkOf(store, "vc-1"));

        await pipeline.RunAsync([source], CancellationToken.None);
        Assert.Equal(T0, WatermarkOf(store, "vc-1"));

        // "Nothing happened" is a complete read too: it moves the watermark.
        clock.UtcNow = T0.AddMinutes(5);
        source.Next = () => new EventRead { Events = [] };
        await pipeline.RunAsync([source], CancellationToken.None);
        Assert.Equal(T0.AddMinutes(5), WatermarkOf(store, "vc-1"));
    }

    [Fact]
    public async Task A_read_that_could_not_ask_leaves_the_watermark_where_it_was()
    {
        var store = new CursorStore();
        var clock = new SteppingClock(T0);
        var pipeline = new EventCollectionPipeline(store, clock);
        var source = new ScriptedSource("vc-1");

        await pipeline.RunAsync([source], CancellationToken.None);

        clock.UtcNow = T0.AddMinutes(5);
        source.Next = () => EventRead.CouldNotAsk("event manager did not answer");
        await pipeline.RunAsync([source], CancellationToken.None);

        clock.UtcNow = T0.AddMinutes(10);
        source.Next = () => throw new InvalidOperationException("connection reset");
        await pipeline.RunAsync([source], CancellationToken.None);

        Assert.Equal(T0, WatermarkOf(store, "vc-1"));
    }

    [Fact]
    public async Task A_source_not_asked_because_its_inventory_did_not_answer_keeps_its_watermark()
    {
        var store = new CursorStore();
        var clock = new SteppingClock(T0);
        var pipeline = new EventCollectionPipeline(store, clock);
        var source = new ScriptedSource("vc-1");

        await pipeline.RunAsync([source], CancellationToken.None);

        clock.UtcNow = T0.AddMinutes(5);
        await pipeline.RunAsync([source], answered: [], Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.Equal(T0, WatermarkOf(store, "vc-1"));
    }

    [Fact]
    public async Task An_abandoned_read_leaves_the_watermark_where_it_was()
    {
        var store = new CursorStore();
        store.Record("vc-deaf", [Event(7, T0.AddMinutes(-6))], complete: true, T0.AddMinutes(-5));
        var pipeline = new EventCollectionPipeline(store, new SteppingClock(T0));

        var run = pipeline.RunAsync(
            [new DeafSource("vc-deaf")], answered: null, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))));
        await run;

        Assert.Equal(T0.AddMinutes(-5), WatermarkOf(store, "vc-deaf"));
    }

    [Fact]
    public async Task A_read_that_stopped_short_of_its_mark_does_not_advance_the_watermark()
    {
        // The newest events are kept and the middle is lost, so the stream is
        // not known to be read up to this read's time. The cursor keeps no
        // earlier complete time, so the watermark is withdrawn rather than
        // moved: "not known" until the next complete read.
        var store = new CursorStore();
        var clock = new SteppingClock(T0);
        var pipeline = new EventCollectionPipeline(store, clock);
        var source = new ScriptedSource("vc-1");

        await pipeline.RunAsync([source], CancellationToken.None);

        clock.UtcNow = T0.AddMinutes(5);
        source.Next = () => new EventRead { Events = [Event(9, T0.AddMinutes(4))], Complete = false };
        await pipeline.RunAsync([source], CancellationToken.None);

        var watermark = Assert.Single(((IEventReader)store).ReadWatermarks);
        Assert.Null(watermark.ReadThroughUtc);
        Assert.Contains("stopped short", watermark.Detail, StringComparison.Ordinal);

        clock.UtcNow = T0.AddMinutes(10);
        source.Next = () => new EventRead { Events = [] };
        await pipeline.RunAsync([source], CancellationToken.None);

        Assert.Equal(T0.AddMinutes(10), WatermarkOf(store, "vc-1"));
    }

    [Fact]
    public void A_source_that_has_only_ever_failed_has_no_watermark_and_says_why()
    {
        var store = new CursorStore();
        store.RecordFailure("vc-1", "vCenter did not answer", T0);

        var watermark = Assert.Single(((IEventReader)store).ReadWatermarks);
        Assert.Null(watermark.ReadThroughUtc);
        Assert.Contains("vCenter did not answer", watermark.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reader_that_keeps_no_cursors_vouches_for_nothing()
    {
        Assert.Empty(new Events([]).ReadWatermarks);
    }

    // --- the event rule --------------------------------------------------------

    private static readonly EventObjectRef Esx01 = new() { MoRef = "host-1", Name = "esx01" };

    private const string Lost = "esx.problem.storage.connectivity.lost";

    private const string LostMessage =
        "Lost connectivity to storage device naa.600a0b80. Path vmhba64:C4:T0:L0 is down.";

    private sealed class Events(IReadOnlyList<SourceEvent> events, params EventReadWatermark[] watermarks) : IEventReader
    {
        public IReadOnlyList<EventReadWatermark> ReadWatermarks => watermarks;

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) =>
            [.. events.Where(e => typeIds.Contains(e.TypeId, StringComparer.OrdinalIgnoreCase) && e.CreatedAtUtc >= createdSinceUtc)];
    }

    private sealed class NoSeries : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query) => new()
        {
            Key = query.Key,
            Resolution = SeriesResolution.FiveMinutes,
        };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    /// <summary>An <see cref="IEventReader"/> that only answers <c>OfTypes</c>, as one written before watermarks would.</summary>
    private sealed class BareEvents : IEventReader
    {
        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }

    private static SourceEvent Report(string source = "vc-1", DateTimeOffset? at = null) => new()
    {
        SourceInstanceId = source,
        Key = 1,
        CreatedAtUtc = at ?? T0.AddMinutes(-30),
        EventClass = "EventEx",
        TypeId = Lost,
        Message = LostMessage,
        Host = Esx01,
    };

    /// <summary>The alert a report from <paramref name="source"/> would hold open.</summary>
    private static AlertDefinition HeldFor(string source = "vc-1") =>
        Assert.Single(EventAlerts.Evaluate([Report(source)], T0));

    private static EventReadWatermark ReadThrough(string source, DateTimeOffset? at, string? detail = null) =>
        new() { SourceInstanceId = source, ReadThroughUtc = at, Detail = detail };

    private static RuleContext Context(IEventReader events, params AlertDefinition[] held) => new()
    {
        ReadGraph = () => EntityGraph.Empty,
        NowUtc = T0,
        Options = new MonitoringOptions(),
        Series = new NoSeries(),
        Events = events,
        HeldBy = rule => rule == EventAlerts.RuleId
            ? [.. held.Select(a => new HeldAlert(a.Fingerprint, a.Entity))]
            : [],
    };

    [Fact]
    public void With_a_fresh_watermark_and_no_event_a_held_alert_is_absent()
    {
        var held = HeldFor();

        var verdict = Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([], ReadThrough("vc-1", T0.AddMinutes(-4))), held)));

        var absent = Assert.IsType<ConditionAbsent>(verdict);
        Assert.Equal([held.Fingerprint], absent.Covers);
    }

    [Fact]
    public void With_a_stale_watermark_a_held_alert_is_unknown_not_absent()
    {
        // The source's last complete read was three cycles ago: its clear, or
        // its next report, may be sitting unread in vCenter.
        var held = HeldFor();
        var readThrough = T0 - (Interval * 3);

        var verdict = Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([], ReadThrough("vc-1", readThrough)), held)));

        var unknown = Assert.IsType<Unknown>(verdict);
        Assert.Equal(UnknownReason.SourceSilent, unknown.Reason);
        Assert.Equal([held.Fingerprint], unknown.Covers);
        Assert.Equal(held.Entity, unknown.Entity);
        Assert.Contains("events not read up to", unknown.Detail, StringComparison.Ordinal);
        Assert.Contains("'vc-1'", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void One_missed_read_is_already_stale()
    {
        // Events are read after each inventory cycle, so a fresh watermark is
        // at most one interval old; two intervals means one read did not land.
        var held = HeldFor();

        var verdict = Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([], ReadThrough("vc-1", T0 - (Interval * 2))), held)));

        Assert.IsType<Unknown>(verdict);
    }

    [Fact]
    public void A_source_whose_latest_read_stopped_short_is_unknown()
    {
        var held = HeldFor();

        var verdict = Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([], ReadThrough("vc-1", null, "the latest read stopped short")), held)));

        var unknown = Assert.IsType<Unknown>(verdict);
        Assert.Contains("stopped short", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_with_no_watermark_at_all_is_unknown()
    {
        var held = HeldFor();

        Assert.IsType<Unknown>(Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([], ReadThrough("vc-other", T0)), held))));
    }

    [Fact]
    public void A_reader_that_cannot_say_how_far_it_has_read_resolves_nothing()
    {
        // The forgetful wiring is the safe one: a reader without watermarks
        // keeps every held alert open.
        var held = HeldFor();

        Assert.IsType<Unknown>(Assert.Single(new EventAlertsRule().Evaluate(Context(new BareEvents(), held))));
    }

    [Fact]
    public void A_stale_source_does_not_hold_another_sources_alert()
    {
        var mine = HeldFor("vc-1");
        var theirs = HeldFor("vc-2");

        var verdicts = new EventAlertsRule().Evaluate(Context(
            new Events([], ReadThrough("vc-1", T0.AddMinutes(-4)), ReadThrough("vc-2", T0.AddHours(-2))),
            mine,
            theirs));

        Assert.IsType<ConditionAbsent>(Assert.Single(verdicts, v => v.Covers.Contains(mine.Fingerprint)));
        Assert.IsType<Unknown>(Assert.Single(verdicts, v => v.Covers.Contains(theirs.Fingerprint)));
    }

    [Fact]
    public void A_condition_still_reported_is_present_as_of_a_fresh_read_and_source_silent_on_a_stale_one()
    {
        // An event cannot be re-read: its evidence holds while its vCenter's
        // stream is read through, dated at that read (ADR-0026 §3). A clear may
        // sit unread behind a stale watermark, so there it is unknown.
        var held = HeldFor();

        var fresh = Assert.IsType<ConditionPresent>(Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([Report()], ReadThrough("vc-1", T0)), held))));
        Assert.Equal(T0, fresh.EvidenceAtUtc);

        var stale = Assert.IsType<Unknown>(Assert.Single(new EventAlertsRule().Evaluate(
            Context(new Events([Report()], ReadThrough("vc-1", T0.AddHours(-2))), held))));
        Assert.Equal(UnknownReason.SourceSilent, stale.Reason);
    }

    [Fact]
    public void An_alert_with_no_subject_is_absent_only_when_every_source_is_fresh()
    {
        // No host and no cluster: the alert is filed against the vCenter and
        // carries no entity, so its source cannot be read off it.
        var bare = Report() with { Host = null };
        var held = Assert.Single(EventAlerts.Evaluate([bare], T0));
        Assert.Null(held.Entity);

        Assert.IsType<ConditionAbsent>(Assert.Single(new EventAlertsRule().Evaluate(Context(
            new Events([], ReadThrough("vc-1", T0.AddMinutes(-4)), ReadThrough("vc-2", T0.AddMinutes(-3))), held))));

        Assert.IsType<Unknown>(Assert.Single(new EventAlertsRule().Evaluate(Context(
            new Events([], ReadThrough("vc-1", T0.AddMinutes(-4)), ReadThrough("vc-2", T0.AddHours(-3))), held))));
    }

    [Fact]
    public void A_source_whose_id_prefixes_another_is_not_confused_with_it()
    {
        // "vc-1" is a prefix of "vc-10"; the owner is the source before the
        // separator, not the first id the entity happens to start with.
        var held = HeldFor("vc-10");

        Assert.IsType<Unknown>(Assert.Single(new EventAlertsRule().Evaluate(Context(
            new Events([], ReadThrough("vc-1", T0.AddMinutes(-4)), ReadThrough("vc-10", T0.AddHours(-3))), held))));
    }
}
