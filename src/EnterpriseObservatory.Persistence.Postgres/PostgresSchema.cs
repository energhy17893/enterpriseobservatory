using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The schema, and the only thing that changes it.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are append-only and never edited once released, for the same
/// reason SQLite's were: an installation that already ran migration 3 will
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
        """
        -- One row per distinct thing measured, so a sample can be three
        -- numbers instead of five strings. A mid-sized estate produces on the
        -- order of ten thousand samples per interval; the width of one row is
        -- the whole design.
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
            return Convert.ToInt32(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
                bump.CommandText = "UPDATE schema_version SET version = $1, applied_at = now();";
                bump.Parameters.Add(new NpgsqlParameter { Value = target });
                bump.ExecuteNonQuery();
            });
        }
    }
}
