using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Sqlite.Tests;

/// <summary>
/// Storing measurements, folding them down, and throwing them away.
/// </summary>
/// <remarks>
/// The failures worth catching here are all quiet ones: a peak averaged away, a
/// gap drawn as a zero, a sample deleted before it was summarised. None of them
/// produces an error — they produce a chart that is merely wrong.
/// </remarks>
public class ObservationStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityId Host = new("vc-1:host-1");

    private readonly MetricsDatabase _database = new(new MetricsStoreOptions
    {
        Path = string.Empty,
        InMemory = true,
    });

    private readonly SqliteObservationStore _store;

    public ObservationStoreTests() => _store = new SqliteObservationStore(_database);

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- round trip -------------------------------------------------------

    [Fact]
    public void A_sample_comes_back_with_the_value_that_went_in()
    {
        _store.Append([Sample("cpu.usage.average", 42.5, T0)]);

        var point = Assert.Single(Query(T0.AddMinutes(-5), T0.AddMinutes(5)).Points);

        Assert.Equal(42.5, point.Last);
        Assert.Equal(T0, point.StartUtc);
    }

    [Fact]
    public void A_series_nobody_has_recorded_is_reported_as_absent()
    {
        // Not an empty list. "We have never measured this" and "we measured it
        // and there was nothing" are different answers, and a chart that draws
        // them the same way is lying about one of them.
        var result = _store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, "cpu.usage.average", string.Empty),
            FromUtc = T0.AddHours(-1),
            ToUtc = T0,
        });

        Assert.False(result.Exists);
        Assert.Empty(result.Points);
    }

    [Fact]
    public void A_gap_stays_a_gap()
    {
        // Zero-filling would draw a line through the floor at exactly the
        // moment the collector was blind, which reads as "it was idle" rather
        // than "we were not looking".
        _store.Append([Sample("cpu.usage.average", 10, T0)]);
        _store.Append([Sample("cpu.usage.average", 20, T0.AddMinutes(10))]);

        var points = Query(T0.AddMinutes(-1), T0.AddMinutes(11)).Points;

        Assert.Equal(2, points.Count);
    }

    [Fact]
    public void Per_device_series_are_kept_apart_from_the_aggregate()
    {
        // An average across paths can hide one sick path behind eleven healthy
        // ones, so the instance is part of the series identity.
        _store.Append([
            Sample("disk.deviceLatency.average", 2, T0, instance: string.Empty),
            Sample("disk.deviceLatency.average", 90, T0, instance: "vmhba2"),
        ]);

        Assert.Equal(2, Query(T0.AddMinutes(-1), T0.AddMinutes(1), "disk.deviceLatency.average").Points[0].Last);

        Assert.Equal(
            90,
            Query(T0.AddMinutes(-1), T0.AddMinutes(1), "disk.deviceLatency.average", "vmhba2").Points[0].Last);
    }

    [Fact]
    public void Re_recording_the_same_instant_replaces_rather_than_duplicates()
    {
        // A cycle that ran twice, or a retry, must not double a total.
        _store.Append([Sample("cpu.usage.average", 10, T0)]);
        _store.Append([Sample("cpu.usage.average", 11, T0)]);

        var point = Assert.Single(Query(T0.AddMinutes(-1), T0.AddMinutes(1)).Points);

        Assert.Equal(11, point.Last);
    }

    // --- folding ----------------------------------------------------------

    [Fact]
    public void A_peak_survives_being_folded_into_a_bucket()
    {
        // The whole reason a bucket is five numbers. Stored as an average
        // alone, a host pinned at 100% for two minutes inside an hour averages
        // to about 3% and vanishes.
        AppendEvery(TimeSpan.FromSeconds(30), T0, [3, 3, 100, 100, 3, 3, 3, 3, 3, 3]);

        Compact(T0.AddMinutes(10));

        var bucket = Assert.Single(
            Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);

        Assert.Equal(100, bucket.Max);
        Assert.Equal(3, bucket.Min);
        Assert.Equal(10, bucket.Count);
    }

    [Fact]
    public void The_last_value_in_a_bucket_is_the_latest_one_not_an_arbitrary_one()
    {
        // For a Latest counter — a datastore's free space, say — the average
        // over an hour is not a quantity anybody asked about and the current
        // figure is. The values here are chosen so that picking the largest,
        // the smallest or the first would all give a different answer.
        AppendEvery(TimeSpan.FromSeconds(30), T0, [5, 9, 7]);

        Compact(T0.AddMinutes(10));

        var bucket = Assert.Single(
            Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);

        Assert.Equal(7, bucket.Last);
        Assert.Equal(9, bucket.Max);
        Assert.Equal(5, bucket.Min);
    }

    [Fact]
    public void An_hour_folded_from_five_minute_buckets_matches_the_raw_samples()
    {
        // The hour is built from the five-minute buckets because by then the
        // raw samples are gone. That is only sound if re-aggregating is exact,
        // which is what this checks.
        var values = Enumerable.Range(1, 120).Select(i => (double)i).ToArray();
        AppendEvery(TimeSpan.FromSeconds(30), T0, values);

        Compact(T0.AddHours(2));

        var hour = Assert.Single(Query(T0, T0.AddHours(1), resolution: SeriesResolution.OneHour).Points);

        Assert.Equal(values.Min(), hour.Min);
        Assert.Equal(values.Max(), hour.Max);
        Assert.Equal(values.Sum(), hour.Sum);
        Assert.Equal(values.Length, hour.Count);
        Assert.Equal(values[^1], hour.Last);
        Assert.Equal(values.Average(), hour.Average, 6);
    }

    [Fact]
    public void A_bucket_that_is_still_filling_is_not_folded()
    {
        // Summarised early it would be a summary of half a bucket, and nothing
        // ever revisits it.
        AppendEvery(TimeSpan.FromSeconds(30), T0, [1, 2]);

        // One minute in: the five-minute bucket has four more minutes to run.
        Compact(T0.AddMinutes(1));

        Assert.Empty(Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);
    }

    [Fact]
    public void Compacting_twice_produces_the_same_numbers()
    {
        // It runs on a timer and an interrupted pass has to be safe to repeat.
        AppendEvery(TimeSpan.FromSeconds(30), T0, [4, 8, 6]);

        Compact(T0.AddMinutes(10));
        var first = Assert.Single(
            Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);

        Compact(T0.AddMinutes(10));
        var second = Assert.Single(
            Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);

        Assert.Equal(first, second);
    }

    // --- retention --------------------------------------------------------

    [Fact]
    public void Raw_samples_are_folded_before_they_are_deleted()
    {
        // The ordering that matters. Deleting first loses the data silently and
        // permanently, and the only symptom is a chart that is emptier than it
        // should be.
        AppendEvery(TimeSpan.FromSeconds(30), T0, [10, 20, 30]);

        // Three days later: well past the two-day raw retention.
        Compact(T0.AddDays(3));

        Assert.Empty(Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.Raw).Points);

        var bucket = Assert.Single(
            Query(T0, T0.AddMinutes(5), resolution: SeriesResolution.FiveMinutes).Points);

        Assert.Equal(30, bucket.Max);
    }

    [Fact]
    public void Everything_past_its_retention_goes()
    {
        AppendEvery(TimeSpan.FromSeconds(30), T0, [1, 2, 3]);

        // Beyond even the hourly retention.
        var report = Compact(T0.AddDays(500));

        Assert.Empty(Query(T0, T0.AddDays(1), resolution: SeriesResolution.FiveMinutes).Points);
        Assert.Empty(Query(T0, T0.AddDays(1), resolution: SeriesResolution.OneHour).Points);
        Assert.Equal(1, report.SeriesForgotten);
    }

    [Fact]
    public void A_series_with_nothing_left_is_forgotten()
    {
        // The dictionary is loaded into memory at startup. Left to grow it
        // would hold every counter of every virtual machine that has ever
        // existed.
        _store.Append([Sample("cpu.usage.average", 1, T0)]);
        Assert.Single(_store.SeriesFor(Host));

        Compact(T0.AddDays(500));

        Assert.Empty(_store.SeriesFor(Host));
    }

    // --- resolution -------------------------------------------------------

    [Theory]
    [InlineData(1, SeriesResolution.Raw)]
    [InlineData(24, SeriesResolution.FiveMinutes)]
    [InlineData(24 * 30, SeriesResolution.OneHour)]
    public void The_range_chooses_the_resolution(int hours, SeriesResolution expected)
    {
        // A client that asked for raw data over thirty days would be asking for
        // eighty thousand points it cannot draw and we no longer have.
        Assert.Equal(expected, SeriesRetentionPolicy.ResolutionFor(TimeSpan.FromHours(hours), 720));
    }

    [Fact]
    public void The_resolution_used_is_reported_back()
    {
        // A chart that does not say it is showing hourly averages reads as a
        // live measurement.
        _store.Append([Sample("cpu.usage.average", 1, T0)]);

        var result = _store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, "cpu.usage.average", string.Empty),
            FromUtc = T0.AddDays(-30),
            ToUtc = T0,
        });

        Assert.Equal(SeriesResolution.OneHour, result.Resolution);
    }

    [Fact]
    public void A_response_is_bounded_and_says_when_it_was_cut()
    {
        AppendEvery(TimeSpan.FromSeconds(30), T0, [.. Enumerable.Range(1, 50).Select(i => (double)i)]);

        var result = _store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, "cpu.usage.average", string.Empty),
            FromUtc = T0,
            ToUtc = T0.AddHours(1),
            MaxPoints = 10,
            Resolution = SeriesResolution.Raw,
        });

        Assert.True(result.Truncated);
        Assert.Equal(10, result.Points.Count);

        // The most recent ones: a chart missing the present is useless in a way
        // that one missing the past is not.
        Assert.Equal(50, result.Points[^1].Last);
    }

    [Fact]
    public void The_counters_recorded_for_an_entity_can_be_listed()
    {
        // So the interface offers what exists rather than a fixed menu that is
        // wrong for half the entity types.
        _store.Append([
            Sample("cpu.usage.average", 1, T0),
            Sample("mem.usage.average", 2, T0),
            Sample("disk.deviceLatency.average", 3, T0, instance: "vmhba1"),
        ]);

        Assert.Equal(3, _store.SeriesFor(Host).Count);
        Assert.Empty(_store.SeriesFor(new EntityId("vc-1:host-9")));
    }

    // --- fixtures ---------------------------------------------------------

    private CompactionReport Compact(DateTimeOffset nowUtc) =>
        _store.Compact(nowUtc, SeriesRetentionPolicy.Default);

    private SeriesResult Query(
        DateTimeOffset from,
        DateTimeOffset to,
        string counter = "cpu.usage.average",
        string instance = "",
        SeriesResolution? resolution = null) =>
        _store.Query(new SeriesQuery
        {
            Key = new SeriesKey(Host, counter, instance),
            FromUtc = from,
            ToUtc = to,
            Resolution = resolution ?? SeriesResolution.Raw,
        });

    private void AppendEvery(TimeSpan step, DateTimeOffset start, params double[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            _store.Append([Sample("cpu.usage.average", values[i], start + (step * i))]);
        }
    }

    private static Observation Sample(
        string counter, double value, DateTimeOffset at, string instance = "") => new()
        {
            Entity = Host,
            SampledAtUtc = at,
            Source = "vc-1",
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
}
