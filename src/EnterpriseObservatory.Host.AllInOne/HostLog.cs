namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Every log message the host emits.
/// </summary>
/// <remarks>
/// Source-generated rather than written inline: the arguments are not evaluated
/// when the level is disabled, which matters because the metric cycle logs on
/// every tick. Collected in one file so the product's operational vocabulary
/// can be read in one place rather than reconstructed from call sites.
/// </remarks>
internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "No collectors are configured. Nothing is being monitored; everything " +
                  "will report as Unknown rather than healthy.")]
    public static partial void NoCollectorsConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "vCenter {InstanceId} at {Address} as {Username}.")]
    public static partial void VsphereConfigured(
        ILogger logger, string instanceId, Uri address, string username);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "vCenter {InstanceId} is configured to accept an untrusted certificate. " +
                  "Traffic is encrypted but the server is not authenticated.")]
    public static partial void UntrustedCertificateAccepted(ILogger logger, string instanceId);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "Inventory cycle: {Active} active, {Vanished} vanished, {Visible} alerts visible.")]
    public static partial void InventoryCycle(ILogger logger, int active, int vanished, int visible);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Debug,
        Message = "Observation cycle: {Samples} samples, {Visible} alerts visible.")]
    public static partial void ObservationCycle(ILogger logger, int samples, int visible);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "{Count} source(s) did not answer: {Sources}. Everything they cover is Unknown.")]
    public static partial void SourcesSilent(ILogger logger, int count, string sources);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Error,
        Message = "The {Cycle} cycle failed; continuing.")]
    public static partial void CycleFailed(ILogger logger, Exception exception, string cycle);

    [LoggerMessage(
        EventId = 1020,
        Message = "[{Kind}] {Severity} {Title}: {Description}")]
    public static partial void AlertNotification(
        ILogger logger,
        LogLevel level,
        Domain.Alerts.AlertNotificationKind kind,
        Domain.Alerts.AlertSeverity severity,
        string title,
        string description);
}
