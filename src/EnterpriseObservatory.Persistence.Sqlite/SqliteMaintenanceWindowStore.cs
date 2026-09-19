using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

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
public sealed class SqliteMaintenanceWindowStore : IMaintenanceWindowStore
{
    private readonly ObservatoryDatabase _database;
    private readonly Lock _gate = new();
    private List<MaintenanceWindow> _windows;

    public SqliteMaintenanceWindowStore(ObservatoryDatabase database)
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
                    INSERT OR REPLACE INTO maintenance_window
                        (id, title, reason, start_utc, end_utc, declared_by, declared_at_utc)
                    VALUES ($id, $title, $reason, $start, $end, $by, $at);
                    """))
                {
                    command.Bind("$id", window.Id);
                    command.Bind("$title", window.Title);
                    command.Bind("$reason", window.Reason);
                    command.Bind("$start", Timestamp(window.StartUtc));
                    command.Bind("$end", Timestamp(window.EndUtc));
                    command.Bind("$by", window.DeclaredBy);
                    command.Bind("$at", Timestamp(window.DeclaredAtUtc));
                    command.ExecuteNonQuery();
                }

                using var entity = Command(connection, """
                    INSERT OR IGNORE INTO maintenance_window_entity (window_id, entity_id)
                    VALUES ($window, $entity);
                    """);

                foreach (var covered in window.Entities)
                {
                    entity.Parameters.Clear();
                    entity.Bind("$window", window.Id);
                    entity.Bind("$entity", covered.Value);
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
                    connection, "DELETE FROM maintenance_window WHERE id = $id;");

                command.Bind("$id", id);

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
                    connection, "DELETE FROM maintenance_window WHERE end_utc < $cutoff;");

                command.Bind("$cutoff", Timestamp(olderThanUtc));

                return command.ExecuteNonQuery();
            });

            if (removed > 0)
            {
                _windows = [.. _windows.Where(w => w.EndUtc >= olderThanUtc)];
            }

            return removed;
        }
    }

    private static List<MaintenanceWindow> Load(SqliteConnection connection)
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
                StartUtc = ReadTimestamp(reader, 3),
                EndUtc = ReadTimestamp(reader, 4),
                DeclaredBy = reader.GetString(5),
                DeclaredAtUtc = ReadTimestamp(reader, 6),
                Entities = entities.TryGetValue(id, out var covered) ? covered : [],
            });
        }

        return windows;
    }

    private static Dictionary<string, List<EntityId>> LoadEntities(SqliteConnection connection)
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
