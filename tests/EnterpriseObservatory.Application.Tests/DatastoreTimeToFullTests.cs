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
    public void A_datastore_already_full_is_a_critical_filling_alert()
    {
        // It used to resolve "filling": the estimate refuses a full volume,
        // and a refusal raised nothing. Full is the worst case of the
        // condition, not its absence (ADR-0026 §5.10).
        var full = new TimeToFullResult.Refusal
        {
            Reason = TimeToFullRefusalReason.AlreadyFull,
            Detail = "The latest reading is at or above capacity.",
            PointsUsed = 200,
        };

        var alert = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(), full)]));

        Assert.Equal(DatastoreTimeToFull.FillingTitle, alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("is full", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void No_free_space_read_this_cycle_is_full_whatever_the_history_says()
    {
        // The current reading is fresher than the newest hourly bucket.
        var alert = Assert.Single(DatastoreTimeToFull.Evaluate([(Store(free: 0), Refusal())]));

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(DatastoreTimeToFull.FillingTitle, alert.Title);
    }

    [Fact]
    public void A_week_with_a_hole_in_it_is_not_estimated_and_says_how_much_was_read()
    {
        // Twenty-one days spanned, but the product was down for four of the
        // last seven: the shared "enough history" test refuses it (ADR-0026).
        var buckets = HourlyGrowth(21)
            .Where(b => b.StartUtc < T0.AddDays(-5) || b.StartUtc > T0.AddDays(-1))
            .ToList();

        var estimate = DatastoreTimeToFull.Estimate(
            new SeriesResult { Key = default, Resolution = SeriesResolution.OneHour, Points = buckets, Exists = true },
            100 * Gb,
            T0,
            DatastoreTimeToFullPolicy.Default);

        var refusal = Assert.IsType<TimeToFullResult.Refusal>(estimate);
        Assert.Equal(TimeToFullRefusalReason.InsufficientHistory, refusal.Reason);
        Assert.Contains("42% of the 7 days read, 80% needed", refusal.Detail, StringComparison.Ordinal);
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
        var buckets = Enumerable.Range(0, (20 * 24) + 1)
            .Select(i => new AggregatedSample
            {
                StartUtc = T0.AddHours(i - (20 * 24)),
                Min = 0,
                Max = (30 + (i / 24d)) * Gb,
                Sum = 10 * Gb,
                Count = 1,
                Last = (30 + (i / 24d)) * Gb,
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

        var alert = Assert.Single(Raised(new DatastoreTimeToFullRule().Evaluate(context)));

        var query = Assert.Single(series.Queries);
        Assert.Equal(Ds, query.Key.Entity);
        Assert.Equal(CapacityCounters.DatastoreUsed, query.Key.Counter);

        // No history at all: over-committed, and says it cannot date it.
        Assert.Equal(DatastoreTimeToFull.OvercommitTitle, alert.Title);
        Assert.Contains("There is no date", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_datastore_whose_history_cannot_be_read_costs_only_that_datastore()
    {
        // Every query used to run inside the one guard around the rule, so a
        // single timeout dropped every datastore's fill date — and
        // reconciliation then resolved them all, unchecked.
        var other = new EntityId("vc-1:datastore-42");
        var series = new GrowingSeries { Failing = Ds };
        var held = DatastoreTimeToFull.Fingerprints(new DatastoreCapacity(Ds, "vmfs01", "vc-1", 100 * Gb, 80 * Gb, null));

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
                        Reading(CapacityCounters.DatastoreCapacity, 100 * Gb, other),
                        Reading(CapacityCounters.DatastoreFree, 50 * Gb, other),
                    ],
                },
            ],
            ReadGraph = () => GraphWith("vmfs01"),
            NowUtc = T0,
            Options = MonitoringOptions.Default,
            Series = series,
            Events = new NoEvents(),
            HeldBy = _ => [.. held.Select(f => new HeldAlert(f, Ds))],
        };

        var verdicts = new DatastoreTimeToFullRule().Evaluate(context);
        var alerts = Raised(verdicts);

        // The readable one is estimated as usual: 2 GB a day, 50 GB left.
        var filling = Assert.Single(alerts, a => a.Title == DatastoreTimeToFull.FillingTitle);
        Assert.Equal(other, filling.Entity);

        // The failure is said, not swallowed.
        var failure = Assert.Single(alerts, a => a.Title == DatastoreTimeToFull.HistoryUnreadableTitle);
        Assert.Contains("'vmfs01'", failure.Description, StringComparison.Ordinal);
        Assert.Contains("TimeoutException", failure.Description, StringComparison.Ordinal);

        // And the unreadable one's alerts are held open rather than resolved:
        // unknown because the rule failed for that datastore, never absent.
        var unknown = verdicts.OfType<Unknown>().Where(u => u.Covers.Any(held.Contains)).ToList();
        Assert.Equal(held.ToHashSet(), unknown.SelectMany(u => u.Covers).ToHashSet());
        Assert.All(unknown, u => Assert.Equal(UnknownReason.RuleFailed, u.Reason));
        Assert.Empty(verdicts.OfType<ConditionAbsent>());
    }

    // --- the estimate is kept while its input stands still -----------------

    [Fact]
    public void The_same_history_capacity_and_hour_is_estimated_once()
    {
        // The hourly tier gains a bucket once an hour; the cycle asks every
        // five minutes and the page on every load.
        var cache = new TimeToFullCache();
        var series = new GrowingSeries();
        var policy = DatastoreTimeToFullPolicy.Default;
        var retention = SeriesRetentionPolicy.Default;

        var first = DatastoreTimeToFull.Read(series, Ds, 100 * Gb, T0, policy, retention, cache);
        var again = DatastoreTimeToFull.Read(series, Ds, 100 * Gb, T0, policy, retention, cache);

        Assert.Equal(1, cache.Computations);
        Assert.Equal(first, again);
        Assert.Equal(2, series.Queries);

        // A different capacity is a different answer.
        DatastoreTimeToFull.Read(series, Ds, 120 * Gb, T0, policy, retention, cache);
        Assert.Equal(2, cache.Computations);

        // So is a new bucket.
        series.Days = 22;
        DatastoreTimeToFull.Read(series, Ds, 120 * Gb, T0, policy, retention, cache);
        Assert.Equal(3, cache.Computations);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void A_cached_forecast_counts_its_days_from_when_it_is_asked()
    {
        var cache = new TimeToFullCache();
        var series = new GrowingSeries();
        var policy = DatastoreTimeToFullPolicy.Default;
        var retention = SeriesRetentionPolicy.Default;

        var first = Assert.IsType<TimeToFullResult.Forecast>(
            DatastoreTimeToFull.Read(series, Ds, 100 * Gb, T0, policy, retention, cache));
        var later = Assert.IsType<TimeToFullResult.Forecast>(
            DatastoreTimeToFull.Read(series, Ds, 100 * Gb, T0.AddMinutes(30), policy, retention, cache));

        Assert.Equal(1, cache.Computations);
        Assert.Equal(first.FullAtUtc, later.FullAtUtc);
        Assert.Equal(first.Days - (30d / 60 / 24), later.Days, 6);

        // The next hour is recomputed.
        DatastoreTimeToFull.Read(series, Ds, 100 * Gb, T0.AddHours(1), policy, retention, cache);
        Assert.Equal(2, cache.Computations);
    }

    [Fact]
    public void The_cache_is_bounded()
    {
        var cache = new TimeToFullCache(maxEntries: 2);
        var series = new GrowingSeries();

        foreach (var id in new[] { "a", "b", "c" })
        {
            DatastoreTimeToFull.Read(
                series, new EntityId(id), 100 * Gb, T0,
                DatastoreTimeToFullPolicy.Default, SeriesRetentionPolicy.Default, cache);
        }

        Assert.True(cache.Count <= 2);
    }

    /// <summary>Hourly buckets of used space over <paramref name="days"/>, rising 2 GB a day to 50 GB at <see cref="T0"/>.</summary>
    private static List<AggregatedSample> HourlyGrowth(int days) =>
    [
        .. Enumerable.Range(0, (days * 24) + 1).Select(i =>
        {
            var used = (50 - (2 * (days - (i / 24d)))) * Gb;

            return new AggregatedSample
            {
                StartUtc = T0.AddHours(i - (days * 24)),
                Min = used,
                Max = used,
                Sum = used,
                Count = 1,
                Last = used,
            };
        }),
    ];

    // --- three values (ADR-0026) -------------------------------------------

    private static RuleContext Context(
        ISeriesReader series,
        IReadOnlyList<Observation> readings,
        params AlertFingerprint[] held) => new()
        {
            Snapshots = [new InventorySnapshot { SourceInstanceId = "vc-1", ReadAtUtc = T0, Observations = readings }],
            ReadGraph = () => GraphWith("vmfs01"),
            NowUtc = T0,
            Options = MonitoringOptions.Default,
            Series = series,
            Events = new NoEvents(),
            HeldBy = _ => [.. held.Select(f => new HeldAlert(f, Ds))],
        };

    private static Observation[] Capacity(double free, double? uncommitted = null) =>
    [
        Reading(CapacityCounters.DatastoreCapacity, 100 * Gb),
        Reading(CapacityCounters.DatastoreFree, free),
        .. uncommitted is { } u ? [Reading(CapacityCounters.DatastoreUncommitted, u)] : Array.Empty<Observation>(),
    ];

    private static AlertFingerprint FillingFp => DatastoreTimeToFull.Fingerprints(Store())[0];

    private static AlertFingerprint OvercommitFp => DatastoreTimeToFull.Fingerprints(Store())[1];

    private static SubjectVerdict VerdictOn(IReadOnlyList<SubjectVerdict> verdicts, AlertFingerprint fingerprint) =>
        Assert.Single(verdicts, v =>
            v.Covers.Contains(fingerprint) ||
            (v is ConditionPresent p && p.Alerts.Any(a => a.Fingerprint == fingerprint)));

    [Fact]
    public void A_fill_date_beyond_the_thresholds_is_absent_on_fresh_evidence()
    {
        // 50 GB free, 2 GB a day: 25 days, so move the capacity to push it out.
        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries(),
            [Reading(CapacityCounters.DatastoreCapacity, 400 * Gb), Reading(CapacityCounters.DatastoreFree, 350 * Gb), Reading(CapacityCounters.DatastoreUncommitted, 0)],
            FillingFp));

        var absent = Assert.IsType<ConditionAbsent>(VerdictOn(verdicts, FillingFp));
        Assert.Equal(Ds, absent.Entity);
        Assert.Equal(T0, absent.EvidenceAtUtc);
    }

    [Fact]
    public void Too_little_history_is_unknown_not_absent_and_says_why()
    {
        // Three days of hourly history: the filling alert cannot be judged,
        // so an open one stays open (it used to resolve).
        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries { Days = 4 }, Capacity(50 * Gb, 0), FillingFp));

        var unknown = Assert.IsType<Unknown>(VerdictOn(verdicts, FillingFp));
        Assert.Equal(UnknownReason.InsufficientSeries, unknown.Reason);
        Assert.Contains("of the 7 days", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_datastore_is_present_critical_even_without_enough_history()
    {
        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries { Days = 2 }, Capacity(0, 0), FillingFp));

        var present = Assert.IsType<ConditionPresent>(VerdictOn(verdicts, FillingFp));
        Assert.Equal(AlertSeverity.Critical, Assert.Single(present.Alerts).Severity);
    }

    [Fact]
    public void Uncommitted_space_that_was_not_read_is_unknown_and_read_within_free_is_absent()
    {
        var unread = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries(), Capacity(80 * Gb, uncommitted: null), OvercommitFp));

        var unknown = Assert.IsType<Unknown>(VerdictOn(unread, OvercommitFp));
        Assert.Equal(UnknownReason.InputNotCollected, unknown.Reason);

        var read = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries(), Capacity(80 * Gb, uncommitted: 40 * Gb), OvercommitFp));

        var absent = Assert.IsType<ConditionAbsent>(VerdictOn(read, OvercommitFp));
        Assert.Equal(2, absent.Resolution?.ConsecutiveAbsent);
    }

    [Fact]
    public void A_datastore_not_read_this_cycle_gets_no_verdict_so_its_alerts_are_not_reported()
    {
        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries(), [], FillingFp, OvercommitFp));

        Assert.DoesNotContain(verdicts, v => v.Covers.Contains(FillingFp) || v.Covers.Contains(OvercommitFp));
    }

    [Fact]
    public void A_renamed_datastores_old_over_commit_alert_is_superseded_by_its_new_name()
    {
        // The over-commit fingerprint is keyed by name; the entity was read
        // this cycle under another one.
        var old = AlertFingerprint.Create("vc-1", "Datastore over-committed", "Capacity", "old-name", "datastore-overcommitted");

        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(
            new GrowingSeries(), Capacity(80 * Gb, 300 * Gb), old));

        var absent = Assert.IsType<ConditionAbsent>(VerdictOn(verdicts, old));
        Assert.Equal(AbsenceKind.Superseded, absent.Because);
    }

    [Fact]
    public void The_history_alert_is_absent_when_every_history_was_read()
    {
        var unreadable = AlertFingerprint.Create(
            "platform", DatastoreTimeToFull.HistoryUnreadableTitle, GuardedRule.Category,
            DatastoreTimeToFull.RuleId, DatastoreTimeToFull.HistoryUnreadableCheckId);

        var verdicts = new DatastoreTimeToFullRule().Evaluate(Context(new GrowingSeries(), Capacity(50 * Gb, 0), unreadable));

        Assert.IsType<ConditionAbsent>(VerdictOn(verdicts, unreadable));
    }

    /// <summary>
    /// Used space rising 2 GB a day to 50 GB at <see cref="T0"/>, one bucket an
    /// hour over <see cref="Days"/> − 1 days; throws for <see cref="Failing"/>.
    /// </summary>
    private sealed class GrowingSeries : ISeriesReader
    {
        public EntityId? Failing { get; init; }

        public int Days { get; set; } = 21;

        public int Queries { get; private set; }

        public SeriesResult Query(SeriesQuery query)
        {
            Queries++;

            if (query.Key.Entity == Failing)
            {
                throw new TimeoutException("statement timeout");
            }

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.OneHour,
                Exists = true,
                Points = HourlyGrowth(Days - 1),
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
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

    private static List<AlertDefinition> Raised(IReadOnlyList<SubjectVerdict> verdicts) =>
        [.. verdicts.OfType<ConditionPresent>().SelectMany(p => p.Alerts)];
}
