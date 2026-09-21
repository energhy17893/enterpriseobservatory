using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Host.AllInOne;
using EnterpriseObservatory.Host.AllInOne.Notifications;
using Microsoft.Extensions.Logging;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Text vCenter controls cannot forge a log line.
/// </summary>
/// <remarks>
/// Anyone who can name a VM can put a line break in an alert description. In a
/// plain-text log the rest of that name then reads as a new, genuine entry —
/// "Warning: operator admin disabled alerting", say. The log shows control
/// characters instead of obeying them; the stored alert keeps what vCenter
/// actually said.
/// </remarks>
public class LogInjectionTests
{
    private const string Forged =
        "VM web-01 powered off\r\n2026-09-21 12:00:00 info: Operator admin acknowledged every alert";

    [Fact]
    public void Line_breaks_and_other_controls_are_shown_not_obeyed()
    {
        Assert.Equal(
            "a" + Shown('r') + Shown('n') + "b" + U(0x1B) + "c" + U(0) + "d" + (char)9 + "e" + U(0x2028) + "f",
            LogText.Escape(
                "a" + (char)13 + (char)10 + "b" + (char)0x1B + "c" + (char)0 + "d" + (char)9 + "e" + (char)0x2028 + "f"));
    }

    /// <summary>The visible escape for a character, as the log shows it.</summary>
    private static string Shown(char letter) => "\\" + letter;

    private static string U(int code) =>
        "\\" + "u" + code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Ordinary_text_passes_through_unchanged()
    {
        const string text = "Datastore ds-01 (ünlü, 東京) is 92% full\tsince 12:00";

        Assert.Same(text, LogText.Escape(text));
        Assert.Equal(string.Empty, LogText.Escape(null));
    }

    [Fact]
    public async Task An_alert_description_cannot_start_a_new_log_line()
    {
        var logger = new CapturingLogger();
        var alert = Alert(title: "VM web-01\nreboot", description: Forged);

        await new LoggingAlertNotifier(logger).DispatchAsync([alert], CancellationToken.None);

        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains(@"powered off\r\n2026-09-21", line, StringComparison.Ordinal);
        Assert.Contains(@"VM web-01\nreboot", line, StringComparison.Ordinal);

        // Only the log is escaped. The stored alert is what vCenter said.
        Assert.Equal(Forged, alert.Description);
    }

    private static AlertInstance Alert(string title, string description) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", title, "Events", "vm-1"),
        Severity = AlertSeverity.Warning,
        State = AlertLifecycleState.Open,
        Title = title,
        Description = description,
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.Raised,
        FirstSeenUtc = DateTimeOffset.UnixEpoch,
        LastSeenUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class CapturingLogger : ILogger<LoggingAlertNotifier>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
