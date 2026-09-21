using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Roadmap M4.3 and M4.4: a datastore's fill date as an alert, and the
/// over-commit finding dated by it.
/// </summary>
public class DatastoreTimeToFullTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const double Gb = 1024d * 1024 * 1024;

    private static readonly EntityId Ds = new("vc-1:datastore-41");

    // --- the alert from an estimate ---------------------------------------

    private static DatastoreCapacity Store(double free = 50 * Gb, double? uncommitted = null) =>
        new(Ds, "vmfs01", "vc-1", 100 * Gb, free, uncommitted);

    private static TimeToFullResult.Forecast Forecast(double days) => new()
    {
        FullAtUtc = T0.AddDays(days),
        Days = days,
        SlopePerDay = 2 * Gb,
        Window = new TrendWindow(T0.AddDays(-30), T0),
        PointsUsed = 720,
        PValue = 0.001,
    };

    private static TimeToFullResult.Refusal Refusal() => new()
    {
        Reason = TimeToFullRefusalReason.TooFewPoints,
        Detail = "3 points; at least 14 are needed.",
        PointsUsed = 3,
    };

    [Theory]
    [InlineData(3d, AlertSeverity.Critical)]
    [InlineData(7d, AlertSeverity.Critical)]
    [InlineData(7.5d, AlertSeverity.Warning)]
    [InlineData(30d, AlertSeverity.Warning)]
    public void A_fill_date_inside_the_thresholds_is_an_alert(double days, AlertSeverity expected)
    {
        var alert = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), Forecast(days))]));

        Assert.Equal(DatastoreTimeToFull.FillingTitle, alert.Title);
        Assert.Equal(expected, alert.Severity);
        Assert.Equal(Ds, alert.Entity);
    }

    [Fact]
    public void A_fill_date_beyond_thirty_days_is_not_an_alert()
    {
        Assert.Empty(DatastoreTimeToFull.Evaluate([(Store(), Forecast(30.5))]));
    }

    [Fact]
    public void A_refusal_raises_nothing()
    {
        // "We cannot say" is not a problem with the datastore. It is shown on
        // the datastore's page instead.
        Assert.Empty(DatastoreTimeToFull.Evaluate([(Store(), Refusal())]));
    }

    [Fact]
    public void The_description_carries_the_date_the_days_the_growth_and_the_window()
    {
        var alert = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), Forecast(10))]));

        Assert.Contains("2026-10-01", alert.Description, StringComparison.Ordinal);
        Assert.Contains("fills in 10 days", alert.Description, StringComparison.Ordinal);
        Assert.Contains("growing 2 GB a day", alert.Description, StringComparison.Ordinal);
        Assert.Contains("30 days of history (2026-08-22 12:00 to 2026-09-21 12:00 UTC, 720 points)",
            alert.Description, StringComparison.Ordinal);
        Assert.Contains("this product's thresholds", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fingerprint_stays_put_as_the_date_moves_and_the_severity_changes()
    {
        // The inbox should say the date moved or the problem got worse, not
        // that a new problem appeared.
        var warning = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), Forecast(20))]));
        var critical = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), Forecast(5))]));

        Assert.Equal(warning.Fingerprint, critical.Fingerprint);
        Assert.NotEqual(warning.Description, critical.Description);
    }

    [Fact]
    public void Two_datastores_are_two_alerts()
    {
        var other = Store() with { Datastore = new EntityId("vc-1:datastore-42"), Name = "vmfs02" };

        var alerts = DatastoreTimeToFull.Evaluate([(Store(), Forecast(5)), (other, Forecast(5))]);

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_thresholds_are_policy()
    {
        var policy = DatastoreTimeToFullPolicy.Default with { WarningWithin = TimeSpan.FromDays(60) };

        Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), Forecast(45))], policy));
    }

    // --- over-commit, dated -----------------------------------------------

    [Fact]
    public void A_datastore_that_has_promised_more_than_it_has_left_is_reported_with_its_date()
    {
        // 20% full, so the fullness alert says nothing, and already certain to
        // fill if the thin disks merely grow into what they were given. Now
        // with when, at the growth actually measured.
        var alerts = DatastoreTimeToFull.Evaluate(
            [(Store(free: 80 * Gb, uncommitted: 300 * Gb), Forecast(90))]);

        var alert = Assert.Single(alerts);
        Assert.Equal(DatastoreTimeToFull.OvercommitTitle, alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("reaches capacity on 2026-12-20, in 90 days", alert.Description, StringComparison.Ordinal);
        Assert.Contains("30 days of history", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_commit_with_no_estimate_says_why_instead_of_a_date()
    {
        var alert = Assert.Single(DatastoreTimeToFull.Evaluate(
            [(Store(free: 80 * Gb, uncommitted: 300 * Gb), Refusal())]));

        Assert.Contains(
            "There is no date for when used space reaches capacity: 3 points; at least 14 are needed.",
            alert.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_over_commit_fingerprint_is_the_one_the_collector_used()
    {
        // The finding moved from the collector to this rule. An alert opened
        // before the move must be the same alert after it, not resolved and
        // replaced.
        var alert = Assert.Single(DatastoreTimeToFull.Evaluate(
            [(Store(free: 80 * Gb, uncommitted: 300 * Gb), Refusal())]));

        Assert.Equal(
            AlertFingerprint.Create(
                "vc-1", "Datastore over-committed", "Capacity", "vmfs01", "datastore-overcommitted"),
            alert.Fingerprint);
    }

    [Fact]
    public void Promises_within_the_remaining_space_are_not_an_alert()
    {
        // Thin provisioning is a technique, not a fault.
        Assert.Empty(DatastoreTimeToFull.Evaluate(
            [(Store(free: 80 * Gb, uncommitted: 40 * Gb), Refusal())]));
    }

    [Fact]
    public void Uncommitted_space_that_was_not_read_is_not_called_over_committed()
    {
        Assert.Empty(DatastoreTimeToFull.Evaluate([(Store(free: 80 * Gb, uncommitted: null), Refusal())]));
    }

    [Fact]
    public void An_over_committed_datastore_filling_soon_raises_both()
    {
        var alerts = DatastoreTimeToFull.Evaluate(
            [(Store(free: 10 * Gb, uncommitted: 300 * Gb), Forecast(5))]);

        Assert.Equal(
            [DatastoreTimeToFull.FillingTitle, DatastoreTimeToFull.OvercommitTitle],
            alerts.Select(a => a.Title));
    }

    // --- what is read -----------------------------------------------------

    private static Observation Reading(string counter, double bytes, EntityId? entity = null) =>
        CapacityCounters.Reading(entity ?? Ds, counter, bytes, T0, "vc-1");

    private static EntityGraph GraphWith(string name) => EntityGraph.Empty with
    {
        Entities = new Dictionary<EntityId, Entity>
        {
            [Ds] = new Entity
            {
                Id = Ds,
                Kind = EntityKind.Datastore,
                DisplayName = name,
                LastSeenUtc = T0,
            },
        },
    };

    [Fact]
    public void This_cycles_readings_give_each_datastore_its_capacity_free_and_promises()
    {
        var read = Assert.Single(DatastoreTimeToFull.CurrentReadings(
            [
                Reading(CapacityCounters.DatastoreCapacity, 100 * Gb),
                Reading(CapacityCounters.DatastoreFree, 20 * Gb),
                Reading(CapacityCounters.DatastoreUsed, 80 * Gb),
                Reading(CapacityCounters.DatastoreUncommitted, 5 * Gb),
            ],
            GraphWith("vmfs01")));

        Assert.Equal(new DatastoreCapacity(Ds, "vmfs01", "vc-1", 100 * Gb, 20 * Gb, 5 * Gb), read);
    }

    [Fact]
    public void A_datastore_without_a_free_space_reading_is_not_estimated()
    {
        Assert.Empty(DatastoreTimeToFull.CurrentReadings(
            [Reading(CapacityCounters.DatastoreCapacity, 100 * Gb)], GraphWith("vmfs01")));
    }

    [Fact]
    public void The_history_is_one_query_of_used_space_over_the_lookback_in_the_hourly_tier()
    {
        var query = DatastoreTimeToFull.HistoryQuery(
            Ds, T0, DatastoreTimeToFullPolicy.Default, SeriesRetentionPolicy.Default);

        Assert.Equal(new SeriesKey(Ds, CapacityCounters.DatastoreUsed, string.Empty), query.Key);
        Assert.Equal(T0.AddDays(-30), query.FromUtc);
        Assert.Equal(T0, query.ToUtc);
        Assert.Equal(SeriesResolution.OneHour, query.Resolution);
        Assert.Equal(720, query.MaxPoints);
    }

    [Fact]
    public void Each_buckets_last_reading_is_the_point()
    {
        // A level, not a rate: the bucket's average is not a quantity anybody
        // asked about. Here the averages are flat and the last readings grow.
        var buckets = Enumerable.Range(0, 21)
            .Select(i => new AggregatedSample
            {
                StartUtc = T0.AddDays(i - 20),
                Min = 0,
                Max = (30 + i) * Gb,
                Sum = 10 * Gb,
                Count = 1,
                Last = (30 + i) * Gb,
            })
            .ToList();

        var estimate = DatastoreTimeToFull.Estimate(
            new SeriesResult { Key = default, Resolution = SeriesResolution.OneHour, Points = buckets, Exists = true },
            100 * Gb,
            T0,
            DatastoreTimeToFullPolicy.Default);

        var forecast = Assert.IsType<TimeToFullResult.Forecast>(estimate);
        Assert.Equal(50d, forecast.Days, 3);
    }

    // --- the adapter ------------------------------------------------------

    [Fact]
    public void The_rule_reads_one_history_per_live_datastore_and_nothing_else()
    {
        var series = new CountingSeries();
        var other = new EntityId("vc-1:datastore-42");

        var context = new RuleContext
        {
            Snapshots =
            [
                new InventorySnapshot
                {
                    SourceInstanceId = "vc-1",
                    ReadAtUtc = T0,
                    Observations =
                    [
                        Reading(CapacityCounters.DatastoreCapacity, 100 * Gb),
                        Reading(CapacityCounters.DatastoreFree, 80 * Gb),
                        Reading(CapacityCounters.DatastoreUsed, 20 * Gb),
                        Reading(CapacityCounters.DatastoreUncommitted, 300 * Gb),

                        // Capacity but no free space: not estimated, not read.
                        Reading(CapacityCounters.DatastoreCapacity, 100 * Gb, other),
                    ],
                },
            ],
            ReadGraph = () => GraphWith("vmfs01"),
            NowUtc = T0,
            Options = MonitoringOptions.Default,
            Series = series,
            Events = new NoEvents(),
        };

        var alert = Assert.Single(new DatastoreTimeToFullRule().Evaluate(context));

        var query = Assert.Single(series.Queries);
        Assert.Equal(Ds, query.Key.Entity);
        Assert.Equal(CapacityCounters.DatastoreUsed, query.Key.Counter);

        // No history at all: over-committed, and says it cannot date it.
        Assert.Equal(DatastoreTimeToFull.OvercommitTitle, alert.Title);
        Assert.Contains("There is no date", alert.Description, StringComparison.Ordinal);
    }

    private sealed class CountingSeries : ISeriesReader
    {
        public List<SeriesQuery> Queries { get; } = [];

        public SeriesResult Query(SeriesQuery query)
        {
            Queries.Add(query);

            return new SeriesResult { Key = query.Key, Resolution = query.Resolution ?? SeriesResolution.Raw };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    private sealed class NoEvents : IEventReader
    {
        public IReadOnlyList<SourceEvent> OfTypes(
            IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }
}
