using EnterpriseObservatory.Application.Collection;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Collected events, and where each source's stream was read up to.
/// </summary>
/// <remarks>
/// <para>
/// The cursors are cached in memory, as the coverage and health stores cache
/// theirs: they are read by the cycle before every read and by the screen on
/// every refresh, and there are as many of them as there are vCenters. The
/// events are not — they are a bounded history read a page at a time, and a
/// copy of thirty days of them in memory would be a cost with no reader.
/// </para>
/// <para>
/// Events and the cursor move together, in one transaction. A mark that moved
/// without its events would skip them for ever; events stored without the mark
/// moving are merely read again next time and ignored as duplicates, which is
/// why the insert tolerates a conflict.
/// </para>
/// </remarks>
public sealed class PostgresEventStore : IEventStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, EventCursor> _cursors;

    public PostgresEventStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _cursors = _database.Read(LoadCursors);
    }

    public IReadOnlyList<EventCursor> Cursors
    {
        get
        {
            lock (_gate)
            {
                return [.. _cursors.Values];
            }
        }
    }

    public void Record(
        string sourceInstanceId,
        IReadOnlyList<SourceEvent> events,
        bool complete,
        DateTimeOffset readAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentNullException.ThrowIfNull(events);

        lock (_gate)
        {
            var previous = _cursors.GetValueOrDefault(sourceInstanceId);

            // The newest of this batch by key. The batch only holds events
            // newer than the old mark, from one numbering — including after a
            // vCenter rebuild, when that numbering is a new one — so its
            // highest key is where the next read should start.
            var newest = events.Count == 0 ? null : events.MaxBy(e => e.Key);

            var cursor = new EventCursor
            {
                SourceInstanceId = sourceInstanceId,
                Mark = newest is null
                    ? previous?.Mark
                    : new EventMark { Key = newest.Key, CreatedAtUtc = newest.CreatedAtUtc },
                LastAttemptUtc = readAtUtc,
                LastSuccessUtc = readAtUtc,
                LastFailure = null,
                LastGapUtc = complete ? previous?.LastGapUtc : readAtUtc,
            };

            _database.Write(connection =>
            {
                foreach (var e in events)
                {
                    using var insert = PgValues.Command(connection, """
                        INSERT INTO source_event (
                            source_instance_id, event_key, created_at_utc, chain_id,
                            event_class, type_id, severity, message, user_name, datacenter_name,
                            compute_resource_ref, compute_resource_name, host_ref, host_name,
                            vm_ref, vm_name, datastore_ref, datastore_name)
                        VALUES (
                            @source, @key, @created, @chain,
                            @class, @type, @severity, @message, @user, @datacenter,
                            @crRef, @crName, @hostRef, @hostName,
                            @vmRef, @vmName, @dsRef, @dsName)
                        ON CONFLICT (source_instance_id, event_key, created_at_utc) DO NOTHING;
                        """);

                    insert.Bind("source", sourceInstanceId);
                    insert.Bind("key", e.Key);
                    insert.BindTime("created", e.CreatedAtUtc);
                    insert.Bind("chain", e.ChainId);
                    insert.Bind("class", e.EventClass);
                    insert.Bind("type", e.TypeId);
                    insert.Bind("severity", e.Severity);
                    insert.Bind("message", e.Message);
                    insert.Bind("user", e.UserName);
                    insert.Bind("datacenter", e.DatacenterName);
                    insert.Bind("crRef", e.ComputeResource?.MoRef);
                    insert.Bind("crName", e.ComputeResource?.Name);
                    insert.Bind("hostRef", e.Host?.MoRef);
                    insert.Bind("hostName", e.Host?.Name);
                    insert.Bind("vmRef", e.VirtualMachine?.MoRef);
                    insert.Bind("vmName", e.VirtualMachine?.Name);
                    insert.Bind("dsRef", e.Datastore?.MoRef);
                    insert.Bind("dsName", e.Datastore?.Name);
                    insert.ExecuteNonQuery();
                }

                Upsert(connection, cursor);
            });

            _cursors[sourceInstanceId] = cursor;
        }
    }

    public void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);

        lock (_gate)
        {
            var previous = _cursors.GetValueOrDefault(sourceInstanceId);

            // The mark and the last success are kept exactly as they were:
            // nothing was read, so nothing about the stream's position changed.
            var cursor = (previous ?? new EventCursor { SourceInstanceId = sourceInstanceId }) with
            {
                LastAttemptUtc = attemptedAtUtc,
                LastFailure = detail,
            };

            _database.Write(connection => Upsert(connection, cursor));

            _cursors[sourceInstanceId] = cursor;
        }
    }

    public IReadOnlyList<SourceEvent> Recent(int limit, string? sourceInstanceId = null)
    {
        var bounded = Math.Clamp(limit, 1, EventCollectionPipeline.MaxRecent);

        return _database.Read(connection =>
        {
            using var command = PgValues.Command(connection, $"""
                SELECT source_instance_id, event_key, created_at_utc, chain_id,
                       event_class, type_id, severity, message, user_name, datacenter_name,
                       compute_resource_ref, compute_resource_name, host_ref, host_name,
                       vm_ref, vm_name, datastore_ref, datastore_name
                FROM source_event
                {(sourceInstanceId is null ? string.Empty : "WHERE source_instance_id = @source")}
                ORDER BY created_at_utc DESC, event_key DESC
                LIMIT @limit;
                """);

            if (sourceInstanceId is not null)
            {
                command.Bind("source", sourceInstanceId);
            }

            command.Bind("limit", bounded);

            using var reader = command.ExecuteReader();
            var events = new List<SourceEvent>();

            while (reader.Read())
            {
                events.Add(Row(reader));
            }

            return events;
        });
    }

    public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc)
    {
        ArgumentNullException.ThrowIfNull(typeIds);

        if (typeIds.Count == 0)
        {
            return [];
        }

        // Case-folded on both sides because vCenter's own catalogue is not
        // consistent about case (com.vmware.vc.HA.* beside com.vmware.vc.ha.*).
        // The time bound is what uses ix_source_event_created; the type test
        // then runs over one window's rows, not thirty days of them.
        var folded = typeIds.Select(t => t.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();

        return _database.Read(connection =>
        {
            using var command = PgValues.Command(connection, """
                SELECT source_instance_id, event_key, created_at_utc, chain_id,
                       event_class, type_id, severity, message, user_name, datacenter_name,
                       compute_resource_ref, compute_resource_name, host_ref, host_name,
                       vm_ref, vm_name, datastore_ref, datastore_name
                FROM source_event
                WHERE created_at_utc >= @since AND upper(type_id) = ANY(@types)
                ORDER BY created_at_utc DESC, event_key DESC
                LIMIT @limit;
                """);

            command.BindTime("since", createdSinceUtc);
            command.Bind("types", folded);
            command.Bind("limit", EventCollectionPipeline.MaxMatching);

            using var reader = command.ExecuteReader();
            var events = new List<SourceEvent>();

            while (reader.Read())
            {
                events.Add(Row(reader));
            }

            return events;
        });
    }

    /// <summary>One row, in the column order both reads select.</summary>
    private static SourceEvent Row(NpgsqlDataReader reader) => new()
    {
        SourceInstanceId = reader.GetString(0),
        Key = reader.GetInt64(1),
        CreatedAtUtc = PgValues.ReadTime(reader, 2),
        ChainId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        EventClass = reader.GetString(4),
        TypeId = reader.GetString(5),
        Severity = PgValues.ReadTextOrNull(reader, 6),
        Message = reader.GetString(7),
        UserName = PgValues.ReadTextOrNull(reader, 8),
        DatacenterName = PgValues.ReadTextOrNull(reader, 9),
        ComputeResource = Ref(reader, 10),
        Host = Ref(reader, 12),
        VirtualMachine = Ref(reader, 14),
        Datastore = Ref(reader, 16),
    };

    public int Prune(DateTimeOffset createdBeforeUtc) =>
        _database.Write(connection =>
        {
            using var command = PgValues.Command(
                connection, "DELETE FROM source_event WHERE created_at_utc < @cutoff;");
            command.BindTime("cutoff", createdBeforeUtc);
            return command.ExecuteNonQuery();
        });

    private static EventObjectRef? Ref(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new EventObjectRef
            {
                MoRef = reader.GetString(ordinal),
                Name = PgValues.ReadTextOrNull(reader, ordinal + 1) ?? reader.GetString(ordinal),
            };

    private static void Upsert(NpgsqlConnection connection, EventCursor cursor)
    {
        using var command = PgValues.Command(connection, """
            INSERT INTO event_cursor (
                source_instance_id, mark_key, mark_created_utc,
                last_attempt_utc, last_success_utc, last_failure, last_gap_utc)
            VALUES (@source, @key, @created, @attempt, @success, @failure, @gap)
            ON CONFLICT (source_instance_id) DO UPDATE SET
                mark_key = EXCLUDED.mark_key,
                mark_created_utc = EXCLUDED.mark_created_utc,
                last_attempt_utc = EXCLUDED.last_attempt_utc,
                last_success_utc = EXCLUDED.last_success_utc,
                last_failure = EXCLUDED.last_failure,
                last_gap_utc = EXCLUDED.last_gap_utc;
            """);

        command.Bind("source", cursor.SourceInstanceId);
        command.Bind("key", cursor.Mark?.Key);
        command.BindTime("created", cursor.Mark?.CreatedAtUtc);
        command.BindTime("attempt", cursor.LastAttemptUtc);
        command.BindTime("success", cursor.LastSuccessUtc);
        command.Bind("failure", cursor.LastFailure);
        command.BindTime("gap", cursor.LastGapUtc);
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, EventCursor> LoadCursors(NpgsqlConnection connection)
    {
        using var command = PgValues.Command(connection, """
            SELECT source_instance_id, mark_key, mark_created_utc,
                   last_attempt_utc, last_success_utc, last_failure, last_gap_utc
            FROM event_cursor;
            """);

        using var reader = command.ExecuteReader();
        var cursors = new Dictionary<string, EventCursor>(StringComparer.Ordinal);

        while (reader.Read())
        {
            var source = reader.GetString(0);

            cursors[source] = new EventCursor
            {
                SourceInstanceId = source,
                Mark = reader.IsDBNull(1) || reader.IsDBNull(2)
                    ? null
                    : new EventMark { Key = reader.GetInt64(1), CreatedAtUtc = PgValues.ReadTime(reader, 2) },
                LastAttemptUtc = PgValues.ReadTimeOrNull(reader, 3),
                LastSuccessUtc = PgValues.ReadTimeOrNull(reader, 4),
                LastFailure = PgValues.ReadTextOrNull(reader, 5),
                LastGapUtc = PgValues.ReadTimeOrNull(reader, 6),
            };
        }

        return cursors;
    }
}
