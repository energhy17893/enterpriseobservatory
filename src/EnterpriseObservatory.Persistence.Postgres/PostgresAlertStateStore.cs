using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

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
/// <para>
/// The lock stays, and stays for the same reason it did over SQLite — but the
/// reason is now the only one. It is not there because the engine has a single
/// writer; it is there because <see cref="Reconcile"/> has to read, decide and
/// store without letting go in between, and that is a property of the decision
/// rather than of the storage.
/// </para>
/// </remarks>
public sealed class PostgresAlertStateStore : IAlertStateStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<AlertInstance>> _instances;
    private readonly Dictionary<string, List<FlapHistory>> _flaps;

    public PostgresAlertStateStore(PostgresDatabase database)
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

                var previous = slice[index];

                _database.Write(connection =>
                {
                    DeleteInstance(connection, fingerprint);
                    WriteInstance(connection, scope, next, previous);
                });

                slice[index] = next;

                return next;
            }

            return null;
        }
    }

    public IReadOnlyList<AlertInstance> MutateMany(
        IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(change);

        if (fingerprints.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            // What the cache will look like once the database agrees, held
            // aside until it does. See below for why it is not applied here.
            var pending = new List<(List<AlertInstance> Slice, int Index, AlertInstance Next)>(
                fingerprints.Count);

            // One transaction as well as one lock: a bulk acknowledgement that
            // half survived a crash would be worse than one that did not
            // happen, because nothing would say which half.
            _database.Write(connection =>
            {
                // Built afresh, not appended to, so that a callback run twice
                // cannot carry the first attempt's decisions into the second.
                pending.Clear();

                foreach (var fingerprint in fingerprints)
                {
                    foreach (var (scope, slice) in _instances)
                    {
                        var index = slice.FindIndex(i => i.Fingerprint == fingerprint);

                        if (index < 0)
                        {
                            continue;
                        }

                        var next = change(slice[index]);

                        DeleteInstance(connection, fingerprint);
                        WriteInstance(connection, scope, next, slice[index]);

                        pending.Add((slice, index, next));

                        break;
                    }
                }
            });

            // Only now, and for the same reason Mutate writes in this order:
            // the database is the record and this is a copy of it. Assigning
            // inside the transaction makes the copy true before the record is,
            // and a statement that fails — or a connection that drops before
            // the commit — leaves twenty alerts reading as acknowledged on
            // every screen while the database still has them open. The request
            // returns a 500 and the screens disagree with it, until a restart
            // reloads from the database and silently puts them back.
            foreach (var (slice, index, next) in pending)
            {
                slice[index] = next;
            }

            return [.. pending.Select(p => p.Next)];
        }
    }

    private void Store(string scope, AlertReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // What this scope held before, so each instance appends only the
        // transitions the history does not have yet.
        var previous = _instances.TryGetValue(scope, out var slice)
            ? slice.ToDictionary(i => i.Fingerprint)
            : new Dictionary<AlertFingerprint, AlertInstance>();

        _database.Write(connection =>
        {
            // One scope replaced wholesale, the others untouched. Wholesale
            // because the reconciler already decided what survives, and
            // applying its retirements by omission is how resolved alerts would
            // accumulate forever; one scope because it decided that only for
            // what it evaluated. See ADR-0009.
            DeleteScope(connection, scope);

            foreach (var instance in result.Instances)
            {
                WriteInstance(connection, scope, instance, previous.GetValueOrDefault(instance.Fingerprint));
            }

            foreach (var history in result.FlapHistories)
            {
                WriteFlap(connection, scope, history);
            }
        });

        // Filed exactly as the reconciler produced it. This used to re-stamp
        // Scope from the argument, because the reconciler returned instances
        // carrying whatever the definition happened to say and the cache had to
        // match what LoadInstances would read back out of the column. The
        // reconciler now stamps its own result from the scope it was given, so
        // re-stamping here would be a second implementation of a rule with one
        // implementation — and a second implementation is exactly what let the
        // live copy and the durable one disagree in the first place. A store
        // that silently corrects the layer above it also hides the day that
        // layer stops being correct.
        _instances[scope] = [.. result.Instances];
        _flaps[scope] = [.. result.FlapHistories];
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
                    UPDATE alert_instance SET pending_notification = @none
                    WHERE scope = @scope AND fingerprint = @fingerprint;
                    """);

                foreach (var fingerprint in wanted)
                {
                    command.Parameters.Clear();
                    command.Bind("@none", nameof(AlertNotificationKind.None));
                    command.Bind("@scope", scope);
                    command.Bind("@fingerprint", fingerprint.Value);
                    command.ExecuteNonQuery();
                }
            });

            _instances[scope] = updated;
        }
    }

    // --- writing ----------------------------------------------------------

    private static void DeleteInstance(NpgsqlConnection connection, AlertFingerprint fingerprint)
    {
        // The instance row only. Its history is in alert_history, which no
        // delete of an instance touches (migration 14).
        using var command = Command(
            connection, "DELETE FROM alert_instance WHERE fingerprint = @fingerprint;");

        command.Bind("@fingerprint", fingerprint.Value);
        command.ExecuteNonQuery();
    }

    private static void DeleteScope(NpgsqlConnection connection, string scope)
    {
        // The cessations go with them by cascade. The alert history does not:
        // it is appended to, never rewritten, and outlives the instances.
        using var command = Command(connection, """
            DELETE FROM alert_instance WHERE scope = @scope;
            DELETE FROM flap_history WHERE scope = @scope;
            """);

        command.Bind("@scope", scope);
        command.ExecuteNonQuery();
    }

    /// <param name="previous">
    /// The same instance as last written, or null. Its transitions are already
    /// in the history, so only the ones after them are appended.
    /// </param>
    private static void WriteInstance(
        NpgsqlConnection connection, string scope, AlertInstance instance, AlertInstance? previous)
    {
        using (var command = Command(connection, """
            INSERT INTO alert_instance (
                fingerprint, scope, severity, state, title, description, category, source,
                entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                silenced_until_utc, rule_id, evidence_at_utc, stale_since_utc, stale_reason,
                stale_detail, consecutive_absent)
            VALUES (
                @fingerprint, @scope, @severity, @state, @title, @description, @category, @source,
                @entity, @derived, @hits, @confirmed, @cleared,
                @pending, @suppressed, @first, @last, @silenced, @rule, @evidence, @staleSince,
                @staleReason, @staleDetail, @absent);
            """))
        {
            command.Bind("@fingerprint", instance.Fingerprint.Value);

            // The scope the store was asked to write, not the one on the
            // instance. They are the same in practice; if they ever differ, the
            // caller's intent is the one that decides where it can be found
            // again, and a row nobody can find is a row that is gone.
            command.Bind("@scope", scope);
            command.Bind("@severity", instance.Severity.ToString());
            command.Bind("@state", instance.State.ToString());
            command.Bind("@title", instance.Title);
            command.Bind("@description", instance.Description);
            command.Bind("@category", instance.Category);
            command.Bind("@source", instance.Source);
            command.Bind("@entity", instance.Entity?.Value);
            command.Bind("@derived", instance.IsDerived);
            command.Bind("@hits", instance.ConsecutiveHits);
            command.Bind("@confirmed", instance.IsConfirmed);
            command.Bind("@cleared", instance.ClearedByOperator);
            command.Bind("@pending", instance.PendingNotification.ToString());
            command.Bind("@suppressed", instance.SuppressedByWindowId);
            command.BindTime("@first", instance.FirstSeenUtc);
            command.BindTime("@last", instance.LastSeenUtc);
            command.BindTime("@silenced", instance.SilencedUntilUtc);
            command.Bind("@rule", instance.RuleId);
            command.BindTime("@evidence", instance.EvidenceAtUtc);
            command.BindTime("@staleSince", instance.StaleSinceUtc);
            command.Bind("@staleReason", instance.StaleReason?.ToString());
            command.Bind("@staleDetail", instance.StaleDetail);
            command.Bind("@absent", instance.ConsecutiveAbsent);
            command.ExecuteNonQuery();
        }

        AppendHistory(connection, scope, instance, previous);
    }

    /// <summary>
    /// Appends the transitions this instance has that the history does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended, never rewritten: the history is the one record that must
    /// outlive the instance, so nothing that deletes or replaces an instance
    /// row may touch it (ADR-0026, migration 14).
    /// </para>
    /// <para>
    /// Keyed by the episode — the instance's first-seen time — as well as the
    /// fingerprint and the position, because a fingerprint retired and later
    /// raised again is a second life with a history of its own. The conflict
    /// clause makes a repeated write of the same step a no-op rather than a
    /// duplicate, which is what a write retried after a dropped connection is.
    /// </para>
    /// </remarks>
    private static void AppendHistory(
        NpgsqlConnection connection, string scope, AlertInstance instance, AlertInstance? previous)
    {
        // An unconfirmed alert was never shown to anyone, so it leaves no
        // durable history: its opening row is written when it is confirmed,
        // and one forgotten before that writes nothing. A pending alert that
        // flapped away and back was a new "Raised" row every time (post-#83
        // measurement: 307 in 43 minutes for one rule's fingerprints).
        if (!instance.IsConfirmed)
        {
            return;
        }

        var from = previous is { IsConfirmed: true } && previous.FirstSeenUtc == instance.FirstSeenUtc
            ? Math.Min(previous.History.Count, instance.History.Count)
            : 0;

        if (from >= instance.History.Count)
        {
            return;
        }

        using var command = Command(connection, """
            INSERT INTO alert_history (
                fingerprint, episode_first_seen_utc, ordinal, from_state, to_state, reason, at_utc,
                actor, detail, rule_id, evidence_at_utc, scope, severity, title, category, source,
                entity_id, is_derived, last_seen_utc)
            VALUES (
                @fingerprint, @episode, @ordinal, @from, @to, @reason, @at,
                @actor, @detail, @rule, @evidence, @scope, @severity, @title, @category, @source,
                @entity, @derived, @last)
            ON CONFLICT (fingerprint, episode_first_seen_utc, ordinal) DO NOTHING;
            """);

        for (var ordinal = from; ordinal < instance.History.Count; ordinal++)
        {
            var step = instance.History[ordinal];

            command.Parameters.Clear();
            command.Bind("@fingerprint", instance.Fingerprint.Value);
            command.BindTime("@episode", instance.FirstSeenUtc);
            command.Bind("@ordinal", ordinal);
            command.Bind("@from", step.From.ToString());
            command.Bind("@to", step.To.ToString());
            command.Bind("@reason", step.Reason.ToString());
            command.BindTime("@at", step.AtUtc);
            command.Bind("@actor", step.Actor);
            command.Bind("@detail", step.Detail);
            command.Bind("@rule", instance.RuleId);
            command.BindTime("@evidence", step.EvidenceAtUtc);
            command.Bind("@scope", scope);
            command.Bind("@severity", instance.Severity.ToString());
            command.Bind("@title", instance.Title);
            command.Bind("@category", instance.Category);
            command.Bind("@source", instance.Source);
            command.Bind("@entity", instance.Entity?.Value);
            command.Bind("@derived", instance.IsDerived);
            command.BindTime("@last", instance.LastSeenUtc);
            command.ExecuteNonQuery();
        }
    }

    private static void WriteFlap(NpgsqlConnection connection, string scope, FlapHistory history)
    {
        using (var command = Command(connection, """
            INSERT INTO flap_history (fingerprint, scope, object_name)
            VALUES (@fingerprint, @scope, @name);
            """))
        {
            command.Bind("@fingerprint", history.Fingerprint.Value);
            command.Bind("@scope", scope);
            command.Bind("@name", history.ObjectName);
            command.ExecuteNonQuery();
        }

        using var cessation = Command(connection, """
            INSERT INTO flap_cessation (fingerprint, ordinal, ceased_at_utc)
            VALUES (@fingerprint, @ordinal, @at);
            """);

        for (var ordinal = 0; ordinal < history.CeasedAtUtc.Count; ordinal++)
        {
            cessation.Parameters.Clear();
            cessation.Bind("@fingerprint", history.Fingerprint.Value);
            cessation.Bind("@ordinal", ordinal);
            cessation.BindTime("@at", history.CeasedAtUtc[ordinal]);
            cessation.ExecuteNonQuery();
        }
    }

    // --- reading ----------------------------------------------------------

    public IReadOnlyList<AlertInstance> ResolvedBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        // From the history, not the cache: a resolved alert retires on the
        // next cycle it is absent, and the report must still be able to say
        // it was there (ADR-0026, migration 14).
        return _database.Read(connection =>
        {
            using var command = Command(connection, """
                WITH episode AS (
                    SELECT DISTINCT fingerprint, episode_first_seen_utc
                    FROM alert_history
                    WHERE to_state = 'Resolved' AND from_state <> 'Resolved'
                      AND at_utc >= @from AND at_utc <= @to
                      -- A transition migration 14 copied without its instance
                      -- is evidence in the table, not a report row: nothing
                      -- says what the alert was called or how severe it was.
                      AND title IS NOT NULL
                )
                SELECT h.fingerprint, h.episode_first_seen_utc, h.from_state, h.to_state, h.reason,
                       h.at_utc, h.actor, h.detail, h.evidence_at_utc, h.rule_id, h.scope,
                       h.severity, h.title, h.category, h.source, h.entity_id, h.is_derived,
                       h.last_seen_utc
                FROM alert_history h
                JOIN episode e
                  ON e.fingerprint = h.fingerprint AND e.episode_first_seen_utc = h.episode_first_seen_utc
                ORDER BY h.fingerprint, h.episode_first_seen_utc, h.ordinal;
                """);
            command.BindTime("@from", fromUtc);
            command.BindTime("@to", toUtc);

            using var reader = command.ExecuteReader();
            var episodes = new List<AlertInstance>();
            AlertInstance? current = null;
            var steps = new List<AlertTransition>();

            void Close()
            {
                // Still resolved at the end of its history: one that came back
                // is open, and is reported as open, from the store.
                if (current is not null && steps[^1].To == AlertLifecycleState.Resolved)
                {
                    episodes.Add(current with { History = [.. steps] });
                }
            }

            while (reader.Read())
            {
                var fingerprint = AlertFingerprint.Restore(reader.GetString(0));
                var episode = ReadTime(reader, 1);

                if (current is null || current.Fingerprint != fingerprint || current.FirstSeenUtc != episode)
                {
                    Close();
                    steps = [];
                }

                steps.Add(ReadTransition(reader, 2));

                var entity = ReadTextOrNull(reader, 15);

                // The newest row's view of the alert: what it was when it ended.
                current = new AlertInstance
                {
                    Fingerprint = fingerprint,
                    FirstSeenUtc = episode,
                    State = ReadEnum<AlertLifecycleState>(reader, 3),
                    RuleId = ReadTextOrNull(reader, 9),
                    Scope = reader.GetString(10),
                    Severity = ReadEnum<AlertSeverity>(reader, 11),
                    Title = reader.GetString(12),
                    Category = reader.GetString(13),
                    Source = reader.GetString(14),
                    Entity = entity is null ? null : new EntityId(entity),
                    IsDerived = reader.GetBoolean(16),
                    LastSeenUtc = ReadTime(reader, 17),
                    ConsecutiveHits = 0,
                    IsConfirmed = true,
                    ClearedByOperator = steps.Any(s => s.Reason == AlertTransitionReason.OperatorCleared),
                    PendingNotification = AlertNotificationKind.None,
                };
            }

            Close();

            return episodes;
        });
    }

    public int PruneHistory(DateTimeOffset olderThanUtc)
    {
        lock (_gate)
        {
            // Only episodes that have ended: a live alert keeps every step of
            // its history however old, because the instance is loaded from it.
            return _database.Write(connection =>
            {
                using var command = Command(connection, """
                    DELETE FROM alert_history h
                    WHERE h.at_utc < @before
                      AND NOT EXISTS (
                          SELECT 1 FROM alert_instance a
                          WHERE a.fingerprint = h.fingerprint
                            AND a.first_seen_utc = h.episode_first_seen_utc);
                    """);
                command.BindTime("@before", olderThanUtc);

                return command.ExecuteNonQuery();
            });
        }
    }

    private static Dictionary<string, List<AlertInstance>> LoadInstances(NpgsqlConnection connection)
    {
        var transitions = LoadTransitions(connection);
        var byScope = new Dictionary<string, List<AlertInstance>>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT fingerprint, scope, severity, state, title, description, category, source,
                   entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                   pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                   silenced_until_utc, rule_id, evidence_at_utc, stale_since_utc, stale_reason,
                   stale_detail, consecutive_absent
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
                IsDerived = reader.GetBoolean(9),
                ConsecutiveHits = reader.GetInt32(10),
                IsConfirmed = reader.GetBoolean(11),
                ClearedByOperator = reader.GetBoolean(12),
                PendingNotification = ReadEnum<AlertNotificationKind>(reader, 13),
                SuppressedByWindowId = ReadTextOrNull(reader, 14),
                FirstSeenUtc = ReadTime(reader, 15),
                LastSeenUtc = ReadTime(reader, 16),
                SilencedUntilUtc = ReadTimeOrNull(reader, 17),
                RuleId = ReadTextOrNull(reader, 18),
                EvidenceAtUtc = ReadTime(reader, 19),
                StaleSinceUtc = ReadTimeOrNull(reader, 20),
                StaleReason = ReadEnumOrNull<UnknownReason>(reader, 21),
                StaleDetail = ReadTextOrNull(reader, 22),
                ConsecutiveAbsent = reader.GetInt32(23),
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

    private static Dictionary<string, List<AlertTransition>> LoadTransitions(
        NpgsqlConnection connection)
    {
        var transitions = new Dictionary<string, List<AlertTransition>>(StringComparer.Ordinal);

        // The live episode of each stored instance, and only that one: earlier
        // lives of the same fingerprint are history, not this alert's.
        using var command = Command(connection, """
            SELECT h.fingerprint, h.from_state, h.to_state, h.reason, h.at_utc, h.actor, h.detail,
                   h.evidence_at_utc
            FROM alert_history h
            JOIN alert_instance a
              ON a.fingerprint = h.fingerprint AND a.first_seen_utc = h.episode_first_seen_utc
            ORDER BY h.fingerprint, h.ordinal;
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

            own.Add(ReadTransition(reader, 1));
        }

        return transitions;
    }

    /// <summary>One history row's transition, from <paramref name="first"/> on: from, to, reason, at, actor, detail, evidence.</summary>
    private static AlertTransition ReadTransition(NpgsqlDataReader reader, int first) => new()
    {
        From = ReadEnum<AlertLifecycleState>(reader, first),
        To = ReadEnum<AlertLifecycleState>(reader, first + 1),
        Reason = ReadEnum<AlertTransitionReason>(reader, first + 2),
        AtUtc = ReadTime(reader, first + 3),
        Actor = ReadTextOrNull(reader, first + 4),
        Detail = ReadTextOrNull(reader, first + 5),
        EvidenceAtUtc = ReadTimeOrNull(reader, first + 6),
    };

    private static Dictionary<string, List<FlapHistory>> LoadFlaps(NpgsqlConnection connection)
    {
        var cessations = LoadCessations(connection);
        var byScope = new Dictionary<string, List<FlapHistory>>(StringComparer.Ordinal);

        using var command = Command(
            connection, "SELECT fingerprint, scope, object_name FROM flap_history;");
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

    private static Dictionary<string, List<DateTimeOffset>> LoadCessations(
        NpgsqlConnection connection)
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

            own.Add(ReadTime(reader, 1));
        }

        return cessations;
    }
}
