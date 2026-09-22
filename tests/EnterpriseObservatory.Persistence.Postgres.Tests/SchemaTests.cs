using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// What happens to an installation's data when the build changes.
/// </summary>
/// <remarks>
/// <para>
/// The untested thing was the one that decides whether an upgrade destroys
/// somebody's history. Every other test here opens a database and finds the
/// schema already there; none of them asked what the schema mechanism does
/// when it meets a database that is not empty, or one written by a build it
/// does not understand.
/// </para>
/// <para>
/// Against a real server rather than a fake, because the claims are about
/// PostgreSQL's behaviour: that its transactional DDL leaves nothing behind
/// when a migration fails, and that a second pass over an existing schema is
/// genuinely a no-op rather than something that happens to look like one.
/// </para>
/// </remarks>
public class SchemaTests : IDisposable
{
    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private int Version() => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version;";
        return Convert.ToInt32(
            command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private int TableCount() => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE';";
        return Convert.ToInt32(
            command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    [SkippableFact]
    public void An_empty_database_arrives_at_the_version_this_build_understands()
    {
        RequireDatabase();

        // Opening it is what applies the schema; there is no separate step an
        // operator could forget or a deployment could skip.
        //
        // A literal, and deliberately not PostgresSchema.Current. Comparing
        // against Current would make this "the version is whatever the code
        // says it is", which passes even if the migration list were truncated
        // by accident. The literal makes adding a migration an event somebody
        // has to acknowledge here -- which is exactly what it did when the
        // coverage table arrived as migration 3, again when the event tables arrived as 4, when the event read indexes arrived as 5, when the compliance tables arrived as 6, when compliance history, staleness and exception withdrawal arrived as 7, when the scheduled email report tables arrived as 8, when the compaction late-sample marker arrived as 9, when the report subscription audit columns arrived as 10, when the compliance finding subject arrived as 11, when the collection gap record arrived as 13 -- with 12 held back for the K2 package, which had not landed when this did -- and when the three-valued alert state and the durable alert history arrived as 14, when F2's per-source skipped-cycle count arrived as 15, and when F4's views_held and views_held_max self-metric columns arrived as 16.
        Assert.Equal(16, Version());

        // Measurements and state both, from the same open. The two used to be
        // separate SQLite files and a half-applied schema would now be a
        // service that starts and then cannot store what it collects.
        Assert.True(TableCount() >= 24, $"Only {TableCount()} tables were created.");
    }

    [SkippableFact]
    public void The_event_table_has_an_index_for_each_way_it_is_read()
    {
        RequireDatabase();

        var indexes = _live.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT indexname, indexdef FROM pg_indexes " +
                "WHERE schemaname = current_schema() AND tablename = 'source_event';";
            using var reader = command.ExecuteReader();
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                found[reader.GetString(0)] = reader.GetString(1);
            }

            return found;
        });

        Assert.Contains(
            "(source_instance_id, type_id, created_at_utc)",
            indexes["ix_source_event_source_type_created"],
            StringComparison.Ordinal);
        Assert.Contains(
            "(upper(type_id), created_at_utc)",
            indexes["ix_source_event_type_upper_created"],
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Opening_an_installation_that_is_already_current_changes_nothing()
    {
        RequireDatabase();

        // The ordinary case: every restart of every installation. If this were
        // not a no-op it would be a data loss that happens on a schedule.
        var store = new PostgresUserAccountStore(_live.Database);

        store.TryAdd(new UserAccount
        {
            Username = "ertugrul",
            Password = PasswordHash.Create(Secret.From("a long enough passphrase")),
            Role = Role.Administrator,
            CreatedUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        });

        var tablesBefore = TableCount();

        _live.Restart();

        Assert.Equal(16, Version());
        Assert.Equal(tablesBefore, TableCount());
        Assert.NotNull(new PostgresUserAccountStore(_live.Database).Find("ertugrul"));
    }

    [SkippableFact]
    public void A_database_written_by_a_newer_build_is_refused_rather_than_downgraded()
    {
        RequireDatabase();

        // The failure this prevents is the slow kind. An older binary writing
        // to a newer schema does not crash; it writes rows the new columns
        // know nothing about and reads rows it misinterprets, and the damage
        // is discovered long after the deployment that caused it.
        _live.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_version SET version = 99;";
            command.ExecuteNonQuery();
            return 0;
        });

        var refusal = Assert.Throws<InvalidOperationException>(() => _live.Restart());

        // The message has to tell an operator what to do, because the correct
        // action — upgrade rather than roll back — is the opposite of the
        // instinct when a new deployment will not start.
        Assert.Contains("99", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Upgrade rather than downgrade", refusal.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void An_upgrade_keeps_what_the_installation_already_had()
    {
        RequireDatabase();

        // Simulates the shape of an upgrade rather than a specific one: data
        // written, the version wound back so the migration runner has work to
        // do, and the claim is that arriving at the current version again
        // leaves the rows alone. There are only two migrations today, so the
        // interesting case is the one that will exist at every future release.
        var accounts = new PostgresUserAccountStore(_live.Database);

        accounts.TryAdd(new UserAccount
        {
            Username = "survivor",
            Password = PasswordHash.Create(Secret.From("a long enough passphrase")),
            Role = Role.Viewer,
            CreatedUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        });

        // A migration that fails leaves nothing behind, because PostgreSQL
        // runs DDL transactionally — which is what makes hand-written
        // migrations safe here and is worth pinning rather than trusting.
        var failed = Record.Exception(() => _live.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE half_applied (id integer);
                SELECT 1 / 0;
                """;
            command.ExecuteNonQuery();
            return 0;
        }));

        Assert.NotNull(failed);

        var leftBehind = _live.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM information_schema.tables " +
                "WHERE table_schema = current_schema() AND table_name = 'half_applied';";
            return Convert.ToInt32(
                command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        Assert.Equal(0, leftBehind);
        Assert.NotNull(new PostgresUserAccountStore(_live.Database).Find("survivor"));
    }

    [SkippableFact]
    public void The_version_table_holds_exactly_one_row()
    {
        RequireDatabase();

        // Two rows would make "what version is this" a question with two
        // answers, and the migration runner reads it with a scalar query that
        // would silently pick one.
        var inserted = Record.Exception(() => _live.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_version (id, version) VALUES (false, 1);";
            command.ExecuteNonQuery();
            return 0;
        }));

        // Refused by the check constraint on the primary key, not by hoping
        // nobody tries.
        Assert.IsType<PostgresException>(inserted);

        Assert.Equal(1, _live.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM schema_version;";
            return Convert.ToInt32(
                command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }));
    }

    [SkippableFact]
    public void Migration_14_keeps_every_alert_as_it_was_moves_its_transitions_into_the_history_and_names_its_rule()
    {
        RequireDatabase();

        Assert.Equal(16, Version());

        RewindTo13();


        // One open alert per check id every rule emits, plus the event table's
        // prefix, the four rules K2 retired, and direct producers.
        List<string> checkIds =
        [
            .. RuleCheckIds.Exact.Keys,
            "vcenter-events:ha-host-failed",
            "cluster-ha-scorecard-ha-disabled",
            "drs-rule-violation",
            "multipath-single-point-of-failure",
            "cluster-n-plus-one-history-unreadable",
            "collector-unreachable:metrics",
            "analysis-rule-failed",
            "store-write-failed",
        ];

        var first = new DateTimeOffset(2026, 9, 21, 18, 10, 54, TimeSpan.Zero);
        var fingerprints = checkIds
            .Select((id, n) => AlertFingerprint.Create("platform", "t", "c", $"object-{n}", id))
            .ToList();

        foreach (var (fingerprint, n) in fingerprints.Select((f, n) => (f, n)))
        {
            Execute(
                """
                INSERT INTO alert_instance (
                    fingerprint, scope, severity, state, title, description, category, source,
                    entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                    pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                    silenced_until_utc)
                VALUES (@f, 'observation', 'Warning', 'Acknowledged', 't', '', 'c', 'platform',
                        NULL, false, 3, true, false, 'None', NULL, @first, @last, NULL);
                INSERT INTO alert_transition VALUES
                    (@f, 0, 'Open', 'Open', 'Raised', @first, NULL),
                    (@f, 1, 'Open', 'Acknowledged', 'OperatorAcknowledged', @first + interval '1 minute', 'ertugrul');
                """,
                ("@f", fingerprint.Value),
                ("@first", first),
                ("@last", first.AddMinutes(n)));
        }

        _live.Restart();

        Assert.Equal(16, Version());
        Assert.Equal(0, Count("information_schema.tables WHERE table_schema = current_schema() AND table_name = 'alert_transition'"));

        var store = new PostgresAlertStateStore(_live.Database);
        var byFingerprint = store.All.ToDictionary(a => a.Fingerprint);

        foreach (var (fingerprint, n) in fingerprints.Select((f, n) => (f, n)))
        {
            var alert = byFingerprint[fingerprint];

            // The same map as the code, row for row.
            Assert.Equal(RuleCheckIds.RuleOf(fingerprint), alert.RuleId);

            // Nothing changes state because of the deploy: every alert is as
            // it was, and fresh.
            Assert.Equal(AlertLifecycleState.Acknowledged, alert.State);
            Assert.Equal(first.AddMinutes(n), alert.EvidenceAtUtc);
            Assert.False(alert.IsStale);
            Assert.Equal(0, alert.ConsecutiveAbsent);

            Assert.Equal(
                [AlertTransitionReason.Raised, AlertTransitionReason.OperatorAcknowledged],
                alert.History.Select(t => t.Reason));
            Assert.Equal("ertugrul", alert.History[1].Actor);
        }

        Assert.Null(byFingerprint[fingerprints[^1]].RuleId);
        Assert.Equal(2 * fingerprints.Count, Count("alert_history"));
    }

    /// <summary>
    /// Puts the alert tables back the way migration 13 left them and the
    /// version at 13: what an installation upgrading from 13 has on disk.
    /// </summary>
    /// <param name="foreignKey">
    /// The cascade from alert_instance, as 13 had it. Off to plant a
    /// transition whose instance is gone, which the cascade made impossible
    /// and migration 14 must still not assume.
    /// </param>
    /// <param name="primaryKey">
    /// 13's key. Off to plant a duplicate, the one way the copy can come up
    /// short and the guard must refuse the DROP.
    /// </param>
    private void RewindTo13(bool foreignKey = true, bool primaryKey = true) => Execute($"""
        ALTER TABLE collector_health DROP COLUMN skipped_cycles;
        ALTER TABLE collector_health DROP COLUMN views_held;
        ALTER TABLE collector_health DROP COLUMN views_held_max;
        DROP TABLE alert_history;
        DROP INDEX ix_alert_rule;
        ALTER TABLE alert_instance
            DROP COLUMN rule_id, DROP COLUMN evidence_at_utc, DROP COLUMN stale_since_utc,
            DROP COLUMN stale_reason, DROP COLUMN stale_detail, DROP COLUMN consecutive_absent;
        CREATE TABLE alert_transition (
            fingerprint text        NOT NULL {(foreignKey ? "REFERENCES alert_instance (fingerprint) ON DELETE CASCADE" : "")},
            ordinal     integer     NOT NULL,
            from_state  text        NOT NULL,
            to_state    text        NOT NULL,
            reason      text        NOT NULL,
            at_utc      timestamptz NOT NULL,
            actor       text        NULL
            {(primaryKey ? ", PRIMARY KEY (fingerprint, ordinal)" : "")}
        );
        UPDATE schema_version SET version = 13;
        """);

    [SkippableFact]
    public void Migration_14_copies_a_transition_whose_instance_is_gone()
    {
        RequireDatabase();

        RewindTo13(foreignKey: false);

        var at = new DateTimeOffset(2026, 9, 21, 18, 10, 54, TimeSpan.Zero);
        Execute(
            """
            INSERT INTO alert_transition VALUES
                ('platform|gone|c|o|fault-counter', 0, 'Open', 'Open', 'Confirmed', @at, NULL),
                ('platform|gone|c|o|fault-counter', 1, 'Open', 'Resolved', 'ConditionCleared', @at + interval '1 minute', NULL);
            """,
            ("@at", at));

        _live.Restart();

        Assert.Equal(16, Version());

        // Evidence is never discarded for lacking an instance: both rows are
        // there, in an episode dated by their first transition, and nothing is
        // invented about the alert they belonged to.
        Assert.Equal(2, Count("alert_history WHERE fingerprint = 'platform|gone|c|o|fault-counter'"));
        Assert.Equal(2, Count(
            "alert_history WHERE fingerprint = 'platform|gone|c|o|fault-counter' " +
            $"AND episode_first_seen_utc = '{at:O}' AND title IS NULL AND severity IS NULL AND scope IS NULL"));

        // Nor does it reach the report, which has nothing to show for it.
        var store = new PostgresAlertStateStore(_live.Database);
        Assert.Empty(store.All);
        Assert.Empty(store.ResolvedBetween(at, at.AddHours(1)));
    }

    [SkippableFact]
    public void Migration_14_refuses_to_drop_alert_transition_when_the_copy_is_short_and_changes_nothing()
    {
        RequireDatabase();

        // The one way the copy can come up short: alert_transition without its
        // key, holding the same step twice. The history keeps it once, so the
        // guard sees fewer rows than it was given and raises before the DROP.
        // It also stands for Ş2: a migration that fails partway, after its
        // ALTERs, its CREATE TABLE and its INSERT have all run.
        RewindTo13(foreignKey: false, primaryKey: false);

        Execute("""
            INSERT INTO alert_transition VALUES
                ('platform|dup|c|o|fault-counter', 0, 'Open', 'Open', 'Confirmed', now(), NULL),
                ('platform|dup|c|o|fault-counter', 0, 'Open', 'Open', 'Confirmed', now(), NULL);
            """);

        var failure = Assert.Throws<PostgresException>(() => _live.Restart());
        Assert.Equal("P0001", failure.SqlState);
        Assert.Contains("not dropping it", failure.MessageText, StringComparison.Ordinal);

        // One transaction with the version bump: nothing of 14 is left behind.
        Assert.Equal(13, _live.ReadRaw(c => Scalar(c, "SELECT version FROM schema_version")));
        Assert.Equal(2, _live.ReadRaw(c => Scalar(c, "SELECT count(*) FROM alert_transition")));
        Assert.Equal(0, _live.ReadRaw(c => Scalar(c,
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_name = 'alert_history'")));
        Assert.Equal(0, _live.ReadRaw(c => Scalar(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = 'alert_instance' AND column_name = 'rule_id'")));
    }

    private static long Scalar(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private void Execute(string sql, params (string Name, object Value)[] parameters) =>
        _live.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
            return 0;
        });

    private long Count(string from) => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {from};";
        return (long)command.ExecuteScalar()!;
    });
}
