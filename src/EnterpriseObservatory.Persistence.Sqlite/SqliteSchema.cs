namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// The schema, as an ordered list of migrations.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated. The previous product had no version
/// control at all, so "what changed and why" was unanswerable; a schema that
/// appears by reflection at startup would reintroduce exactly that for the part
/// of the system where being wrong is permanent. A migration here is a diff
/// someone reviewed.
/// </para>
/// <para>
/// Migrations are append-only and never edited once released, for the same
/// reason an accepted ADR is never edited: an installation that already ran
/// migration 1 will never run it again, so changing it changes nothing except
/// what a reader believes happened.
/// </para>
/// <para>
/// Enumerations are stored as their names rather than their numbers. A number
/// is smaller and silently means something else the day somebody inserts a
/// value into the middle of an enum; a name is wrong loudly or not at all. The
/// same argument as the API's string enums, with higher stakes because this
/// data outlives the process.
/// </para>
/// <para>
/// Timestamps are ISO-8601 with offset, which sorts correctly as text and
/// carries its own meaning. A number of ticks would need this file to explain
/// it.
/// </para>
/// </remarks>
internal static class SqliteSchema
{
    /// <summary>
    /// Every migration, in order. The index plus one is its version.
    /// </summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE entity (
            id                  TEXT    NOT NULL PRIMARY KEY,
            kind                TEXT    NOT NULL,
            display_name        TEXT    NOT NULL,
            source_instance_id  TEXT    NOT NULL,
            health              TEXT    NOT NULL,
            observation_state   TEXT    NOT NULL,
            last_seen_utc       TEXT    NOT NULL
        ) STRICT;

        CREATE INDEX ix_entity_source ON entity (source_instance_id);
        CREATE INDEX ix_entity_kind ON entity (kind);

        CREATE TABLE identity_mark (
            entity_id   TEXT NOT NULL REFERENCES entity (id) ON DELETE CASCADE,
            kind        TEXT NOT NULL,
            value       TEXT NOT NULL,
            source      TEXT NOT NULL,
            PRIMARY KEY (entity_id, kind, value, source)
        ) STRICT;

        CREATE TABLE relationship (
            from_id         TEXT NOT NULL,
            to_id           TEXT NOT NULL,
            kind            TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL,
            PRIMARY KEY (from_id, to_id, kind)
        ) STRICT;

        CREATE INDEX ix_relationship_to ON relationship (to_id);

        -- Evidence is kept because ADR-0004 models an identity match as a
        -- retractable claim rather than a merge. A claim nobody can audit is
        -- not retractable in practice.
        CREATE TABLE relationship_evidence (
            from_id TEXT NOT NULL,
            to_id   TEXT NOT NULL,
            kind    TEXT NOT NULL,
            mark_kind   TEXT NOT NULL,
            mark_value  TEXT NOT NULL,
            mark_source TEXT NOT NULL,
            PRIMARY KEY (from_id, to_id, kind, mark_kind, mark_value, mark_source),
            FOREIGN KEY (from_id, to_id, kind)
                REFERENCES relationship (from_id, to_id, kind) ON DELETE CASCADE
        ) STRICT;

        CREATE TABLE alert_instance (
            fingerprint             TEXT    NOT NULL PRIMARY KEY,
            scope                   TEXT    NOT NULL,
            severity                TEXT    NOT NULL,
            state                   TEXT    NOT NULL,
            title                   TEXT    NOT NULL,
            description             TEXT    NOT NULL,
            category                TEXT    NOT NULL,
            source                  TEXT    NOT NULL,
            entity_id               TEXT    NULL,
            is_derived              INTEGER NOT NULL,
            consecutive_hits        INTEGER NOT NULL,
            is_confirmed            INTEGER NOT NULL,
            cleared_by_operator     INTEGER NOT NULL,
            pending_notification    TEXT    NOT NULL,
            suppressed_by_window_id TEXT    NULL,
            first_seen_utc          TEXT    NOT NULL,
            last_seen_utc           TEXT    NOT NULL,
            silenced_until_utc      TEXT    NULL
        ) STRICT;

        CREATE INDEX ix_alert_scope ON alert_instance (scope);
        CREATE INDEX ix_alert_entity ON alert_instance (entity_id);

        -- The audit trail. "Why did this fire and who closed it" has to stay
        -- answerable after a restart, or the history is decorative.
        CREATE TABLE alert_transition (
            fingerprint TEXT    NOT NULL REFERENCES alert_instance (fingerprint) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            from_state  TEXT    NOT NULL,
            to_state    TEXT    NOT NULL,
            reason      TEXT    NOT NULL,
            at_utc      TEXT    NOT NULL,
            actor       TEXT    NULL,
            PRIMARY KEY (fingerprint, ordinal)
        ) STRICT;

        -- Survives the instances it describes, which is the whole point: a
        -- problem that appears and vanishes fifty times a day leaves no
        -- instance behind and would otherwise be perfectly invisible.
        CREATE TABLE flap_history (
            fingerprint TEXT NOT NULL PRIMARY KEY,
            scope       TEXT NOT NULL,
            object_name TEXT NOT NULL
        ) STRICT;

        CREATE INDEX ix_flap_scope ON flap_history (scope);

        CREATE TABLE flap_cessation (
            fingerprint   TEXT    NOT NULL REFERENCES flap_history (fingerprint) ON DELETE CASCADE,
            ordinal       INTEGER NOT NULL,
            ceased_at_utc TEXT    NOT NULL,
            PRIMARY KEY (fingerprint, ordinal)
        ) STRICT;

        -- Keyed by source and role together: one vCenter is read by two
        -- collectors that fail independently. See ADR-0009.
        CREATE TABLE collector_health (
            instance_id          TEXT    NOT NULL,
            role                 TEXT    NOT NULL,
            health               TEXT    NOT NULL,
            last_success_utc     TEXT    NULL,
            consecutive_failures INTEGER NOT NULL,
            is_backing_off       INTEGER NOT NULL,
            last_failure_detail  TEXT    NULL,
            PRIMARY KEY (instance_id, role)
        ) STRICT;
        """,

        // --- 2: accounts -------------------------------------------------
        //
        // Added rather than folded into migration 1, because migration 1 has
        // been released and an accepted migration is never edited — an
        // installation that already ran it will never run it again, so changing
        // it changes nothing except what a reader believes happened.
        """
        CREATE TABLE user_account (
            username           TEXT    NOT NULL PRIMARY KEY,
            password_hash      TEXT    NOT NULL,
            role               TEXT    NOT NULL,
            created_utc        TEXT    NOT NULL,
            last_signed_in_utc TEXT    NULL,
            failed_attempts    INTEGER NOT NULL,
            locked_until_utc   TEXT    NULL
        ) STRICT;
        """,

        // --- 3: maintenance windows ---------------------------------------
        """
        CREATE TABLE maintenance_window (
            id              TEXT NOT NULL PRIMARY KEY,
            title           TEXT NOT NULL,
            reason          TEXT NOT NULL,
            start_utc       TEXT NOT NULL,
            end_utc         TEXT NOT NULL,
            declared_by     TEXT NOT NULL,
            declared_at_utc TEXT NOT NULL
        ) STRICT;

        -- Retention sweeps by end time across every window.
        CREATE INDEX ix_window_end ON maintenance_window (end_utc);

        -- No rows means the window covers the whole estate, which is what a
        -- datacentre power test actually is. Deliberately expressible and
        -- deliberately blunt: everything goes quiet, including the failure the
        -- test was meant to reveal.
        CREATE TABLE maintenance_window_entity (
            window_id TEXT NOT NULL REFERENCES maintenance_window (id) ON DELETE CASCADE,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (window_id, entity_id)
        ) STRICT;
        """,

        // --- 4: what the breaker needs to decide with -----------------------
        //
        // The breaker measured its cooldown from the last success, so a source
        // that had been down longer than the cooldown was never held off, and
        // it refused to open at all for a source that had never succeeded. A
        // wrong vCenter password was therefore retried three times a cycle,
        // every cycle, against a directory that locks accounts after five.
        //
        // Deciding otherwise needs two facts that were not being kept: when
        // the source was last asked, and whether the last answer was one that
        // retrying cannot change.
        """
        ALTER TABLE collector_health ADD COLUMN last_attempt_utc TEXT NULL;
        ALTER TABLE collector_health ADD COLUMN last_failure_kind TEXT NULL;
        """,

        // --- 5: connections entered in the product --------------------------
        //
        // Until now a vCenter could only arrive from configuration, which
        // means a shell, which means shell quoting: a password with a '$' in
        // it is silently altered by PowerShell before the product ever sees
        // it, and what comes back is "vCenter rejected the credentials" with
        // nothing to suggest the value was mangled in transit. A form in the
        // product removes the quoting layer entirely.
        //
        // The password column holds ciphertext, never a password. See
        // ADR-0015 for what that does and does not protect — it is not the
        // same claim as "the password is safe", and pretending otherwise is
        // how the previous product was described right up until it leaked.
        """
        CREATE TABLE source_connection (
            instance_id        TEXT    NOT NULL PRIMARY KEY,
            kind               TEXT    NOT NULL,
            base_address       TEXT    NOT NULL,
            username           TEXT    NOT NULL,
            password_protected TEXT    NOT NULL,
            accept_untrusted   INTEGER NOT NULL,
            page_size          INTEGER NOT NULL,
            is_enabled         INTEGER NOT NULL,
            created_utc        TEXT    NOT NULL,
            created_by         TEXT    NOT NULL,
            password_set_utc   TEXT    NULL
        ) STRICT;
        """,

        // --- 6: everything a reachable source could not read -----------------
        //
        // Collector health carried one string for this, holding failures[0].
        // Against a real estate that meant a message about a missing virtual
        // machine counter was the only thing visible, while a second problem —
        // no datastore measurements at all, across forty-one volumes — sat
        // behind it with nothing anywhere to suggest it existed.
        //
        // A table rather than a column, because the operator's question is
        // "what can this collector not read", and the answer is a list whose
        // useful half is the target: naming the counter is what turns a
        // sentence into a decision.
        """
        CREATE TABLE collector_partial_failure (
            instance_id TEXT NOT NULL,
            role        TEXT NOT NULL,
            kind        TEXT NOT NULL,
            target      TEXT NOT NULL,
            detail      TEXT NOT NULL,
            PRIMARY KEY (instance_id, role, target, detail)
        ) STRICT;
        """,
    ];

    /// <summary>The version a database is brought up to.</summary>
    public static int Current => Migrations.Length;

    /// <summary>
    /// Brings the database up to <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// Uses SQLite's own <c>user_version</c> rather than a table of applied
    /// migrations. There is exactly one linear history and one writer, so a
    /// table would record the same integer with more ceremony and one more
    /// thing that can disagree with reality.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// If the database is newer than this build understands. Refusing is the
    /// only safe answer: an older binary writing to a newer schema corrupts it
    /// slowly and silently.
    /// </exception>
    public static void Apply(ObservatoryDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var version = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        if (version > Current)
        {
            throw new InvalidOperationException(
                $"The database is at schema version {version} but this build understands {Current}. " +
                "It was written by a newer version of the product. Upgrade rather than downgrade: " +
                "an older binary writing to a newer schema corrupts it slowly and without saying so.");
        }

        for (var next = version; next < Current; next++)
        {
            var sql = Migrations[next];
            var target = next + 1;

            database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                // The version is set in the same transaction as the migration,
                // so a crash half way leaves the database at the version it is
                // actually at rather than the one we intended.
                command.CommandText = $"{sql}\nPRAGMA user_version = {target};";
                command.ExecuteNonQuery();
            });
        }
    }
}
