using System.Globalization;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Extensions.Logging;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// G-SVC: makes the host runnable as a real Windows service instead of a
/// console process started over WMI, and gives it a log file that survives a
/// restart.
/// </summary>
/// <remarks>
/// <para>
/// The problem this answers is not "the product should be a service" in the
/// abstract — it is that a console process has no protection from
/// CTRL_CLOSE_EVENT (0xC000013A), and a sibling test PostgreSQL on this same
/// machine has been killed by exactly that signal twice. Once installed with
/// <c>sc create</c> (done once, by hand, in an elevated shell — never by this
/// code), the product itself stops being reachable that way too.
/// </para>
/// <para>
/// <see cref="Apply"/> is called on both <see cref="WebApplicationBuilder"/>
/// instances Program.cs builds — first-run setup mode and the normal host —
/// because each is its own builder and neither inherits the other's
/// configuration. Both effects are no-ops outside a real service session:
/// <c>UseWindowsService</c> only engages when
/// <c>WindowsServiceHelpers.IsWindowsService()</c> is true, and the file log
/// is added as one more <see cref="ILoggerProvider"/> on
/// <c>builder.Logging</c> -- never by replacing the logger factory the way
/// <c>Host.UseSerilog</c> does. Replacing it was tried first and rejected: it
/// forwards every message through Serilog's own renderer, which quotes scalar
/// string arguments, and that changed console output and broke a log-content
/// assertion in SetupModeTests that has nothing to do with G-SVC. Adding a
/// provider leaves the console logger, its formatting, and every other
/// registered provider exactly as they were; `dotnet run`, the exe run
/// directly, and <c>WebApplicationFactory</c> in tests all behave exactly as
/// before -- they simply also write a file nobody but an operator reads.
/// </para>
/// </remarks>
public static class HostServiceIntegration
{
    /// <summary>
    /// Configuration key for overriding where the rolling log file is
    /// written, e.g. <c>"Logging:File:Path": "D:\\logs\\host-.log"</c>.
    /// </summary>
    public const string PathSetting = "Logging:File:Path";

    /// <summary>Files kept before the oldest daily log is deleted.</summary>
    public const int RetainedFileCount = 14;

    public static void Apply(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseWindowsService();

        var logPath = LogFilePath(builder.Configuration);

        var fileLogger = new LoggerConfiguration()
            .WriteTo.File(
                logPath,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount)
            .CreateLogger();

        // One more provider, not a replacement -- see the remarks above.
        // dispose: true so the file (and its buffered writer) closes when
        // the host does, the same lifetime every other provider gets.
        builder.Logging.AddProvider(new SerilogLoggerProvider(fileLogger, dispose: true));
    }

    /// <summary>
    /// Where the rolling log file is written: <see cref="PathSetting"/> from
    /// configuration when set, else the file-sink's own name (<c>host-.log</c>,
    /// which Serilog turns into <c>host-20260101.log</c> per day) under the
    /// same ProgramData root the key ring uses; see ADR-0020. Material that
    /// has to survive an upgrade and a restart cannot sit where either one
    /// writes, which is why this is not beside the binary.
    /// </summary>
    public static string LogFilePath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[PathSetting];

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EnterpriseObservatory",
                "logs",
                "host-.log")
            : configured;
    }
}
