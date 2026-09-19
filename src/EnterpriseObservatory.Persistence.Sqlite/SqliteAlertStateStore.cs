using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Alert state, durable.
/// </summary>
/// <remarks>
/// <para>
/// This is the reason the persistence layer exists. Losing the entity graph on
/// restart costs one inventory cycle; losing this forgets every
/// acknowledgement, every operator clear and every record of what has already
/// been notified. The product would come back up, decide that thirty
/// long-running problems were new, and page somebody for all of them — after
/// which nobody trusts its notifications again.
/// </para>
/// <para>
/// Write-through, like the graph: the database is the record and the cached
/// copy is what the interface reads on each request.
/// </para>
/// </remarks>
public sealed class SqliteAlertStateStore : IAlertStateStore
{
    private readonly ObservatoryDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<AlertInstance>> _instances;
    private readonly Dictionary<string, List<FlapHistory>> _flaps;

    public SqliteAlertStateStore(ObservatoryDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));

        (_instances, _flaps) = _database.Read(connection =>
            (LoadInstances(connection), LoadFlaps(connection)));
    }

    public IReadOnlyList<AlertInstance> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances.Values.SelectMany(s => s)];
            }
        }
    }

    public IReadOnlyList<AlertInstance> InstancesIn(string scope)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(scope, out var slice) ? [.. slice] : [];
        }
    }

    public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope)
    {
        lock (_gate)
        {
            return _flaps.TryGetValue(scope, out var slice) ? [.. slice] : [];
        }
    }

    public AlertReconciliationResult Reconcile(
        string scope,
        Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile)
    {
        ArgumentNullException.ThrowIfNull(reconcile);

        lock (_gate)
        {
            // Read, decide and store without letting go in between. An operator
            // acknowledging an alert in the gap would otherwise be overwritten
            // by this result — the button would appear to work and the alert
            // would reopen, with nothing to show why.
            var stored = _instances.TryGetValue(scope, out var slice)
                ? (IReadOnlyList<AlertInstance>)[.. slice]
                : [];

            var flaps = _flaps.TryGetValue(scope, out var histories)
                ? (IReadOnlyList<FlapHistory>)[.. histories]
                : [];

            var result = reconcile(stored, flaps);

            Store(scope, result);

            return result;
        }
    }

    public AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            foreach (var (scope, slice) in _instances)
            {
                var index = slice.FindIndex(i => i.Fingerprint == fingerprint);

                if (index < 0)
                {
                    continue;
                }

                // Applied to the instance as stored, never to a copy the caller
                // brought with it.
                var next = change(slice[index]);

                _database.Write(connection =>
                {
                    DeleteInstance(connection, fingerprint);
                    WriteInstance(connection, scope, next);
                });

                slice[index] = next;

                return next;
            }

            return null;
        }
    }

    private void Store(string scope, AlertReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        {
            _database.Write(connection =>
            {
                // One scope replaced wholesale, the others untouched. Wholesale
                // because the reconciler already decided what survives, and
                // applying its retirements by omission is how resolved alerts
                // would accumulate forever; one scope because it decided that
                // only for what it evaluated. See ADR-0009.
                DeleteScope(connection, scope);

                foreach (var instance in result.Instances)
                {
                    WriteInstance(connection, scope, instance);
                }

                foreach (var history in result.FlapHistories)
                {
                    WriteFlap(connection, scope, history);
                }
            });

            _instances[scope] = [.. result.Instances];
            _flaps[scope] = [.. result.FlapHistories];
        }
    }

    public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        if (fingerprints.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_instances.TryGetValue(scope, out var slice))
            {
                return;
            }

            var wanted = fingerprints.ToHashSet();

            // Re-read from the cached slice rather than trusting the dispatched
            // copies: reconciliation may have run again while the dispatcher
            // was working, and writing back a stale instance would undo it.
            var updated = slice
                .Select(i => wanted.Contains(i.Fingerprint) ? AlertLifecycle.MarkNotified(i) : i)
                .ToList();

            _database.Write(connection =>
            {
                using var command = Command(connection, """
                    UPDATE alert_instance SET pending_notification = $none
                    WHERE scope = $scope AND fingerprint = $fingerprint;
                    """);

                foreach (var fingerprint in wanted)
                {
                    command.Parameters.Clear();
                    command.Bind("$none", nameof(AlertNotificationKind.None));
                    command.Bind("$scope", scope);
                    command.Bind("$fingerprint", fingerprint.Value);
                    command.ExecuteNonQuery();
                }
            });

            _instances[scope] = updated;
        }
    }

    // --- writing ----------------------------------------------------------

    private static void DeleteInstance(SqliteConnection connection, AlertFingerprint fingerprint)
    {
        // The transitions go with it by cascade, and are rewritten from the new
        // instance's own history.
        using var command = Command(
            connection, "DELETE FROM alert_instance WHERE fingerprint = $fingerprint;");

        command.Bind("$fingerprint", fingerprint.Value);
        command.ExecuteNonQuery();
    }

    private static void DeleteScope(SqliteConnection connection, string scope)
    {
        // The transitions and cessations go with them by cascade.
        using var command = Command(connection, """
            DELETE FROM alert_instance WHERE scope = $scope;
            DELETE FROM flap_history WHERE scope = $scope;
            """);

        command.Bind("$scope", scope);
        command.ExecuteNonQuery();
    }

    private static void WriteInstance(SqliteConnection connection, string scope, AlertInstance instance)
    {
        using (var command = Command(connection, """
            INSERT INTO alert_instance (
                fingerprint, scope, severity, state, title, description, category, source,
                entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                silenced_until_utc)
            VALUES (
                $fingerprint, $scope, $severity, $state, $title, $description, $category, $source,
                $entity, $derived, $hits, $confirmed, $cleared,
                $pending, $suppressed, $first, $last, $silenced);
            """))
        {
            command.Bind("$fingerprint", instance.Fingerprint.Value);
            // The scope the store was asked to write, not the one on the
            // instance. They are the same in practice; if they ever differ, the
            // caller's intent is the one that decides where it can be found
            // again, and a row nobody can find is a row that is gone.
            command.Bind("$scope", scope);
            command.Bind("$severity", instance.Severity.ToString());
            command.Bind("$state", instance.State.ToString());
            command.Bind("$title", instance.Title);
            command.Bind("$description", instance.Description);
            command.Bind("$category", instance.Category);
            command.Bind("$source", instance.Source);
            command.Bind("$entity", TextOrNull(instance.Entity?.Value));
            command.Bind("$derived", instance.IsDerived ? 1 : 0);
            command.Bind("$hits", instance.ConsecutiveHits);
            command.Bind("$confirmed", instance.IsConfirmed ? 1 : 0);
            command.Bind("$cleared", instance.ClearedByOperator ? 1 : 0);
            command.Bind("$pending", instance.PendingNotification.ToString());
            command.Bind("$suppressed", TextOrNull(instance.SuppressedByWindowId));
            command.Bind("$first", Timestamp(instance.FirstSeenUtc));
            command.Bind("$last", Timestamp(instance.LastSeenUtc));
            command.Bind("$silenced", TimestampOrNull(instance.SilencedUntilUtc));
            command.ExecuteNonQuery();
        }

        using var transition = Command(connection, """
            INSERT INTO alert_transition (fingerprint, ordinal, from_state, to_state, reason, at_utc, actor)
            VALUES ($fingerprint, $ordinal, $from, $to, $reason, $at, $actor);
            """);

        for (var ordinal = 0; ordinal < instance.History.Count; ordinal++)
        {
            var step = instance.History[ordinal];

            transition.Parameters.Clear();
            transition.Bind("$fingerprint", instance.Fingerprint.Value);
            transition.Bind("$ordinal", ordinal);
            transition.Bind("$from", step.From.ToString());
            transition.Bind("$to", step.To.ToString());
            transition.Bind("$reason", step.Reason.ToString());
            transition.Bind("$at", Timestamp(step.AtUtc));
            transition.Bind("$actor", TextOrNull(step.Actor));
            transition.ExecuteNonQuery();
        }
    }

    private static void WriteFlap(SqliteConnection connection, string scope, FlapHistory history)
    {
        using (var command = Command(connection, """
            INSERT INTO flap_history (fingerprint, scope, object_name)
            VALUES ($fingerprint, $scope, $name);
            """))
        {
            command.Bind("$fingerprint", history.Fingerprint.Value);
            command.Bind("$scope", scope);
            command.Bind("$name", history.ObjectName);
            command.ExecuteNonQuery();
        }

        using var cessation = Command(connection, """
            INSERT INTO flap_cessation (fingerprint, ordinal, ceased_at_utc)
            VALUES ($fingerprint, $ordinal, $at);
            """);

        for (var ordinal = 0; ordinal < history.CeasedAtUtc.Count; ordinal++)
        {
            cessation.Parameters.Clear();
            cessation.Bind("$fingerprint", history.Fingerprint.Value);
            cessation.Bind("$ordinal", ordinal);
            cessation.Bind("$at", Timestamp(history.CeasedAtUtc[ordinal]));
            cessation.ExecuteNonQuery();
        }
    }

    // --- reading ----------------------------------------------------------

    private static Dictionary<string, List<AlertInstance>> LoadInstances(SqliteConnection connection)
    {
        var transitions = LoadTransitions(connection);
        var byScope = new Dictionary<string, List<AlertInstance>>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT fingerprint, scope, severity, state, title, description, category, source,
                   entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                   pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                   silenced_until_utc
            FROM alert_instance;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var fingerprint = reader.GetString(0);
            var scope = reader.GetString(1);
            var entity = ReadTextOrNull(reader, 8);

            var instance = new AlertInstance
            {
                Fingerprint = AlertFingerprint.Restore(fingerprint),
                Scope = scope,
                Severity = ReadEnum<AlertSeverity>(reader, 2),
                State = ReadEnum<AlertLifecycleState>(reader, 3),
                Title = reader.GetString(4),
                Description = reader.GetString(5),
                Category = reader.GetString(6),
                Source = reader.GetString(7),
                Entity = entity is null ? null : new EntityId(entity),
                IsDerived = reader.GetInt64(9) != 0,
                ConsecutiveHits = (int)reader.GetInt64(10),
                IsConfirmed = reader.GetInt64(11) != 0,
                ClearedByOperator = reader.GetInt64(12) != 0,
                PendingNotification = ReadEnum<AlertNotificationKind>(reader, 13),
                SuppressedByWindowId = ReadTextOrNull(reader, 14),
                FirstSeenUtc = ReadTimestamp(reader, 15),
                LastSeenUtc = ReadTimestamp(reader, 16),
                SilencedUntilUtc = ReadTimestampOrNull(reader, 17),
                History = transitions.TryGetValue(fingerprint, out var own) ? own : [],
            };

            if (!byScope.TryGetValue(scope, out var slice))
            {
                slice = [];
                byScope[scope] = slice;
            }

            slice.Add(instance);
        }

        return byScope;
    }

    private static Dictionary<string, List<AlertTransition>> LoadTransitions(SqliteConnection connection)
    {
        var transitions = new Dictionary<string, List<AlertTransition>>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT fingerprint, from_state, to_state, reason, at_utc, actor
            FROM alert_transition
            ORDER BY fingerprint, ordinal;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var fingerprint = reader.GetString(0);

            if (!transitions.TryGetValue(fingerprint, out var own))
            {
                own = [];
                transitions[fingerprint] = own;
            }

            own.Add(new AlertTransition
            {
                From = ReadEnum<AlertLifecycleState>(reader, 1),
                To = ReadEnum<AlertLifecycleState>(reader, 2),
                Reason = ReadEnum<AlertTransitionReason>(reader, 3),
                AtUtc = ReadTimestamp(reader, 4),
                Actor = ReadTextOrNull(reader, 5),
            });
        }

        return transitions;
    }

    private static Dictionary<string, List<FlapHistory>> LoadFlaps(SqliteConnection connection)
    {
        var cessations = LoadCessations(connection);
        var byScope = new Dictionary<string, List<FlapHistory>>(StringComparer.Ordinal);

        using var command = Command(connection, "SELECT fingerprint, scope, object_name FROM flap_history;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var fingerprint = reader.GetString(0);
            var scope = reader.GetString(1);

            var history = new FlapHistory
            {
                Fingerprint = AlertFingerprint.Restore(fingerprint),
                Scope = scope,
                ObjectName = reader.GetString(2),
                CeasedAtUtc = cessations.TryGetValue(fingerprint, out var own) ? own : [],
            };

            if (!byScope.TryGetValue(scope, out var slice))
            {
                slice = [];
                byScope[scope] = slice;
            }

            slice.Add(history);
        }

        return byScope;
    }

    private static Dictionary<string, List<DateTimeOffset>> LoadCessations(SqliteConnection connection)
    {
        var cessations = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT fingerprint, ceased_at_utc FROM flap_cessation ORDER BY fingerprint, ordinal;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var fingerprint = reader.GetString(0);

            if (!cessations.TryGetValue(fingerprint, out var own))
            {
                own = [];
                cessations[fingerprint] = own;
            }

            own.Add(ReadTimestamp(reader, 1));
        }

        return cessations;
    }
}
