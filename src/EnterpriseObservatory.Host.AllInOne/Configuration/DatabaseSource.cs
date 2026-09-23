using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.Extensions.Configuration;

// The framework has its own type called Secret, and it is not this one.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>Where the database connection came from.</summary>
public enum DatabaseSourceKind
{
    /// <summary>The protected file the setup screen wrote.</summary>
    ProtectedFile,

    /// <summary><c>Database:*</c> in configuration, password from user secrets or the environment.</summary>
    Configuration,

    /// <summary>Neither: the service starts in setup mode.</summary>
    Setup,
}

/// <summary>The database connection the service will use, and where it came from.</summary>
/// <param name="Kind">Which source won.</param>
/// <param name="Options">The connection; null only in setup mode.</param>
public sealed record DatabaseSource(DatabaseSourceKind Kind, PostgresOptions? Options)
{
    /// <summary>
    /// Decides where the database connection comes from, in one fixed order.
    /// </summary>
    /// <param name="credentialFile">Where the protected file is.</param>
    /// <param name="openFile">
    /// Opens it. Called only when the file exists, because opening it means
    /// standing up the key ring, and an installation configured the old way
    /// should not have that happen on its behalf.
    /// </param>
    /// <param name="configuration">The host's configuration.</param>
    /// <remarks>
    /// <list type="number">
    /// <item>The protected file, when it exists. Setup wrote it, so it is the
    /// most recent deliberate act on this machine.</item>
    /// <item>Configuration, when it carries a password — user secrets or an
    /// environment variable, exactly as before (the live server's path).</item>
    /// <item>Otherwise setup mode.</item>
    /// </list>
    /// A file that exists and cannot be read is not skipped in favour of the
    /// next source: it throws, because quietly falling back would connect the
    /// product to a different database than the one setup chose.
    /// </remarks>
    public static DatabaseSource Resolve(
        string credentialFile, Func<DatabaseCredentialFile> openFile, IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialFile);
        ArgumentNullException.ThrowIfNull(openFile);
        ArgumentNullException.ThrowIfNull(configuration);

        if (File.Exists(credentialFile))
        {
            return new DatabaseSource(DatabaseSourceKind.ProtectedFile, openFile().Read());
        }

        var configured = FromConfiguration(configuration);

        return configured.Password.IsEmpty
            ? new DatabaseSource(DatabaseSourceKind.Setup, null)
            : new DatabaseSource(DatabaseSourceKind.Configuration, configured);
    }

    /// <summary>Whether this installation has no database source at all.</summary>
    public static bool SetupRequired(string credentialFile, IConfiguration configuration) =>
        !File.Exists(credentialFile) && FromConfiguration(configuration).Password.IsEmpty;

    /// <summary>
    /// <c>Database:*</c> from configuration, with the defaults the product has
    /// always used. Moved here unchanged from <c>Program.cs</c>.
    /// </summary>
    public static PostgresOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Database");

        return new PostgresOptions
        {
            Host = Text(section["Host"], "127.0.0.1"),
            Port = int.TryParse(section["Port"], out var port) ? port : 5432,
            Database = Text(section["Database"], "observatory"),
            Username = Text(section["Username"], "observatory"),

            // Bound through the ordinary configuration system so user secrets, an
            // environment variable and a key vault all work with no special
            // support — and then checked by CredentialSourceGuard, because the
            // previous product's leak was a password sitting in a settings file.
            Password = Secret.From(section["Password"]),

            // Named so one server can hold a lab beside a production installation.
            Schema = Text(section["Schema"], "public"),

            RequireTls = bool.TryParse(section["RequireTls"], out var tls) && tls,
        };

        static string Text(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
