using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Measurements, stored and downsampled.
/// </summary>
/// <remarks>
/// <para>
/// Three things happen here and they are separate on purpose: samples are
/// appended as they arrive, coarser buckets are folded from finer ones on a
/// timer, and anything past its retention is deleted. Folding and deleting are
/// the same pass because doing them in the wrong order would delete a sample
/// before it had been summarised.
/// </para>
/// <para>
/// See ADR-0012.
/// </para>
/// </remarks>
public sealed class SqliteObservationStore : IObservationStore
{
    /// <summary>The most points any single query will return.</summary>
    /// <remarks>
    /// A hard ceiling above the caller's own budget. Even at the coarsest
    /// resolution a long enough range exceeds what anyone can use, and an
    /// unbounded response is the easiest way to make the interface unusable
    /// while the server looks healthy.
    /// </remarks>
    public const int HardMaxPoints = 5_000;

    private readonly MetricsDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<SeriesKey, long> _seriesIds = [];

    public SqliteObservationStore(MetricsDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));

        // The dictionary is small — one row per entity and counter — and it is
        // consulted for every sample of every cycle. Reading it once is the
        // difference between one query per cycle and ten thousand.
        foreach (var (key, id) in _database.Read(LoadSeries))
        {
            _seriesIds[key] = id;
        }
    }

    // --- writing ----------------------------------------------------------

    public void Append(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _database.Write(connection =>
            {
                using var insert = Command(connection, """
                    INSERT OR REPLACE INTO sample (series_id, at_utc, value)
                    VALUES ($series, $at, $value);
                    """);

                foreach (var observation in observations)
                {
                    var key = new SeriesKey(
                        observation.Entity,
                        observation.Value.CounterName,
                        observation.Value.Instance);

                    var id = SeriesId(connection, key, observation.Value);

                    insert.Parameters.Clear();
                    insert.Bind("$series", id);
                    insert.Bind("$at", Seconds(observation.SampledAtUtc));
                    insert.Bind("$value", observation.Value.Raw);
                    insert.ExecuteNonQuery();
                }
            });
        }
    }

    /// <summary>
    /// The id of a series, creating it the first time it is seen.
    /// </summary>
    /// <remarks>
    /// Stores the raw value, not a converted one. What a number means is
    /// carried by its unit and rollup, which are recorded here once rather than
    /// applied at write time — converting on the way in would make the stored
    /// history depend on the version of the code that wrote it, and a
    /// correction to a conversion could never be applied to what is already
    /// there. See the metric contract.
    /// </remarks>
    private long SeriesId(SqliteConnection connection, SeriesKey key, CounterValue value)
    {
        if (_seriesIds.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var command = Command(connection, """
            INSERT INTO series (entity_id, counter, instance, unit, rollup)
            VALUES ($entity, $counter, $instance, $unit, $rollup)
            ON CONFLICT (entity_id, counter, instance) DO UPDATE SET unit = excluded.unit
            RETURNING id;
            """);

        command.Bind("$entity", key.Entity.Value);
        command.Bind("$counter", key.Counter);
        command.Bind("$instance", key.Instance);
        command.Bind("$unit", value.Unit);
        command.Bind("$rollup", value.Rollup.ToString());

        var id = (long)command.ExecuteScalar()!;
        _seriesIds[key] = id;

        return id;
    }

    // --- reading ----------------------------------------------------------

    public SeriesResult Query(SeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resolution = query.Resolution
            ?? SeriesRetentionPolicy.ResolutionFor(query.ToUtc - query.FromUtc, query.MaxPoints);

        var limit = Math.Clamp(query.MaxPoints, 1, HardMaxPoints);

        return _database.Read(connection =>
        {
            var series = FindSeries(connection, query.Key);

            if (series is not { } found)
            {
                // Not an empty series: a series nobody has ever recorded and an
                // entity that has genuinely reported nothing are different
                // answers, and the caller has to be able to tell them apart.
                return new SeriesResult
                {
                    Key = query.Key,
                    Resolution = resolution,
                    Exists = false,
                };
            }

            var points = resolution == SeriesResolution.Raw
                ? ReadSamples(connection, found.Id, query.FromUtc, query.ToUtc, limit + 1)
                : ReadBuckets(connection, found.Id, resolution, query.FromUtc, query.ToUtc, limit + 1);

            // Read newest-first so that a range with more points than the caller
            // can take keeps the recent end: a chart missing the present is
            // useless in a way that one missing the past is not. Reversed here
            // so callers always receive it oldest-first.
            var truncated = points.Count > limit;

            if (truncated)
            {
                points.RemoveRange(limit, points.Count - limit);
            }

            points.Reverse();

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = resolution,
                Points = points,
                Unit = found.Unit,
                Rollup = found.Rollup,
                Truncated = truncated,
                Exists = true,
            };
        });
    }

    public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => _database.Read(connection =>
    {
        var keys = new List<SeriesKey>();

        using var command = Command(connection, """
            SELECT counter, instance FROM series WHERE entity_id = $entity
            ORDER BY counter, instance;
            """);

        command.Bind("$entity", entity.Value);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            keys.Add(new SeriesKey(entity, reader.GetString(0), reader.GetString(1)));
        }

        return (IReadOnlyList<SeriesKey>)keys;
    });

    // --- compaction -------------------------------------------------------

    public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            // Folding before deleting, in that order and in one pass. The other
            // order deletes a sample before it has been summarised, and the
            // loss is silent and permanent.
            var written =
                Fold(nowUtc, policy, SeriesResolution.FiveMinutes) +
                Fold(nowUtc, policy, SeriesResolution.OneHour);

            var samplesDeleted = DeleteAgedSamples(nowUtc, policy);
            var bucketsDeleted =
                DeleteAgedBuckets(nowUtc, policy, SeriesResolution.FiveMinutes) +
                DeleteAgedBuckets(nowUtc, policy, SeriesResolution.OneHour);

            return new CompactionReport
            {
                BucketsWritten = written,
                SamplesDeleted = samplesDeleted,
                BucketsDeleted = bucketsDeleted,
                SeriesForgotten = ForgetEmptySeries(),
            };
        }
    }

    /// <summary>
    /// Builds every complete bucket of one resolution that has not been built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by a watermark so a pass costs the same whether the service has
    /// been running for an hour or a year. The watermark is written in the same
    /// transaction as the buckets, so an interrupted pass resumes from what it
    /// actually did.
    /// </para>
    /// <para>
    /// Idempotent regardless: a bucket is computed from its sources rather than
    /// accumulated into, so running twice writes the same numbers.
    /// </para>
    /// </remarks>
    private int Fold(DateTimeOffset nowUtc, SeriesRetentionPolicy policy, SeriesResolution target)
    {
        var source = SeriesResolutions.Source(target)!.Value;

        // Only buckets that can no longer receive a sample. A bucket summarised
        // while still filling would be a summary of half of it, and nothing
        // ever revisits it.
        var completeTo = SeriesResolutions.BucketStart(nowUtc - policy.CompactionGrace, target);

        return _database.Write(connection =>
        {
            var from = Watermark(connection, target)
                ?? EarliestSource(connection, source, target);

            if (from is not { } start || start >= completeTo)
            {
                return 0;
            }

            var written = source == SeriesResolution.Raw
                ? FoldFromSamples(connection, target, start, completeTo)
                : FoldFromBuckets(connection, target, source, start, completeTo);

            SetWatermark(connection, target, completeTo);

            return written;
        });
    }

    private static int FoldFromSamples(
        SqliteConnection connection, SeriesResolution target, DateTimeOffset from, DateTimeOffset to)
    {
        var width = (long)SeriesResolutions.Width(target).TotalSeconds;

        // The last value is picked with a window function rather than by
        // leaving a bare column next to the aggregates. SQLite does define a
        // bare column to come from the extreme row, but only when the query has
        // exactly one MIN or MAX; with four aggregates the row it comes from is
        // arbitrary, and an arbitrary "last value" would be wrong in a way
        // nothing would ever report.
        using var command = Command(connection, $"""
            WITH ranked AS (
                SELECT series_id,
                       at_utc - (at_utc % {width}) AS bucket_start,
                       value,
                       ROW_NUMBER() OVER (
                           PARTITION BY series_id, at_utc - (at_utc % {width})
                           ORDER BY at_utc DESC) AS recency
                FROM sample
                WHERE at_utc >= $from AND at_utc < $to
            )
            INSERT OR REPLACE INTO bucket
                (series_id, resolution, start_utc, min_value, max_value, sum_value,
                 sample_count, last_value)
            SELECT series_id,
                   $resolution,
                   bucket_start,
                   MIN(value), MAX(value), SUM(value), COUNT(*),
                   MAX(CASE WHEN recency = 1 THEN value END)
            FROM ranked
            GROUP BY series_id, bucket_start;
            """);

        command.Bind("$resolution", target.ToString());
        command.Bind("$from", Seconds(from));
        command.Bind("$to", Seconds(to));

        return command.ExecuteNonQuery();
    }

    private static int FoldFromBuckets(
        SqliteConnection connection,
        SeriesResolution target,
        SeriesResolution source,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var width = (long)SeriesResolutions.Width(target).TotalSeconds;

        // Min of mins, max of maxes, sum of sums, count of counts: exact,
        // which is the whole reason those five numbers are what is stored. The
        // last value comes from the latest source bucket, chosen the same
        // deliberate way as when folding raw samples.
        using var command = Command(connection, $"""
            WITH ranked AS (
                SELECT series_id,
                       start_utc - (start_utc % {width}) AS bucket_start,
                       min_value, max_value, sum_value, sample_count, last_value,
                       ROW_NUMBER() OVER (
                           PARTITION BY series_id, start_utc - (start_utc % {width})
                           ORDER BY start_utc DESC) AS recency
                FROM bucket
                WHERE resolution = $source AND start_utc >= $from AND start_utc < $to
            )
            INSERT OR REPLACE INTO bucket
                (series_id, resolution, start_utc, min_value, max_value, sum_value,
                 sample_count, last_value)
            SELECT series_id,
                   $target,
                   bucket_start,
                   MIN(min_value), MAX(max_value), SUM(sum_value), SUM(sample_count),
                   MAX(CASE WHEN recency = 1 THEN last_value END)
            FROM ranked
            GROUP BY series_id, bucket_start;
            """);

        command.Bind("$target", target.ToString());
        command.Bind("$source", source.ToString());
        command.Bind("$from", Seconds(from));
        command.Bind("$to", Seconds(to));

        return command.ExecuteNonQuery();
    }

    private static DateTimeOffset? Watermark(SqliteConnection connection, SeriesResolution resolution)
    {
        using var command = Command(
            connection, "SELECT completed_to_utc FROM compaction WHERE resolution = $resolution;");

        command.Bind("$resolution", resolution.ToString());

        return command.ExecuteScalar() is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private static void SetWatermark(
        SqliteConnection connection, SeriesResolution resolution, DateTimeOffset completedTo)
    {
        using var command = Command(connection, """
            INSERT OR REPLACE INTO compaction (resolution, completed_to_utc)
            VALUES ($resolution, $to);
            """);

        command.Bind("$resolution", resolution.ToString());
        command.Bind("$to", Seconds(completedTo));
        command.ExecuteNonQuery();
    }

    /// <summary>The first bucket that could be built, on a database with no watermark.</summary>
    private static DateTimeOffset? EarliestSource(
        SqliteConnection connection, SeriesResolution source, SeriesResolution target)
    {
        using var command = source == SeriesResolution.Raw
            ? Command(connection, "SELECT MIN(at_utc) FROM sample;")
            : Command(connection, "SELECT MIN(start_utc) FROM bucket WHERE resolution = $source;");

        if (source != SeriesResolution.Raw)
        {
            command.Bind("$source", source.ToString());
        }

        return command.ExecuteScalar() is long seconds
            ? SeriesResolutions.BucketStart(DateTimeOffset.FromUnixTimeSeconds(seconds), target)
            : null;
    }

    private int DeleteAgedSamples(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) =>
        _database.Write(connection =>
        {
            using var command = Command(connection, "DELETE FROM sample WHERE at_utc < $cutoff;");
            command.Bind("$cutoff", Seconds(nowUtc - policy.Raw));
            return command.ExecuteNonQuery();
        });

    private int DeleteAgedBuckets(
        DateTimeOffset nowUtc, SeriesRetentionPolicy policy, SeriesResolution resolution) =>
        _database.Write(connection =>
        {
            using var command = Command(connection, """
                DELETE FROM bucket WHERE resolution = $resolution AND start_utc < $cutoff;
                """);

            command.Bind("$resolution", resolution.ToString());
            command.Bind("$cutoff", Seconds(nowUtc - policy.For(resolution)));

            return command.ExecuteNonQuery();
        });

    /// <summary>
    /// Removes series with nothing left at any resolution.
    /// </summary>
    /// <remarks>
    /// Otherwise the dictionary grows forever with every counter of every
    /// virtual machine that has ever existed, and it is loaded into memory at
    /// startup.
    /// </remarks>
    private int ForgetEmptySeries()
    {
        var removed = _database.Write(connection =>
        {
            using var command = Command(connection, """
                DELETE FROM series
                WHERE NOT EXISTS (SELECT 1 FROM sample WHERE sample.series_id = series.id)
                  AND NOT EXISTS (SELECT 1 FROM bucket WHERE bucket.series_id = series.id)
                RETURNING entity_id, counter, instance;
                """);

            var keys = new List<SeriesKey>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                keys.Add(new SeriesKey(
                    new EntityId(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
            }

            return keys;
        });

        foreach (var key in removed)
        {
            _seriesIds.Remove(key);
        }

        return removed.Count;
    }

    // --- internals --------------------------------------------------------

    private static long Seconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static Dictionary<SeriesKey, long> LoadSeries(SqliteConnection connection)
    {
        var series = new Dictionary<SeriesKey, long>();

        using var command = Command(connection, "SELECT id, entity_id, counter, instance FROM series;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            series[new SeriesKey(
                new EntityId(reader.GetString(1)), reader.GetString(2), reader.GetString(3))] =
                reader.GetInt64(0);
        }

        return series;
    }

    private static (long Id, string Unit, RollupType Rollup)? FindSeries(
        SqliteConnection connection, SeriesKey key)
    {
        using var command = Command(connection, """
            SELECT id, unit, rollup FROM series
            WHERE entity_id = $entity AND counter = $counter AND instance = $instance;
            """);

        command.Bind("$entity", key.Entity.Value);
        command.Bind("$counter", key.Counter);
        command.Bind("$instance", key.Instance);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetInt64(0), reader.GetString(1), ReadEnum<RollupType>(reader, 2))
            : null;
    }

    /// <summary>
    /// Raw samples, newest first, each presented as a bucket of one.
    /// </summary>
    /// <remarks>
    /// Shaped like a bucket so that a caller draws the same thing whatever
    /// resolution answered: a single sample is its own minimum, maximum, sum
    /// and last value.
    /// </remarks>
    private static List<AggregatedSample> ReadSamples(
        SqliteConnection connection, long seriesId, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        // Newest first; the caller reverses. One more than the budget is read
        // so that "there was more" can be reported rather than guessed at.
        using var command = Command(connection, """
            SELECT at_utc, value FROM sample
            WHERE series_id = $series AND at_utc >= $from AND at_utc < $to
            ORDER BY at_utc DESC
            LIMIT $limit;
            """);

        command.Bind("$series", seriesId);
        command.Bind("$from", Seconds(from));
        command.Bind("$to", Seconds(to));
        command.Bind("$limit", limit);

        var points = new List<AggregatedSample>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var value = reader.GetDouble(1);

            points.Add(new AggregatedSample
            {
                StartUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                Min = value,
                Max = value,
                Sum = value,
                Count = 1,
                Last = value,
            });
        }

        return points;
    }

    private static List<AggregatedSample> ReadBuckets(
        SqliteConnection connection,
        long seriesId,
        SeriesResolution resolution,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit)
    {
        using var command = Command(connection, """
            SELECT start_utc, min_value, max_value, sum_value, sample_count, last_value
            FROM bucket
            WHERE series_id = $series AND resolution = $resolution
              AND start_utc >= $from AND start_utc < $to
            ORDER BY start_utc DESC
            LIMIT $limit;
            """);

        command.Bind("$series", seriesId);
        command.Bind("$resolution", resolution.ToString());
        command.Bind("$from", Seconds(from));
        command.Bind("$to", Seconds(to));
        command.Bind("$limit", limit);

        var points = new List<AggregatedSample>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            points.Add(new AggregatedSample
            {
                StartUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                Min = reader.GetDouble(1),
                Max = reader.GetDouble(2),
                Sum = reader.GetDouble(3),
                Count = (int)reader.GetInt64(4),
                Last = reader.GetDouble(5),
            });
        }

        return points;
    }
}
