using System.Globalization;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// The measurement schema.
/// </summary>
/// <remarks>
/// <para>
/// Shaped by volume in a way the state schema is not. A mid-sized estate
/// produces on the order of ten thousand samples every sampling interval, so
/// the width of one row is the whole design: repeating the entity id and the
/// counter name on every sample would multiply the file by roughly ten and
/// make every range scan read that much more from disk.
/// </para>
/// <para>
/// Hence a series dictionary and an integer key. Timestamps are Unix seconds
/// rather than ISO text for the same reason — eight bytes instead of thirty-odd,
/// and integer comparison for the range scan that every query performs.
/// </para>
/// <para>
/// This is the one place in the product where readability loses to size, and it
/// is a deliberate exception rather than a habit: the state database stores
/// names precisely because it is small enough to afford them.
/// </para>
/// </remarks>
internal static class MetricsSchema
{
    private static readonly string[] Migrations =
    [
        """
        -- One row per distinct thing measured, so a sample can be three
        -- numbers instead of five strings.
        CREATE TABLE series (
            id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            entity_id   TEXT    NOT NULL,
            counter     TEXT    NOT NULL,
            instance    TEXT    NOT NULL,
            unit        TEXT    NOT NULL,
            rollup      TEXT    NOT NULL
        ) STRICT;

        CREATE UNIQUE INDEX ux_series ON series (entity_id, counter, instance);

        -- WITHOUT ROWID with this primary key makes the table itself the index
        -- for the only query anyone runs: one series over a time range. A
        -- secondary index would double the writes to serve the same reads.
        CREATE TABLE sample (
            series_id INTEGER NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            at_utc    INTEGER NOT NULL,
            value     REAL    NOT NULL,
            PRIMARY KEY (series_id, at_utc)
        ) STRICT, WITHOUT ROWID;

        -- Five numbers per bucket, not one. Storing only the average is how a
        -- host pinned at 100% for two minutes inside an hour averages to 3% and
        -- disappears. Sum and count rather than the average itself, so a
        -- coarser bucket can be folded from finer ones exactly.
        CREATE TABLE bucket (
            series_id    INTEGER NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            resolution   TEXT    NOT NULL,
            start_utc    INTEGER NOT NULL,
            min_value    REAL    NOT NULL,
            max_value    REAL    NOT NULL,
            sum_value    REAL    NOT NULL,
            sample_count INTEGER NOT NULL,
            last_value   REAL    NOT NULL,
            PRIMARY KEY (series_id, resolution, start_utc)
        ) STRICT, WITHOUT ROWID;

        -- Retention deletes by age across every series, which the primary key
        -- cannot serve.
        CREATE INDEX ix_bucket_age ON bucket (resolution, start_utc);

        -- How far each resolution has been folded. Written in the same
        -- transaction as the buckets it accounts for, so an interrupted pass
        -- resumes from where it actually got to rather than where it intended.
        CREATE TABLE compaction (
            resolution        TEXT    NOT NULL PRIMARY KEY,
            completed_to_utc  INTEGER NOT NULL
        ) STRICT;
        """,
    ];

    public static int Current => Migrations.Length;

    /// <summary>Brings the database up to <see cref="Current"/>.</summary>
    /// <exception cref="InvalidOperationException">If it is newer than this build.</exception>
    public static void Apply(MetricsDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var version = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

        if (version > Current)
        {
            throw new InvalidOperationException(
                $"The metrics database is at schema version {version} but this build understands " +
                $"{Current}. It was written by a newer version of the product. Upgrade rather than " +
                "downgrade: an older binary writing to a newer schema corrupts it slowly and " +
                "without saying so.");
        }

        for (var next = version; next < Current; next++)
        {
            var sql = Migrations[next];
            var target = next + 1;

            database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"{sql}\nPRAGMA user_version = {target};";
                command.ExecuteNonQuery();
            });
        }
    }
}
