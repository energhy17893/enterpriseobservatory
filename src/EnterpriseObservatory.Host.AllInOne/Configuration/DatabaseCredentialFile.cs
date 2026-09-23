using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Postgres;

// The framework has its own type called Secret, and it is not this one.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// The product's own database connection, written by the setup screen, in a
/// file beside the key ring with its password protected by that key ring.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0010 left one question open: if a credential is entered through a
/// wizard, which store does it go to? This is the answer — the old product's
/// mechanism (DPAPI) in the location ADR-0020 settled (ProgramData, inspected
/// at startup by <see cref="KeyRingDurabilityGuard"/>). Not a configuration
/// file: nothing reads it through <c>IConfiguration</c>, it does not travel with
/// the application, and the password in it is ciphertext from the same
/// <see cref="ISecretProtector"/> that protects every vCenter password.
/// </para>
/// <para>
/// Inside the key ring directory rather than elsewhere: it is useless without
/// that directory's keys, so the two are backed up, moved and lost together,
/// and the durability check already made on the directory covers it. The
/// Data Protection repository only reads <c>*.xml</c>, and the key count only
/// counts <c>key-*.xml</c>, so neither sees it.
/// </para>
/// <para>
/// Written to a temporary name and moved into place, so a crash mid-write
/// leaves the old file or the new one and never half of either.
/// </para>
/// </remarks>
public sealed class DatabaseCredentialFile
{
    public const string FileName = "database-connection.json";

    private const int Format = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ISecretProtector _protector;

    public DatabaseCredentialFile(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        Path = path;
        _protector = protector;
    }

    /// <summary>Where the file lives for a key ring at <paramref name="keyRingPath"/>.</summary>
    public static string PathFor(string keyRingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRingPath);
        return System.IO.Path.Combine(keyRingPath, FileName);
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>When the password in the file was set, if the file exists.</summary>
    public DateTimeOffset? PasswordSetUtc => Exists ? Load().PasswordSetUtc : null;

    /// <summary>Reads the connection, password included.</summary>
    /// <exception cref="InvalidOperationException">
    /// If the file cannot be read or its password cannot be decrypted. The
    /// message names the file and the remedy, never the value.
    /// </exception>
    public PostgresOptions Read()
    {
        var stored = Load();

        Secret password;

        try
        {
            password = _protector.Unprotect(stored.Password ?? string.Empty);
        }
        catch (SecretUnprotectException ex)
        {
            throw new InvalidOperationException(
                $"The database password in {Path} cannot be decrypted: the key ring beside it is " +
                "missing or belongs to another installation. Restore the key ring, or delete this file " +
                "and run setup again (the database itself is not affected).",
                ex);
        }

        return new PostgresOptions
        {
            Host = stored.Host ?? string.Empty,
            Port = stored.Port,
            Database = stored.Database ?? string.Empty,
            Username = stored.Username ?? string.Empty,
            Schema = stored.Schema ?? "public",
            RequireTls = stored.RequireTls,
            Password = password,
        };
    }

    /// <summary>Writes <paramref name="options"/>, replacing any file already there.</summary>
    public void Write(PostgresOptions options) => Commit(Stage(options));

    /// <summary>
    /// Writes <paramref name="options"/> beside the file without replacing it.
    /// </summary>
    /// <remarks>
    /// For rotation, where the new password must exist on disk before the
    /// server is told about it, and must not replace the old one until it has.
    /// </remarks>
    public string Stage(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stored = new StoredConnection
        {
            Format = Format,
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Schema = options.Schema,
            RequireTls = options.RequireTls,
            Password = _protector.Protect(options.Password),
            PasswordSetUtc = DateTimeOffset.UtcNow,
        };

        var staged = Path + ".pending";

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(staged, JsonSerializer.Serialize(stored, Json));

        return staged;
    }

    /// <summary>Moves a staged file into place.</summary>
    public void Commit(string staged) => File.Move(staged, Path, overwrite: true);

    /// <summary>Removes a staged file that will not be committed.</summary>
    public static void Discard(string staged)
    {
        if (File.Exists(staged))
        {
            File.Delete(staged);
        }
    }

    /// <summary>Removes the file, so that the next start runs setup again.</summary>
    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    private StoredConnection Load()
    {
        StoredConnection? stored;

        try
        {
            stored = JsonSerializer.Deserialize<StoredConnection>(File.ReadAllText(Path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException(
                $"The database connection file {Path} cannot be read ({ex.GetType().Name}). Fix its " +
                "permissions, or delete it and run setup again.",
                ex);
        }

        if (stored is null || stored.Format != Format)
        {
            throw new InvalidOperationException(
                $"The database connection file {Path} is not in a format this build reads. Delete it and " +
                "run setup again.");
        }

        return stored;
    }

    private sealed record StoredConnection
    {
        public int Format { get; init; }

        public string? Host { get; init; }

        public int Port { get; init; }

        public string? Database { get; init; }

        public string? Username { get; init; }

        public string? Schema { get; init; }

        public bool RequireTls { get; init; }

        /// <summary>Data Protection ciphertext, never the password.</summary>
        public string? Password { get; init; }

        public DateTimeOffset PasswordSetUtc { get; init; }
    }
}
