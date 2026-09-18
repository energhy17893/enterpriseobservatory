using Microsoft.Extensions.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// Refuses to start when a credential was read from a settings file.
/// </summary>
/// <remarks>
/// <para>
/// The previous product encrypted its credentials with DPAPI and still leaked
/// one: a vCenter password was serialised inside a free-text JSON field that
/// the encryption did not cover, and sat in plaintext in a file for as long as
/// the product existed. Nothing was broken. The mechanism simply had a way
/// around it, and nobody was told when it was taken.
/// </para>
/// <para>
/// So the rule is enforced rather than documented. Configuration binding is
/// left completely ordinary — user secrets, environment variables, a key vault
/// and a mounted secret all work with no special support — and afterwards we
/// ask which provider actually supplied each credential. A file-backed one
/// stops the service with a message naming the file.
/// </para>
/// <para>
/// This is the runtime half of the rule. The other half, marking
/// credential-bearing fields in the type system so a secret cannot be embedded
/// in free text at all, belongs with the persistence layer.
/// </para>
/// </remarks>
public static class CredentialSourceGuard
{
    /// <summary>
    /// Throws if any of <paramref name="keys"/> was supplied by a file.
    /// </summary>
    /// <param name="configuration">The built configuration root.</param>
    /// <param name="keys">Fully qualified keys holding secrets.</param>
    /// <exception cref="InvalidOperationException">
    /// If a secret came from a settings file. The message names the key and the
    /// file, and never the value.
    /// </exception>
    public static void EnsureNotFromFiles(IConfiguration configuration, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(keys);

        if (configuration is not IConfigurationRoot root)
        {
            // Cannot see the providers, so cannot make the guarantee. Saying so
            // is better than appearing to check.
            throw new InvalidOperationException(
                "Credential sources cannot be verified: the configuration is not a root.");
        }

        var offences = keys
            .Select(key => (Key: key, File: FileProviding(root, key)))
            .Where(x => x.File is not null)
            .Select(x => $"  {x.Key} was read from {x.File}")
            .ToList();

        if (offences.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A credential was read from a settings file:" + Environment.NewLine +
            string.Join(Environment.NewLine, offences) + Environment.NewLine +
            "Remove it from the file and supply it through user secrets, an environment " +
            "variable or a secret store. Then treat the value as compromised and rotate it: " +
            "it has been on disk in the clear, and may be in a backup or a commit.");
    }

    /// <summary>
    /// The file that supplied a key, or null if no file did.
    /// </summary>
    /// <remarks>
    /// Walked in reverse because later providers win in .NET configuration. The
    /// question is which provider the bound value actually came from, not
    /// whether a file happens to mention the key — a password left in
    /// appsettings.json but overridden by an environment variable is still a
    /// password on disk, but it is a different and lesser problem, and the
    /// first provider to answer is the one that matters.
    /// </remarks>
    private static string? FileProviding(IConfigurationRoot root, string key)
    {
        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            return provider is FileConfigurationProvider file
                ? Describe(file)
                : null;
        }

        return null;
    }

    /// <summary>
    /// Names the offending file as fully as it can be named.
    /// </summary>
    /// <remarks>
    /// A configuration source's own path is relative to its file provider, so
    /// on its own it reads as a bare file name. Someone who has just been told
    /// a secret is on disk needs to know which disk and where, not which of the
    /// several files called appsettings.json it might have been.
    /// </remarks>
    private static string Describe(FileConfigurationProvider provider)
    {
        if (provider.Source.Path is not { Length: > 0 } path)
        {
            return "a settings file";
        }

        return provider.Source.FileProvider?.GetFileInfo(path).PhysicalPath ?? path;
    }
}
