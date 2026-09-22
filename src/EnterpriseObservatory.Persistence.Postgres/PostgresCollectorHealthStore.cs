using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Collector health, durable.
/// </summary>
/// <remarks>
/// <para>
/// Keeping this across a restart matters more than its size suggests. The
/// circuit breaker counts consecutive failures and measures its cooldown from
/// the last attempt, so a process that forgets them comes back up and hammers a
/// collector that has been refusing it for a day — and an account that was
/// merely rate-limited gets locked out by the monitoring tool, which is
/// precisely what product principle 5 forbids.
/// </para>
/// <para>
/// Keyed by source and role together: one vCenter is read by two collectors
/// that fail independently. See ADR-0009.
/// </para>
/// </remarks>
public sealed class PostgresCollectorHealthStore : ICollectorHealthStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string InstanceId, CollectorRole Role), CollectorHealth> _health;

    public PostgresCollectorHealthStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _health = _database.Read(Load);
    }

    /// <remarks>
    /// The in-memory copy is kept — unlike the account store, which reads
    /// through every time — because this is consulted by the runner for every
    /// source on every cycle, and because it is written by the same cycle that
    /// reads it. The lock guards the dictionary, not the database.
    /// </remarks>
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

        // Memory first, and the database outside the lock. Both halves were
        // the other way round, and both were wrong in the same place.
        //
        // The runner decides whether to ask a source again from Current, every
        // cycle. While this only updated memory after a write that succeeded,
        // a database outage froze it: the failure count stopped moving and the
        // one-strike rule never saw the rejected password, so with two faults
        // at once the product guessed a credential every thirty seconds — the
        // lockout the breaker exists to prevent, and what this class's own
        // remarks say must not happen. What the breaker needs is what was
        // observed, not what was saved.
        //
        // And the lock guards the dictionary, as Current's remarks already
        // said. Held across the transaction, a slow write on the inventory
        // loop stalled the observation loop and the API behind it for as long
        // as the command timeout.
        //
        // The write still throws. The cycle reports it as state that could
        // not be saved, which is true: a restart during the outage comes back
        // with the older record.
        lock (_gate)
        {
            // Merged rather than replaced: a cycle only reports on the sources
            // it ran, and the two cycles run on different schedules. Replacing
            // would erase the other one's findings.
            foreach (var entry in health)
            {
                _health[(entry.InstanceId, entry.Role)] = entry;
            }
        }

        // Unordered against the other loop's write, and safe to be: the two
        // loops never write the same (instance, role) row.
        _database.Write(connection =>
        {
            using var command = Command(connection, """
                INSERT INTO collector_health (
                    instance_id, role, health, last_success_utc,
                    consecutive_failures, is_backing_off, last_failure_detail,
                    last_attempt_utc, last_failure_kind, skipped_cycles,
                    views_held, views_held_max)
                VALUES (@instance, @role, @health, @success, @failures, @backing, @detail,
                        @attempt, @kind, @skipped, @views, @viewsMax)
                ON CONFLICT (instance_id, role) DO UPDATE SET
                    health = EXCLUDED.health,
                    last_success_utc = EXCLUDED.last_success_utc,
                    consecutive_failures = EXCLUDED.consecutive_failures,
                    is_backing_off = EXCLUDED.is_backing_off,
                    last_failure_detail = EXCLUDED.last_failure_detail,
                    last_attempt_utc = EXCLUDED.last_attempt_utc,
                    last_failure_kind = EXCLUDED.last_failure_kind,
                    skipped_cycles = EXCLUDED.skipped_cycles,
                    views_held = EXCLUDED.views_held,
                    views_held_max = EXCLUDED.views_held_max;
                """);

            foreach (var entry in health)
            {
                command.Parameters.Clear();
                command.Bind("@instance", entry.InstanceId);
                command.Bind("@role", entry.Role.ToString());
                command.Bind("@health", entry.Health.ToString());
                command.BindTime("@success", entry.LastSuccessUtc);
                command.Bind("@failures", entry.ConsecutiveFailures);
                command.Bind("@backing", entry.IsBackingOff);
                command.Bind("@detail", entry.LastFailureDetail);
                command.BindTime("@attempt", entry.LastAttemptUtc);
                command.Bind("@kind", entry.LastFailureKind?.ToString());
                command.Bind("@skipped", entry.SkippedCycles);
                command.Bind("@views", entry.ViewsHeld);
                command.Bind("@viewsMax", entry.ViewsHeldMax);
                command.ExecuteNonQuery();
            }

            // Replaced wholesale per collector, never merged. These
            // describe one attempt, so a problem that has been fixed has to
            // disappear — a list that only grows is a list that stops being
            // read.
            WritePartialFailures(connection, health);
        });
    }

    private static void WritePartialFailures(
        NpgsqlConnection connection, IReadOnlyList<CollectorHealth> health)
    {
        using var clear = Command(connection, """
            DELETE FROM collector_partial_failure
            WHERE instance_id = @instance AND role = @role;
            """);

        using var insert = Command(connection, """
            INSERT INTO collector_partial_failure (instance_id, role, kind, target, detail)
            VALUES (@instance, @role, @kind, @target, @detail)
            ON CONFLICT (instance_id, role, target, detail) DO UPDATE SET kind = EXCLUDED.kind;
            """);

        foreach (var entry in health)
        {
            clear.Parameters.Clear();
            clear.Bind("@instance", entry.InstanceId);
            clear.Bind("@role", entry.Role.ToString());
            clear.ExecuteNonQuery();

            foreach (var failure in entry.PartialFailures)
            {
                insert.Parameters.Clear();
                insert.Bind("@instance", entry.InstanceId);
                insert.Bind("@role", entry.Role.ToString());
                insert.Bind("@kind", failure.Kind.ToString());
                insert.Bind("@target", failure.Target);
                insert.Bind("@detail", failure.Detail);
                insert.ExecuteNonQuery();
            }
        }
    }

    /// <remarks>
    /// <c>views_held</c> and <c>views_held_max</c> are deliberately not
    /// selected here. Both default to zero on <see cref="CollectorHealth"/>,
    /// and <c>views_held_max</c> in particular must: it answers "since this
    /// process started", so hydrating it from what an earlier process left on
    /// disk would understate a regression this process introduces. Both are
    /// written fresh by the first cycle either role runs; a role read only by
    /// the other loop keeps whatever the last write for it happened to be
    /// until that loop runs again, which is the same story
    /// <c>skipped_cycles</c> and every other per-role figure already tell.
    /// </remarks>
    private static Dictionary<(string, CollectorRole), CollectorHealth> Load(
        NpgsqlConnection connection)
    {
        var health = new Dictionary<(string, CollectorRole), CollectorHealth>();

        using (var command = Command(connection, """
            SELECT instance_id, role, health, last_success_utc,
                   consecutive_failures, is_backing_off, last_failure_detail,
                   last_attempt_utc, last_failure_kind, skipped_cycles
            FROM collector_health;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var entry = new CollectorHealth
                {
                    InstanceId = reader.GetString(0),
                    Role = ReadEnum<CollectorRole>(reader, 1),
                    Health = ReadEnum<HealthState>(reader, 2),
                    LastSuccessUtc = ReadTimeOrNull(reader, 3),
                    ConsecutiveFailures = reader.GetInt32(4),
                    IsBackingOff = reader.GetBoolean(5),
                    LastFailureDetail = ReadTextOrNull(reader, 6),
                    LastAttemptUtc = ReadTimeOrNull(reader, 7),

                    // Null reads as "retryable" — the forgiving direction, and
                    // the right one: a classification nobody recorded must not
                    // be invented.
                    LastFailureKind = ReadEnumOrNull<CollectionFailureKind>(reader, 8),
                    SkippedCycles = reader.GetInt32(9),
                };

                health[(entry.InstanceId, entry.Role)] = entry;
            }
        }

        return WithPartialFailures(connection, health);
    }

    private static Dictionary<(string, CollectorRole), CollectorHealth> WithPartialFailures(
        NpgsqlConnection connection,
        Dictionary<(string, CollectorRole), CollectorHealth> health)
    {
        var byCollector = new Dictionary<(string, CollectorRole), List<PartialFailure>>();

        using (var command = Command(connection, """
            SELECT instance_id, role, kind, target, detail
            FROM collector_partial_failure
            ORDER BY target, detail;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), ReadEnum<CollectorRole>(reader, 1));

                if (!byCollector.TryGetValue(key, out var list))
                {
                    byCollector[key] = list = [];
                }

                list.Add(new PartialFailure
                {
                    Kind = ReadEnum<CollectionFailureKind>(reader, 2),
                    Target = reader.GetString(3),
                    Detail = reader.GetString(4),
                });
            }
        }

        foreach (var (key, failures) in byCollector)
        {
            // A row with no matching collector is skipped rather than
            // resurrecting one: the health record is the thing that exists, and
            // these only describe it.
            if (health.TryGetValue(key, out var entry))
            {
                health[key] = entry with { PartialFailures = failures };
            }
        }

        return health;
    }
}
