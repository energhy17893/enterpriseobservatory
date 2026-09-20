using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How far one vantage point may disagree with the others before it is news.
/// </summary>
/// <remarks>
/// Both gates are needed and they fail differently. Without the floor, a host
/// at 2ms among peers at 0 is a large multiple of nothing; without the
/// multiple, a busy afternoon takes every host over the floor together and the
/// product reports ten faults where there is one array under load.
/// </remarks>
public sealed record PeerOutlierPolicy
{
    /// <summary>
    /// How many vantage points must exist before any of them can be an outlier.
    /// </summary>
    /// <remarks>
    /// Three, because two cannot say which of them is wrong. With a pair there
    /// is a disagreement and no majority, and calling the higher one faulty is
    /// a coin toss dressed as a diagnosis.
    /// </remarks>
    public int MinimumVantagePoints { get; init; } = 3;

    /// <summary>
    /// The reading the outlier must reach on its own, whatever its peers do.
    /// </summary>
    /// <remarks>
    /// Five milliseconds. On the estate this was written against every volume
    /// sits on all-flash and the platform truncates anything under a
    /// millisecond to zero, so peers normally read 0 or 1 — and a ratio
    /// against that is meaningless noise. This is the minimum-impact gate the
    /// reference literature names: something must actually be slow before
    /// anybody is told which host it is slow from.
    /// </remarks>
    public double MinimumMilliseconds { get; init; } = 5d;

    /// <summary>How many times its peers' median the outlier must reach.</summary>
    /// <remarks>
    /// The peer median is floored at one before dividing, because the usual
    /// median here is exactly zero and every reading is an infinite multiple
    /// of that.
    /// </remarks>
    public double Multiple { get; init; } = 4d;

    public static PeerOutlierPolicy Default { get; } = new();
}

/// <summary>
/// Finds a shared resource that is slow from one vantage point and not the rest.
/// </summary>
/// <remarks>
/// <para>
/// The rung of the diagnostic ladder the whole storage chain was built for. A
/// volume slow from every host that mounts it is the array or the fabric; the
/// same volume slow from one host is that host's HBA, cable or path. Every
/// coarser number averages the two situations together and reports something
/// mild.
/// </para>
/// <para>
/// It compares peers at one instant rather than against history, which is why
/// it needs no baseline, no percentile and no schema change. That matters:
/// the reference research found Dynatrace's auto-adaptive thresholds need a
/// 99th percentile and an interquartile range over seven days, and this
/// product's buckets cannot produce either. A relative comparison sidesteps
/// the whole question — and is the better answer anyway for heterogeneity,
/// which is the first thing static thresholds are said to fail at.
/// </para>
/// <para>
/// Milliseconds only, and that is not an implementation detail. Throughput
/// legitimately differs between vantage points — one host runs more virtual
/// machines and does more I/O, which is not a fault — while a service time
/// measured from several places onto one shared resource should agree. Peers
/// disagreeing about how long the resource takes is evidence about the path
/// between them; peers disagreeing about how much they ask of it is not.
/// </para>
/// </remarks>
public static class PeerOutliers
{
    public const string Category = "Storage path";
    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "peer-outliers";


    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        PeerOutlierPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var rules = policy ?? PeerOutlierPolicy.Default;
        var alerts = new List<AlertDefinition>();

        var groups = observations
            .Where(o => o.Value.InstanceIsVantagePoint && IsDuration(o.Value.Unit))
            .GroupBy(o => (o.Entity, o.Value.CounterName));

        foreach (var group in groups)
        {
            if (Outlier(group, rules) is { } found)
            {
                alerts.Add(found);
            }
        }

        return alerts;
    }

    private static AlertDefinition? Outlier(
        IEnumerable<Observation> readings, PeerOutlierPolicy rules)
    {
        var ordered = readings.OrderByDescending(o => o.Value.Raw).ToList();

        if (ordered.Count < rules.MinimumVantagePoints)
        {
            return null;
        }

        var worst = ordered[0];

        if (worst.Value.Raw < rules.MinimumMilliseconds)
        {
            return null;
        }

        // The median of everyone else, so the outlier is not compared with
        // itself. With it included, one bad reading among three drags the
        // median up and hides behind it.
        var peers = ordered.Skip(1).Select(o => o.Value.Raw).ToList();
        var median = Median(peers);

        // Floored at one before dividing: the usual median on an all-flash
        // estate is exactly zero, and dividing by it makes every reading an
        // infinite multiple.
        if (worst.Value.Raw < rules.Multiple * Math.Max(median, 1d))
        {
            return null;
        }

        return new AlertDefinition
        {
            // The entity and counter, not the vantage point. The vantage point
            // is what the alert is about, but which host is worst may change
            // between cycles while the volume stays the sick one — and a
            // fingerprint that moved with it would raise a new alert each time
            // and lose the history of a fault that has run for hours.
            Fingerprint = AlertFingerprint.Create(
                worst.Source, Title, Category, $"{worst.Entity.Value}/{worst.Value.CounterName}",
                "peer-outlier"),
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description = Describe(worst, peers, median),
            Category = Category,
            Source = worst.Source,
            Entity = worst.Entity,
        };
    }

    private const string Title = "Slow from one host only";

    private static string Describe(
        Observation worst, List<double> peers, double median)
    {
        var value = worst.Value;

        static string Ms(double v) =>
            v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        return
            $"'{value.CounterName}' reads {Ms(value.Raw)} ms from '{value.Instance}' while the " +
            $"other {peers.Count} host(s) that mount this see a median of {Ms(median)} ms " +
            $"(worst peer {Ms(peers.Count > 0 ? peers.Max() : 0)} ms). A shared volume that is " +
            "slow from one host and not the rest is not the array: the difference is in the " +
            "path between that host and the storage. Check that host's HBA, its cable and " +
            "transceiver, and its zoning before looking at the array.";
    }

    /// <summary>
    /// Milliseconds, and deliberately nothing else. See the type's remarks.
    /// </summary>
    private static bool IsDuration(string unit) =>
        string.Equals(unit, "millisecond", StringComparison.OrdinalIgnoreCase);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }
}
