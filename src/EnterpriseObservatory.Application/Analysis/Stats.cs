namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// The arithmetic the rules share, written once so that two rules that must
/// agree cannot drift apart.
/// </summary>
internal static class Stats
{
    /// <summary>
    /// The middle value; for an even count, the mean of the two middle values;
    /// for none, zero.
    /// </summary>
    /// <remarks>
    /// Zero for an empty list rather than an exception, because every caller
    /// floors the median before multiplying by it — see
    /// <see cref="FlooredMultiple"/>.
    /// </remarks>
    public static double Median(IReadOnlyList<double> values)
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

    /// <summary>
    /// <paramref name="multiple"/> times <paramref name="baseline"/>, the
    /// baseline floored at <paramref name="floor"/> first.
    /// </summary>
    /// <remarks>
    /// The peer test every "one stands out" rule makes. The floor is there
    /// because on this estate the usual baseline is exactly zero, and every
    /// reading is an infinite multiple of zero. Only the threshold is shared:
    /// each caller keeps its own comparison, since which side of it counts as
    /// "stands out" is part of that rule.
    /// </remarks>
    public static double FlooredMultiple(double multiple, double baseline, double floor) =>
        multiple * Math.Max(baseline, floor);
}
