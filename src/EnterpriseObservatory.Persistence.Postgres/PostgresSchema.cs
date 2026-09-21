using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The schema, and the only thing that changes it.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are append-only and never edited once released, for the same
/// reason SQLite's were: an installation that already ran migration 2 will
/// never run it again, so changing it changes nothing except what a reader
/// believes happened.
/// </para>
/// <para>
/// PostgreSQL has no <c>PRAGMA user_version</c>, so the version lives in a
/// table — written in the same transaction as the migration it accounts for.
/// An interrupted upgrade therefore leaves the version at the last migration
/// that actually completed, rather than at the one that was attempted.
/// </para>
/// </remarks>
internal static class PostgresSchema
{
    private static readonly string[] Migrations =
    [
        // --- 1: measurements -------------------------------------------------
        //
        // Shaped by volume in a way the state tables are not. A mid-sized
        // estate produces on the order of ten thousand samples every sampling
        // interval, so the width of one row is the whole design: repeating the
        // entity id and the counter name on every sample would multiply the
        // table by roughly ten and make every range scan read that much more.
        """
        CREATE TABLE series (
            id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            entity_id  text   NOT NULL,
            counter    text   NOT NULL,
            instance   text   NOT NULL,
            unit       text   NOT NULL,
            rollup     text   NOT NULL
        );

        CREATE UNIQUE INDEX ux_series ON series (entity_id, counter, instance);

        -- Listing what was recorded for one entity is how the interface offers
        -- a real menu instead of a fixed one that is wrong for half the kinds.
        CREATE INDEX ix_series_entity ON series (entity_id);

        -- Timestamps as bigint Unix seconds rather than timestamptz. Eight
        -- bytes, integer comparison for the range scan every query performs,
        -- and no time zone to be misread — the product's clock is UTC
        -- throughout and storing an offset would invite the question of which
        -- one applied.
        CREATE TABLE sample (
            series_id bigint           NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            at_utc    bigint           NOT NULL,
            value     double precision NOT NULL,
            PRIMARY KEY (series_id, at_utc)
        );

        -- Five numbers per bucket, not one. Storing only the average is how a
        -- host pinned at 100% for two minutes inside an hour averages to 3%
        -- and disappears. Sum and count rather than the average itself, so a
        -- coarser bucket folds from finer ones exactly — which is what makes
        -- keeping hourly history for years cost almost nothing.
        CREATE TABLE bucket (
            series_id    bigint           NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            resolution   text             NOT NULL,
            start_utc    bigint           NOT NULL,
            min_value    double precision NOT NULL,
            max_value    double precision NOT NULL,
            sum_value    double precision NOT NULL,
            sample_count integer          NOT NULL,
            last_value   double precision NOT NULL,
            PRIMARY KEY (series_id, resolution, start_utc)
        );

        -- Retention deletes by age across every series, which the primary key
        -- cannot serve: it leads with series_id.
        CREATE INDEX ix_bucket_age ON bucket (resolution, start_utc);

        -- How far each resolution has been folded. Written in the same
        -- transaction as the buckets it accounts for, so an interrupted pass
        -- resumes from where it actually got to rather than where it intended.
        CREATE TABLE compaction (
            resolution       text   NOT NULL PRIMARY KEY,
            completed_to_utc bigint NOT NULL
        );
        """,

        // --- 2: state --------------------------------------------------------
        //
        // The six SQLite migrations that built this are not replayed here. They
        // are that database's history, not this one's, and an installation of
        // this build starts empty — so the tables are created in the shape they
        // ended up in. Append-only applies from here.
        //
        // State and measurements share one database, unlike the two SQLite
        // files ADR-0012 separated. That separation was about file mechanics: a
        // metric history churning one file should not be able to take alerting
        // down with it. With a server there is no shared file to contend for,
        // and one database is one backup, one connection and one operational
        // story (ADR-0016).
        //
        // Timestamps here are timestamptz rather than the bigint the
        // measurement tables use. State is small and read by people — an
        // alert's history is evidence in an incident review — so it is worth
        // the width to have the database itself understand the value. The
        // measurement tables made the opposite trade for the opposite reason.
        """
        CREATE TABLE entity (
            id                 text        NOT NULL PRIMARY KEY,
            kind               text        NOT NULL,
            display_name       text        NOT NULL,
            source_instance_id text        NOT NULL,
            health             text        NOT NULL,
            observation_state  text        NOT NULL,
            last_seen_utc      timestamptz NOT NULL
        );

        CREATE INDEX ix_entity_source ON entity (source_instance_id);
        CREATE INDEX ix_entity_kind ON entity (kind);

        CREATE TABLE identity_mark (
            entity_id text NOT NULL REFERENCES entity (id) ON DELETE CASCADE,
            kind      text NOT NULL,
            value     text NOT NULL,
            source    text NOT NULL,
            PRIMARY KEY (entity_id, kind, value, source)
        );

        -- No foreign key to entity: a relationship may be observed before
        -- either end has been collected, and refusing it would let the order
        -- two independent collectors happen to run in decide what the graph
        -- contains.
        CREATE TABLE relationship (
            from_id         text        NOT NULL,
            to_id           text        NOT NULL,
            kind            text        NOT NULL,
            observed_at_utc timestamptz NOT NULL,
            PRIMARY KEY (from_id, to_id, kind)
        );

        CREATE INDEX ix_relationship_to ON relationship (to_id);

        CREATE TABLE relationship_evidence (
            from_id     text NOT NULL,
            to_id       text NOT NULL,
            kind        text NOT NULL,
            mark_kind   text NOT NULL,
            mark_value  text NOT NULL,
            mark_source text NOT NULL,
            PRIMARY KEY (from_id, to_id, kind, mark_kind, mark_value, mark_source),
            FOREIGN KEY (from_id, to_id, kind)
                REFERENCES relationship (from_id, to_id, kind) ON DELETE CASCADE
        );

        CREATE TABLE alert_instance (
            fingerprint             text        NOT NULL PRIMARY KEY,
            scope                   text        NOT NULL,
            severity                text        NOT NULL,
            state                   text        NOT NULL,
            title                   text        NOT NULL,
            description             text        NOT NULL,
            category                text        NOT NULL,
            source                  text        NOT NULL,
            entity_id               text        NULL,
            is_derived              boolean     NOT NULL,
            consecutive_hits        integer     NOT NULL,
            is_confirmed            boolean     NOT NULL,
            cleared_by_operator     boolean     NOT NULL,
            pending_notification    text        NOT NULL,
            suppressed_by_window_id text        NULL,
            first_seen_utc          timestamptz NOT NULL,
            last_seen_utc           timestamptz NOT NULL,
            silenced_until_utc      timestamptz NULL
        );

        -- A reconciliation reads one scope at a time and treats what it reads
        -- as the whole truth, so this index is on the path of every cycle.
        CREATE INDEX ix_alert_scope ON alert_instance (scope);
        CREATE INDEX ix_alert_entity ON alert_instance (entity_id);

        CREATE TABLE alert_transition (
            fingerprint text        NOT NULL REFERENCES alert_instance (fingerprint) ON DELETE CASCADE,
            ordinal     integer     NOT NULL,
            from_state  text        NOT NULL,
            to_state    text        NOT NULL,
            reason      text        NOT NULL,
            at_utc      timestamptz NOT NULL,
            actor       text        NULL,
            PRIMARY KEY (fingerprint, ordinal)
        );

        CREATE TABLE flap_history (
            fingerprint text NOT NULL PRIMARY KEY,
            scope       text NOT NULL,
            object_name text NOT NULL
        );

        CREATE INDEX ix_flap_scope ON flap_history (scope);

        CREATE TABLE flap_cessation (
            fingerprint   text        NOT NULL REFERENCES flap_history (fingerprint) ON DELETE CASCADE,
            ordinal       integer     NOT NULL,
            ceased_at_utc timestamptz NOT NULL,
            PRIMARY KEY (fingerprint, ordinal)
        );

        -- Keyed by source and role together: one vCenter is read by two
        -- collectors that fail independently. See ADR-0009.
        CREATE TABLE collector_health (
            instance_id          text        NOT NULL,
            role                 text        NOT NULL,
            health               text        NOT NULL,
            last_success_utc     timestamptz NULL,
            consecutive_failures integer     NOT NULL,
            is_backing_off       boolean     NOT NULL,
            last_failure_detail  text        NULL,
            last_attempt_utc     timestamptz NULL,
            last_failure_kind    text        NULL,
            PRIMARY KEY (instance_id, role)
        );

        -- Everything a reachable source could not read. All of them, not the
        -- first: keeping only one hid an entire class of measurement behind an
        -- unrelated message about a different entity kind.
        CREATE TABLE collector_partial_failure (
            instance_id text NOT NULL,
            role        text NOT NULL,
            kind        text NOT NULL,
            target      text NOT NULL,
            detail      text NOT NULL,
            PRIMARY KEY (instance_id, role, target, detail)
        );

        CREATE TABLE user_account (
            username           text        NOT NULL PRIMARY KEY,
            password_hash      text        NOT NULL,
            role               text        NOT NULL,
            created_utc        timestamptz NOT NULL,
            last_signed_in_utc timestamptz NULL,
            failed_attempts    integer     NOT NULL,
            locked_until_utc   timestamptz NULL
        );

        CREATE TABLE maintenance_window (
            id              text        NOT NULL PRIMARY KEY,
            title           text        NOT NULL,
            reason          text        NOT NULL,
            start_utc       timestamptz NOT NULL,
            end_utc         timestamptz NOT NULL,
            declared_by     text        NOT NULL,
            declared_at_utc timestamptz NOT NULL
        );

        -- Retention sweeps by end time across every window.
        CREATE INDEX ix_window_end ON maintenance_window (end_utc);

        -- No rows means the window covers the whole estate, which is what a
        -- datacentre power test actually is. Deliberately expressible and
        -- deliberately blunt: everything goes quiet, including the failure the
        -- test was meant to reveal.
        CREATE TABLE maintenance_window_entity (
            window_id text NOT NULL REFERENCES maintenance_window (id) ON DELETE CASCADE,
            entity_id text NOT NULL,
            PRIMARY KEY (window_id, entity_id)
        );

        -- password_protected holds ciphertext, never a password. See ADR-0015
        -- for what that protects and what it does not: it is a narrower claim
        -- than "the password is encrypted", and the narrower one is the true
        -- one.
        CREATE TABLE source_connection (
            instance_id        text        NOT NULL PRIMARY KEY,
            kind               text        NOT NULL,
            base_address       text        NOT NULL,
            username           text        NOT NULL,
            password_protected text        NOT NULL,
            accept_untrusted   boolean     NOT NULL,
            page_size          integer     NOT NULL,
            is_enabled         boolean     NOT NULL,
            created_utc        timestamptz NOT NULL,
            created_by         text        NOT NULL,
            password_set_utc   timestamptz NULL
        );
        """,

        // --- coverage --------------------------------------------------------
        //
        // What each source managed to read, property by property. State rather
        // than history: one row per source, object type and property, replaced
        // every inventory cycle. A time series of coverage would answer "was
        // this readable last Tuesday", which is a real question and a later
        // one; the question this table exists for is whether a rule's silence
        // right now is a verdict or a gap.
        //
        // measured_at_utc is on every row rather than per source, because a
        // report that cannot say when it was taken invites being read as
        // current after the collector has stopped running.
        """
        CREATE TABLE property_coverage (
            instance_id     text        NOT NULL,
            object_type     text        NOT NULL,
            property        text        NOT NULL,
            asked           integer     NOT NULL,
            answered        integer     NOT NULL,
            measured_at_utc timestamptz NOT NULL,
            PRIMARY KEY (instance_id, object_type, property)
        );
        """,
    ];

    public static int Current => Migrations.Length;

    /// <summary>Brings the database up to <see cref="Current"/>.</summary>
    /// <exception cref="InvalidOperationException">If it is newer than this build.</exception>
    public static void Apply(PostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var version = database.Write(connection =>
        {
            // Created first and separately: every later migration is recorded
            // in it, so it cannot itself be one of them.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    id         boolean PRIMARY KEY DEFAULT true CHECK (id),
                    version    integer NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );

                INSERT INTO schema_version (version) VALUES (0)
                ON CONFLICT (id) DO NOTHING;
                """;
            create.ExecuteNonQuery();

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT version FROM schema_version;";
            return Convert.ToInt32(
                read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        if (version > Current)
        {
            throw new InvalidOperationException(
                $"The database is at schema version {version} but this build understands " +
                $"{Current}. It was written by a newer version of the product. Upgrade rather " +
                "than downgrade: an older binary writing to a newer schema corrupts it slowly " +
                "and without saying so.");
        }

        for (var next = version; next < Current; next++)
        {
            var sql = Migrations[next];
            var target = next + 1;

            database.Write(connection =>
            {
                using var command = connection.CreateCommand();

                // One transaction, one version bump. PostgreSQL runs DDL
                // transactionally — unlike most engines — so a migration that
                // fails halfway leaves nothing behind, which is the property
                // that makes hand-written migrations safe here.
                command.CommandText = sql;
                command.ExecuteNonQuery();

                using var bump = connection.CreateCommand();
                bump.CommandText = "UPDATE schema_version SET version = @version, applied_at = now();";
                bump.Parameters.AddWithValue("version", target);
                bump.ExecuteNonQuery();
            });
        }
    }
}
