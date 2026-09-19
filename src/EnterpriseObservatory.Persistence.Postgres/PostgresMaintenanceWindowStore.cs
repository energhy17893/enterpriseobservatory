using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Maintenance windows, durable.
/// </summary>
/// <remarks>
/// <para>
/// Read on every collection cycle, so the set is held in memory and written
/// through. There are a handful of windows at any time and thousands of cycles
/// a day; re-reading them each time would be paying for durability on the wrong
/// side.
/// </para>
/// <para>
/// Finished windows are kept. A window is the answer to "why was nobody paged
/// last Tuesday", and that question is asked long after the work is done.
/// </para>
/// </remarks>
public sealed class PostgresMaintenanceWindowStore : IMaintenanceWindowStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private List<MaintenanceWindow> _windows;

    public PostgresMaintenanceWindowStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _windows = _database.Read(Load);
    }

    public IReadOnlyList<MaintenanceWindow> All()
    {
        lock (_gate)
        {
            return [.. _windows];
        }
    }

    public IReadOnlyList<MaintenanceWindow> ActiveAt(DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            return [.. _windows.Where(w => w.IsActiveAt(atUtc))];
        }
    }

    public void Add(MaintenanceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            _database.Write(connection =>
            {
                using (var command = Command(connection, """
                    INSERT INTO maintenance_window
                        (id, title, reason, start_utc, end_utc, declared_by, declared_at_utc)
                    VALUES (@id, @title, @reason, @start, @end, @by, @at)
                    ON CONFLICT (id) DO UPDATE SET
                        title = EXCLUDED.title,
                        reason = EXCLUDED.reason,
                        start_utc = EXCLUDED.start_utc,
                        end_utc = EXCLUDED.end_utc,
                        declared_by = EXCLUDED.declared_by,
                        declared_at_utc = EXCLUDED.declared_at_utc;
                    """))
                {
                    command.Bind("@id", window.Id);
                    command.Bind("@title", window.Title);
                    command.Bind("@reason", window.Reason);
                    command.BindTime("@start", window.StartUtc);
                    command.BindTime("@end", window.EndUtc);
                    command.Bind("@by", window.DeclaredBy);
                    command.BindTime("@at", window.DeclaredAtUtc);
                    command.ExecuteNonQuery();
                }

                using var entity = Command(connection, """
                    INSERT INTO maintenance_window_entity (window_id, entity_id)
                    VALUES (@window, @entity)
                    ON CONFLICT (window_id, entity_id) DO NOTHING;
                    """);

                foreach (var covered in window.Entities)
                {
                    entity.Parameters.Clear();
                    entity.Bind("@window", window.Id);
                    entity.Bind("@entity", covered.Value);
                    entity.ExecuteNonQuery();
                }
            });

            _windows = [.. _windows.Where(w => w.Id != window.Id), window];
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _database.Write(connection =>
            {
                using var command = Command(
                    connection, "DELETE FROM maintenance_window WHERE id = @id;");

                command.Bind("@id", id);

                return command.ExecuteNonQuery() == 1;
            });

            if (removed)
            {
                _windows = [.. _windows.Where(w => w.Id != id)];
            }

            return removed;
        }
    }

    public int Forget(DateTimeOffset olderThanUtc)
    {
        lock (_gate)
        {
            var removed = _database.Write(connection =>
            {
                using var command = Command(
                    connection, "DELETE FROM maintenance_window WHERE end_utc < @cutoff;");

                command.BindTime("@cutoff", olderThanUtc);

                return command.ExecuteNonQuery();
            });

            if (removed > 0)
            {
                _windows = [.. _windows.Where(w => w.EndUtc >= olderThanUtc)];
            }

            return removed;
        }
    }

    private static List<MaintenanceWindow> Load(NpgsqlConnection connection)
    {
        var entities = LoadEntities(connection);
        var windows = new List<MaintenanceWindow>();

        using var command = Command(connection, """
            SELECT id, title, reason, start_utc, end_utc, declared_by, declared_at_utc
            FROM maintenance_window;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetString(0);

            windows.Add(new MaintenanceWindow
            {
                Id = id,
                Title = reader.GetString(1),
                Reason = reader.GetString(2),
                StartUtc = ReadTime(reader, 3),
                EndUtc = ReadTime(reader, 4),
                DeclaredBy = reader.GetString(5),
                DeclaredAtUtc = ReadTime(reader, 6),

                // An empty list means the window covers the whole estate, which
                // is what a datacentre power test actually is. Distinguishing
                // it from "we failed to read the rows" is not possible here,
                // and the schema makes the distinction unnecessary: the rows
                // cascade with the window.
                Entities = entities.TryGetValue(id, out var covered) ? covered : [],
            });
        }

        return windows;
    }

    private static Dictionary<string, List<EntityId>> LoadEntities(NpgsqlConnection connection)
    {
        var entities = new Dictionary<string, List<EntityId>>(StringComparer.Ordinal);

        using var command = Command(
            connection, "SELECT window_id, entity_id FROM maintenance_window_entity;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var window = reader.GetString(0);

            if (!entities.TryGetValue(window, out var covered))
            {
                covered = [];
                entities[window] = covered;
            }

            covered.Add(new EntityId(reader.GetString(1)));
        }

        return entities;
    }
}
