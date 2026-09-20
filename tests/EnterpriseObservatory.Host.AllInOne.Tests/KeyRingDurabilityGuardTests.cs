using EnterpriseObservatory.Host.AllInOne.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The outage this product had on 20 September 2026, checked for rather than
/// written down.
/// </summary>
/// <remarks>
/// The Data Protection key ring — the only thing that decrypts every vCenter
/// password an operator typed into the product — was configured under %TEMP%.
/// Windows emptied it. One connection could no longer be read and its password
/// was gone for good: it is not in a database backup, because the database
/// never held the key. Every test here is about being told before that happens
/// rather than after.
/// </remarks>
public sealed class KeyRingDurabilityGuardTests : IDisposable
{
    private readonly List<string> _made = [];

    public void Dispose()
    {
        foreach (var directory in _made)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover fixture directory is not worth failing a run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // --- the location that already cost us a credential ----------------------

    [Fact]
    public void A_key_ring_under_the_machines_temp_directory_is_reported_as_losable()
    {
        // This is the exact setting that was live this afternoon. If this
        // returns Durable the product starts silently on an installation whose
        // every stored vCenter password is one Disk Cleanup away from being
        // permanently unrecoverable, which is the outage we already had.
        var path = Fixture(Path.Combine(Path.GetTempPath(), $"eo-live-{Guid.NewGuid():n}", "keys"));

        Assert.Equal(KeyRingRisk.Losable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    [Fact]
    public void A_directory_with_a_Temp_segment_is_losable_even_though_no_variable_points_at_it()
    {
        // D:\Temp is pointed at by no environment variable and is the first
        // thing every maintenance script is aimed at. Relying on %TEMP% alone
        // would clear this path, and an installer that puts the keys in a
        // hand-made D:\Temp\eo gets no warning at all.
        var path = Fixture(Path.Combine(Root(), "Temp", "eo", "keys"));

        Assert.Equal(KeyRingRisk.Losable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    [Theory]
    [InlineData("tmp")]
    [InlineData("TEMP")]
    [InlineData("Temporary")]
    public void Every_spelling_of_a_scratch_directory_is_losable(string segment)
    {
        // Case and spelling are the installer's choice, not ours. Matching only
        // "Temp" would let an installation that writes its keys to \tmp\ pass
        // the check while being emptied by precisely the same sweep.
        var path = Fixture(Path.Combine(Root(), $"eo-{Guid.NewGuid():n}", segment, "keys"));

        Assert.Equal(KeyRingRisk.Losable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    [Fact]
    public void The_reason_names_the_scratch_directory_rather_than_describing_one()
    {
        // An operator reading this has to know which part of the path to
        // change. "A temporary directory" sends them to work out what we meant,
        // and the fix is a five-minute job that must not need a support call.
        var scratch = Path.Combine(Root(), $"eo-{Guid.NewGuid():n}", "Temp");
        var path = Fixture(Path.Combine(scratch, "keys"));

        Assert.Contains(scratch, KeyRingDurabilityGuard.Inspect(path).Reason, StringComparison.Ordinal);
    }

    // --- and the installations that must hear nothing ------------------------

    [Fact]
    public void The_products_own_ProgramData_default_is_silent()
    {
        // The default the code already ships, and the path the live
        // installation was moved to this afternoon. A check that fires on the
        // correct configuration is a check operators learn to dismiss, and it
        // will be dismissed on the day it is right.
        var path = Fixture(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            $"EnterpriseObservatoryTest-{Guid.NewGuid():n}",
            "keys"));

        var location = KeyRingDurabilityGuard.Inspect(path);

        Assert.Equal(KeyRingRisk.Durable, location.Risk);
        Assert.Empty(location.Reason);
    }

    [Fact]
    public void A_durable_path_an_operator_chose_for_themselves_is_silent()
    {
        // ADR-0015 requires the key ring to be backed up separately from the
        // database, so operators will put it on a volume of their own choosing.
        // Insisting on ProgramData would fire on every one of them and make the
        // separate-backup rule harder to follow than ignoring us.
        var path = Fixture(Path.Combine(Root(), $"EOKeys-{Guid.NewGuid():n}"));

        Assert.Equal(KeyRingRisk.Durable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    [Fact]
    public void A_directory_whose_name_merely_contains_the_letters_temp_is_silent()
    {
        // Contemporary, Template, Temperature. A substring match would accuse
        // all three, and one false accusation is enough to teach an operator
        // that this message does not mean anything.
        var path = Fixture(Path.Combine(Root(), $"Contemporary-{Guid.NewGuid():n}", "Templates", "keys"));

        Assert.Equal(KeyRingRisk.Durable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    [Fact]
    public void A_sibling_of_the_temp_directory_is_not_treated_as_being_inside_it()
    {
        // C:\Temp2 starts with C:\Temp as text and is a different directory that
        // nothing sweeps. Comparing paths as strings rather than as segments is
        // the classic way this check acquires a false positive.
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var path = Fixture(root + $"2-eo-{Guid.NewGuid():n}");

        Assert.Equal(KeyRingRisk.Durable, KeyRingDurabilityGuard.Inspect(path).Risk);
    }

    // --- a directory that is not there yet -----------------------------------

    [Fact]
    public void A_durable_directory_that_does_not_exist_yet_is_created_and_stays_silent()
    {
        // The first start of a fresh installation. If this reported a problem,
        // every new install would begin with a Critical line about its key
        // ring, which is the fastest possible way to make the message worthless.
        var path = Path.Combine(Root(), $"EOKeys-{Guid.NewGuid():n}", "keys");
        _made.Add(Path.GetDirectoryName(path)!);

        Assert.False(Directory.Exists(path));

        var location = KeyRingDurabilityGuard.Inspect(path);

        Assert.Equal(KeyRingRisk.Durable, location.Risk);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void A_directory_that_cannot_be_created_stops_the_service()
    {
        // The one refusal. A key ring with nowhere to live means a form that
        // accepts a vCenter password and silently discards it on the next
        // restart -- worse than not starting, because it looks like it worked.
        var file = Path.Combine(Path.GetTempPath(), $"eo-blocker-{Guid.NewGuid():n}");
        File.WriteAllText(file, "not a directory");
        _made.Add(file);

        var location = KeyRingDurabilityGuard.Inspect(Path.Combine(file, "keys"));

        Assert.Equal(KeyRingRisk.Unusable, location.Risk);
        Assert.Throws<InvalidOperationException>(() => KeyRingDurabilityGuard.EnsureUsable(location));
    }

    [Fact]
    public void A_losable_key_ring_starts_the_service_rather_than_stopping_it()
    {
        // Deliberately not CredentialSourceGuard's answer. The keys are still
        // readable at this moment, so the estate is still monitorable and the
        // fix -- a setting and a restart -- is not helped by being down. A
        // monitoring product that refuses to boot monitors nothing.
        var location = KeyRingDurabilityGuard.Inspect(
            Fixture(Path.Combine(Path.GetTempPath(), $"eo-live-{Guid.NewGuid():n}", "keys")));

        Assert.Equal(KeyRingRisk.Losable, location.Risk);

        KeyRingDurabilityGuard.EnsureUsable(location);
    }

    [Fact]
    public void The_refusal_says_where_to_put_the_key_ring_instead()
    {
        // Somebody is reading this at 3am with a service that will not start.
        // A message that says only "no" leaves them guessing at a setting name.
        var file = Path.Combine(Path.GetTempPath(), $"eo-blocker-{Guid.NewGuid():n}");
        File.WriteAllText(file, "not a directory");
        _made.Add(file);

        var error = Assert.Throws<InvalidOperationException>(() =>
            KeyRingDurabilityGuard.EnsureUsable(
                KeyRingDurabilityGuard.Inspect(Path.Combine(file, "keys"))));

        Assert.Contains("Storage:KeyRingPath", error.Message, StringComparison.Ordinal);
    }

    // --- the case that is already past saving --------------------------------

    [Fact]
    public void An_empty_key_ring_beside_stored_connections_means_the_passwords_are_gone()
    {
        // This afternoon, exactly: the folder had been recreated that day and a
        // configured vCenter could not be read. Those two facts cannot both be
        // original -- a stored connection only exists because the key ring
        // encrypted it (ADR-0015) -- so an empty key ring beside one is the
        // evidence that something deleted the keys.
        var location = Empty(keys: 0);

        Assert.True(KeyRingDurabilityGuard.CredentialsAreUnrecoverable(location, storedConnectionCount: 1));
    }

    [Fact]
    public void A_brand_new_installation_with_no_keys_and_no_connections_is_silent()
    {
        // This is what distinguishes a wipe from a first start, and it is the
        // whole reason the connection count is in the question. Without it the
        // very first boot of every installation announces that its credentials
        // have been lost, and nobody believes the message again.
        var location = Empty(keys: 0);

        Assert.False(KeyRingDurabilityGuard.CredentialsAreUnrecoverable(location, storedConnectionCount: 0));
    }

    [Fact]
    public void A_key_ring_that_still_has_its_keys_is_silent_however_many_connections_there_are()
    {
        // A healthy installation has both. Reporting lost credentials here
        // would send an administrator to re-type passwords that decrypt
        // perfectly well, and re-typing a password is not free -- it is a
        // change window and a vCenter account somebody can lock out.
        var location = Empty(keys: 3);

        Assert.False(KeyRingDurabilityGuard.CredentialsAreUnrecoverable(location, storedConnectionCount: 7));
    }

    [Fact]
    public void A_key_ring_directory_that_could_not_be_read_is_not_reported_as_wiped()
    {
        // Unreadable is not empty. A permissions problem on the folder would
        // otherwise produce "your credentials are permanently lost", which is
        // both wrong and the single most alarming thing this product can say.
        var location = Empty(keys: -1);

        Assert.False(KeyRingDurabilityGuard.CredentialsAreUnrecoverable(location, storedConnectionCount: 4));
    }

    [Fact]
    public void Keys_present_on_disk_are_counted_and_never_opened()
    {
        // The count is what the Critical line prints, so it has to be real: an
        // operator told to move "0 key file(s)" out of Temp when there are two
        // leaves one behind, and the one left behind is the one that decrypts
        // the password.
        var directory = Fixture(Path.Combine(Root(), $"EOKeys-{Guid.NewGuid():n}"));
        File.WriteAllText(Path.Combine(directory, "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(directory, "key-2.xml"), "<key/>");
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "not a key");

        Assert.Equal(2, KeyRingDurabilityGuard.Inspect(directory).KeyCount);
    }

    [Fact]
    public void Nothing_the_guard_reports_contains_anything_out_of_a_key_file()
    {
        // The guard reads a directory full of key material. A message that
        // quoted any of it would make the log the next place the key leaks,
        // which is the previous product's defect with the roles reversed.
        var directory = Fixture(Path.Combine(Path.GetTempPath(), $"eo-live-{Guid.NewGuid():n}"));
        File.WriteAllText(Path.Combine(directory, "key-1.xml"), "<key>S3cretKeyMaterial</key>");

        var location = KeyRingDurabilityGuard.Inspect(directory);

        Assert.DoesNotContain("S3cretKeyMaterial", location.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cretKeyMaterial", location.Path, StringComparison.Ordinal);
    }

    // --- fixtures ------------------------------------------------------------

    /// <summary>A directory the test owns, created and cleaned up.</summary>
    private string Fixture(string path)
    {
        Directory.CreateDirectory(path);
        _made.Add(path);
        return path;
    }

    /// <summary>
    /// A location with a chosen key count and nothing else that matters.
    /// </summary>
    private static KeyRingLocation Empty(int keys) =>
        new(Path.Combine(Root(), "keys"), KeyRingRisk.Durable, string.Empty, keys);

    /// <summary>
    /// A durable root to hang fixtures off.
    /// </summary>
    /// <remarks>
    /// Deliberately not the temp directory — most of these fixtures exist to
    /// prove the temp directory is detected, so building them there would make
    /// every one of them pass for the wrong reason. The test binary's own
    /// directory is durable, writable without elevation, and has no scratch
    /// segment in its path.
    /// </remarks>
    private static string Root() => AppContext.BaseDirectory;
}
