using System.Globalization;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How far back a datastore's growth is read, and how near a fill date has to
/// be before it is an alert. Each default says whose it is.
/// </summary>
public sealed record DatastoreTimeToFullPolicy
{
    /// <summary>
    /// How much history the slope is fitted to. Default 30 days — <b>ours</b>:
    /// four weekly cycles, so one unusual week is a quarter of the evidence
    /// rather than all of it. The hourly tier answers it (kept 90 days); the
    /// five-minute tier would too, just, at twelve times the points.
    /// </summary>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Most points read per datastore. Default 720 — thirty days of hourly
    /// buckets, and the size <see cref="Trend"/>'s O(n²) note calls fine.
    /// </summary>
    public int MaxPoints { get; init; } = 720;

    /// <summary>
    /// Warn when the fill date is this close. Default 30 days — <b>this
    /// product's choice</b>: long enough to order and install disk, or move
    /// machines, before it is an outage.
    /// </summary>
    public TimeSpan WarningWithin { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Critical when the fill date is this close. Default 7 days — <b>this
    /// product's choice</b>: inside a week there is only time to move things.
    /// </summary>
    public TimeSpan CriticalWithin { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The refusal thresholds of the estimate itself.</summary>
    public TimeToFullPolicy Estimate { get; init; } = TimeToFullPolicy.Default;

    public static DatastoreTimeToFullPolicy Default { get; } = new();
}

/// <summary>
/// What this inventory cycle read about one datastore's capacity.
/// </summary>
/// <param name="Datastore">The datastore entity.</param>
/// <param name="Name">Its display name, as the fingerprints have always used it.</param>
/// <param name="Source">The source that read it.</param>
/// <param name="CapacityBytes">What the volume holds.</param>
/// <param name="FreeBytes">What is left.</param>
/// <param name="UncommittedBytes">Promised to thin disks and not yet taken; null when not read.</param>
public sealed record DatastoreCapacity(
    EntityId Datastore,
    string Name,
    string Source,
    double CapacityBytes,
    double FreeBytes,
    double? UncommittedBytes);

/// <summary>
/// When each datastore fills at its current growth — and, beside it, the
/// over-commit finding, dated by the same estimate.
/// </summary>
/// <remarks>
/// <para>
/// Only a forecast alerts. A refusal (too little history, a step, no trend,
/// shrinking, beyond the horizon) raises nothing of its own: "we cannot say"
/// is not a problem with the datastore, and it is shown on the datastore's
/// page instead, where somebody asking the question will look.
/// </para>
/// <para>
/// The over-commit finding lives here rather than in the collector (roadmap
/// M4.4). It needs the date this rule computes, a collector may not read
/// history, and building the whole finding in one place is simpler than
/// having the application layer find another component's alert by its text
/// and rewrite it. Its fingerprint is exactly the one the collector used, so
/// an alert already open stays the same alert.
/// </para>
/// </remarks>
public static class DatastoreTimeToFull
{
    public const string RuleId = "datastore-time-to-full";

    public const string FillingTitle = "Datastore filling";

    public const string OvercommitTitle = "Datastore over-committed";

    private const string Category = "Capacity";

    /// <summary>
    /// The datastores this cycle has a capacity reading for, from the readings
    /// the inventory carried.
    /// </summary>
    /// <remarks>
    /// A datastore is included only when both its capacity and its free space
    /// were read this cycle; the collector writes neither for an inaccessible
    /// one, so "live" is decided where the numbers are.
    /// </remarks>
    public static IReadOnlyList<DatastoreCapacity> CurrentReadings(
        IEnumerable<Observation> observations, EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(graph);

        var found = new List<DatastoreCapacity>();

        foreach (var datastore in observations
            .Where(o => o.Value.CounterName is
                CapacityCounters.DatastoreCapacity or
                CapacityCounters.DatastoreFree or
                CapacityCounters.DatastoreUncommitted)
            .GroupBy(o => o.Entity))
        {
            double? Latest(string counter) =>
                datastore.Where(o => o.Value.CounterName == counter)
                    .OrderBy(o => o.SampledAtUtc)
                    .Select(o => (double?)o.Value.Raw)
                    .LastOrDefault();

            if (Latest(CapacityCounters.DatastoreCapacity) is not (> 0 and { } capacity) ||
                Latest(CapacityCounters.DatastoreFree) is not (>= 0 and { } free))
            {
                continue;
            }

            var name = graph.Entities.TryGetValue(datastore.Key, out var entity)
                ? entity.DisplayName
                : datastore.Key.Value;

            found.Add(new DatastoreCapacity(
                datastore.Key,
                name,
                datastore.First().Source,
                capacity,
                free,
                Latest(CapacityCounters.DatastoreUncommitted)));
        }

        return found;
    }

    /// <summary>
    /// The one query that fetches a datastore's used-space history.
    /// </summary>
    /// <remarks>
    /// The tier is chosen the way the interface chooses it: the finest one
    /// that fits the point budget and is still kept as far back as the
    /// lookback reaches. At the defaults that is the hourly tier.
    /// </remarks>
    public static SeriesQuery HistoryQuery(
        EntityId datastore,
        DateTimeOffset nowUtc,
        DatastoreTimeToFullPolicy policy,
        SeriesRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(retention);

        var resolution = retention.RetainedResolutionFor(
            policy.Lookback, policy.MaxPoints, policy.Lookback);

        return new SeriesQuery
        {
            Key = new SeriesKey(datastore, CapacityCounters.DatastoreUsed, string.Empty),
            FromUtc = nowUtc - policy.Lookback,
            ToUtc = nowUtc,
            Resolution = resolution,
            MaxPoints = policy.MaxPoints,
        };
    }

    /// <summary>
    /// Fits the history: each bucket's last reading is one point, at the
    /// bucket's start.
    /// </summary>
    public static TimeToFullResult Estimate(
        SeriesResult usedHistory,
        double capacityBytes,
        DateTimeOffset nowUtc,
        DatastoreTimeToFullPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(usedHistory);
        ArgumentNullException.ThrowIfNull(policy);

        return TimeToFull.Estimate(
            [.. usedHistory.Points.Select(p => new TrendPoint(p.StartUtc, p.Last))],
            capacityBytes,
            nowUtc,
            policy.Estimate);
    }

    /// <summary>Reads one datastore's history (one query) and estimates.</summary>
    public static TimeToFullResult Read(
        ISeriesReader series,
        EntityId datastore,
        double capacityBytes,
        DateTimeOffset nowUtc,
        DatastoreTimeToFullPolicy policy,
        SeriesRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(series);

        return Estimate(
            series.Query(HistoryQuery(datastore, nowUtc, policy, retention)),
            capacityBytes,
            nowUtc,
            policy);
    }

    /// <summary>
    /// The most recent capacity reading recorded for a datastore, or null.
    /// </summary>
    /// <remarks>
    /// For a caller outside the cycle, which has no reading of its own in
    /// hand. Looked for in the raw tier over its whole retention: a capacity
    /// older than that is not the datastore's capacity now.
    /// </remarks>
    public static double? LatestCapacity(
        ISeriesReader series,
        EntityId datastore,
        DateTimeOffset nowUtc,
        SeriesRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(retention);

        var result = series.Query(new SeriesQuery
        {
            Key = new SeriesKey(datastore, CapacityCounters.DatastoreCapacity, string.Empty),
            FromUtc = nowUtc - retention.Raw,
            ToUtc = nowUtc + TimeSpan.FromTicks(1),
            Resolution = SeriesResolution.Raw,

            // The store keeps the newest end when it truncates.
            MaxPoints = 1,
        });

        return result.Points.Count > 0 && result.Points[^1].Last is > 0 and var capacity
            ? capacity
            : null;
    }

    /// <summary>
    /// The alerts for what was read and estimated. Pure.
    /// </summary>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<(DatastoreCapacity Datastore, TimeToFullResult Estimate)> datastores,
        DatastoreTimeToFullPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(datastores);

        var rules = policy ?? DatastoreTimeToFullPolicy.Default;
        var alerts = new List<AlertDefinition>();

        foreach (var (datastore, estimate) in datastores)
        {
            if (Filling(datastore, estimate, rules) is { } filling)
            {
                alerts.Add(filling);
            }

            if (Overcommitted(datastore, estimate) is { } overcommitted)
            {
                alerts.Add(overcommitted);
            }
        }

        return alerts;
    }

    /// <summary>
    /// One sentence for a person: the date and the window it rests on, or why
    /// there is none. Shared by the alerts and the datastore's page.
    /// </summary>
    public static string Explain(TimeToFullResult estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        return estimate switch
        {
            TimeToFullResult.Forecast f => string.Create(CultureInfo.InvariantCulture, 
                $"fills in {f.Days:0.#} days (on {f.FullAtUtc:yyyy-MM-dd}), growing {Gigabytes(f.SlopePerDay):0.##} GB a day, " +
                $"based on {Describe(f.Window, f.PointsUsed)}"),
            TimeToFullResult.Refusal r => string.Create(CultureInfo.InvariantCulture, $"cannot estimate a fill date: {r.Detail}"),
            _ => throw new ArgumentOutOfRangeException(nameof(estimate)),
        };
    }

    /// <summary>The window an estimate was computed over, in words.</summary>
    public static string Describe(TrendWindow window, int points) => string.Create(CultureInfo.InvariantCulture, 
        $"{window.Span.TotalDays:0.#} days of history ({window.FromUtc:yyyy-MM-dd HH:mm} to {window.ToUtc:yyyy-MM-dd HH:mm} UTC, {points} points)");

    public static double Gigabytes(double bytes) => bytes / 1024d / 1024d / 1024d;

    private static AlertDefinition? Filling(
        DatastoreCapacity datastore, TimeToFullResult estimate, DatastoreTimeToFullPolicy policy)
    {
        if (estimate is not TimeToFullResult.Forecast forecast)
        {
            return null;
        }

        var severity = forecast.Days switch
        {
            var d when d <= policy.CriticalWithin.TotalDays => AlertSeverity.Critical,
            var d when d <= policy.WarningWithin.TotalDays => AlertSeverity.Warning,
            _ => (AlertSeverity?)null,
        };

        if (severity is not { } level)
        {
            return null;
        }

        return new AlertDefinition
        {
            // Per datastore and nothing else: the date moves every cycle and
            // is in the description, so the inbox holds one alert whose
            // wording updates, not a new alert each time the slope shifts.
            // One fingerprint across both severities, as the fullness alert.
            // The entity id rather than the name, so a rename does not close
            // one alert and open another.
            Fingerprint = AlertFingerprint.Create(
                datastore.Source, FillingTitle, Category, datastore.Datastore.Value, RuleId),
            Severity = level,
            Title = FillingTitle,
            Description = string.Create(CultureInfo.InvariantCulture, 
                $"'{datastore.Name}' {Explain(forecast)}. {Gigabytes(datastore.FreeBytes):0.#} GB of " +
                $"{Gigabytes(datastore.CapacityBytes):0.#} GB is free. Warning within " +
                $"{policy.WarningWithin.TotalDays:0} days and critical within {policy.CriticalWithin.TotalDays:0} " +
                $"are this product's thresholds, not a vendor's."),
            Category = Category,
            Source = datastore.Source,
            Entity = datastore.Datastore,
            IsDerived = true,
        };
    }

    /// <summary>
    /// Reports a datastore that has promised more than it has left, with the
    /// date used space reaches capacity at the current growth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thin provisioning means a disk occupies what it uses rather than what
    /// it was given, and the difference is a promise. Counter map §4 calls
    /// <c>summary.uncommitted</c> the only measure that warns long before a
    /// datastore fills, and the comparison worth making is against what
    /// remains: once the promises exceed the free space, the volume fills if
    /// the machines merely do what they were provisioned to do.
    /// </para>
    /// <para>
    /// Warning only, with no critical tier: the crisis tiers are the fullness
    /// alert at 95% and the fill-date alert inside a week. This one's purpose
    /// is to arrive earlier. The date answers "when", which the promise alone
    /// cannot: a volume over-committed tenfold that has not grown in a month
    /// is a different conversation from one filling next Tuesday.
    /// </para>
    /// </remarks>
    private static AlertDefinition? Overcommitted(DatastoreCapacity datastore, TimeToFullResult estimate)
    {
        // Must have been read. An unreadable figure is not a small one, and a
        // datastore with no thin disks legitimately reports nothing here.
        if (datastore.UncommittedBytes is not (> 0 and { } promised) || promised <= datastore.FreeBytes)
        {
            return null;
        }

        var when = estimate switch
        {
            TimeToFullResult.Forecast f => string.Create(CultureInfo.InvariantCulture, 
                $"At the current growth, used space reaches capacity on {f.FullAtUtc:yyyy-MM-dd}, in " +
                $"{f.Days:0.#} days ({Gigabytes(f.SlopePerDay):0.##} GB a day over {Describe(f.Window, f.PointsUsed)})."),
            TimeToFullResult.Refusal r => string.Create(CultureInfo.InvariantCulture, 
                $"There is no date for when used space reaches capacity: {r.Detail}"),
            _ => string.Empty,
        };

        return new AlertDefinition
        {
            // Exactly the collector's fingerprint, so an alert opened before
            // this rule owned the finding is the same alert now.
            Fingerprint = AlertFingerprint.Create(
                datastore.Source, OvercommitTitle, Category, datastore.Name, "datastore-overcommitted"),
            Severity = AlertSeverity.Warning,
            Title = OvercommitTitle,
            Description = string.Create(CultureInfo.InvariantCulture, 
                $"'{datastore.Name}' has promised {Gigabytes(promised):0.#} GB to thin disks that have " +
                $"not claimed it yet, and has {Gigabytes(datastore.FreeBytes):0.#} GB free. If those disks " +
                $"grow into what they were given, the datastore fills. {when}"),
            Category = Category,
            Source = datastore.Source,
            Entity = datastore.Datastore,
        };
    }
}
