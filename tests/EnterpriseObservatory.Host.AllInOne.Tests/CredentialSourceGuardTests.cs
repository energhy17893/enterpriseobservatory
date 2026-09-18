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

    [Fact]
    public void A_password_in_a_settings_file_stops_the_service()
    {
        var configuration = FileWith("""{ "VCenters": [ { "Password": "hunter2" } ] }""");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]));

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
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]));

        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_to_rotate_the_password_as_well_as_to_move_it()
    {
        // Moving it out of the file does not un-write the backups, the commits
        // and the copies. The value is spent.
        var configuration = FileWith("""{ "VCenters": [ { "Password": "hunter2" } ] }""");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]));

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

        CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]);
    }

    [Fact]
    public void An_environment_variable_overriding_the_file_is_accepted()
    {
        // A password left in appsettings.json but overridden elsewhere is still
        // a password on disk — but it is a different and lesser problem, and
        // the value actually in use is the one this guard is about.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Write("""{ "VCenters": [ { "Password": "stale" } ] }"""))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VCenters:0:Password"] = "hunter2",
            })
            .Build();

        CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]);
    }

    [Fact]
    public void A_key_nobody_supplied_is_not_an_offence()
    {
        // Missing configuration is validation's problem, not this one's.
        var configuration = new ConfigurationBuilder().Build();

        CredentialSourceGuard.EnsureNotFromFiles(configuration, ["VCenters:0:Password"]);
    }

    [Fact]
    public void A_configuration_whose_providers_cannot_be_seen_is_refused()
    {
        // Appearing to check is worse than not checking.
        var configuration = new ConfigurationBuilder().Build().GetSection("VCenters");

        Assert.Throws<InvalidOperationException>(() =>
            CredentialSourceGuard.EnsureNotFromFiles(configuration, ["0:Password"]));
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
