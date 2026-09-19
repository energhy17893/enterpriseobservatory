using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

// The framework has its own type called Secret, and it is not this one.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// Reads a connection the product already holds, and decrypts its password.
/// </summary>
/// <remarks>
/// <para>
/// So that diagnosing a vCenter never requires retyping its credential.
/// Retyping is not a neutral act: PowerShell rewrites <c>$</c> and backticks
/// inside double quotes, and this product spent a day chasing "vCenter rejected
/// the credentials" for a password that had been correct the whole time.
/// </para>
/// <para>
/// One credential is still typed — the database's — and it unlocks all the
/// others without them being typed at all. That is the trade, and it is a good
/// one: the database password is one value an operator already has to hand,
/// while the vCenter passwords are the ones that have to survive being correct.
/// </para>
/// <para>
/// Works only on the machine that holds the key ring. If this cannot decrypt,
/// neither can the service, and that is itself the answer to a question
/// somebody was about to ask.
/// </para>
/// </remarks>
internal static class StoredConnection
{
    /// <summary>Must match the service, or nothing decrypts.</summary>
    private const string ApplicationName = "EnterpriseObservatory";

    private const string Purpose = "EnterpriseObservatory.SourceConnection.Password.v1";

    public static (string Url, string User, string Password, bool Insecure) Read(string instanceId)
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

        var all = store.All;

        var connection = string.IsNullOrWhiteSpace(instanceId)
            ? (all.Count > 0 ? all[0] : null)
            : store.Find(instanceId);

        if (connection is null)
        {
            var known = all.Select(c => c.InstanceId).ToList();

            throw new InvalidOperationException(known.Count == 0
                ? "The database holds no connections."
                : $"No connection called '{instanceId}'. Known: {string.Join(", ", known)}.");
        }

        if (connection.PasswordUnreadable)
        {
            throw new InvalidOperationException(
                $"'{connection.InstanceId}' has a stored password that cannot be decrypted. The " +
                "key material is missing or belongs to a different installation — which means " +
                "the service cannot read it either.");
        }

        return (
            connection.BaseAddress.ToString(),
            connection.Username,
            connection.Password.Reveal(),
            connection.AcceptUntrustedCertificate);
    }

    /// <summary>
    /// The service's own key ring, configured the same way.
    /// </summary>
    /// <remarks>
    /// Configured identically on purpose. A diagnostic tool that decrypted
    /// differently from the service it diagnoses would be answering a different
    /// question, and would say "the password is fine" about a value the service
    /// cannot read.
    /// </remarks>
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
                    $"No key ring at {keys}. Stored passwords cannot be read without it — which " +
                    "is the point of keeping it out of the database's backup. Set EO_KEYRING if " +
                    "it lives somewhere else.");
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
