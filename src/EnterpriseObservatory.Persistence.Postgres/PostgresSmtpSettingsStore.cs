using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The installation's SMTP settings, durable. One row — see the migration 8
/// comment in <see cref="PostgresSchema"/> for why.
/// </summary>
/// <remarks>
/// Kept in memory after the first read and written through, the same pattern
/// <see cref="PostgresSourceConnectionStore"/> uses: settings are read on
/// every dispatch pass and change only when someone clicks Save.
/// </remarks>
public sealed class PostgresSmtpSettingsStore : ISmtpSettingsStore
{
    private readonly PostgresDatabase _database;
    private readonly ISecretProtector _protector;
    private readonly Lock _gate = new();
    private SmtpSettings _settings;

    public PostgresSmtpSettingsStore(PostgresDatabase database, ISecretProtector protector)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _settings = _database.Read(Load);
    }

    public SmtpSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public void Save(SmtpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            // An empty password means "leave it alone" -- the same rule
            // PostgresSourceConnectionStore.Update follows and for the same
            // reason: nothing can read the stored password back out, so an
            // edit form that shows a blank field must not erase a working
            // credential by being saved unchanged. But that reuse is only
            // safe when the relay being saved is, in every field that
            // decides who receives the password, the one already stored --
            // see SmtpSettings.HasSameConnectionDetails. EmailApi rejects a
            // mismatched blank-password save before this is ever called;
            // this is the same guard applied again here, so a caller that
            // reaches the store directly cannot hand the stored password to
            // a different relay just by leaving the field blank.
            var merged = settings.Password.IsEmpty && settings.HasSameConnectionDetails(_settings)
                ? settings with { Password = _settings.Password, PasswordSetUtc = _settings.PasswordSetUtc }
                : settings with { PasswordSetUtc = settings.Password.IsEmpty ? null : DateTimeOffset.UtcNow };

            _database.Write(connection =>
            {
                using var command = Command(connection, """
                    INSERT INTO smtp_settings (
                        id, host, port, tls_mode, from_address, username, password_protected,
                        allow_unencrypted, password_set_utc)
                    VALUES (true, @host, @port, @tls, @from, @user, @password, @allow, @passwordSet)
                    ON CONFLICT (id) DO UPDATE SET
                        host = EXCLUDED.host,
                        port = EXCLUDED.port,
                        tls_mode = EXCLUDED.tls_mode,
                        from_address = EXCLUDED.from_address,
                        username = EXCLUDED.username,
                        password_protected = EXCLUDED.password_protected,
                        allow_unencrypted = EXCLUDED.allow_unencrypted,
                        password_set_utc = EXCLUDED.password_set_utc;
                    """);

                command.Bind("@host", merged.Host);
                command.Bind("@port", merged.Port);
                command.Bind("@tls", merged.TlsMode.ToString());
                command.Bind("@from", merged.FromAddress);
                command.Bind("@user", merged.Username);

                // The one place a credential crosses into storage, same
                // discipline as PostgresSourceConnectionStore.Write.
                command.Bind("@password", _protector.Protect(merged.Password));

                command.Bind("@allow", merged.AllowUnencrypted);
                command.BindTime("@passwordSet", merged.PasswordSetUtc);
                command.ExecuteNonQuery();
            });

            _settings = merged;
        }
    }

    private SmtpSettings Load(NpgsqlConnection connection)
    {
        using var command = Command(connection, """
            SELECT host, port, tls_mode, from_address, username, password_protected,
                   allow_unencrypted, password_set_utc
            FROM smtp_settings WHERE id = true;
            """);
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return new SmtpSettings();
        }

        var stored = reader.GetString(5);
        var password = Secret.Empty;

        try
        {
            password = _protector.Unprotect(stored);
        }
        catch (SecretUnprotectException)
        {
            // Same posture as source_connection: a key ring that cannot
            // decrypt this must not stop the service from starting. The
            // password simply reads as unset until it is entered again.
        }

        return new SmtpSettings
        {
            Host = reader.GetString(0),
            Port = reader.GetInt32(1),
            TlsMode = Enum.Parse<SmtpTlsMode>(reader.GetString(2)),
            FromAddress = reader.GetString(3),
            Username = reader.GetString(4),
            Password = password,
            AllowUnencrypted = reader.GetBoolean(6),
            PasswordSetUtc = ReadTimeOrNull(reader, 7),
        };
    }
}
