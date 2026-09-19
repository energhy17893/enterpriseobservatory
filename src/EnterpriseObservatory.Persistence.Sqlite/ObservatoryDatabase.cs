using Microsoft.Data.Sqlite;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>Where the database lives and how it behaves.</summary>
public sealed record SqliteStoreOptions
{
    /// <summary>
    /// The database file.
    /// </summary>
    /// <remarks>
    /// A file, not a server. ADR-0001 requires an MSI that installs without an
    /// appliance, and a database nobody has to provision is the difference
    /// between a product an operator installs in a maintenance window and one
    /// that needs a project.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>
    /// An in-memory database, for tests.
    /// </summary>
    /// <remarks>
    /// The same implementation, not a second one. A hand-written in-memory
    /// store would be a second set of semantics to keep in step with this one,
    /// and the first thing to drift would be the subtleties that matter —
    /// which scope a write replaces, whether retirements are applied.
    /// </remarks>
    public bool InMemory { get; init; }
}

/// <summary>
/// The database connection and its schema.
/// </summary>
/// <remarks>
/// <para>
/// One connection for the process, guarded by a lock. The write volume is one
/// transaction every thirty seconds and one every five minutes; a connection
/// pool would add a concurrency model to reason about in exchange for
/// throughput nobody needs.
/// </para>
/// <para>
/// Every write is a transaction. A cycle's reconciliation is a decision about a
/// whole scope, and applying half of it would leave alerts that are neither
/// firing nor resolved — a state no code reads correctly because no code
/// expects it.
/// </para>
/// </remarks>
public sealed class ObservatoryDatabase : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public ObservatoryDatabase(SqliteStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.InMemory ? $"eo-{Guid.NewGuid():n}" : options.Path,
            Mode = options.InMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            // Shared so an in-memory database outlives a single command and can
            // be reopened within the process, which is what lets a test prove
            // that a restart reads back what was written.
            Cache = options.InMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = false,
        };

        if (!options.InMemory)
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.Path));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        if (!options.InMemory)
        {
            // Readers do not block the writer. The web interface queries this
            // database on every page load while a collection cycle may be
            // writing; without WAL those would contend, and the symptom would
            // be an interface that intermittently stalls for no visible reason.
            Execute("PRAGMA journal_mode = WAL;");
            Execute("PRAGMA synchronous = NORMAL;");
        }

        Execute("PRAGMA foreign_keys = ON;");

        SqliteSchema.Apply(this);
    }

    /// <summary>Runs work against the connection under the write lock.</summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            return work(_connection);
        }
    }

    /// <summary>
    /// Runs work inside a transaction, committing only if it returns.
    /// </summary>
    /// <remarks>
    /// The unit of work is a whole cycle's decision, never a row. See the
    /// remarks on this class.
    /// </remarks>
    public void Write(Action<SqliteConnection> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Write(connection =>
        {
            work(connection);
            return 0;
        });
    }

    /// <summary>Runs work inside a transaction and returns its result.</summary>
    public T Write<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var result = work(_connection);
            transaction.Commit();
            return result;
        }
    }

    /// <summary>
    /// Runs raw SQL outside a transaction.
    /// </summary>
    /// <remarks>
    /// Public so that tests can put a database into a state the product cannot
    /// produce — a value from a newer build, a row edited by hand — and check
    /// that reading it fails loudly. Those are the cases nothing else can
    /// reach, and they are exactly the ones that must not fail quietly.
    /// </remarks>
    public void Execute(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
