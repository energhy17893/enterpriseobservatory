using EnterpriseObservatory.Host.AllInOne.Configuration;
using Microsoft.Extensions.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The rule the previous product had and did not enforce.
/// </summary>
/// <remarks>
/// Its credentials were encrypted with DPAPI and one still leaked: a vCenter
/// password serialised inside a free-text JSON field the encryption did not
/// cover. Nothing was broken — the mechanism simply had a way around it, and
/// nobody was told when it was taken. So this is checked at startup rather than
/// written down.
/// </remarks>
public class CredentialSourceGuardTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"eo-guard-{Guid.NewGuid():n}.json");

    /// <summary>The fixture file sits here, so here is "inside the application".</summary>
    private static string ContentRoot => Path.GetTempPath();

    [Fact]
    public void A_password_in_a_settings_file_stops_the_service()
    {
        var configuration = FileWith("""{ "VCenters": [ { "Password": "hunter2" } ] }""");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"], ContentRoot));

        Assert.Contains("VCenters:0:Password", error.Message, StringComparison.Ordinal);
        Assert.Contains(_file, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_key_and_the_file_but_never_the_value()
    {
        // Reporting a leaked secret by printing it is how the transcript
        // becomes the next place it leaks.
        var configuration = FileWith("""{ "VCenters": [ { "Password": "hunter2" } ] }""");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"], ContentRoot));

        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_to_rotate_the_password_as_well_as_to_move_it()
    {
        // Moving it out of the file does not un-write the backups, the commits
        // and the copies. The value is spent.
        var configuration = FileWith("""{ "VCenters": [ { "Password": "hunter2" } ] }""");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"], ContentRoot));

        Assert.Contains("rotate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_password_from_the_environment_is_accepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VCenters:0:Password"] = "hunter2",
            })
            .Build();

        CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"], ContentRoot);
    }

    [Fact]
    public void A_password_in_a_settings_file_is_refused_even_when_something_overrides_it()
    {
        // Being overridden does not un-write the file. The value is in source
        // control and in the backups whether or not anything reads it, and an
        // earlier version of this guard waved it through — which is exactly the
        // arrangement the previous product leaked from.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Write("""{ "VCenters": [ { "Password": "stale" } ] }"""))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VCenters:0:Password"] = "hunter2",
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(
                configuration, ["VCenters:0:Password"], ContentRoot));

        Assert.Contains(_file, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_nobody_supplied_is_not_an_offence()
    {
        // Missing configuration is validation's problem, not this one's.
        var configuration = new ConfigurationBuilder().Build();

        CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"], ContentRoot);
    }

    [Fact]
    public void The_user_secrets_store_is_accepted()
    {
        // ADR-0010 names user secrets as one of the ways a credential is meant
        // to arrive, and the README tells people to use it. An earlier version
        // of this guard rejected it — a file is a file — which closed the door
        // the product holds open. The risk is not that a credential lives in a
        // file; it is a file that travels into source control, a backup or an
        // installer.
        var elsewhere = Path.Combine(
            Path.GetTempPath(), $"eo-secrets-{Guid.NewGuid():n}", "secrets.json");

        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, """{ "VCenters": [ { "Password": "hunter2" } ] }""");

        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(elsewhere).Build();

            CredentialSourceGuard.EnsureNotFromFiles(
                configuration,
                ["VCenters:0:Password"],
                // The application lives somewhere else entirely.
                Path.Combine(Path.GetTempPath(), "eo-app"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(elsewhere)!, recursive: true);
        }
    }

    [Fact]
    public void A_file_named_appsettings_is_refused_wherever_it_sits()
    {
        // The name is enough. An appsettings.json is a file that ships, whether
        // or not it happens to be beside the binary today.
        var directory = Path.Combine(Path.GetTempPath(), $"eo-app-{Guid.NewGuid():n}");
        var settings = Path.Combine(directory, "appsettings.Production.json");

        Directory.CreateDirectory(directory);
        File.WriteAllText(settings, """{ "VCenters": [ { "Password": "hunter2" } ] }""");

        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(settings).Build();

            Assert.Throws<InvalidOperationException>(() =>
                CredentialSourceGuard.EnsureNotFromFiles(
                    configuration, ["VCenters:0:Password"], Path.Combine(Path.GetTempPath(), "nowhere")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_configuration_whose_providers_cannot_be_seen_is_refused()
    {
        // Appearing to check is worse than not checking.
        var configuration = new ConfigurationBuilder().Build().GetSection("VCenters");

        Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["0:Password"], ContentRoot));
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }

        GC.SuppressFinalize(this);
    }

    private IConfiguration FileWith(string json) =>
        new ConfigurationBuilder().AddJsonFile(Write(json)).Build();

    private string Write(string json)
    {
        File.WriteAllText(_file, json);
        return _file;
    }
}
