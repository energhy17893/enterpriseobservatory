using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// The entity graph, durable.
/// </summary>
/// <remarks>
/// <para>
/// Write-through: the database is the record, memory is the working copy. The
/// graph is read on every API request and on every cycle, and re-reading a few
/// thousand rows each time would make persistence a performance decision rather
/// than a durability one. There is exactly one writer process and every write
/// goes through here, so the copy cannot drift — and a test proves a fresh
/// instance reads back what was written.
/// </para>
/// <para>
/// Losing the graph on restart was survivable: collection rebuilds it within
/// one inventory cycle. What was not survivable is the thirty-day tombstone
/// from ADR-0004 — a restart reset every vanished entity's clock, so a
/// decommissioned host would either reappear as new or be forgotten early, and
/// the history a swap or a maintenance window is supposed to survive did not.
/// </para>
/// </remarks>
public sealed class SqliteEntityGraphStore : IEntityGraphStore
{
    private readonly ObservatoryDatabase _database;
    private volatile EntityGraph _current;

    public SqliteEntityGraphStore(ObservatoryDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _current = Load();
    }

    public EntityGraph Current => _current;

    public void Replace(EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _database.Write(connection =>
        {
            // Replaced wholesale, in one transaction. The graph is a single
            // decision about what exists; a partial write would describe an
            // estate that was never observed.
            Execute(connection, "DELETE FROM relationship; DELETE FROM entity;");

            WriteEntities(connection, graph);
            WriteRelationships(connection, graph);
        });

        _current = graph;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private static void WriteEntities(SqliteConnection connection, EntityGraph graph)
    {
        using var entity = Command(connection, """
            INSERT INTO entity
                (id, kind, display_name, source_instance_id, health, observation_state, last_seen_utc)
            VALUES ($id, $kind, $name, $source, $health, $state, $seen);
            """);

        using var mark = Command(connection, """
            INSERT OR IGNORE INTO identity_mark (entity_id, kind, value, source)
            VALUES ($entity, $kind, $value, $source);
            """);

        foreach (var value in graph.Entities.Values)
        {
            entity.Parameters.Clear();
            entity.Bind("$id", value.Id.Value);
            entity.Bind("$kind", value.Kind.ToString());
            entity.Bind("$name", value.DisplayName);
            entity.Bind("$source", value.SourceInstanceId);
            entity.Bind("$health", value.Health.ToString());
            entity.Bind("$state", value.ObservationState.ToString());
            entity.Bind("$seen", Timestamp(value.LastSeenUtc));
            entity.ExecuteNonQuery();

            foreach (var evidence in value.Marks)
            {
                mark.Parameters.Clear();
                mark.Bind("$entity", value.Id.Value);
                mark.Bind("$kind", evidence.Kind.ToString());
                mark.Bind("$value", evidence.Value);
                mark.Bind("$source", evidence.Source);
                mark.ExecuteNonQuery();
            }
        }
    }

    private static void WriteRelationships(SqliteConnection connection, EntityGraph graph)
    {
        using var edge = Command(connection, """
            INSERT OR REPLACE INTO relationship (from_id, to_id, kind, observed_at_utc)
            VALUES ($from, $to, $kind, $observed);
            """);

        using var evidence = Command(connection, """
            INSERT OR IGNORE INTO relationship_evidence
                (from_id, to_id, kind, mark_kind, mark_value, mark_source)
            VALUES ($from, $to, $kind, $markKind, $markValue, $markSource);
            """);

        foreach (var value in graph.Relationships)
        {
            edge.Parameters.Clear();
            edge.Bind("$from", value.From.Value);
            edge.Bind("$to", value.To.Value);
            edge.Bind("$kind", value.Kind.ToString());
            edge.Bind("$observed", Timestamp(value.ObservedAtUtc));
            edge.ExecuteNonQuery();

            foreach (var mark in value.Evidence)
            {
                evidence.Parameters.Clear();
                evidence.Bind("$from", value.From.Value);
                evidence.Bind("$to", value.To.Value);
                evidence.Bind("$kind", value.Kind.ToString());
                evidence.Bind("$markKind", mark.Kind.ToString());
                evidence.Bind("$markValue", mark.Value);
                evidence.Bind("$markSource", mark.Source);
                evidence.ExecuteNonQuery();
            }
        }
    }

    private EntityGraph Load() => _database.Read(connection =>
    {
        var marks = LoadMarks(connection);
        var entities = new Dictionary<EntityId, Entity>();

        using (var command = Command(connection, """
            SELECT id, kind, display_name, source_instance_id, health, observation_state, last_seen_utc
            FROM entity;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = new EntityId(reader.GetString(0));

                entities[id] = new Entity
                {
                    Id = id,
                    Kind = ReadEnum<EntityKind>(reader, 1),
                    DisplayName = reader.GetString(2),
                    SourceInstanceId = reader.GetString(3),
                    Health = ReadEnum<HealthState>(reader, 4),
                    ObservationState = ReadEnum<ObservationState>(reader, 5),
                    LastSeenUtc = ReadTimestamp(reader, 6),
                    Marks = marks.TryGetValue(id, out var own) ? own : [],
                };
            }
        }

        return new EntityGraph
        {
            Entities = entities,
            Relationships = LoadRelationships(connection),
        };
    });

    private static Dictionary<EntityId, List<IdentityMark>> LoadMarks(SqliteConnection connection)
    {
        var marks = new Dictionary<EntityId, List<IdentityMark>>();

        using var command = Command(connection, "SELECT entity_id, kind, value, source FROM identity_mark;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = new EntityId(reader.GetString(0));

            if (!marks.TryGetValue(id, out var own))
            {
                own = [];
                marks[id] = own;
            }

            // Constructed directly rather than through Create: the value was
            // normalized when it was first observed, and normalizing again on
            // every read would quietly hide a change to that normalization.
            own.Add(new IdentityMark(
                ReadEnum<IdentityMarkKind>(reader, 1), reader.GetString(2), reader.GetString(3)));
        }

        return marks;
    }

    private static List<Relationship> LoadRelationships(SqliteConnection connection)
    {
        var evidence = LoadEvidence(connection);
        var relationships = new List<Relationship>();

        using var command = Command(
            connection, "SELECT from_id, to_id, kind, observed_at_utc FROM relationship;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));

            relationships.Add(new Relationship
            {
                From = new EntityId(key.Item1),
                To = new EntityId(key.Item2),
                Kind = ReadEnum<RelationshipKind>(reader, 2),
                ObservedAtUtc = ReadTimestamp(reader, 3),
                Evidence = evidence.TryGetValue(key, out var marks) ? marks : [],
            });
        }

        return relationships;
    }

    private static Dictionary<(string From, string To, string Kind), List<IdentityMark>> LoadEvidence(
        SqliteConnection connection)
    {
        var evidence = new Dictionary<(string, string, string), List<IdentityMark>>();

        using var command = Command(connection, """
            SELECT from_id, to_id, kind, mark_kind, mark_value, mark_source FROM relationship_evidence;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));

            if (!evidence.TryGetValue(key, out var marks))
            {
                marks = [];
                evidence[key] = marks;
            }

            marks.Add(new IdentityMark(
                ReadEnum<IdentityMarkKind>(reader, 3), reader.GetString(4), reader.GetString(5)));
        }

        return evidence;
    }
}
