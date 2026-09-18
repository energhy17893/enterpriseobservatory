using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne.Notifications;

/// <summary>
/// Writes notifications to the log.
/// </summary>
/// <remarks>
/// <para>
/// The whole dispatcher, for now. It is here because the cycle must have
/// somewhere to hand a notification to — a notification that is computed and
/// then dropped is worse than none, since the alert is marked notified either
/// way and never notifies again.
/// </para>
/// <para>
/// The routing decision is the point of <see cref="AlertNotificationKind"/> and
/// is preserved even in this trivial implementation: "it got worse" and "it got
/// better" are logged at different levels, because sending both to the same
/// place at the same urgency is how notification fatigue starts.
/// </para>
/// </remarks>
public sealed class LoggingAlertNotifier(ILogger<LoggingAlertNotifier> logger) : IAlertNotifier
{
    private readonly ILogger<LoggingAlertNotifier> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        foreach (var alert in pending)
        {
            var level = LevelFor(alert.PendingNotification);

            HostLog.AlertNotification(
                _logger,
                level,
                alert.PendingNotification,
                alert.Severity,
                alert.Title,
                alert.Description);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// How loudly a notification is delivered.
    /// </summary>
    /// <remarks>
    /// "It got better" is worth recording and not worth waking anyone for. A
    /// real dispatcher will make a bigger version of this same decision — which
    /// is why the cycle reports the kind rather than deciding the channel.
    /// </remarks>
    private static LogLevel LevelFor(AlertNotificationKind kind) => kind switch
    {
        AlertNotificationKind.Escalated => LogLevel.Error,
        AlertNotificationKind.Raised => LogLevel.Warning,
        AlertNotificationKind.Returned => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}
