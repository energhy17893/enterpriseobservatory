using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

// The framework has its own type called Secret, and it is not this one.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// Reads a <c>redfish</c> or <c>simplivity</c> connection the product already
/// holds, and decrypts its password.
/// </summary>
/// <remarks>
/// <para>
/// The same trade <c>EnterpriseObservatory.VsphereProbe.StoredConnection</c>
/// makes, for the same reason: retyping an iLO or OVC password is how a
/// working credential gets mistaken for a broken one. This copy exists
/// because the two probes are separate executables with no shared reference
/// between them -- not because the decryption differs. If it ever needs to
/// differ, that is the day this stops being a copy and becomes a library.
/// </para>
/// <para>
/// Works only on the machine that holds the key ring, configured identically
/// to the service: a tool that decrypted differently would be answering a
/// different question about whether a credential works.
/// </para>
/// </remarks>
internal static class StoredConnection
{
    private const string ApplicationName = "EnterpriseObservatory";

    private const string Purpose = "EnterpriseObservatory.SourceConnection.Password.v1";

    public static (string InstanceId, string Kind, string Url, string User, string Password, bool Insecure) Read(
        string kind, string instanceId)
    {
        var options = new PostgresOptions
        {
            Host = Environment.GetEnvironmentVariable("EO_PG_HOST") ?? "127.0.0.1",
            Database = Environment.GetEnvironmentVariable("EO_PG_DATABASE") ?? "observatory",
            Username = Environment.GetEnvironmentVariable("EO_PG_USER") ?? "observatory",
            Password = Secret.From(Environment.GetEnvironmentVariable("EO_PG_PASSWORD")),
            Schema = Environment.GetEnvironmentVariable("EO_PG_SCHEMA") ?? "public",
        };

        var problems = options.Validate();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The database is not reachable with what was supplied: " +
                string.Join(" ", problems) +
                " Set EO_PG_PASSWORD, and EO_PG_HOST / EO_PG_DATABASE / EO_PG_USER if they " +
                "differ from the defaults.");
        }

        using var database = new PostgresDatabase(options);

        var store = new PostgresSourceConnectionStore(database, new KeyRingProtector());

        var ofKind = store.All.Where(c => string.Equals(c.Kind, kind, StringComparison.Ordinal)).ToList();

        var connection = string.IsNullOrWhiteSpace(instanceId)
            ? ofKind.Count > 0 ? ofKind[0] : null
            : ofKind.FirstOrDefault(c => string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal));

        if (connection is null)
        {
            var known = ofKind.Select(c => c.InstanceId).ToList();

            throw new InvalidOperationException(known.Count == 0
                ? $"The database holds no '{kind}' connections. Enter one before probing it."
                : $"No '{kind}' connection called '{instanceId}'. Known: {string.Join(", ", known)}.");
        }

        if (connection.PasswordUnreadable)
        {
            throw new InvalidOperationException(
                $"'{connection.InstanceId}' has a stored password that cannot be decrypted. The " +
                "key material is missing or belongs to a different installation -- which means " +
                "the service cannot read it either.");
        }

        return (
            connection.InstanceId,
            connection.Kind,
            connection.BaseAddress.ToString(),
            connection.Username,
            connection.Password.Reveal(),
            connection.AcceptUntrustedCertificate);
    }

    /// <summary>The service's own key ring, configured the same way.</summary>
    private sealed class KeyRingProtector : ISecretProtector
    {
        private readonly IDataProtector _protector;

        public KeyRingProtector()
        {
            var keys = Environment.GetEnvironmentVariable("EO_KEYRING")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "EnterpriseObservatory",
                    "keys");

            if (!Directory.Exists(keys))
            {
                throw new DirectoryNotFoundException(
                    $"No key ring at {keys}. Stored passwords cannot be read without it. Set " +
                    "EO_KEYRING if it lives somewhere else.");
            }

            var services = new ServiceCollection();

            var builder = services.AddDataProtection()
                .SetApplicationName(ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(keys));

            if (OperatingSystem.IsWindows())
            {
                builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }

            _protector = services.BuildServiceProvider()
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(Purpose);
        }

        public string Protect(Secret secret) =>
            secret.IsEmpty ? string.Empty : _protector.Protect(secret.Reveal());

        public Secret Unprotect(string protectedValue)
        {
            if (protectedValue.Length == 0)
            {
                return Secret.Empty;
            }

            try
            {
                return Secret.From(_protector.Unprotect(protectedValue));
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new SecretUnprotectException(
                    "A stored password could not be decrypted with this key ring.", ex);
            }
        }
    }
}
