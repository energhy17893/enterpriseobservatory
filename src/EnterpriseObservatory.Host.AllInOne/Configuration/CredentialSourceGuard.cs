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
    /// Every configuration key that must never come from a settings file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than at the call site, and the move was the point. While
    /// this list was written out in <c>Program.cs</c>, dropping the database
    /// password from it was one deleted <c>.Append(...)</c> — a change that
    /// reads as tidying — and nothing could have failed, because this class's
    /// tests supply their own list and so stayed green while the thing they
    /// guard was no longer guarded. The same shape as a rule that is tested
    /// thoroughly and then never called.
    /// </para>
    /// <para>
    /// With the list in here the call site cannot get it wrong. Un-guarding
    /// the database password now means deleting the whole call, which is a
    /// line a reviewer sees.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> CredentialKeys(int vCenterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vCenterCount);

        return Enumerable
            .Range(0, vCenterCount)
            .Select(i => $"VCenters:{i}:Password")
            .Append("Database:Password");
    }

    /// <summary>
    /// Throws if any credential came from a settings file that travels with
    /// the application.
    /// </summary>
    /// <remarks>
    /// The overload to call. It decides for itself which keys hold secrets, so
    /// a caller cannot leave one out.
    /// </remarks>
    public static void EnsureNotFromFiles(
        IConfiguration configuration, int vCenterCount, string contentRoot) =>
        EnsureNotFromFiles(configuration, CredentialKeys(vCenterCount), contentRoot);

    /// <summary>
    /// Throws if any of <paramref name="keys"/> came from a settings file that
    /// travels with the application.
    /// </summary>
    /// <param name="configuration">The built configuration root.</param>
    /// <param name="keys">Fully qualified keys holding secrets.</param>
    /// <param name="contentRoot">
    /// The application's own directory. A file inside it is one that ends up in
    /// source control, in a backup and in the installer.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// If a secret came from such a file. The message names the key and the
    /// file, and never the value.
    /// </exception>
    public static void EnsureNotFromFiles(
        IConfiguration configuration, IEnumerable<string> keys, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(contentRoot);

        if (configuration is not IConfigurationRoot root)
        {
            // Cannot see the providers, so cannot make the guarantee. Saying so
            // is better than appearing to check.
            throw new InvalidOperationException(
                "Credential sources cannot be verified: the configuration is not a root.");
        }

        var offences = keys
            .SelectMany(key => FilesSupplying(root, key).Select(file => (Key: key, File: file)))
            .Where(x => TravelsWithTheApplication(x.File, contentRoot))
            .Select(x => $"  {x.Key} is in {x.File}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (offences.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A credential is sitting in a settings file:" + Environment.NewLine +
            string.Join(Environment.NewLine, offences) + Environment.NewLine +
            "Remove it from the file and supply it through user secrets, an environment " +
            "variable or a secret store. Then treat the value as compromised and rotate it: " +
            "it has been on disk in the clear, and may be in a backup or a commit.");
    }

    /// <summary>
    /// Whether a file is one that ships with the application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The risk is not that a credential lives in a file — a mounted secret and
    /// the user secrets store are both files, and both are deliberate. The risk
    /// is a file that <em>travels</em>: into source control, into a backup,
    /// into the installer. That is exactly what happened before, and it is what
    /// this refuses.
    /// </para>
    /// <para>
    /// So the test is location, not format: anything named
    /// <c>appsettings*.json</c>, and anything inside the application's own
    /// directory. The user secrets store sits under the user's profile,
    /// outside both — which is why ADR-0010 names it as one of the ways a
    /// credential is meant to arrive.
    /// </para>
    /// </remarks>
    private static bool TravelsWithTheApplication(string path, string contentRoot)
    {
        var name = Path.GetFileName(path);

        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (contentRoot.Length == 0)
        {
            // Nothing to compare against. Refusing is the safe reading: it is
            // better to stop than to wave through a file we cannot place.
            return true;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(contentRoot);

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every file that holds a value for a key, not merely the winning one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one, deliberately. The question this guard answers is what is
    /// written on disk, not which value the application ended up using, and
    /// being overridden by an environment variable does not un-write a file.
    /// A password in appsettings.json is in source control and in the backups
    /// whether or not anything reads it.
    /// </para>
    /// <para>
    /// An earlier version reported only the provider that supplied the bound
    /// value, which quietly waved through exactly the arrangement the previous
    /// product leaked from.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> FilesSupplying(IConfigurationRoot root, string key)
    {
        foreach (var provider in root.Providers)
        {
            if (provider is FileConfigurationProvider file &&
                provider.TryGet(key, out var value) &&
                !string.IsNullOrEmpty(value))
            {
                yield return Describe(file);
            }
        }
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
