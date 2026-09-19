using EnterpriseObservatory.Application.Security;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>How to reach the database.</summary>
/// <remarks>
/// The password is a <see cref="Secret"/> and the connection string is built
/// here rather than accepted as text. A connection string carrying a password
/// is a credential wearing the shape of a setting, and every place it can be
/// passed as a string is a place it can be logged, echoed into a diagnostic or
/// written into appsettings.json — which is precisely what ADR-0010 exists to
/// prevent.
/// </remarks>
public sealed record PostgresOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5432;

    public string Database { get; init; } = "observatory";

    public string Username { get; init; } = "observatory";

    public Secret Password { get; init; } = Secret.Empty;

    /// <summary>
    /// Whether to require TLS.
    /// </summary>
    /// <remarks>
    /// Default off because the expected deployment is a database on the same
    /// host as the service, where TLS protects nothing that the loopback
    /// interface does not. Expressible because that assumption stops being true
    /// the moment the database moves, and a credential crossing a network in
    /// the clear should be a decision somebody made rather than a default
    /// nobody noticed.
    /// </remarks>
    public bool RequireTls { get; init; }

    /// <summary>What is wrong with these options, or empty if nothing is.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("A host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add("The port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            problems.Add("A database name is required.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add("A username is required.");
        }

        if (Password.IsEmpty)
        {
            problems.Add(
                "A password is required. Supply it through user secrets, an environment " +
                "variable or a secret store — never in appsettings.json. See ADR-0010.");
        }

        return problems;
    }

    internal string ToConnectionString() =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password.Reveal(),
            SslMode = RequireTls ? SslMode.Require : SslMode.Prefer,

            // The pool is the reason for choosing this engine over a file, so
            // it is left on and sized rather than disabled. A collection cycle
            // writes from one thread while the interface reads from several.
            Pooling = true,
            MaxPoolSize = 20,

            // Long enough to survive a busy server, short enough that a cycle
            // waiting on the database gives up before the next one starts.
            Timeout = 15,
            CommandTimeout = 30,
        }.ConnectionString;
}

/// <summary>
/// One PostgreSQL database, and the only thing that opens connections to it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately unlike <c>ObservatoryDatabase</c>, which holds a single
/// connection behind a lock because SQLite has one writer and serialising
/// access is the only way to use it correctly. Copying that here would throw
/// away the main reason for having a database server: PostgreSQL handles
/// concurrent writers itself, and a global lock in front of it would make a
/// multi-user engine behave like a single-user file.
/// </para>
/// <para>
/// So this is a connection factory over Npgsql's pool. Each operation takes a
/// connection, uses it, and returns it.
/// </para>
/// </remarks>
public sealed class PostgresDatabase : IDisposable
{
    private readonly NpgsqlDataSource _source;
    private bool _disposed;

    public PostgresDatabase(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Validate();

        if (problems.Count > 0)
        {
            throw new ArgumentException(
                "The database options are not usable: " + string.Join(" ", problems),
                nameof(options));
        }

        _source = NpgsqlDataSource.Create(options.ToConnectionString());

        PostgresSchema.Apply(this);
    }

    /// <summary>Runs a read and returns its result.</summary>
    public T Read<T>(Func<NpgsqlConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connection = _source.OpenConnection();
        return read(connection);
    }

    /// <summary>
    /// Runs a write inside one transaction.
    /// </summary>
    /// <remarks>
    /// A transaction rather than a bare command, because every write this
    /// product performs is several statements that are only correct together:
    /// a compaction pass writes buckets and moves the watermark, and a pass
    /// that did the first without the second would fold the same window twice.
    /// </remarks>
    public void Write(Action<NpgsqlConnection> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connection = _source.OpenConnection();
        using var transaction = connection.BeginTransaction();

        write(connection);

        transaction.Commit();
    }

    /// <summary>Runs a write that reports what it did.</summary>
    public T Write<T>(Func<NpgsqlConnection, T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connection = _source.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var result = write(connection);

        transaction.Commit();
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.Dispose();
    }
}
