using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The measurement store, against a real server.
/// </summary>
/// <remarks>
/// Weighted towards what a storage layer gets quietly wrong rather than what it
/// obviously gets right. An adapter that cannot store a number fails loudly on
/// the first cycle; one that loses the newest point, folds a bucket twice, or
/// returns a gap as a zero looks correct for months.
/// <para>
/// A schema per test rather than per class. The compaction watermark is global
/// to a database — deliberately, since it is what stops a pass rescanning the
/// whole history — so tests that compact would otherwise move it past each
/// other's data and pass or fail depending on the order they ran in.
/// </para>
/// </remarks>
public class ObservationStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private PostgresObservationStore Store() => new(_live.Database);

    /// <summary>Skips with a reason when there is no database to talk to.</summary>
    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static EntityId Host(string name = "host-1") => EntityId.For("vc-1", name);

    private static Observation Sample(
        DateTimeOffset at, double value, string counter = "cpu.usage.average", string instance = "") =>
        new()
        {
            Entity = Host(),
            Source = "vc-1",
            SampledAtUtc = at,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = value,
                Rollup = RollupType.Average,
                Interval = TimeSpan.FromSeconds(20),
                Unit = "percent",
                Instance = instance,
            },
        };

    private static SeriesQuery Ask(
        DateTimeOffset from,
        DateTimeOffset to,
        SeriesResolution? resolution = null,
        int maxPoints = 720,
        string counter = "cpu.usage.average") => new()
        {
            Key = new SeriesKey(Host(), counter, string.Empty),
            FromUtc = from,
            ToUtc = to,
            Resolution = resolution,
            MaxPoints = maxPoints,
        };

    [SkippableFact]
    public void A_sample_written_can_be_read_back()
    {
        RequireDatabase();

        var store = Store();
        store.Append([Sample(T0, 17.5)]);

        var result = store.Query(Ask(T0.AddMinutes(-1), T0.AddMinutes(1), SeriesResolution.Raw));

        Assert.True(result.Exists);
        Assert.Equal(17.5, Assert.Single(result.Points).Last);
        Assert.Equal("percent", result.Unit);
        Assert.Equal(RollupType.Average, result.Rollup);
    }

    [SkippableFact]
    public void A_series_nobody_recorded_says_so_rather_than_returning_nothing()
    {
        RequireDatabase();

        var result = Store().Query(Ask(T0, T0.AddHours(1), counter: "never.collected.average"));

        // "We have never heard of this" and "this reported nothing" are
        // different answers and the caller has to tell them apart.
        Assert.False(result.Exists);
        Assert.Empty(result.Points);
    }

    [SkippableFact]
    public void The_same_sample_arriving_twice_is_stored_once()
    {
        RequireDatabase();

        // Normal operation, not an edge case: vCenter returns several samples
        // per query and consecutive cycles overlap, so the same reading arrives
        // more than once. COPY cannot express a conflict rule, and a failed
        // batch would lose a whole cycle over a duplicate.
        var store = Store();
        var at = T0.AddMinutes(5);

        store.Append([Sample(at, 20)]);
        store.Append([Sample(at, 20)]);

        var points = store.Query(Ask(at.AddSeconds(-1), at.AddSeconds(1), SeriesResolution.Raw)).Points;

        Assert.Single(points);
    }

    [SkippableFact]
    public void A_batch_of_many_samples_is_written_whole()
    {
        RequireDatabase();

        // The path that matters in production: a mid-sized estate appends
        // thousands per interval, and this is the COPY route rather than the
        // row-by-row one.
        var store = Store();
        var start = T0.AddHours(1);

        store.Append([.. Enumerable.Range(0, 500)
            .Select(i => Sample(start.AddSeconds(i * 20), i, counter: "bulk.counter.average"))]);

        var result = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "bulk.counter.average", string.Empty),
            FromUtc = start.AddSeconds(-1),
            ToUtc = start.AddSeconds(500 * 20),
            Resolution = SeriesResolution.Raw,
            MaxPoints = 1000,
        });

        Assert.Equal(500, result.Points.Count);
        Assert.False(result.Truncated);
    }

    [SkippableFact]
    public void Points_come_back_oldest_first()
    {
        RequireDatabase();

        var store = Store();
        var start = T0.AddHours(2);

        store.Append([
            Sample(start.AddMinutes(2), 3, counter: "order.counter.average"),
            Sample(start, 1, counter: "order.counter.average"),
            Sample(start.AddMinutes(1), 2, counter: "order.counter.average"),
        ]);

        var points = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "order.counter.average", string.Empty),
            FromUtc = start.AddSeconds(-1),
            ToUtc = start.AddMinutes(5),
            Resolution = SeriesResolution.Raw,
        }).Points;

        Assert.Equal([1d, 2d, 3d], points.Select(p => p.Last));
    }

    [SkippableFact]
    public void A_response_is_bounded_and_keeps_the_recent_end()
    {
        RequireDatabase();

        // A chart missing the present is useless in a way one missing the past
        // is not, so truncation drops the oldest — and says that it did.
        var store = Store();
        var start = T0.AddHours(3);

        store.Append([.. Enumerable.Range(0, 20)
            .Select(i => Sample(start.AddSeconds(i * 20), i, counter: "bounded.counter.average"))]);

        var result = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "bounded.counter.average", string.Empty),
            FromUtc = start.AddSeconds(-1),
            ToUtc = start.AddSeconds(20 * 20),
            Resolution = SeriesResolution.Raw,
            MaxPoints = 5,
        });

        Assert.True(result.Truncated);
        Assert.Equal(5, result.Points.Count);
        Assert.Equal(19d, result.Points[^1].Last);
    }

    [SkippableFact]
    public void Instances_of_one_counter_are_separate_series()
    {
        RequireDatabase();

        // A counter is not a series. On a host with thirty-two LUNs,
        // disk.deviceLatency is one counter and thirty-two series, and
        // collapsing them is how "one LUN is slow" becomes "storage is
        // slightly slow".
        var store = Store();
        var at = T0.AddHours(4);

        store.Append([
            Sample(at, 5, counter: "disk.deviceLatency.average", instance: "naa.aaa"),
            Sample(at, 99, counter: "disk.deviceLatency.average", instance: "naa.bbb"),
        ]);

        var first = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "disk.deviceLatency.average", "naa.aaa"),
            FromUtc = at.AddSeconds(-1),
            ToUtc = at.AddSeconds(1),
            Resolution = SeriesResolution.Raw,
        });

        var second = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "disk.deviceLatency.average", "naa.bbb"),
            FromUtc = at.AddSeconds(-1),
            ToUtc = at.AddSeconds(1),
            Resolution = SeriesResolution.Raw,
        });

        Assert.Equal(5d, Assert.Single(first.Points).Last);
        Assert.Equal(99d, Assert.Single(second.Points).Last);
    }

    [SkippableFact]
    public void Everything_survives_a_restart()
    {
        RequireDatabase();

        var at = T0.AddHours(5);
        Store().Append([Sample(at, 42, counter: "durable.counter.average")]);

        _live.Restart();

        var result = new PostgresObservationStore(_live.Database).Query(new SeriesQuery
        {
            Key = new SeriesKey(Host(), "durable.counter.average", string.Empty),
            FromUtc = at.AddSeconds(-1),
            ToUtc = at.AddSeconds(1),
            Resolution = SeriesResolution.Raw,
        });

        Assert.Equal(42d, Assert.Single(result.Points).Last);
    }

    [SkippableFact]
    public void The_counters_recorded_for_an_entity_can_be_listed()
    {
        RequireDatabase();

        // So the interface offers what exists rather than a fixed menu that is
        // wrong for half the entity kinds.
        var store = Store();
        var at = T0.AddHours(6);

        store.Append([Sample(at, 1, counter: "listed.one.average")]);

        var keys = store.SeriesFor(Host());

        Assert.Contains(keys, k => k.Counter == "listed.one.average");
    }
}
