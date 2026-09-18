using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Collector health, durable.
/// </summary>
/// <remarks>
/// <para>
/// Keeping this across a restart matters more than its size suggests. The
/// circuit breaker counts consecutive failures, so a process that forgets them
/// comes back up and hammers a collector that has been refusing it for a day —
/// and an account that was merely rate-limited gets locked out by the
/// monitoring tool, which is precisely what product principle 5 forbids.
/// </para>
/// <para>
/// Keyed by source and role together: one vCenter is read by two collectors
/// that fail independently. See ADR-0009.
/// </para>
/// </remarks>
public sealed class SqliteCollectorHealthStore : ICollectorHealthStore
{
    private readonly ObservatoryDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string InstanceId, CollectorRole Role), CollectorHealth> _health;

    public SqliteCollectorHealthStore(ObservatoryDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _health = _database.Read(Load);
    }

    public IReadOnlyList<CollectorHealth> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _health.Values];
            }
        }
    }

    public void Merge(IReadOnlyList<CollectorHealth> health)
    {
        ArgumentNullException.ThrowIfNull(health);

        if (health.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _database.Write(connection =>
            {
                using var command = Command(connection, """
                    INSERT OR REPLACE INTO collector_health (
                        instance_id, role, health, last_success_utc,
                        consecutive_failures, is_backing_off, last_failure_detail)
                    VALUES ($instance, $role, $health, $success, $failures, $backing, $detail);
                    """);

                foreach (var entry in health)
                {
                    command.Parameters.Clear();
                    command.Bind("$instance", entry.InstanceId);
                    command.Bind("$role", entry.Role.ToString());
                    command.Bind("$health", entry.Health.ToString());
                    command.Bind("$success", TimestampOrNull(entry.LastSuccessUtc));
                    command.Bind("$failures", entry.ConsecutiveFailures);
                    command.Bind("$backing", entry.IsBackingOff ? 1 : 0);
                    command.Bind("$detail", TextOrNull(entry.LastFailureDetail));
                    command.ExecuteNonQuery();
                }
            });

            // Merged rather than replaced: a cycle only reports on the sources
            // it ran, and the two cycles run on different schedules. Replacing
            // would erase the other one's findings.
            foreach (var entry in health)
            {
                _health[(entry.InstanceId, entry.Role)] = entry;
            }
        }
    }

    private static Dictionary<(string, CollectorRole), CollectorHealth> Load(SqliteConnection connection)
    {
        var health = new Dictionary<(string, CollectorRole), CollectorHealth>();

        using var command = Command(connection, """
            SELECT instance_id, role, health, last_success_utc,
                   consecutive_failures, is_backing_off, last_failure_detail
            FROM collector_health;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var entry = new CollectorHealth
            {
                InstanceId = reader.GetString(0),
                Role = ReadEnum<CollectorRole>(reader, 1),
                Health = ReadEnum<HealthState>(reader, 2),
                LastSuccessUtc = ReadTimestampOrNull(reader, 3),
                ConsecutiveFailures = (int)reader.GetInt64(4),
                IsBackingOff = reader.GetInt64(5) != 0,
                LastFailureDetail = ReadTextOrNull(reader, 6),
            };

            health[(entry.InstanceId, entry.Role)] = entry;
        }

        return health;
    }
}
