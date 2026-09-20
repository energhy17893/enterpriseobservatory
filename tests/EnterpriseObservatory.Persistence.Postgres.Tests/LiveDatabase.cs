using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Postgres;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// A real PostgreSQL, in a schema of its own, dropped afterwards.
/// </summary>
/// <remarks>
/// <para>
/// A real server rather than a fake, because the things worth testing here are
/// the things only a server decides: whether <c>COPY</c> accepts the binary
/// types, whether the conflict clause matches the index, whether a window
/// function inside an <c>INSERT ... SELECT</c> parses. A fake that answered
/// those would be answering for itself.
/// </para>
/// <para>
/// That lesson is recent and expensive. This product spent a day on four
/// consecutive "reasonable" fixes to vSphere collection, each green in tests
/// and each wrong against the live server, because the failure mode there is
/// silence rather than an error. A storage adapter has the same property: a
/// query that returns nothing looks exactly like a series with no data.
/// </para>
/// <para>
/// Configured from the environment so no credential reaches the repository:
/// </para>
/// <code>
/// $env:EO_TEST_PG_HOST     = "127.0.0.1"   # optional, defaults to loopback
/// $env:EO_TEST_PG_PORT     = "5432"        # optional
/// $env:EO_TEST_PG_DATABASE = "observatory" # optional
/// $env:EO_TEST_PG_USER     = "observatory" # optional
/// $env:EO_TEST_PG_PASSWORD = "..."         # required, or the tests skip
/// </code>
/// <para>
/// The port is configurable because a developer machine may already have a
/// server on 5432 that these tests must not touch. Without it the only way to
/// run the suite locally is against whatever happens to own the default port.
/// </para>
/// <para>
/// Absent configuration skips rather than fails. A machine without a database
/// should be able to run the rest of the suite, and a skipped test that says
/// why is more honest than a green one that tested nothing.
/// </para>
/// </remarks>
public sealed class LiveDatabase : IDisposable
{
    private readonly string _schema = "eo_test_" + Guid.NewGuid().ToString("n")[..12];
    private PostgresDatabase? _database;

    /// <summary>Why the tests are skipped, or null when they can run.</summary>
    public static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EO_TEST_PG_PASSWORD"))
            ? "Set EO_TEST_PG_PASSWORD to run the PostgreSQL integration tests."
            : null;

    public static PostgresOptions Options(string schema) => new()
    {
        Host = Environment.GetEnvironmentVariable("EO_TEST_PG_HOST") ?? "127.0.0.1",
        Port = int.TryParse(
            Environment.GetEnvironmentVariable("EO_TEST_PG_PORT"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port) ? port : 5432,
        Database = Environment.GetEnvironmentVariable("EO_TEST_PG_DATABASE") ?? "observatory",
        Username = Environment.GetEnvironmentVariable("EO_TEST_PG_USER") ?? "observatory",
        Password = Secret.From(Environment.GetEnvironmentVariable("EO_TEST_PG_PASSWORD")),
        Schema = schema,
    };

    /// <summary>The database, created on first use.</summary>
    public PostgresDatabase Database => _database ??= new PostgresDatabase(Options(_schema));

    /// <summary>Re-opens it, as a service restart would.</summary>
    public void Restart()
    {
        _database?.Dispose();
        _database = new PostgresDatabase(Options(_schema));
    }

    public void Dispose()
    {
        _database?.Dispose();
        _database = null;

        if (SkipReason is not null)
        {
            return;
        }

        // Dropped through a connection of its own, because the pooled ones are
        // pinned to a search path that is about to stop existing.
        var cleanup = Options("public");

        using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder
            {
                Host = cleanup.Host,
                Port = cleanup.Port,
                Database = cleanup.Database,
                Username = cleanup.Username,
                Password = cleanup.Password.Reveal(),
            }.ConnectionString);

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE;";
        command.ExecuteNonQuery();
    }
}
