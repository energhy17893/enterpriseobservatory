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
        store.Compact(T0.AddDays(500), new SeriesRetentionPolicy());

        Assert.Empty(Read(store, entity, T0, T0.AddDays(1), SeriesResolution.FiveMinutes).Points);
        Assert.Empty(Read(store, entity, T0, T0.AddDays(1), SeriesResolution.OneHour).Points);
    }

    [SkippableFact]
    public void A_series_keeps_its_row_after_its_last_measurement_has_aged_out()
    {
        RequireDatabase();

        // The sweep used to delete this row, and deleting it is what put a
        // foreign key race on the ingest path: sample.series_id cascades, so a
        // delete that won the race against an appending cycle took that cycle's
        // samples — for every entity, not just this one — and said nothing.
        // Restore it and this test is the thing that notices.
        //
        // What is given up instead is stated rather than hidden: the row now
        // survives forever, at about 300 bytes of disk and 370 of memory. On
        // the measured estate that is roughly 1 MB a year of decommissioned
        // machines. ADR-0019 says where it stops being affordable.
        var store = Store();
        var entity = Subject("outlives-its-data");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1);
        Assert.Single(store.SeriesFor(entity));

        store.Compact(T0.AddDays(500), new SeriesRetentionPolicy());

        Assert.Empty(Read(store, entity, T0, T0.AddDays(1), SeriesResolution.OneHour).Points);
        Assert.Single(store.SeriesFor(entity));
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
    public void A_sample_arriving_after_its_bucket_was_folded_reaches_both_tiers()
    {
        RequireDatabase();

        // Normal operation since samples carry vCenter's own time: datastores
        // arrive as historical 300 s data up to twenty minutes old, and
        // backfill writes what a gap missed. Folding used to be forward-only,
        // so such a sample stayed in raw for two days and never reached the
        // tiers kept for a month and a quarter.
        var store = Store();
        var entity = Subject("late");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 10, 20, 30);
        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        // Both tiers have folded T0's buckets. Now the late one, a peak.
        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0.AddMinutes(3), 500);

        // Across a restart: the marker is on disk, not in this instance.
        _live.Restart();
        var restarted = new PostgresObservationStore(_live.Database);

        var report = restarted.Compact(T0.AddHours(2), new SeriesRetentionPolicy());
        Assert.True(report.BucketsWritten > 0);

        var five = Assert.Single(
            Read(restarted, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);
        var hour = Assert.Single(
            Read(restarted, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points);

        // Rebuilt whole, not added to: every one of the five numbers reflects
        // all four samples exactly once.
        foreach (var bucket in new[] { five, hour })
        {
            Assert.Equal(500, bucket.Max);
            Assert.Equal(10, bucket.Min);
            Assert.Equal(560, bucket.Sum);
            Assert.Equal(4, bucket.Count);
            Assert.Equal(500, bucket.Last);
        }
    }

    [SkippableFact]
    public void Re_folding_after_a_late_sample_is_idempotent()
    {
        RequireDatabase();

        var store = Store();
        var entity = Subject("late-twice");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 4, 8, 6);
        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0.AddMinutes(2), 7);

        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());
        var firstFive = Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points;
        var firstHour = Read(store, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points;

        // The marker is cleared by the pass that dealt with it, so a second
        // pass has nothing to do and the numbers are the same.
        var second = store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        Assert.Equal(0, second.BucketsWritten);
        Assert.Equal(firstFive, Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);
        Assert.Equal(firstHour, Read(store, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points);
        Assert.Equal(4, Assert.Single(firstHour).Count);
    }

    [SkippableFact]
    public void A_sample_sent_again_does_not_trigger_a_re_fold()
    {
        RequireDatabase();

        // Consecutive cycles re-send the same twenty-minute datastore window.
        // A duplicate changes no bucket, so it must not drag the next pass
        // twenty minutes back — only samples actually added count.
        var store = Store();
        var entity = Subject("resent");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1, 2, 3);
        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 1, 2, 3);

        Assert.Equal(0, store.Compact(T0.AddHours(2), new SeriesRetentionPolicy()).BucketsWritten);
    }

    [SkippableFact]
    public void A_sample_older_than_raw_retention_does_not_replace_its_bucket()
    {
        RequireDatabase();

        // Documented, not accidental: by the time it arrives, retention has
        // deleted the raw samples its bucket was built from, so rebuilding the
        // bucket would replace a summary of three samples with a summary of
        // one. It is left out of the tiers and aged out of raw by the same
        // sweep. See RefoldWindow.Floor.
        var store = Store();
        var entity = Subject("too-late");

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0, 10, 20, 30);
        store.Compact(T0.AddDays(3), new SeriesRetentionPolicy());

        AppendEvery(store, entity, TimeSpan.FromSeconds(30), T0.AddMinutes(3), 500);
        store.Compact(T0.AddDays(3), new SeriesRetentionPolicy());

        var five = Assert.Single(
            Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points);
        var hour = Assert.Single(
            Read(store, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points);

        Assert.Equal((30d, 3), (five.Max, five.Count));
        Assert.Equal((30d, 3), (hour.Max, hour.Count));
        Assert.Empty(Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.Raw).Points);
    }

    [SkippableFact]
    public void A_large_dirty_range_is_rebuilt_in_bounded_slices_with_exact_aggregates()
    {
        RequireDatabase();

        // A backfill after downtime dirties hours at once. Rebuilt in one
        // transaction it would hold the watermark lock — which an append takes
        // before commit — for as long as the whole range takes, and a cycle
        // that waits past its thirty-second timeout is dropped. So the range
        // is rebuilt a bounded number of buckets at a time.
        var slices = new List<FoldSlice>();
        var store = new PostgresObservationStore(_live.Database)
        {
            BucketsPerSlice = 2,
            SliceCommitted = slices.Add,
        };
        var entity = Subject("sliced");

        // Three hours, one sample every five minutes, all folded.
        AppendEvery(store, entity, TimeSpan.FromMinutes(5), T0, [.. Enumerable.Repeat(1d, 36)]);
        store.Compact(T0.AddHours(4), new SeriesRetentionPolicy());

        // Then a second sample into every one of those buckets, late.
        AppendEvery(store, entity, TimeSpan.FromMinutes(5), T0.AddMinutes(1), [.. Enumerable.Repeat(3d, 36)]);
        slices.Clear();
        store.Compact(T0.AddHours(4), new SeriesRetentionPolicy());

        var fives = slices.Where(s => s.Resolution == SeriesResolution.FiveMinutes).ToList();

        Assert.True(fives.Count >= 18, $"Only {fives.Count} five-minute slices.");
        // A slice that found nothing to fold may skip the empty stretch after
        // it in one step; every slice that rebuilt something stays bounded.
        Assert.All(slices.Where(s => s.BucketsWritten > 0), s => Assert.True(
            s.ToUtc - s.FromUtc <= SeriesResolutions.Width(s.Resolution) * 2,
            $"A {s.Resolution} slice spanned {s.FromUtc:O} to {s.ToUtc:O}."));

        var buckets = Read(store, entity, T0, T0.AddHours(3), SeriesResolution.FiveMinutes).Points;

        Assert.Equal(36, buckets.Count);
        Assert.All(buckets, b => Assert.Equal((2, 4d, 3d), (b.Count, b.Sum, b.Last)));

        var hours = Read(store, entity, T0, T0.AddHours(3), SeriesResolution.OneHour).Points;

        Assert.Equal(3, hours.Count);
        Assert.All(hours, h => Assert.Equal((24, 48d, 1d, 3d), (h.Count, h.Sum, h.Min, h.Max)));
    }

    [SkippableFact]
    public void An_append_between_slices_is_not_blocked_and_is_folded_by_the_same_pass()
    {
        RequireDatabase();

        // The lock is released between slices, so a cycle arriving mid-fold
        // commits at once instead of waiting for the whole range. This append
        // runs from the slice callback on the same thread: were the lock still
        // held it could never finish. Its sample lands behind where the fold
        // has got to, lowers the marker again, and the same pass goes back
        // for it.
        var entity = Subject("between");
        var appended = false;
        PostgresObservationStore? store = null;

        store = new PostgresObservationStore(_live.Database)
        {
            BucketsPerSlice = 1,
            SliceCommitted = slice =>
            {
                if (!appended && slice.Resolution == SeriesResolution.FiveMinutes
                    && slice.FromUtc >= T0.AddMinutes(30))
                {
                    appended = true;
                    AppendEvery(store!, entity, TimeSpan.FromSeconds(30), T0.AddMinutes(2), 900);
                }
            },
        };

        AppendEvery(store, entity, TimeSpan.FromMinutes(5), T0, [.. Enumerable.Repeat(1d, 12)]);
        store.Compact(T0.AddHours(2), new SeriesRetentionPolicy());

        Assert.True(appended);

        var first = Read(store, entity, T0, T0.AddMinutes(5), SeriesResolution.FiveMinutes).Points;
        Assert.Equal((2, 900d), (Assert.Single(first).Count, first[0].Max));

        var hour = Assert.Single(Read(store, entity, T0, T0.AddHours(1), SeriesResolution.OneHour).Points);
        Assert.Equal((13, 900d), (hour.Count, hour.Max));
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
