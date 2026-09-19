using EnterpriseObservatory.Application.Security;
using Microsoft.Data.Sqlite;
using static EnterpriseObservatory.Persistence.Sqlite.SqlValues;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Accounts, durable.
/// </summary>
/// <remarks>
/// <para>
/// Read straight from the database on every call rather than cached. Accounts
/// change rarely and are read once per sign-in, so there is nothing to gain —
/// and a stale cache here would mean a removed account still working, or a
/// lockout that a second process did not see.
/// </para>
/// <para>
/// Only the verifier is stored, never the password. See
/// <see cref="PasswordHash"/>.
/// </para>
/// </remarks>
public sealed class SqliteUserAccountStore(ObservatoryDatabase database) : IUserAccountStore
{
    private readonly ObservatoryDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public bool Any => _database.Read(connection =>
    {
        using var command = Command(connection, "SELECT EXISTS (SELECT 1 FROM user_account);");
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    });

    public UserAccount? Find(string username) => _database.Read(connection =>
    {
        using var command = Command(connection, $"{Columns} WHERE username = $username;");
        command.Bind("$username", UserAccount.Normalize(username));

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : null;
    });

    public IReadOnlyList<UserAccount> All() => _database.Read(connection =>
    {
        var accounts = new List<UserAccount>();

        using var command = Command(connection, $"{Columns} ORDER BY username;");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            accounts.Add(Read(reader));
        }

        return (IReadOnlyList<UserAccount>)accounts;
    });

    public bool TryAdd(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return _database.Write(connection =>
        {
            using var command = Command(connection, """
                INSERT INTO user_account (
                    username, password_hash, role, created_utc,
                    last_signed_in_utc, failed_attempts, locked_until_utc)
                VALUES ($username, $hash, $role, $created, $signedIn, $failed, $locked)
                ON CONFLICT (username) DO NOTHING;
                """);

            Bind(command, account);

            // Zero rows means the name was taken. Decided by the database
            // rather than by a check followed by an insert, which two callers
            // can both pass at the same moment.
            return command.ExecuteNonQuery() == 1;
        });
    }

    public void Update(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        _database.Write(connection =>
        {
            using var command = Command(connection, """
                UPDATE user_account SET
                    password_hash = $hash,
                    role = $role,
                    last_signed_in_utc = $signedIn,
                    failed_attempts = $failed,
                    locked_until_utc = $locked
                WHERE username = $username;
                """);

            Bind(command, account);
            command.ExecuteNonQuery();
        });
    }

    public bool Remove(string username) => _database.Write(connection =>
    {
        using var command = Command(connection, "DELETE FROM user_account WHERE username = $username;");
        command.Bind("$username", UserAccount.Normalize(username));

        return command.ExecuteNonQuery() == 1;
    });

    private const string Columns = """
        SELECT username, password_hash, role, created_utc,
               last_signed_in_utc, failed_attempts, locked_until_utc
        FROM user_account
        """;

    private static void Bind(SqliteCommand command, UserAccount account)
    {
        command.Bind("$username", account.Username);
        command.Bind("$hash", account.Password.Encoded);
        command.Bind("$role", account.Role.ToString());
        command.Bind("$created", Timestamp(account.CreatedUtc));
        command.Bind("$signedIn", TimestampOrNull(account.LastSignedInUtc));
        command.Bind("$failed", account.FailedAttempts);
        command.Bind("$locked", TimestampOrNull(account.LockedUntilUtc));
    }

    private static UserAccount Read(SqliteDataReader reader) => new()
    {
        Username = reader.GetString(0),
        Password = PasswordHash.Restore(reader.GetString(1)),
        Role = ReadEnum<Role>(reader, 2),
        CreatedUtc = ReadTimestamp(reader, 3),
        LastSignedInUtc = ReadTimestampOrNull(reader, 4),
        FailedAttempts = (int)reader.GetInt64(5),
        LockedUntilUtc = ReadTimestampOrNull(reader, 6),
    };
}
