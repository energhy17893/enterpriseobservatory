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
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "This installation has no accounts yet and nothing can be seen or changed until " +
                  "it does. Create the first administrator with this one-time setup token: " +
                  "{Token}. It is valid only until an account exists, and restarting the service " +
                  "issues a new one.")]
    public static partial void SetupTokenIssued(ILogger logger, string token);

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
        EventId = 1014,
        Level = LogLevel.Debug,
        Message = "Compaction: {Written} buckets written, {SamplesDeleted} samples and " +
                  "{BucketsDeleted} buckets aged out.")]
    public static partial void Compacted(
        ILogger logger, int written, int samplesDeleted, int bucketsDeleted);

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Error,
        Message = "Compaction failed; the next pass will pick it up.")]
    public static partial void CompactionFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Warning,
        Message = "Samples could not be recorded: {Detail}. Collection continues; the history will " +
                  "have a gap, which is how this product says it was not looking.")]
    public static partial void StorageFailed(ILogger logger, string detail);

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

    /// <summary>A connection exists but is not being read, and why.</summary>
    /// <remarks>
    /// <para>
    /// Said out loud because the alternative is silence that looks like health.
    /// A connection sitting in the list with nothing coming from it produces no
    /// alert — there is no collector to fail — so without this line the screen
    /// shows a vCenter and the estate shows nothing, with nothing anywhere to
    /// connect the two.
    /// </para>
    /// <para>
    /// 1004 rather than 1013, which it shared with <see cref="CycleFailed"/>.
    /// Those two are exactly the pair an operator filters on when the product
    /// goes quiet, and one id for both means a log pipeline cannot separate
    /// "the cycle is throwing" from "this vCenter is not being polled" — two
    /// different faults needing two different fixes. This one moved because
    /// 1004 puts it beside the other startup lines about a configured
    /// connection, which is where it belongs; 1013 sits in the block about
    /// running cycles.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Connection {InstanceId} is not being polled: {Reason}.")]
    public static partial void ConnectionNotPolled(ILogger logger, string instanceId, string reason);
}
