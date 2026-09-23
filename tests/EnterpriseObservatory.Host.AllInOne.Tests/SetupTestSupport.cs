using System.Collections.Concurrent;
using System.Text;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Host.AllInOne.Security;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Every log line a host writes, formatted, with its exception and its
/// structured values — everything a sink could possibly persist.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public string All => string.Join(Environment.NewLine, _lines);

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue($"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;

            lines.Enqueue($"{logLevel} {category} {formatter(state, exception)} {values} {exception}");
        }
    }
}

/// <summary>Helpers shared by the first-run setup tests (G-DB).</summary>
internal static class SetupTestSupport
{
    /// <summary>Why the live setup tests skip, or null when they can run.</summary>
    public static string? AdminSkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_USER")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_PASSWORD"))
            ? "No admin credential for the create-database path; set EO_TEST_PG_ADMIN_USER/PASSWORD."
            : null;

    public static string TestHost => Environment.GetEnvironmentVariable("EO_TEST_PG_HOST") ?? "127.0.0.1";

    public static int TestPort => int.TryParse(
        Environment.GetEnvironmentVariable("EO_TEST_PG_PORT"),
        System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture,
        out var port) ? port : 5432;

    public static PostgresAdminCredential Admin() => new()
    {
        Username = Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_USER") ?? string.Empty,
        Password = Secret.From(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_PASSWORD")),
    };

    public static string TempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A key file still open at teardown is a temp directory left
            // behind, not a failed test.
        }
    }

    /// <summary>The protected file for a key ring directory, opened with the real key ring.</summary>
    public static (DatabaseCredentialFile File, ServiceProvider KeyRing) OpenFile(string keyRingDirectory)
    {
        var keyRing = KeyRing.Standalone(keyRingDirectory);

        var file = new DatabaseCredentialFile(
            DatabaseCredentialFile.PathFor(keyRingDirectory),
            new DataProtectionSecretProtector(
                keyRing.GetRequiredService<IDataProtectionProvider>(),
                DataProtectionSecretProtector.DatabasePasswordPurpose));

        return (file, keyRing);
    }

    /// <summary>
    /// Whether <paramref name="value"/> appears in any file under
    /// <paramref name="directory"/>, as UTF-8 or as UTF-16.
    /// </summary>
    public static IReadOnlyList<string> FilesContaining(string directory, string value)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var utf8 = Encoding.UTF8.GetBytes(value);
        var utf16 = Encoding.Unicode.GetBytes(value);

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                // ReadWrite: the host's file log is still open for writing,
                // and it is one of the files a credential must not reach.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                return bytes.AsSpan().IndexOf(utf8) >= 0 || bytes.AsSpan().IndexOf(utf16) >= 0;
            })
            .ToList();
    }
}
