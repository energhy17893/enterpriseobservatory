using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Npgsql;
using NpgsqlTypes;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The entity graph, durable.
/// </summary>
/// <remarks>
/// <para>
/// Write-through: the database is the record, memory is the working copy. The
/// graph is read on every API request and on every cycle, and re-reading a few
/// thousand rows each time would make persistence a performance decision rather
/// than a durability one.
/// </para>
/// <para>
/// Losing the graph on restart was survivable: collection rebuilds it within
/// one inventory cycle. What was not survivable is the thirty-day tombstone
/// from ADR-0004 — a restart reset every vanished entity's clock, so a
/// decommissioned host would either reappear as new or be forgotten early, and
/// the history a swap or a maintenance window is supposed to survive did not.
/// </para>
/// <para>
/// Written by <c>COPY</c> rather than a statement per row, which the SQLite
/// version had no reason to do and this one does. An estate of five hundred
/// hosts is on the order of fifteen thousand rows rewritten every inventory
/// cycle; in-process that is nothing, and across a connection it is fifteen
/// thousand round trips. Principle 5 is about not burdening the systems being
/// monitored, but a monitoring tool that shows up on its own database's graphs
/// has missed the spirit of it.
/// </para>
/// </remarks>
public sealed class PostgresEntityGraphStore : IEntityGraphStore
{
    private readonly PostgresDatabase _database;
    private volatile EntityGraph _current;

    public PostgresEntityGraphStore(PostgresDatabase database)
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
            // estate that was never observed. Marks and evidence go with their
            // owners by cascade.
            Execute(connection, "DELETE FROM relationship; DELETE FROM entity;");

            CopyEntities(connection, graph);
            CopyMarks(connection, graph);
            CopyRelationships(connection, graph);
            CopyEvidence(connection, graph);
        });

        _current = graph;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Straight into the table: the graph's entities are a dictionary, so a
    /// duplicate id is impossible and a conflict here would be a defect worth
    /// hearing about rather than absorbing.
    /// </summary>
    private static void CopyEntities(NpgsqlConnection connection, EntityGraph graph)
    {
        using var writer = connection.BeginBinaryImport("""
            COPY entity (id, kind, display_name, source_instance_id, health,
                         observation_state, last_seen_utc)
            FROM STDIN (FORMAT BINARY)
            """);

        foreach (var value in graph.Entities.Values)
        {
            writer.StartRow();
            writer.Write(value.Id.Value, NpgsqlDbType.Text);
            writer.Write(value.Kind.ToString(), NpgsqlDbType.Text);
            writer.Write(value.DisplayName, NpgsqlDbType.Text);
            writer.Write(value.SourceInstanceId, NpgsqlDbType.Text);
            writer.Write(value.Health.ToString(), NpgsqlDbType.Text);
            writer.Write(value.ObservationState.ToString(), NpgsqlDbType.Text);
            writer.Write(value.LastSeenUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        }

        writer.Complete();
    }

    /// <summary>
    /// Through a staging table, because two collectors may report the same mark
    /// for the same entity and neither is wrong. COPY cannot express that, so
    /// the conflict rule is applied on the way across.
    /// </summary>
    private static void CopyMarks(NpgsqlConnection connection, EntityGraph graph)
    {
        Execute(connection, """
            CREATE TEMP TABLE incoming_mark (
                entity_id text, kind text, value text, source text
            ) ON COMMIT DROP;
            """);

        using (var writer = connection.BeginBinaryImport(
            "COPY incoming_mark (entity_id, kind, value, source) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var value in graph.Entities.Values)
            {
                foreach (var mark in value.Marks)
                {
                    writer.StartRow();
                    writer.Write(value.Id.Value, NpgsqlDbType.Text);
                    writer.Write(mark.Kind.ToString(), NpgsqlDbType.Text);
                    writer.Write(mark.Value, NpgsqlDbType.Text);
                    writer.Write(mark.Source, NpgsqlDbType.Text);
                }
            }

            writer.Complete();
        }

        Execute(connection, """
            INSERT INTO identity_mark (entity_id, kind, value, source)
            SELECT entity_id, kind, value, source FROM incoming_mark
            ON CONFLICT (entity_id, kind, value, source) DO NOTHING;
            """);
    }

    private static void CopyRelationships(NpgsqlConnection connection, EntityGraph graph)
    {
        Execute(connection, """
            CREATE TEMP TABLE incoming_edge (
                from_id text, to_id text, kind text, observed_at_utc timestamptz
            ) ON COMMIT DROP;
            """);

        using (var writer = connection.BeginBinaryImport(
            "COPY incoming_edge (from_id, to_id, kind, observed_at_utc) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var value in graph.Relationships)
            {
                writer.StartRow();
                writer.Write(value.From.Value, NpgsqlDbType.Text);
                writer.Write(value.To.Value, NpgsqlDbType.Text);
                writer.Write(value.Kind.ToString(), NpgsqlDbType.Text);
                writer.Write(value.ObservedAtUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        // Last observation wins, as the SQLite version's INSERT OR REPLACE did.
        // Two sources reporting the same edge is normal and the later reading
        // is the one worth keeping.
        Execute(connection, """
            INSERT INTO relationship (from_id, to_id, kind, observed_at_utc)
            SELECT from_id, to_id, kind, observed_at_utc FROM incoming_edge
            ON CONFLICT (from_id, to_id, kind) DO UPDATE SET
                observed_at_utc = EXCLUDED.observed_at_utc;
            """);
    }

    private static void CopyEvidence(NpgsqlConnection connection, EntityGraph graph)
    {
        Execute(connection, """
            CREATE TEMP TABLE incoming_evidence (
                from_id text, to_id text, kind text,
                mark_kind text, mark_value text, mark_source text
            ) ON COMMIT DROP;
            """);

        using (var writer = connection.BeginBinaryImport("""
            COPY incoming_evidence (from_id, to_id, kind, mark_kind, mark_value, mark_source)
            FROM STDIN (FORMAT BINARY)
            """))
        {
            foreach (var value in graph.Relationships)
            {
                foreach (var mark in value.Evidence)
                {
                    writer.StartRow();
                    writer.Write(value.From.Value, NpgsqlDbType.Text);
                    writer.Write(value.To.Value, NpgsqlDbType.Text);
                    writer.Write(value.Kind.ToString(), NpgsqlDbType.Text);
                    writer.Write(mark.Kind.ToString(), NpgsqlDbType.Text);
                    writer.Write(mark.Value, NpgsqlDbType.Text);
                    writer.Write(mark.Source, NpgsqlDbType.Text);
                }
            }

            writer.Complete();
        }

        Execute(connection, """
            INSERT INTO relationship_evidence
                (from_id, to_id, kind, mark_kind, mark_value, mark_source)
            SELECT from_id, to_id, kind, mark_kind, mark_value, mark_source
            FROM incoming_evidence
            ON CONFLICT (from_id, to_id, kind, mark_kind, mark_value, mark_source) DO NOTHING;
            """);
    }

    private EntityGraph Load() => _database.Read(connection =>
    {
        var marks = LoadMarks(connection);
        var entities = new Dictionary<EntityId, Entity>();

        using (var command = Command(connection, """
            SELECT id, kind, display_name, source_instance_id, health,
                   observation_state, last_seen_utc
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
                    LastSeenUtc = ReadTime(reader, 6),
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

    private static Dictionary<EntityId, List<IdentityMark>> LoadMarks(NpgsqlConnection connection)
    {
        var marks = new Dictionary<EntityId, List<IdentityMark>>();

        using var command = Command(
            connection, "SELECT entity_id, kind, value, source FROM identity_mark;");
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

    private static List<Relationship> LoadRelationships(NpgsqlConnection connection)
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
                ObservedAtUtc = ReadTime(reader, 3),
                Evidence = evidence.TryGetValue(key, out var marks) ? marks : [],
            });
        }

        return relationships;
    }

    private static Dictionary<(string From, string To, string Kind), List<IdentityMark>> LoadEvidence(
        NpgsqlConnection connection)
    {
        var evidence = new Dictionary<(string, string, string), List<IdentityMark>>();

        using var command = Command(connection, """
            SELECT from_id, to_id, kind, mark_kind, mark_value, mark_source
            FROM relationship_evidence;
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
