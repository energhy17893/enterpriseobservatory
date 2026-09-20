namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// How much the key ring's directory can be trusted to still be there tomorrow.
/// </summary>
public enum KeyRingRisk
{
    /// <summary>
    /// A directory nothing deletes on its own. Silent: this is the normal case
    /// and a check that speaks on a correct installation teaches operators to
    /// stop reading it.
    /// </summary>
    Durable,

    /// <summary>
    /// A directory that something else on this machine is entitled to empty —
    /// Disk Cleanup, Storage Sense, a maintenance script, the automatic sweep
    /// that runs when the volume fills. The keys are readable today, so the
    /// stored passwords still work today. That is what makes this recoverable.
    /// </summary>
    Losable,

    /// <summary>
    /// The directory does not exist and cannot be created. Nothing can be
    /// encrypted or decrypted, so nothing about the product works.
    /// </summary>
    Unusable,
}

/// <summary>
/// What was found where the key ring is configured to live.
/// </summary>
/// <param name="Path">The directory. Never any of its contents.</param>
/// <param name="Risk">The verdict on the location.</param>
/// <param name="Reason">
/// Why, in the words an operator gets. Empty when <see cref="Risk"/> is
/// <see cref="KeyRingRisk.Durable"/>, because there is nothing to say.
/// </param>
/// <param name="KeyCount">
/// How many key files were there before Data Protection was given a chance to
/// generate one. A count, deliberately: the number of keys is diagnostic and
/// their contents are the thing being protected. Negative when the directory
/// could not be read, which is not the same fact as empty and must not be
/// rounded into it.
/// </param>
public sealed record KeyRingLocation(string Path, KeyRingRisk Risk, string Reason, int KeyCount);

/// <summary>
/// Refuses to be quiet about a key ring that is going to be deleted.
/// </summary>
/// <remarks>
/// <para>
/// The key ring decrypts every vCenter password an operator typed into the
/// product (ADR-0015). It was configured to live under <c>%TEMP%</c>, and
/// %TEMP% is a directory Windows itself empties. It was emptied. One connection
/// could no longer be read, and the product said so — correctly, and hours
/// after the credential was already unrecoverable.
/// </para>
/// <para>
/// So the check moved to the front. Where the key ring is going to live is
/// knowable before a single metric is collected, and a losable location is
/// still fully recoverable at that moment: the keys are intact, the passwords
/// decrypt, and moving the directory costs one setting and one restart.
/// </para>
/// <para>
/// This is the sibling of <see cref="CredentialSourceGuard"/> and deliberately
/// does not copy it. That guard refuses to start, because a password already
/// sitting in a settings file is not made better by anything the running
/// product could do, and the fix — user secrets, an environment variable —
/// happens outside the product with the service stopped. Here the opposite
/// holds: the fix for a wiped key ring is an administrator signing in and
/// re-entering the passwords, which a product that will not boot makes
/// impossible. Refusing would turn a warning into the outage it was warning
/// about. See ADR-0020.
/// </para>
/// </remarks>
public static class KeyRingDurabilityGuard
{
    /// <summary>
    /// Path segments that name a directory as scratch space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched as whole segments, never as substrings. <c>Temp</c> inside
    /// <c>C:\ProgramData\Contemporary\keys</c> is five letters of a word, and
    /// firing on it would be exactly the false positive principle 4 forbids —
    /// an operator who is warned about a directory that is fine learns to
    /// dismiss the warning about the one that is not.
    /// </para>
    /// <para>
    /// This list rather than the environment is the primary rule, because the
    /// environment only describes <em>this</em> process. A service running as
    /// LocalSystem resolves a different <c>%TEMP%</c> from the installer that
    /// configured the path, and <c>D:\Temp</c> is pointed at by no variable at
    /// all while being the first thing any cleanup script is aimed at.
    /// </para>
    /// </remarks>
    private static readonly string[] ScratchSegments = ["temp", "tmp", "temporary"];

    /// <summary>
    /// Looks at where the key ring is configured to live, and creates it if it
    /// is not there.
    /// </summary>
    /// <param name="path">The configured or defaulted key ring directory.</param>
    /// <remarks>
    /// Creating is part of inspecting rather than a separate step, because
    /// "cannot be created" is one of the answers and there is no way to learn
    /// it except by trying. The call is the one <c>Program.cs</c> used to make
    /// on its own; it now reports what happened instead of discarding it.
    /// </remarks>
    public static KeyRingLocation Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full;

        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path at all. The same consequence as one that cannot be
            // created, and reported as such rather than thrown from here, so
            // that every unusable case leaves through one door.
            return new KeyRingLocation(
                path,
                KeyRingRisk.Unusable,
                "it is not a usable file system path",
                0);
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new KeyRingLocation(
                full,
                KeyRingRisk.Unusable,
                "the directory does not exist and could not be created: " + e.Message,
                0);
        }

        var keys = CountKeys(full);
        var scratch = ScratchRootContaining(full);

        return scratch is null
            ? new KeyRingLocation(full, KeyRingRisk.Durable, string.Empty, keys)
            : new KeyRingLocation(
                full,
                KeyRingRisk.Losable,
                $"it is inside {scratch}, which Windows Disk Cleanup, Storage Sense and the " +
                "automatic sweep that runs when the volume fills are all entitled to empty",
                keys);
    }

    /// <summary>
    /// Stops the service when the key ring has nowhere to live.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If <paramref name="location"/> is <see cref="KeyRingRisk.Unusable"/>.
    /// </exception>
    /// <remarks>
    /// The one case that does refuse, and it refuses for the same reason the
    /// database does a few lines above it in <c>Program.cs</c>: a key ring that
    /// cannot be written is not a degraded product, it is a product that cannot
    /// store a credential at all. Starting would produce a service that accepts
    /// a password on a form and loses it on the next restart, which is worse
    /// than not starting because it looks like it worked.
    /// </remarks>
    public static void EnsureUsable(KeyRingLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (location.Risk is not KeyRingRisk.Unusable)
        {
            return;
        }

        throw new InvalidOperationException(
            "The Data Protection key ring has nowhere to live:" + Environment.NewLine +
            "  " + location.Path + Environment.NewLine +
            "  " + location.Reason + Environment.NewLine +
            "Every vCenter password entered in the product is encrypted with this key ring, " +
            "so without it no credential can be stored or read. Point Storage:KeyRingPath at a " +
            "durable directory the service account can write — not a temporary one — and back " +
            "it up separately from the database. See ADR-0015 and ADR-0020.");
    }

    /// <summary>
    /// Whether the key ring that is present can still decrypt what is stored.
    /// </summary>
    /// <param name="location">What <see cref="Inspect"/> found.</param>
    /// <param name="storedConnectionCount">
    /// How many connections were entered in the product and are sitting in the
    /// database encrypted. Connections that came from configuration do not
    /// count: their passwords never went through the key ring.
    /// </param>
    /// <returns>
    /// True when the stored passwords are already unrecoverable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the case that is past saving, and it is separable from the
    /// location check because it is a different sentence to an operator. "Your
    /// keys are somewhere that gets deleted" means move them today. "Your keys
    /// were deleted" means the passwords are gone and re-entering them is the
    /// only fix — there is no restore, no repair, and no amount of restarting
    /// that helps.
    /// </para>
    /// <para>
    /// It is detectable at all because of ADR-0015: a stored connection can
    /// only exist if the key ring encrypted its password. So a key ring holding
    /// nothing, beside a database holding connections, is two facts that cannot
    /// both be original. A genuinely new installation has no stored connections
    /// yet — it cannot, nobody has signed in — so it takes this path silently,
    /// and that is the whole distinction between "new install" and "wiped".
    /// </para>
    /// <para>
    /// It cannot tell a wiped directory from one whose path was just changed to
    /// a fresh location. That is not a gap: the consequence is identical — the
    /// keys those passwords were encrypted with are not the keys that are here
    /// — and so is the remedy.
    /// </para>
    /// </remarks>
    public static bool CredentialsAreUnrecoverable(KeyRingLocation location, int storedConnectionCount)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegative(storedConnectionCount);

        return location.KeyCount == 0 && storedConnectionCount > 0;
    }

    /// <summary>
    /// The scratch directory a path sits inside, or null.
    /// </summary>
    /// <remarks>
    /// Returns the offending prefix rather than a bare true, because a message
    /// that names <c>C:\Users\svc\AppData\Local\Temp</c> tells an operator what
    /// to go and change, and one that says "a temporary directory" makes them
    /// go and find out which part we meant.
    /// </remarks>
    private static string? ScratchRootContaining(string full)
    {
        var segments = full.Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (!ScratchSegments.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Rebuilt from the original string so the answer reads like the
            // path the operator configured, root and separators included.
            var end = IndexAfterSegment(full, segments, i);
            return full[..end];
        }

        // The environment as a second opinion, for a scratch directory that is
        // not named after one. %TEMP% can be redirected anywhere by policy, and
        // a directory called C:\Scratch is emptied just as thoroughly for not
        // advertising it.
        foreach (var root in EnvironmentScratchRoots())
        {
            if (IsInside(full, root))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>
    /// Where in the original string the <paramref name="index"/>'th segment ends.
    /// </summary>
    private static int IndexAfterSegment(string full, string[] segments, int index)
    {
        var at = 0;

        for (var i = 0; i <= index; i++)
        {
            at = full.IndexOf(segments[i], at, StringComparison.Ordinal) + segments[i].Length;
        }

        return at;
    }

    /// <summary>
    /// Scratch directories this process can name from its environment.
    /// </summary>
    private static IEnumerable<string> EnvironmentScratchRoots()
    {
        yield return System.IO.Path.GetTempPath();

        string[] variables = ["TEMP", "TMP"];

        foreach (var variable in variables)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="full"/> is <paramref name="root"/> or beneath it.
    /// </summary>
    /// <remarks>
    /// Compared segment-wise after normalisation, so that <c>C:\Temp2</c> is not
    /// read as being inside <c>C:\Temp</c> — the prefix match that a plain
    /// StartsWith would make, and a false positive that costs this check its
    /// credibility.
    /// </remarks>
    private static bool IsInside(string full, string root)
    {
        string normalised;

        try
        {
            normalised = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var candidate = System.IO.Path.TrimEndingDirectorySeparator(full);

        if (candidate.Equals(normalised, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(normalised, StringComparison.OrdinalIgnoreCase) &&
               candidate.Length > normalised.Length &&
               (candidate[normalised.Length] == System.IO.Path.DirectorySeparatorChar ||
                candidate[normalised.Length] == System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// How many key files are present, counted and never opened.
    /// </summary>
    private static int CountKeys(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "key-*.xml", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as empty, and guessing zero here would
            // announce that credentials are lost on the strength of a
            // permissions problem. Reported as "not empty" so the louder of the
            // two messages stays for evidence that actually shows a wipe.
            return -1;
        }
    }
}
