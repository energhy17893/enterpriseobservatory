using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// Reads a connection the product already holds, and decrypts its password.
/// </summary>
/// <remarks>
/// <para>
/// So that diagnosing a connection never requires retyping its credential.
/// Retyping is not a neutral act: PowerShell rewrites <c>$</c> and backticks
/// inside double quotes, and this product spent a day chasing "vCenter rejected
/// the credentials" for a password that had been correct the whole time. A
/// diagnostic tool that reintroduces that hazard is a diagnostic tool that
/// invents its own faults.
/// </para>
/// <para>
/// Works only on the machine that wrote the database, with its key ring
/// present — which is the same guarantee ADR-0015 describes, arrived at from
/// the other side. If this cannot decrypt, neither can the service, and that
/// is itself the answer to a question somebody was about to ask.
/// </para>
/// </remarks>
internal static class StoredConnection
{
    /// <summary>Must match the service, or nothing decrypts.</summary>
    private const string ApplicationName = "EnterpriseObservatory";

    private const string Purpose = "EnterpriseObservatory.SourceConnection.Password.v1";

    public static (string Url, string User, string Password, bool Insecure) Read(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        var keys = Path.Combine(Path.GetDirectoryName(full) ?? ".", "keys");

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No database at {full}.");
        }

        if (!Directory.Exists(keys))
        {
            throw new DirectoryNotFoundException(
                $"No key ring beside the database at {keys}. Stored passwords cannot be read " +
                "without it — which is the point of keeping them in separate backups.");
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, base_address, username, password_protected, accept_untrusted
            FROM source_connection
            ORDER BY instance_id
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException("The database holds no connections.");
        }

        var address = reader.GetString(1);
        var user = reader.GetString(2);
        var protectedPassword = reader.GetString(3);
        var insecure = reader.GetInt64(4) != 0;

        return (address, user, Unprotect(keys, protectedPassword), insecure);
    }

    private static string Unprotect(string keyRing, string protectedValue)
    {
        var services = new ServiceCollection();

        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyRing));

        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        using var provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Unprotect(protectedValue);
    }
}
