using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>The source-level gap record, durable (migration 13).</summary>
/// <remarks>
/// Read through on every call rather than cached: a source asks for its open
/// gaps once a cycle, there are a handful at most, and the row is the truth a
/// restart has to come back to.
/// </remarks>
public sealed class PostgresCollectionGapStore : ICollectionGapStore
{
    private const string Columns = """
        id, source_instance_id, gap_from_utc, gap_to_utc, filled_to_utc, state,
        lost_before_utc, opened_at_utc, closed_at_utc
        """;

    private readonly PostgresDatabase _database;

    public PostgresCollectionGapStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <remarks>
    /// One index probe per series rather than an aggregate over the samples:
    /// <c>sample</c>'s key leads with the series, so the newest row of each is
    /// the last entry of its range. Two days of raw history for a few thousand
    /// entities is hundreds of millions of rows, and a <c>max()</c> over a join
    /// would read all of them to answer a question about the last one.
    /// </remarks>
    public IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        if (entities.Count == 0)
        {
            return new Dictionary<EntityId, DateTimeOffset>();
        }

        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT entity_id, max(latest) FROM (
                    SELECT s.entity_id,
                           (SELECT x.at_utc FROM sample x
                            WHERE x.series_id = s.id
                            ORDER BY x.at_utc DESC LIMIT 1) AS latest
                    FROM series s
                    WHERE s.entity_id = ANY(@entities)
                ) newest
                WHERE latest IS NOT NULL
                GROUP BY entity_id;
                """;
            command.Parameters.Add(new NpgsqlParameter("entities", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = entities.Select(e => e.Value).Distinct(StringComparer.Ordinal).ToArray(),
            });

            var marks = new Dictionary<EntityId, DateTimeOffset>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                marks[new EntityId(reader.GetString(0))] = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
            }

            return (IReadOnlyDictionary<EntityId, DateTimeOffset>)marks;
        });
    }

    public IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId) =>
        Select(sourceInstanceId, onlyOpen: true);

    public IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId) =>
        Select(sourceInstanceId, onlyOpen: false);

    public CollectionGap Open(CollectionGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        var id = _database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO collection_gap (
                    source_instance_id, gap_from_utc, gap_to_utc, filled_to_utc, state,
                    lost_before_utc, opened_at_utc, closed_at_utc)
                VALUES (@source, @from, @to, @filled, @state, @lost, @opened, @closed)
                RETURNING id;
                """;
            Bind(command, gap);
            return (long)command.ExecuteScalar()!;
        });

        return gap with { Id = id };
    }

    /// <remarks>
    /// Guarded in SQL rather than trusted from the caller: a read the runner
    /// abandoned can still be running when the next cycle writes, and its
    /// older view of a gap must not move the fill point back or reopen a gap
    /// that has since closed.
    /// </remarks>
    public void Update(CollectionGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        _database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE collection_gap SET
                    filled_to_utc   = GREATEST(filled_to_utc, @filled),
                    lost_before_utc = CASE
                        WHEN @lost::bigint IS NULL THEN lost_before_utc
                        ELSE GREATEST(COALESCE(lost_before_utc, @lost::bigint), @lost::bigint)
                    END,
                    state           = @state,
                    closed_at_utc   = @closed
                WHERE id = @id AND state = 'open';
                """;
            Bind(command, gap);
            command.Parameters.AddWithValue("id", gap.Id);
            return command.ExecuteNonQuery();
        });
    }

    public IReadOnlyDictionary<CollectionGapState, int> CountsByState()
    {
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state, count(*) FROM collection_gap GROUP BY state;";

            var counts = new Dictionary<CollectionGapState, int>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                counts[ParseState(reader.GetString(0))] = checked((int)reader.GetInt64(1));
            }

            return (IReadOnlyDictionary<CollectionGapState, int>)counts;
        });
    }

    private List<CollectionGap> Select(string sourceInstanceId, bool onlyOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);

        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {Columns} FROM collection_gap
                WHERE source_instance_id = @source {(onlyOpen ? "AND state = 'open'" : string.Empty)}
                ORDER BY gap_from_utc, id;
                """;
            command.Parameters.AddWithValue("source", sourceInstanceId);

            var gaps = new List<CollectionGap>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                gaps.Add(new CollectionGap
                {
                    Id = reader.GetInt64(0),
                    SourceInstanceId = reader.GetString(1),
                    FromUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    ToUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                    FilledToUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    State = ParseState(reader.GetString(5)),
                    LostBeforeUtc = reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                    OpenedAtUtc = PgValues.ReadTime(reader, 7),
                    ClosedAtUtc = PgValues.ReadTimeOrNull(reader, 8),
                });
            }

            return gaps;
        });
    }

    private static void Bind(NpgsqlCommand command, CollectionGap gap)
    {
        command.Parameters.AddWithValue("source", gap.SourceInstanceId);
        command.Parameters.AddWithValue("from", gap.FromUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("to", gap.ToUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("filled", gap.FilledToUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("state", StateName(gap.State));
        command.Parameters.Add(new NpgsqlParameter("lost", NpgsqlDbType.Bigint)
        {
            Value = gap.LostBeforeUtc is { } lost ? lost.ToUnixTimeSeconds() : DBNull.Value,
        });
        PgValues.BindTime(command, "opened", gap.OpenedAtUtc);
        PgValues.BindTime(command, "closed", gap.ClosedAtUtc);
    }

    private static string StateName(CollectionGapState state) => state switch
    {
        CollectionGapState.Open => "open",
        CollectionGapState.Filled => "filled",
        CollectionGapState.Unrecoverable => "unrecoverable",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static CollectionGapState ParseState(string stored) => stored switch
    {
        "open" => CollectionGapState.Open,
        "filled" => CollectionGapState.Filled,
        "unrecoverable" => CollectionGapState.Unrecoverable,
        _ => throw new InvalidOperationException(
            $"'{stored}' is not a known collection gap state. The database was written by a " +
            "different version of the product, or has been edited by hand."),
    };
}
