using System.Text;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.Extensions.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Where the database connection comes from (G-DB): the protected file, else
/// configuration, else setup mode — and what the file holds on disk.
/// </summary>
/// <remarks>
/// Against the real key ring (<c>KeyRing.Standalone</c>, DPAPI machine scope on
/// Windows) in a temporary directory, because the claim "the password is not
/// plaintext on disk" is only worth something about the mechanism that ships.
/// </remarks>
public sealed class DatabaseSourceTests : IDisposable
{
    // Not a real credential; it exists to be searched for in the file's bytes.
    private const string FilePassword = "file-sentinel-3b9d0c1e7a";
    private const string ConfigPassword = "config-sentinel-5f2e8a4c11";

    private readonly string _keyRing = SetupTestSupport.TempDirectory("eo-dbsource-keys-");

    public void Dispose() => SetupTestSupport.DeleteQuietly(_keyRing);

    private static IConfiguration Configuration(string? password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "config-host",
                ["Database:Password"] = password,
            })
            .Build();

    private static PostgresOptions FileOptions => new()
    {
        Host = "file-host",
        Port = 6543,
        Database = "observatory_file",
        Username = "observatory_file",
        Schema = "public",
        Password = Secret.From(FilePassword),
    };

    private DatabaseSource Resolve(IConfiguration configuration)
    {
        var (file, keyRing) = SetupTestSupport.OpenFile(_keyRing);

        using (keyRing)
        {
            return DatabaseSource.Resolve(file.Path, () => file, configuration);
        }
    }

    private void WriteFile()
    {
        var (file, keyRing) = SetupTestSupport.OpenFile(_keyRing);

        using (keyRing)
        {
            file.Write(FileOptions);
        }
    }

    [Fact]
    public void The_protected_file_wins_over_configuration()
    {
        WriteFile();

        var source = Resolve(Configuration(ConfigPassword));

        Assert.Equal(DatabaseSourceKind.ProtectedFile, source.Kind);
        Assert.Equal("file-host", source.Options!.Host);
        Assert.Equal(6543, source.Options.Port);
        Assert.Equal(FilePassword, source.Options.Password.Reveal());
    }

    [Fact]
    public void Configuration_is_used_when_there_is_no_file()
    {
        // The live server's path (user secrets), unchanged.
        var source = Resolve(Configuration(ConfigPassword));

        Assert.Equal(DatabaseSourceKind.Configuration, source.Kind);
        Assert.Equal("config-host", source.Options!.Host);
        Assert.Equal(ConfigPassword, source.Options.Password.Reveal());
        Assert.False(DatabaseSource.SetupRequired(DatabaseCredentialFile.PathFor(_keyRing), Configuration(ConfigPassword)));
    }

    [Fact]
    public void Setup_mode_when_there_is_neither_a_file_nor_a_configured_password()
    {
        var source = Resolve(Configuration(password: null));

        Assert.Equal(DatabaseSourceKind.Setup, source.Kind);
        Assert.Null(source.Options);
        Assert.True(DatabaseSource.SetupRequired(DatabaseCredentialFile.PathFor(_keyRing), Configuration(null)));
    }

    [Fact]
    public void A_file_is_a_source_even_when_configuration_has_no_password()
    {
        WriteFile();

        Assert.False(DatabaseSource.SetupRequired(DatabaseCredentialFile.PathFor(_keyRing), Configuration(null)));
        Assert.Equal(DatabaseSourceKind.ProtectedFile, Resolve(Configuration(null)).Kind);
    }

    [Fact]
    public void A_file_that_cannot_be_decrypted_stops_the_service_rather_than_falling_back_to_configuration()
    {
        WriteFile();

        // The file moved to a key ring that never protected it: a restore
        // without the keys beside it.
        var elsewhere = SetupTestSupport.TempDirectory("eo-dbsource-other-keys-");

        try
        {
            File.Copy(DatabaseCredentialFile.PathFor(_keyRing), DatabaseCredentialFile.PathFor(elsewhere));

            var (file, keyRing) = SetupTestSupport.OpenFile(elsewhere);

            using (keyRing)
            {
                var refused = Assert.Throws<InvalidOperationException>(() =>
                    DatabaseSource.Resolve(file.Path, () => file, Configuration(ConfigPassword)));

                Assert.Contains(file.Path, refused.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(FilePassword, refused.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            SetupTestSupport.DeleteQuietly(elsewhere);
        }
    }

    [Fact]
    public void The_password_in_the_protected_file_is_not_plaintext()
    {
        WriteFile();

        var path = DatabaseCredentialFile.PathFor(_keyRing);
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(FilePassword)) < 0, "The password is in the file as UTF-8.");
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(FilePassword)) < 0, "The password is in the file as UTF-16.");

        // Nor anywhere else in the key ring directory.
        Assert.Empty(SetupTestSupport.FilesContaining(_keyRing, FilePassword));

        // The positive control: what is in the file is the password, protected.
        var (file, keyRing) = SetupTestSupport.OpenFile(_keyRing);

        using (keyRing)
        {
            Assert.Equal(FilePassword, file.Read().Password.Reveal());
        }
    }

    [Fact]
    public void The_file_sits_inside_the_key_ring_directory_and_is_not_counted_as_a_key()
    {
        WriteFile();

        Assert.Equal(_keyRing, Path.GetDirectoryName(DatabaseCredentialFile.PathFor(_keyRing)));

        var location = KeyRingDurabilityGuard.Inspect(_keyRing);

        Assert.Equal(
            Directory.EnumerateFiles(_keyRing, "key-*.xml").Count(),
            location.KeyCount);
    }
}
