using System.Collections.Concurrent;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// The last fill-date estimate made for each datastore, and what it was made
/// from.
/// </summary>
/// <remarks>
/// <para>
/// The estimate is O(n²) in the history's points and its input is the hourly
/// tier, which gains a bucket once an hour. The inventory cycle asks every
/// five minutes and the datastore's page on every load, so without this the
/// same 720 points were fitted twelve times an hour per datastore, and again
/// per page view. See <see cref="DatastoreTimeToFull.Read"/>.
/// </para>
/// <para>
/// One entry per datastore, replaced when anything it was computed from
/// changes — so the size is the number of datastores, and bounded by
/// <see cref="MaxEntries"/> besides: past it the cache is emptied rather than
/// managed, which costs one recomputation per datastore and nothing else.
/// </para>
/// <para>
/// The key is the history itself (the count, the first and last bucket and
/// the last reading — buckets are written once, after the hour closes), the
/// capacity, the policy, and the hour of evaluation. The hour is there
/// because a refusal "beyond the horizon" depends on when it is asked; a
/// forecast's date does not, and a forecast served later in the same hour has
/// its days-from-now recounted from the date.
/// </para>
/// </remarks>
public sealed class TimeToFullCache
{
    private readonly ConcurrentDictionary<EntityId, Entry> _entries = new();

    private long _computations;

    public TimeToFullCache(int maxEntries = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        MaxEntries = maxEntries;
    }

    /// <summary>The one the rule and the datastore's page share.</summary>
    public static TimeToFullCache Shared { get; } = new();

    public int MaxEntries { get; }

    public int Count => _entries.Count;

    /// <summary>How many estimates were actually computed rather than served.</summary>
    public long Computations => Interlocked.Read(ref _computations);

    /// <summary>What an estimate was made from.</summary>
    public readonly record struct Key(
        double CapacityBytes,
        int Points,
        DateTimeOffset FirstUtc,
        DateTimeOffset LastUtc,
        double LastValue,
        DateTimeOffset HourUtc,
        DatastoreTimeToFullPolicy Policy);

    public static Key KeyOf(
        SeriesResult history,
        double capacityBytes,
        DateTimeOffset nowUtc,
        DatastoreTimeToFullPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        var points = history.Points;

        return new Key(
            capacityBytes,
            points.Count,
            points.Count > 0 ? points[0].StartUtc : default,
            points.Count > 0 ? points[^1].StartUtc : default,
            points.Count > 0 ? points[^1].Last : 0d,
            SeriesResolutions.BucketStart(nowUtc, SeriesResolution.OneHour),
            policy);
    }

    /// <summary>
    /// The cached estimate for <paramref name="key"/>, or <paramref name="compute"/>'s.
    /// </summary>
    public TimeToFullResult GetOrAdd(
        EntityId datastore, Key key, DateTimeOffset nowUtc, Func<TimeToFullResult> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        if (_entries.TryGetValue(datastore, out var cached) && cached.Key.Equals(key))
        {
            return AsOf(cached, nowUtc);
        }

        var result = compute();
        Interlocked.Increment(ref _computations);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(datastore))
        {
            _entries.Clear();
        }

        _entries[datastore] = new Entry(key, nowUtc, result);

        return result;
    }

    /// <summary>
    /// A forecast's days are counted from when it is asked; its date is not.
    /// </summary>
    private static TimeToFullResult AsOf(Entry entry, DateTimeOffset nowUtc)
    {
        if (entry.ComputedAtUtc == nowUtc || entry.Result is not TimeToFullResult.Forecast forecast)
        {
            return entry.Result;
        }

        var days = Math.Max(0d, (forecast.FullAtUtc - nowUtc).TotalDays);

        return forecast with
        {
            Days = days,
            FullAtUtc = days == 0 ? nowUtc : forecast.FullAtUtc,
        };
    }

    private sealed record Entry(Key Key, DateTimeOffset ComputedAtUtc, TimeToFullResult Result);
}
