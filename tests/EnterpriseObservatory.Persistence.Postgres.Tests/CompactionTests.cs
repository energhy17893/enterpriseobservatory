using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Folding and retention, against a real server.
/// </summary>
/// <remarks>
/// <para>
/// The most intricate part of the adapter and the part most likely to be wrong
/// without anybody noticing. A bucket folded from half a window, a watermark
/// that moves further than the work it accounts for, a "last value" taken from
/// an arbitrary row — none of these throw, and all of them produce a chart that
/// is merely a bit wrong.
/// </para>
/// <para>
/// A schema per test rather than per class. The compaction watermark is global
/// to a database — deliberately, since it is what stops a pass rescanning the
/// whole history — so tests that compact would otherwise move it past each
/// other's data and pass or fail depending on the order they ran in.
/// </para>
/// </remarks>
public class CompactionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private PostgresObservationStore Store() => new(_live.Database);

    /// <summary>A fresh entity per test, so one test's retention is not another's.</summary>
    private static EntityId Subject(string name) => EntityId.For("vc-1", name);

    private static void AppendEvery(
        PostgresObservationStore store,
        EntityId entity,
        TimeSpan step,
        DateTimeOffset from,
        params double[] values) =>
        store.Append([.. values.Select((value, i) => new Observation
        {
            Entity = entity,
            Source = "vc-1",
            SampledAtUtc = from + (step * i),
            Value = new CounterValue
            {
                CounterName = "cpu.usage.average",
                Raw = value,
                Rollup = RollupType.Average,
                Interval = step,
                Unit = "percent",
            },
        })]);

    private static SeriesResult Read(
        PostgresObservationStore store,
        EntityId entity,
        DateTimeOffset from,
        DateTimeOffset to,
        SeriesResolution resolution) =>
        store.Query(new SeriesQuery
        {
            Key = new SeriesKey(entity, "cpu.usage.average", string.Empty),
            FromUtc = from,
            ToUtc = to,
            Resolution = resolution,
            MaxPoints = 2000,
        });

    [SkippableFact]
    public void A_peak_survives_being_folded_into_a_bucket()
    {
        RequireDatabase();

        // The whole reason a bucket is five numbers. Stored as an average
        // alone, a host pinned at 100% for two minutes inside an hour averages
        // to about 3% and vanishes.
        var store = Store();
        var entity = Subject("peak");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 3, 3, 100, 100, 3, 3, 3, 3, 3, 3);
        store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());

        var bucket = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);

        Assert.Equal(100, bucket.Max);
        Assert.Equal(3, bucket.Min);
        Assert.Equal(10, bucket.Count);
    }

    [SkippableFact]
    public void The_last_value_in_a_bucket_is_the_latest_one_not_an_arbitrary_one()
    {
        RequireDatabase();

        // For a Latest counter — a datastore's free space, say — the average
        // over an hour is not a quantity anybody asked about and the current
        // figure is. The values are chosen so that picking the largest, the
        // smallest or the first would each give a different answer.
        var store = Store();
        var entity = Subject("last");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 5, 9, 7);
        store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());

        var bucket = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);

        Assert.Equal(7, bucket.Last);
        Assert.Equal(9, bucket.Max);
        Assert.Equal(5, bucket.Min);
    }

    [SkippableFact]
    public void An_hour_folded_from_five_minute_buckets_matches_the_raw_samples()
    {
        RequireDatabase();

        // The hour is built from the five-minute buckets because by then the
        // raw samples are gone. That is only sound if re-aggregating is exact,
        // which is what this checks — and it is the property that makes keeping
        // hourly history for years cost almost nothing.
        var store = Store();
        var entity = Subject("exact");
        var values = Enumerable.Range(1, 120).Select(i => (double)i).ToArray();

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, values);
        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        var hour = Assert.Single(
            Read(store, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points);

        Assert.Equal(values.Min(), hour.Min);
        Assert.Equal(values.Max(), hour.Max);
        Assert.Equal(values.Sum(), hour.Sum);
        Assert.Equal(values.Length, hour.Count);
        Assert.Equal(values[^1], hour.Last);
        Assert.Equal(values.Average(), hour.Average, 6);
    }

    [SkippableFact]
    public void A_bucket_that_is_still_filling_is_not_folded()
    {
        RequireDatabase();

        // Summarised early it would be a summary of half a bucket, and nothing
        // ever revisits it.
        var store = Store();
        var entity = Subject("filling");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1, 2);

        // One minute in: the five-minute bucket has four more minutes to run.
        store.Compact(T0.AddMinutes(1), new SeriesRetentionPolicy());

        Assert.Empty(Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);
    }

    [SkippableFact]
    public void Compacting_twice_produces_the_same_numbers()
    {
        RequireDatabase();

        // It runs on a timer and an interrupted pass has to be safe to repeat.
        // Idempotent because a bucket is computed from its sources rather than
        // accumulated into — which the ON CONFLICT clause has to preserve.
        var store = Store();
        var entity = Subject("idempotent");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 4, 8, 6);

        store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());
        var first = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);

        store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());
        var second = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);

        Assert.Equal(first, second);
    }

    [SkippableFact]
    public void Raw_samples_are_folded_before_they_are_deleted()
    {
        RequireDatabase();

        // The ordering that matters. Deleting first loses the data silently and
        // permanently, and the only symptom is a chart emptier than it should
        // be.
        var store = Store();
        var entity = Subject("ordering");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 10, 20, 30);

        // Three days later: well past the two-day raw retention.
        store.Compact(T0.AddDays(3), new SeriesRetentionPolicy());

        Assert.Empty(Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.Raw).Points);

        var bucket = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);

        Assert.Equal(30, bucket.Max);
    }

    [SkippableFact]
    public void Everything_past_its_retention_goes()
    {
        RequireDatabase();

        var store = Store();
        var entity = Subject("aged");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1, 2, 3);

        // Beyond even the hourly retention.
        var report = store.Compact(T0.AddDays(500), new SeriesRetentionPolicy());

        Assert.Empty(Read(store, entity, T0, T0.AddDays(1), SeriesResolution.FiveMinutes).Points);
        Assert.Empty(Read(store, entity, T0, T0.AddDays(1), SeriesResolution.OneHour).Points);
        Assert.True(report.SeriesForgotten >= 1);
    }

    [SkippableFact]
    public void A_series_with_nothing_left_is_forgotten()
    {
        RequireDatabase();

        // The dictionary is loaded into memory at startup. Left to grow it
        // would hold every counter of every virtual machine that has ever
        // existed.
        var store = Store();
        var entity = Subject("forgotten");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1);
        Assert.Single(store.SeriesFor(entity));

        store.Compact(T0.AddDays(500), new SeriesRetentionPolicy());

        Assert.Empty(store.SeriesFor(entity));
    }

    [SkippableFact]
    public void A_gap_stays_a_gap()
    {
        RequireDatabase();

        // Absent buckets are absent, never zero-filled. A gap means "we were
        // not looking"; a zero means "we looked and it was nothing", and
        // drawing the second when the first is true is how a monitoring product
        // tells its first lie. Principle 1.
        var store = Store();
        var entity = Subject("gap");

        AppendEvery(store, entity, TimeSpan.FromMinutes(30), T0, 1, 2);
        store.Compact(T0.AddHours(3), new SeriesRetentionPolicy());

        var points = Read(store, entity, T0, T0.AddHours(3), SeriesResolution.FiveMinutes).Points;

        // Two samples half an hour apart produce two buckets, not the seven
        // that would fill the span between them.
        Assert.Equal(2, points.Count);
    }

    [SkippableFact]
    public void The_store_answers_the_resolution_it_is_told_and_does_not_pick_a_retained_one()
    {
        RequireDatabase();

        // The boundary, stated rather than assumed. The store's own rule picks
        // a resolution from the WIDTH of the range alone: an hour-wide window
        // chooses Raw whether it is this hour or one from a fortnight ago.
        // Raw is kept two days, so for a window older than that the answer
        // exists in the five-minute tier and this query cannot reach it.
        //
        // That is not fixed here on purpose. Choosing a tier that is still
        // retained needs the retention policy — which Compact receives per
        // call, so this store is deliberately stateless about it — and a clock
        // to know how far back the window reaches. ReadModel holds both and
        // names the resolution; see RetainedResolutionFor.
        var store = Store();
        var entity = Subject("told");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 10, 20, 30);

        // Ten days on: raw is long gone, the five-minute bucket is well inside
        // its window.
        store.Compact(T0.AddDays(10), new SeriesRetentionPolicy());

        var unnamed = store.Query(new SeriesQuery
        {
            Key = new SeriesKey(entity, "cpu.usage.average", string.Empty),
            FromUtc = T0,
            ToUtc = T0.AddHours(1),
            MaxPoints = 720,
        });

        Assert.Equal(SeriesResolution.Raw, unnamed.Resolution);
        Assert.True(unnamed.Exists);
        Assert.Empty(unnamed.Points);

        // Named, it answers — which is what the caller now does.
        Assert.NotEmpty(Read(store, entity, T0, T0.AddHours(1), SeriesResolution.FiveMinutes).Points);
    }

    [SkippableFact]
    public void A_watermark_stops_the_second_pass_redoing_the_first()
    {
        RequireDatabase();

        // Bounded so a pass costs the same whether the service has been running
        // for an hour or a year. Without it, every compaction would rescan the
        // whole history and quietly get slower for the life of the
        // installation.
        var store = Store();
        var entity = Subject("watermark");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1, 2, 3);

        var first = store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());
        var second = store.Compact(T0.AddMinutes(10), new SeriesRetentionPolicy());

        Assert.True(first.BucketsWritten > 0);
        Assert.Equal(0, second.BucketsWritten);
    }
}
