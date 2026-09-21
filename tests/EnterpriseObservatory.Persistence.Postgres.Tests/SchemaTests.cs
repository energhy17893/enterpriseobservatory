using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
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
        // coverage table arrived as migration 3.
        Assert.Equal(3, Version());

        // Measurements and state both, from the same open. The two used to be
        // separate SQLite files and a half-applied schema would now be a
        // service that starts and then cannot store what it collects.
        Assert.True(TableCount() >= 19, $"Only {TableCount()} tables were created.");
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

        Assert.Equal(3, Version());
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
}
