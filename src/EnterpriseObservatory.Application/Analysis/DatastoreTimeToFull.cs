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

    /// <summary>
    /// How much of the history must have been read before a date is given:
    /// the shared "enough history" test, seven days and 80% of their points —
    /// <b>product policy</b> (ADR-0026).
    /// </summary>
    public HistoryCoveragePolicy History { get; init; } = HistoryCoveragePolicy.Default;

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

        // The shared "enough history" test (ADR-0026), before any fit: a
        // month spanned with a hole in the last week is not a week of evidence.
        var coverage = HistoryCoverage.Of(
            usedHistory.Points.Select(p => p.StartUtc),
            SeriesResolutions.Width(usedHistory.Resolution),
            nowUtc,
            policy.History);

        if (!coverage.IsEnough)
        {
            return new TimeToFullResult.Refusal
            {
                Reason = TimeToFullRefusalReason.InsufficientHistory,
                Detail = $"Not enough history: {coverage.Reason}.",
                PointsUsed = usedHistory.Points.Count,
                Window = usedHistory.Points.Count > 0
                    ? new TrendWindow(usedHistory.Points[0].StartUtc, usedHistory.Points[^1].StartUtc)
                    : null,
            };
        }

        return TimeToFull.Estimate(
            [.. usedHistory.Points.Select(p => new TrendPoint(p.StartUtc, p.Last))],
            capacityBytes,
            nowUtc,
            policy.Estimate);
    }

    /// <summary>Reads one datastore's history (one query) and estimates.</summary>
    /// <remarks>
    /// The query runs every time; the estimate does not. Its input is the
    /// hourly tier, whose buckets are written once, after the hour closes, so
    /// the five-minute cycle and every page load in between would otherwise
    /// fit the same 720 points again — about 260 000 pairwise slopes each.
    /// The estimate is kept in <paramref name="cache"/> (the process-wide
    /// <see cref="TimeToFullCache.Shared"/> unless one is given), shared by
    /// the rule and the datastore's page, and recomputed when the history,
    /// the capacity, the policy or the hour changes.
    /// </remarks>
    public static TimeToFullResult Read(
        ISeriesReader series,
        EntityId datastore,
        double capacityBytes,
        DateTimeOffset nowUtc,
        DatastoreTimeToFullPolicy policy,
        SeriesRetentionPolicy retention,
        TimeToFullCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(policy);

        var history = series.Query(HistoryQuery(datastore, nowUtc, policy, retention));

        return (cache ?? TimeToFullCache.Shared).GetOrAdd(
            datastore,
            TimeToFullCache.KeyOf(history, capacityBytes, nowUtc, policy),
            nowUtc,
            () => Estimate(history, capacityBytes, nowUtc, policy));
    }

    /// <summary>N of the over-commit alert: 2, where filling is the rule's 3 (design note §3.1).</summary>
    public static ResolutionPolicy OvercommitResolution { get; } = new() { ConsecutiveAbsent = 2 };

    /// <summary>
    /// Estimates each datastore read this cycle and says, for each of its two
    /// alerts, present, absent or unknown (ADR-0026) — so that one whose
    /// history cannot be read costs that datastore and not the others.
    /// </summary>
    /// <param name="datastores">This cycle's capacity readings.</param>
    /// <param name="estimate">Reads and estimates one datastore; may throw.</param>
    /// <param name="held">The alerts the rule holds, for the renamed-datastore case.</param>
    /// <param name="evidenceAtUtc">When the capacity readings were taken.</param>
    /// <param name="policy">The thresholds.</param>
    /// <remarks>
    /// <para>
    /// Filling: present for a date inside the thresholds, or a datastore
    /// already full (Critical: the worst case of the condition, not its
    /// absence). Absent for a date beyond them, or a refusal that is itself an
    /// answer — not filling, beyond the horizon, below the usage floor, no
    /// trend. Unknown for a refusal that is not — too few points, too short a
    /// window, a step, too little of the week read.
    /// </para>
    /// <para>
    /// Over-commit: present when the promises exceed the free space; absent
    /// when <c>summary.uncommitted</c> was read and they do not; unknown when it
    /// was not read, which coverage cannot tell apart from "no thin disks"
    /// (design note §5.10).
    /// </para>
    /// <para>
    /// A datastore not read this cycle gets no verdict: its alerts are "not
    /// reported", never absent. A history that throws is
    /// <see cref="UnknownReason.RuleFailed"/> for that datastore, and the
    /// failure is reported, never swallowed, as <see cref="HistoryUnreadableTitle"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SubjectVerdict> Judge(
        IReadOnlyList<DatastoreCapacity> datastores,
        Func<DatastoreCapacity, TimeToFullResult> estimate,
        IReadOnlyList<HeldAlert> held,
        DateTimeOffset evidenceAtUtc,
        DatastoreTimeToFullPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(datastores);
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(held);

        var rules = policy ?? DatastoreTimeToFullPolicy.Default;
        var verdicts = new List<SubjectVerdict>();
        var failed = new List<(DatastoreCapacity Datastore, Exception Error)>();
        var current = new HashSet<AlertFingerprint>();

        foreach (var datastore in datastores)
        {
            current.UnionWith(Fingerprints(datastore));

            TimeToFullResult result;

            try
            {
                result = estimate(datastore);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Justified: the query crosses a network to a
            // database and may throw anything; one datastore's history must
            // not cost every other datastore its fill date.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed.Add((datastore, ex));
                verdicts.Add(new Unknown
                {
                    Covers = Fingerprints(datastore),
                    Entity = datastore.Datastore,
                    Reason = UnknownReason.RuleFailed,
                    Detail = $"the used-space history of '{datastore.Name}' could not be read: " +
                             $"{ex.GetType().Name}: {ex.Message}",
                });

                continue;
            }

            verdicts.Add(FillingVerdict(datastore, result, rules, evidenceAtUtc));
            verdicts.Add(OvercommitVerdict(datastore, result, evidenceAtUtc));
        }

        // Only when there was something to read: with no datastore read,
        // "every history was read" would be said of nothing.
        if (datastores.Count > 0)
        {
            verdicts.Add(failed.Count > 0
                ? new ConditionPresent
                {
                    Covers = [HistoryUnreadableFingerprint],
                    Alerts = [HistoryUnreadable(failed)],
                    EvidenceAtUtc = evidenceAtUtc,
                }
                : new ConditionAbsent { Covers = [HistoryUnreadableFingerprint], EvidenceAtUtc = evidenceAtUtc });
        }

        // The over-commit fingerprint is keyed by name. A datastore read this
        // cycle under a new name has its old alert superseded by the new one,
        // on a fresh read of the same entity, rather than left "not reported".
        var read = datastores.Select(d => d.Datastore).ToHashSet();

        foreach (var alert in held)
        {
            if (alert.Entity is { } entity && read.Contains(entity) &&
                IsOvercommit(alert.Fingerprint) && !current.Contains(alert.Fingerprint))
            {
                verdicts.Add(new ConditionAbsent
                {
                    Covers = [alert.Fingerprint],
                    Entity = entity,
                    EvidenceAtUtc = evidenceAtUtc,
                    Because = AbsenceKind.Superseded,
                    Resolution = OvercommitResolution,
                });
            }
        }

        return verdicts;
    }

    private static SubjectVerdict FillingVerdict(
        DatastoreCapacity datastore, TimeToFullResult estimate, DatastoreTimeToFullPolicy policy, DateTimeOffset at)
    {
        IReadOnlyList<AlertFingerprint> covers = [FillingFingerprint(datastore)];

        if (Filling(datastore, estimate, policy) is { } alert)
        {
            return new ConditionPresent { Covers = covers, Alerts = [alert], Entity = datastore.Datastore, EvidenceAtUtc = at };
        }

        if (estimate is TimeToFullResult.Refusal
            {
                Reason: TimeToFullRefusalReason.TooFewPoints or TimeToFullRefusalReason.WindowTooShort or
                        TimeToFullRefusalReason.StepChange or TimeToFullRefusalReason.InsufficientHistory,
            } refusal)
        {
            return new Unknown
            {
                Covers = covers,
                Entity = datastore.Datastore,
                Reason = UnknownReason.InsufficientSeries,
                Detail = $"cannot estimate a fill date for '{datastore.Name}': {refusal.Detail}",
            };
        }

        return new ConditionAbsent { Covers = covers, Entity = datastore.Datastore, EvidenceAtUtc = at };
    }

    private static SubjectVerdict OvercommitVerdict(DatastoreCapacity datastore, TimeToFullResult estimate, DateTimeOffset at)
    {
        IReadOnlyList<AlertFingerprint> covers = [OvercommitFingerprint(datastore)];

        if (Overcommitted(datastore, estimate) is { } alert)
        {
            return new ConditionPresent { Covers = covers, Alerts = [alert], Entity = datastore.Datastore, EvidenceAtUtc = at };
        }

        if (datastore.UncommittedBytes is null)
        {
            return new Unknown
            {
                Covers = covers,
                Entity = datastore.Datastore,
                Reason = UnknownReason.InputNotCollected,
                Detail = $"summary.uncommitted was not read for '{datastore.Name}' this cycle",
            };
        }

        return new ConditionAbsent
        {
            Covers = covers,
            Entity = datastore.Datastore,
            EvidenceAtUtc = at,
            Resolution = OvercommitResolution,
        };
    }

    /// <summary>The fingerprint of the "history could not be read" alert: one for the rule.</summary>
    public static AlertFingerprint HistoryUnreadableFingerprint { get; } = AlertFingerprint.Create(
        "platform", HistoryUnreadableTitle, GuardedRule.Category, RuleId, HistoryUnreadableCheckId);

    public const string HistoryUnreadableTitle = "Datastore history could not be read";

    private static AlertDefinition HistoryUnreadable(
        List<(DatastoreCapacity Datastore, Exception Error)> failed)
    {
        const int named = 5;
        var names = string.Join(", ", failed.Take(named).Select(f => $"'{f.Datastore.Name}'"));
        var more = failed.Count > named ? $" and {failed.Count - named} more" : string.Empty;
        var (_, first) = failed[0];

        return new AlertDefinition
        {
            // One alert for the rule, not one per datastore: it is about this
            // product's reading, and it must not belong to a datastore's
            // source, or a silent vCenter would hold it open.
            Fingerprint = HistoryUnreadableFingerprint,

            // Warning, for the reason a failed rule is one: nothing says the
            // estate is broken, only that part of it is not being rechecked.
            Severity = AlertSeverity.Warning,
            Title = HistoryUnreadableTitle,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"The used-space history of {failed.Count} datastore(s) could not be read this cycle " +
                $"({names}{more}); the first threw {first.GetType().Name}: {first.Message} " +
                $"Their fill-date and over-commit alerts are kept as they were, not rechecked, until " +
                $"the history can be read again. The other datastores were estimated normally."),
            Category = GuardedRule.Category,
            Source = "platform",
            IsDerived = true,
        };
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

    /// <summary>
    /// Every fingerprint this rule can raise for one datastore — what a
    /// datastore whose history could not be read holds open.
    /// </summary>
    public static IReadOnlyList<AlertFingerprint> Fingerprints(DatastoreCapacity datastore)
    {
        ArgumentNullException.ThrowIfNull(datastore);

        return [FillingFingerprint(datastore), OvercommitFingerprint(datastore)];
    }

    // Per datastore and nothing else: the date moves every cycle and is in
    // the description, so the inbox holds one alert whose wording updates,
    // not a new alert each time the slope shifts. One fingerprint across both
    // severities, as the fullness alert. The entity id rather than the name,
    // so a rename does not close one alert and open another.
    private static AlertFingerprint FillingFingerprint(DatastoreCapacity datastore) =>
        AlertFingerprint.Create(
            datastore.Source, FillingTitle, Category, datastore.Datastore.Value, RuleId);

    // Exactly the collector's fingerprint, so an alert opened before this rule
    // owned the finding is the same alert now. It is keyed by the display
    // name, unlike the fill-date alert, and that is kept on purpose: moving
    // it to the entity id would close and reopen every over-commit alert
    // open today. The cost is that renaming a datastore resolves its alert
    // and raises a new one — accepted, because renames are rare and the new
    // alert says the same thing.
    private static AlertFingerprint OvercommitFingerprint(DatastoreCapacity datastore) =>
        AlertFingerprint.Create(
            datastore.Source, OvercommitTitle, Category, datastore.Name, OvercommitCheckId);

    /// <summary>The check id of the over-commit alert, the fingerprint's last segment.</summary>
    public const string OvercommitCheckId = "datastore-overcommitted";

    /// <summary>The check id of the "history could not be read" alert.</summary>
    public const string HistoryUnreadableCheckId = "datastore-history-unreadable";

    /// <summary>Whether a fingerprint is an over-commit alert, which resolves at its own N.</summary>
    public static bool IsOvercommit(AlertFingerprint fingerprint) =>
        fingerprint.Value.EndsWith("|" + OvercommitCheckId, StringComparison.Ordinal);

    private static AlertDefinition? Filling(
        DatastoreCapacity datastore, TimeToFullResult estimate, DatastoreTimeToFullPolicy policy)
    {
        // Full is the worst case of "filling", not its absence (ADR-0026
        // §5.10). This cycle's free space is fresher than the newest hourly
        // bucket, so it decides first; the estimate's own "already full" on
        // the history is the other way to know.
        if (datastore.FreeBytes <= 0 ||
            estimate is TimeToFullResult.Refusal { Reason: TimeToFullRefusalReason.AlreadyFull })
        {
            return new AlertDefinition
            {
                Fingerprint = FillingFingerprint(datastore),
                Severity = AlertSeverity.Critical,
                Title = FillingTitle,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"'{datastore.Name}' is full: {Gigabytes(Math.Max(0, datastore.FreeBytes)):0.#} GB of " +
                    $"{Gigabytes(datastore.CapacityBytes):0.#} GB is free. Writes to its thin disks and " +
                    $"snapshots can fail now; there is no fill date to wait for."),
                Category = Category,
                Source = datastore.Source,
                Entity = datastore.Datastore,
                IsDerived = true,
            };
        }

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
            Fingerprint = FillingFingerprint(datastore),
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
            Fingerprint = OvercommitFingerprint(datastore),
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
