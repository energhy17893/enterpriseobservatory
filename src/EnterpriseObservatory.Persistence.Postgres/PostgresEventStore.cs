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
public sealed class PostgresEventStore : IEventStore, IEventHistory
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

    /// <inheritdoc />
    /// <remarks>
    /// Offset paging, with the count as a second statement of the same read.
    /// No index serves the text match: it scans the held events (30 days;
    /// Kibar holds 10,825), which was measured acceptable in
    /// docs/measurements/ux-a4-a8-e2-queries.md. Revisit with that file if
    /// the table grows by an order of magnitude.
    /// </remarks>
    public EventPage Recent(int offset, int limit, string? sourceInstanceId = null, string? search = null)
    {
        var bounded = Math.Clamp(limit, 1, EventCollectionPipeline.MaxRecent);
        var skip = Math.Max(offset, 0);
        var pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search.Trim()) + "%";

        string?[] conditions =
        [
            sourceInstanceId is null ? null : "source_instance_id = @source",
            pattern is null
                ? null
                : "(message ILIKE @pattern OR type_id ILIKE @pattern OR vm_name ILIKE @pattern " +
                  "OR host_name ILIKE @pattern OR user_name ILIKE @pattern)",
        ];
        var present = conditions.OfType<string>().ToList();
        var filter = present.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", present);

        void BindFilter(NpgsqlCommand command)
        {
            if (sourceInstanceId is not null)
            {
                command.Bind("source", sourceInstanceId);
            }

            if (pattern is not null)
            {
                command.Bind("pattern", pattern);
            }
        }

        return _database.Read(connection =>
        {
            using var count = PgValues.Command(connection, $"SELECT count(*)::int FROM source_event {filter};");
            BindFilter(count);
            var total = (int)count.ExecuteScalar()!;

            using var command = PgValues.Command(connection, $"""
                SELECT source_instance_id, event_key, created_at_utc, chain_id,
                       event_class, type_id, severity, message, user_name, datacenter_name,
                       compute_resource_ref, compute_resource_name, host_ref, host_name,
                       vm_ref, vm_name, datastore_ref, datastore_name
                FROM source_event
                {filter}
                ORDER BY created_at_utc DESC, event_key DESC
                LIMIT @limit OFFSET @offset;
                """);
            BindFilter(command);
            command.Bind("limit", bounded);
            command.Bind("offset", skip);

            return new EventPage(ReadEvents(command), total);
        });
    }

    /// <summary>Makes typed text literal inside ILIKE: its own %, _ and \ match themselves.</summary>
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Type ids are matched without regard to case, exactly as
    /// <see cref="OfTypes"/> matches them, and for the same reason: vCenter's
    /// catalogue is not consistent about case.
    /// </para>
    /// <para>
    /// At most <see cref="EventCollectionPipeline.MaxMatching"/> rows, newest
    /// first — a window over a busy vCenter must not become an unbounded read.
    /// Served by <c>ix_source_event_source_type_created</c> for the source and
    /// window, with the case-folded type test applied to its rows.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SourceEvent> Find(
        string sourceInstanceId,
        IReadOnlyCollection<string> typeIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentNullException.ThrowIfNull(typeIds);

        if (typeIds.Count == 0 || toUtc < fromUtc)
        {
            return [];
        }

        var folded = Fold(typeIds);

        return _database.Read(connection =>
        {
            using var command = PgValues.Command(connection, """
                SELECT source_instance_id, event_key, created_at_utc, chain_id,
                       event_class, type_id, severity, message, user_name, datacenter_name,
                       compute_resource_ref, compute_resource_name, host_ref, host_name,
                       vm_ref, vm_name, datastore_ref, datastore_name
                FROM source_event
                WHERE source_instance_id = @source
                  AND upper(type_id) = ANY(@types)
                  AND created_at_utc BETWEEN @from AND @to
                ORDER BY created_at_utc DESC, event_key DESC
                LIMIT @limit;
                """);

            command.Bind("source", sourceInstanceId);
            command.Bind("types", folded);
            command.BindTime("from", fromUtc);
            command.BindTime("to", toUtc);
            command.Bind("limit", EventCollectionPipeline.MaxMatching);

            return ReadEvents(command);
        });
    }

    /// <inheritdoc />
    public DateTimeOffset? EarliestHeld(string sourceInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);

        return _database.Read(connection =>
        {
            using var command = PgValues.Command(
                connection, "SELECT min(created_at_utc) FROM source_event WHERE source_instance_id = @source;");
            command.Bind("source", sourceInstanceId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? PgValues.ReadTimeOrNull(reader, 0) : null;
        });
    }

    /// <inheritdoc />
    public EventCursor? Cursor(string sourceInstanceId)
    {
        lock (_gate)
        {
            return _cursors.GetValueOrDefault(sourceInstanceId);
        }
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
        // The folded type and the time bound are what
        // ix_source_event_type_upper_created serves.
        //
        // The cap is spent breadth first. Each (source, type, subject) is
        // ranked newest first, and the cap takes every group's newest before
        // any group's second: a storm of one kind — a flapping uplink — can
        // only crowd out its own older reports, never the one newest event
        // that decides whether some other condition is still open. The result
        // is then put back newest first.
        var folded = Fold(typeIds);

        return _database.Read(connection =>
        {
            using var command = PgValues.Command(connection, """
                SELECT source_instance_id, event_key, created_at_utc, chain_id,
                       event_class, type_id, severity, message, user_name, datacenter_name,
                       compute_resource_ref, compute_resource_name, host_ref, host_name,
                       vm_ref, vm_name, datastore_ref, datastore_name
                FROM (
                    SELECT *,
                           row_number() OVER (
                               PARTITION BY source_instance_id, upper(type_id),
                                            host_ref, vm_ref, compute_resource_ref
                               ORDER BY created_at_utc DESC, event_key DESC) AS rank_in_group
                    FROM source_event
                    WHERE created_at_utc >= @since AND upper(type_id) = ANY(@types)
                    ORDER BY rank_in_group, created_at_utc DESC, event_key DESC
                    LIMIT @limit
                ) AS kept
                ORDER BY created_at_utc DESC, event_key DESC;
                """);

            command.BindTime("since", createdSinceUtc);
            command.Bind("types", folded);
            command.Bind("limit", EventCollectionPipeline.MaxMatching);

            return ReadEvents(command);
        });
    }

    private static string[] Fold(IReadOnlyCollection<string> typeIds) =>
        [.. typeIds.Select(t => t.ToUpperInvariant()).Distinct(StringComparer.Ordinal)];

    private static List<SourceEvent> ReadEvents(NpgsqlCommand command)
    {
        using var reader = command.ExecuteReader();
        var events = new List<SourceEvent>();

        while (reader.Read())
        {
            events.Add(new SourceEvent
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
            });
        }

        return events;
    }

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
