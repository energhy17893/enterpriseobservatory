using System.Buffers;
using System.Runtime.InteropServices;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// The arithmetic the rules share, written once so that two rules that must
/// agree cannot drift apart.
/// </summary>
/// <remarks>
/// Public so that its arithmetic is pinned by tests of its own: it used to be
/// four private copies, and the even-count median was checked by none of them.
/// </remarks>
public static class Stats
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
        ArgumentNullException.ThrowIfNull(values);

        var count = values.Count;

        if (count == 0)
        {
            return 0d;
        }

        // The caller's list is left as it was: the selection runs on a pooled
        // copy rather than on a sorted one.
        var buffer = ArrayPool<double>.Shared.Rent(count);

        try
        {
            var span = buffer.AsSpan(0, count);

            switch (values)
            {
                case double[] array:
                    array.AsSpan().CopyTo(span);
                    break;
                case List<double> list:
                    CollectionsMarshal.AsSpan(list).CopyTo(span);
                    break;
                default:
                    for (var i = 0; i < count; i++)
                    {
                        span[i] = values[i];
                    }

                    break;
            }

            return MedianInPlace(span);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// <see cref="Median"/>, computed by reordering <paramref name="values"/>
    /// in place instead of sorting a copy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller that owns a scratch buffer — Theil–Sen's quarter of a
    /// million pairwise slopes — and does not want a second copy of it sorted.
    /// Expected O(n) by quickselect; the order the values are left in is
    /// unspecified.
    /// </para>
    /// <para>
    /// The same answer as sorting, to the bit: the two middle values are the
    /// same elements a sort would put there, ordered as
    /// <see cref="double.CompareTo(double)"/> orders them (as the sort did),
    /// and averaged the same way.
    /// </para>
    /// </remarks>
    public static double MedianInPlace(Span<double> values)
    {
        var count = values.Length;

        if (count == 0)
        {
            return 0d;
        }

        var middle = count / 2;
        Select(values, middle);

        if (count % 2 == 1)
        {
            return values[middle];
        }

        // Everything left of the middle is no greater than it, so the lower
        // middle value is the largest of those.
        var lower = values[0];
        for (var i = 1; i < middle; i++)
        {
            if (values[i].CompareTo(lower) > 0)
            {
                lower = values[i];
            }
        }

        return (lower + values[middle]) / 2d;
    }

    /// <summary>
    /// Hoare's selection: afterwards <paramref name="values"/>[k] is the k-th
    /// smallest, nothing before it is greater and nothing after it smaller.
    /// </summary>
    /// <remarks>
    /// Hoare's partition rather than Lomuto's on purpose: a flat datastore has
    /// every slope equal, and Lomuto's scheme is quadratic on equal keys where
    /// Hoare's splits them down the middle.
    /// </remarks>
    private static void Select(Span<double> values, int k)
    {
        var lo = 0;
        var hi = values.Length - 1;

        while (hi > lo)
        {
            // Median of three, which also leaves a sentinel at each end.
            var mid = lo + ((hi - lo) / 2);
            if (values[mid].CompareTo(values[lo]) < 0)
            {
                Swap(values, lo, mid);
            }

            if (values[hi].CompareTo(values[lo]) < 0)
            {
                Swap(values, lo, hi);
            }

            if (values[hi].CompareTo(values[mid]) < 0)
            {
                Swap(values, mid, hi);
            }

            var pivot = values[mid];
            var i = lo;
            var j = hi;

            while (i <= j)
            {
                while (values[i].CompareTo(pivot) < 0)
                {
                    i++;
                }

                while (pivot.CompareTo(values[j]) < 0)
                {
                    j--;
                }

                if (i <= j)
                {
                    Swap(values, i, j);
                    i++;
                    j--;
                }
            }

            // [lo, j] <= pivot, (j, i) == pivot, [i, hi] >= pivot.
            if (k <= j)
            {
                hi = j;
            }
            else if (k >= i)
            {
                lo = i;
            }
            else
            {
                return;
            }
        }
    }

    private static void Swap(Span<double> values, int a, int b) =>
        (values[a], values[b]) = (values[b], values[a]);

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
