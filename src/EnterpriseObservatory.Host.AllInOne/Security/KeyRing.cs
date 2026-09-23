using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseObservatory.Host.AllInOne.Security;

/// <summary>
/// The one definition of how the product's Data Protection key ring is set up.
/// </summary>
/// <remarks>
/// <para>
/// Moved out of <c>Program.cs</c> unchanged when first-run setup (G-DB) needed
/// the same key ring in two more places: the setup host, which writes the
/// protected database file, and the moment before the normal host is built,
/// when that file has to be read to know which database to open. Three copies
/// of these lines would be three chances for one of them to drift to a
/// different application name or a different protection, and a file written
/// under one and read under another is a file nobody can read.
/// </para>
/// <para>
/// Machine-scope DPAPI on Windows, for the reason given in <c>Program.cs</c>
/// and ADR-0015: a service account does not reliably have a loaded profile.
/// </para>
/// </remarks>
public static class KeyRing
{
    public const string ApplicationName = "EnterpriseObservatory";

    /// <summary>Adds the key ring at <paramref name="path"/> to <paramref name="services"/>.</summary>
    public static IDataProtectionBuilder Add(IServiceCollection services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var keyRing = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(path));

        if (OperatingSystem.IsWindows())
        {
            keyRing.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        return keyRing;
    }

    /// <summary>
    /// The same key ring, outside any host, for reading the protected database
    /// file before the host that will use it has been built.
    /// </summary>
    /// <remarks>The caller disposes it as soon as the read is done.</remarks>
    public static ServiceProvider Standalone(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Add(services, path);
        return services.BuildServiceProvider();
    }
}
