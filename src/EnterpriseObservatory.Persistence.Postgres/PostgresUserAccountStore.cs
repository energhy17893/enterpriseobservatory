using EnterpriseObservatory.Application.Security;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

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
/// That last clause stops being hypothetical with a database server. Two
/// processes reading the same accounts is the deployment ADR-0001 always
/// anticipated and SQLite could not serve, so a cache here would now be wrong
/// in production rather than merely wrong in principle.
/// </para>
/// <para>
/// Only the verifier is stored, never the password. See
/// <see cref="PasswordHash"/>.
/// </para>
/// </remarks>
public sealed class PostgresUserAccountStore(PostgresDatabase database) : IUserAccountStore
{
    private readonly PostgresDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public bool Any => _database.Read(connection =>
    {
        using var command = Command(connection, "SELECT EXISTS (SELECT 1 FROM user_account);");
        return (bool)command.ExecuteScalar()!;
    });

    public UserAccount? Find(string username) => _database.Read(connection =>
    {
        using var command = Command(connection, $"{Columns} WHERE username = @username;");
        command.Bind("@username", UserAccount.Normalize(username));

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
                VALUES (@username, @hash, @role, @created, @signedIn, @failed, @locked)
                ON CONFLICT (username) DO NOTHING;
                """);

            Bind(command, account);

            // Zero rows means the name was taken. Decided by the database
            // rather than by a check followed by an insert, which two callers
            // can both pass at the same moment — and now genuinely can, since
            // two processes may be serving sign-ups at once.
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
                    password_hash = @hash,
                    role = @role,
                    last_signed_in_utc = @signedIn,
                    failed_attempts = @failed,
                    locked_until_utc = @locked
                WHERE username = @username;
                """);

            Bind(command, account);
            command.ExecuteNonQuery();
        });
    }

    public bool Remove(string username) => _database.Write(connection =>
    {
        using var command = Command(connection, "DELETE FROM user_account WHERE username = @username;");
        command.Bind("@username", UserAccount.Normalize(username));

        return command.ExecuteNonQuery() == 1;
    });

    private const string Columns = """
        SELECT username, password_hash, role, created_utc,
               last_signed_in_utc, failed_attempts, locked_until_utc
        FROM user_account
        """;

    private static void Bind(NpgsqlCommand command, UserAccount account)
    {
        command.Bind("@username", account.Username);
        command.Bind("@hash", account.Password.Encoded);
        command.Bind("@role", account.Role.ToString());
        command.BindTime("@created", account.CreatedUtc);
        command.BindTime("@signedIn", account.LastSignedInUtc);
        command.Bind("@failed", account.FailedAttempts);
        command.BindTime("@locked", account.LockedUntilUtc);
    }

    private static UserAccount Read(NpgsqlDataReader reader) => new()
    {
        Username = reader.GetString(0),
        Password = PasswordHash.Restore(reader.GetString(1)),
        Role = ReadEnum<Role>(reader, 2),
        CreatedUtc = ReadTime(reader, 3),
        LastSignedInUtc = ReadTimeOrNull(reader, 4),
        FailedAttempts = reader.GetInt32(5),
        LockedUntilUtc = ReadTimeOrNull(reader, 6),
    };
}
