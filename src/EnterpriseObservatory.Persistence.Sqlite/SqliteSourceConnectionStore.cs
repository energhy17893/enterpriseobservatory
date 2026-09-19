using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Connections entered in the product, durable.
/// </summary>
/// <remarks>
/// <para>
/// The password goes through <see cref="ISecretProtector"/> on the way in and
/// on the way out, and the protected form is the only thing this class ever
/// holds in a column. That is a narrower claim than "the password is
/// encrypted", and the narrower claim is the true one: see ADR-0015.
/// </para>
/// <para>
/// Read into memory once and kept there, like the other stores. A collection
/// cycle asks for the connection list every time it runs, and going to disk for
/// that would put a file read on the path of every cycle for data that changes
/// when a person clicks Save.
/// </para>
/// </remarks>
public sealed class SqliteSourceConnectionStore : ISourceConnectionStore
{
    private readonly ObservatoryDatabase _database;
    private readonly ISecretProtector _protector;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceConnection> _connections;

    public SqliteSourceConnectionStore(ObservatoryDatabase database, ISecretProtector protector)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _connections = _database.Read(Load);
    }

    public IReadOnlyList<SourceConnection> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections.Values.OrderBy(c => c.InstanceId, StringComparer.Ordinal)];
            }
        }
    }

    public SourceConnection? Find(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        lock (_gate)
        {
            return _connections.GetValueOrDefault(instanceId);
        }
    }

    public bool Add(SourceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            if (_connections.ContainsKey(connection.InstanceId))
            {
                return false;
            }

            Write(connection);
            _connections[connection.InstanceId] = connection;
            return true;
        }
    }

    public bool Update(SourceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.InstanceId, out var existing))
            {
                return false;
            }

            // An empty password means "leave it alone", not "erase it". The
            // edit form cannot show the stored password — nothing can read it
            // back out through the API — so submitting that form unchanged
            // arrives here with an empty one, and taking it at face value
            // would break a working collector by opening a page and saving it.
            var merged = connection.Password.IsEmpty && !existing.PasswordUnreadable
                ? connection with
                {
                    Password = existing.Password,
                    PasswordSetUtc = existing.PasswordSetUtc,
                }
                : connection;

            Write(merged);
            _connections[merged.InstanceId] = merged;
            return true;
        }
    }

    public bool Remove(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        lock (_gate)
        {
            if (!_connections.Remove(instanceId))
            {
                return false;
            }

            _database.Write(connection =>
            {
                using var command = Command(
                    connection, "DELETE FROM source_connection WHERE instance_id = $id;");
                command.Bind("$id", instanceId);
                command.ExecuteNonQuery();
            });

            return true;
        }
    }

    private void Write(SourceConnection entry) =>
        _database.Write(connection =>
        {
            using var command = Command(connection, """
                INSERT OR REPLACE INTO source_connection (
                    instance_id, kind, base_address, username, password_protected,
                    accept_untrusted, page_size, is_enabled, created_utc, created_by,
                    password_set_utc)
                VALUES ($id, $kind, $address, $user, $password,
                        $untrusted, $page, $enabled, $created, $by, $passwordSet);
                """);

            command.Bind("$id", entry.InstanceId);
            command.Bind("$kind", entry.Kind);
            command.Bind("$address", entry.BaseAddress.ToString());
            command.Bind("$user", entry.Username);

            // The one place a credential crosses into storage. Deliberately a
            // single conspicuous line rather than something spread across the
            // class, so that reviewing "where does the password go" is a
            // question with one answer.
            command.Bind("$password", _protector.Protect(entry.Password));

            command.Bind("$untrusted", entry.AcceptUntrustedCertificate ? 1 : 0);
            command.Bind("$page", entry.PageSize);
            command.Bind("$enabled", entry.IsEnabled ? 1 : 0);
            command.Bind("$created", entry.CreatedUtc.ToString("o"));
            command.Bind("$by", entry.CreatedBy);
            command.Bind("$passwordSet", TimestampOrNull(entry.PasswordSetUtc));
            command.ExecuteNonQuery();
        });

    private Dictionary<string, SourceConnection> Load(SqliteConnection connection)
    {
        var connections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT instance_id, kind, base_address, username, password_protected,
                   accept_untrusted, page_size, is_enabled, created_utc, created_by,
                   password_set_utc
            FROM source_connection;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // A password that cannot be decrypted must not stop the service
            // from starting. The usual cause is a database restored without
            // the key ring beside it, and refusing to boot would take the
            // whole estate's alerting down over one unreadable column —
            // turning a recoverable mistake into an outage. So the connection
            // loads, says it cannot be read, and is not polled: a failed login
            // every thirty seconds would be the other way to make this worse.
            var stored = reader.GetString(4);
            var password = Secret.Empty;
            var unreadable = false;

            try
            {
                password = _protector.Unprotect(stored);
            }
            catch (SecretUnprotectException)
            {
                unreadable = true;
            }

            var entry = new SourceConnection
            {
                InstanceId = reader.GetString(0),
                Kind = reader.GetString(1),
                BaseAddress = new Uri(reader.GetString(2)),
                Username = reader.GetString(3),
                Password = password,
                PasswordUnreadable = unreadable,
                AcceptUntrustedCertificate = reader.GetInt64(5) != 0,
                PageSize = (int)reader.GetInt64(6),
                IsEnabled = reader.GetInt64(7) != 0,
                CreatedUtc = DateTimeOffset.Parse(reader.GetString(8), null),
                CreatedBy = reader.GetString(9),
                PasswordSetUtc = ReadTimestampOrNull(reader, 10),
                Origin = ConnectionOrigin.Managed,
            };

            connections[entry.InstanceId] = entry;
        }

        return connections;
    }
}
