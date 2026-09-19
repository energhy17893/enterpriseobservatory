using Microsoft.Data.Sqlite;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>Where the measurements live.</summary>
public sealed record MetricsStoreOptions
{
    /// <summary>
    /// The measurement database file.
    /// </summary>
    /// <remarks>
    /// A separate file from the state database, same engine. They grow and
    /// churn completely differently: this one is append-heavy and has rows
    /// deleted by retention every few minutes, which fragments a file over
    /// time. Keeping alert state out of that file means an oversized or
    /// damaged metric history cannot take alerting down with it, and that a
    /// support copy of the state is kilobytes rather than gigabytes.
    /// </remarks>
    public required string Path { get; init; }

    public bool InMemory { get; init; }
}

/// <summary>
/// The measurement database.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ObservatoryDatabase"/> on purpose; see
/// <see cref="MetricsStoreOptions.Path"/> and ADR-0012.
/// </para>
/// <para>
/// One connection guarded by a lock, as with state. The write pattern here is
/// one large transaction every sampling interval rather than many small ones,
/// which is the shape SQLite is fastest at and the shape a pool would not help.
/// </para>
/// </remarks>
public sealed class MetricsDatabase : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public MetricsDatabase(MetricsStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.InMemory ? $"eo-metrics-{Guid.NewGuid():n}" : options.Path,
            Mode = options.InMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
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
            Execute("PRAGMA journal_mode = WAL;");

            // NORMAL rather than FULL: a crash can lose the last transaction,
            // which here means losing one cycle's samples. That is thirty
            // seconds of one metric — recoverable by waiting. Paying an fsync
            // per commit to protect it would slow every write for no gain
            // anybody would notice.
            Execute("PRAGMA synchronous = NORMAL;");
        }

        Execute("PRAGMA foreign_keys = ON;");

        MetricsSchema.Apply(this);
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            return work(_connection);
        }
    }

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

    public void Write(Action<SqliteConnection> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Write(connection =>
        {
            work(connection);
            return 0;
        });
    }

    /// <summary>Runs raw SQL outside a transaction. See the state database.</summary>
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
