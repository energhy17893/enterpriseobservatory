using Microsoft.Data.Sqlite;

namespace EnterpriseObservatory.Persistence.Sqlite.Tests;

/// <summary>
/// The schema itself: applied once, refused when it is from the future, and
/// loud when what it reads back does not make sense.
/// </summary>
public class SchemaTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"eo-schema-{Guid.NewGuid():n}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.GetFiles(
            Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Temp-directory leftovers are not worth failing a run over.
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Opening_an_existing_database_does_not_reapply_the_schema()
    {
        // The obvious failure: migrations that run every time. It would show up
        // as a startup crash on the second run, which is at least honest — but
        // only if something checks.
        using (var first = Open())
        {
            Assert.Equal(1, UserVersion(first));
        }

        using var second = Open();

        Assert.Equal(1, UserVersion(second));
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused()
    {
        // An older binary writing to a newer schema corrupts it slowly and
        // without saying so. Refusing to start is the only answer that leaves
        // the data recoverable.
        using (var database = Open())
        {
            database.Execute("PRAGMA user_version = 999;");
        }

        var error = Assert.Throws<InvalidOperationException>(Open);

        Assert.Contains("999", error.Message, StringComparison.Ordinal);
        Assert.Contains("Upgrade rather than downgrade", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_is_not_a_known_enumeration_name_stops_the_read()
    {
        // Falling back to the default would be a silent lie: an alert whose
        // severity could not be read would become Info and disappear from the
        // inbox. A startup that stops and says what it found is better.
        using var database = Open();

        database.Execute("""
            INSERT INTO alert_instance (
                fingerprint, scope, severity, state, title, description, category, source,
                entity_id, is_derived, consecutive_hits, is_confirmed, cleared_by_operator,
                pending_notification, suppressed_by_window_id, first_seen_utc, last_seen_utc,
                silenced_until_utc)
            VALUES (
                'f', 'inventory', 'Catastrophic', 'Open', 't', 'd', 'c', 's',
                NULL, 0, 1, 1, 0, 'None', NULL,
                '2026-09-19T09:00:00.0000000+00:00', '2026-09-19T09:00:00.0000000+00:00', NULL);
            """);

        var error = Assert.Throws<InvalidOperationException>(() => new SqliteAlertStateStore(database));

        Assert.Contains("Catastrophic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_database_file_is_created_along_with_its_directory()
    {
        // The MSI installs into a directory that may not exist yet, and
        // "directory not found" at first start is a support call for something
        // one line prevents.
        var nested = Path.Combine(
            Path.GetTempPath(), $"eo-nested-{Guid.NewGuid():n}", "data", "observatory.db");

        using (var database = new ObservatoryDatabase(new SqliteStoreOptions { Path = nested }))
        {
            Assert.Equal(1, UserVersion(database));
        }

        Assert.True(File.Exists(nested));

        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(nested)!)!, recursive: true);
    }

    [Fact]
    public void A_failed_write_leaves_nothing_behind()
    {
        // A cycle's reconciliation is one decision. Half of it applied would
        // leave alerts that are neither firing nor resolved — a state no code
        // reads correctly because no code expects it.
        using var database = Open();

        Assert.Throws<SqliteException>(() => database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO collector_health (
                    instance_id, role, health, last_success_utc,
                    consecutive_failures, is_backing_off, last_failure_detail)
                VALUES ('vc-1', 'Inventory', 'Healthy', NULL, 0, 0, NULL);

                INSERT INTO collector_health (
                    instance_id, role, health, last_success_utc,
                    consecutive_failures, is_backing_off, last_failure_detail)
                VALUES ('vc-1', 'Inventory', 'Healthy', NULL, 0, 0, NULL);
                """;
            command.ExecuteNonQuery();
        }));

        Assert.Empty(new SqliteCollectorHealthStore(database).Current);
    }

    private ObservatoryDatabase Open() => new(new SqliteStoreOptions { Path = _path });

    private static int UserVersion(ObservatoryDatabase database) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });
}
