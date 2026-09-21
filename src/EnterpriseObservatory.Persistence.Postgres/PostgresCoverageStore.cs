using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// What each source managed to read, durable.
/// </summary>
/// <remarks>
/// <para>
/// Kept across a restart for one reason, and it is the reason the coverage
/// surface exists at all: a report that is blank until the next inventory
/// cycle reads exactly like a report saying nothing is wrong. An operator who
/// opens this screen a minute after a restart must not be told that the
/// product can see everything, and the honest alternative to a stale number is
/// a number with a timestamp on it, not an empty page.
/// </para>
/// <para>
/// Replaced per source rather than merged. A property the collector no longer
/// asks about must stop being reported; left to accumulate, this table would
/// slowly fill with gaps that were closed by deleting the question.
/// </para>
/// </remarks>
public sealed class PostgresCoverageStore : ICoverageStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceCoverage> _coverage;

    public PostgresCoverageStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _coverage = _database.Read(Load);
    }

    /// <remarks>
    /// An in-memory copy for the same reason the health store keeps one: it is
    /// written by the cycle and read by the projection, and a read-through on
    /// every page view would put a query behind a screen that is refreshed
    /// while somebody watches it.
    /// </remarks>
    public IReadOnlyList<SourceCoverage> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _coverage.Values];
            }
        }
    }

    public void Replace(
        string sourceInstanceId,
        IReadOnlyList<PropertyCoverage> coverage,
        DateTimeOffset measuredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentNullException.ThrowIfNull(coverage);

        lock (_gate)
        {
            _database.Write(connection =>
            {
                // Delete then insert, in one transaction. An upsert would
                // leave behind rows for properties that are no longer asked
                // about, and those read as gaps rather than as questions
                // somebody withdrew.
                using var clear = connection.CreateCommand();
                clear.CommandText = "DELETE FROM property_coverage WHERE instance_id = @instance;";
                clear.Parameters.AddWithValue("instance", sourceInstanceId);
                clear.ExecuteNonQuery();

                foreach (var row in coverage)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO property_coverage (
                            instance_id, object_type, property, asked, answered, measured_at_utc)
                        VALUES (@instance, @type, @property, @asked, @answered, @measured);
                        """;
                    insert.Parameters.AddWithValue("instance", sourceInstanceId);
                    insert.Parameters.AddWithValue("type", row.ObjectType);
                    insert.Parameters.AddWithValue("property", row.Property);
                    insert.Parameters.AddWithValue("asked", row.Asked);
                    insert.Parameters.AddWithValue("answered", row.Answered);
                    insert.Parameters.AddWithValue("measured", measuredAtUtc.UtcDateTime);
                    insert.ExecuteNonQuery();
                }
            });

            // A source that measured nothing keeps a row with no properties
            // rather than disappearing, because "this source reported and
            // measured no coverage" and "this source has never been heard
            // from" are different, and only the second deserves silence.
            _coverage[sourceInstanceId] = new SourceCoverage
            {
                SourceInstanceId = sourceInstanceId,
                MeasuredAtUtc = measuredAtUtc,
                Properties = [.. coverage],
            };
        }
    }

    private static Dictionary<string, SourceCoverage> Load(NpgsqlConnection connection)
    {
        var rows = new Dictionary<string, (DateTimeOffset At, List<PropertyCoverage> Rows)>(
            StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, object_type, property, asked, answered, measured_at_utc
            FROM property_coverage;
            """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var instance = reader.GetString(0);
            var measured = new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero);

            if (!rows.TryGetValue(instance, out var entry))
            {
                entry = (measured, []);
                rows[instance] = entry;
            }

            entry.Rows.Add(new PropertyCoverage
            {
                ObjectType = reader.GetString(1),
                Property = reader.GetString(2),
                Asked = reader.GetInt32(3),
                Answered = reader.GetInt32(4),
            });
        }

        return rows.ToDictionary(
            r => r.Key,
            r => new SourceCoverage
            {
                SourceInstanceId = r.Key,
                MeasuredAtUtc = r.Value.At,
                Properties = r.Value.Rows,
            },
            StringComparer.Ordinal);
    }
}
