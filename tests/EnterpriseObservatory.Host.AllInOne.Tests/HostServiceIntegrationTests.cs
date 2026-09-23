using EnterpriseObservatory.Host.AllInOne.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// G-SVC: the file-log path Program.cs resolves, and that the integration is
/// harmless everywhere it is not a real Windows service.
/// </summary>
public sealed class HostServiceIntegrationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Fact]
    public void With_no_override_the_log_path_is_under_the_key_rings_own_ProgramData_root()
    {
        // ADR-0020 put the key ring at C:/ProgramData/EnterpriseObservatory/keys
        // precisely because it must survive an upgrade and a restart; the log
        // file needs the same guarantee, so it lives beside it rather than
        // wherever the binary happens to be installed.
        var path = HostServiceIntegration.LogFilePath(Configuration());

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EnterpriseObservatory", "logs");

        Assert.StartsWith(expectedRoot, path, StringComparison.Ordinal);
        Assert.EndsWith("host-.log", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Logging_File_Path_overrides_the_default()
    {
        var configured = Path.Combine(Path.GetTempPath(), $"eo-log-override-{Guid.NewGuid():n}", "host-.log");

        var path = HostServiceIntegration.LogFilePath(
            Configuration((HostServiceIntegration.PathSetting, configured)));

        Assert.Equal(configured, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_falls_back_to_the_default(string blank)
    {
        var path = HostServiceIntegration.LogFilePath(
            Configuration((HostServiceIntegration.PathSetting, blank)));

        Assert.EndsWith(
            Path.Combine("EnterpriseObservatory", "logs", "host-.log"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_does_not_engage_the_Windows_service_lifetime_outside_a_real_service()
    {
        // The one guarantee dev and CI depend on: this process was started by
        // `dotnet test`, not the Service Control Manager, so UseWindowsService
        // must stay a no-op. If this ever returned true here, every host built
        // by WebApplicationFactory in this suite would try to behave like a
        // service instead of a test host.
        Assert.False(WindowsServiceHelpers.IsWindowsService());
    }

    [Fact]
    public void Apply_adds_the_file_log_as_one_more_provider_without_touching_existing_ones()
    {
        var builder = WebApplication.CreateBuilder();
        var logPath = Path.Combine(Path.GetTempPath(), $"eo-log-apply-{Guid.NewGuid():n}", "host-.log");
        builder.Configuration[HostServiceIntegration.PathSetting] = logPath;

        var before = builder.Services.Count(d => d.ServiceType == typeof(ILoggerProvider));

        HostServiceIntegration.Apply(builder);

        var after = builder.Services.Count(d => d.ServiceType == typeof(ILoggerProvider));

        // Exactly one more registration. A regression that swaps in
        // Host.UseSerilog's own ILoggerFactory instead would not show up as a
        // missing provider here -- it showed up as SetupModeTests' log-content
        // assertion failing, because Serilog's own renderer quotes scalar
        // string arguments before handing the message to every other
        // provider. Kept as history: that is why this class exists rather
        // than a one-line smoke check.
        Assert.Equal(before + 1, after);

        try
        {
            using var app = builder.Build();
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(logPath)))
            {
                Directory.Delete(Path.GetDirectoryName(logPath)!, recursive: true);
            }
        }
    }
}
