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

    /// <summary>The key ring is somewhere that gets deleted.</summary>
    /// <remarks>
    /// <para>
    /// 1005 and 1006 rather than a shared id, and they are two messages rather
    /// than one for the same reason 1004 was moved off 1013: an operator filters
    /// on these. "The keys are in a directory Windows empties" is a maintenance
    /// task with a deadline. "The keys were emptied" is an incident whose only
    /// remedy is re-typing passwords. One id for both means a pipeline cannot
    /// page on the second without paging on the first for ever.
    /// </para>
    /// <para>
    /// Critical, and only at startup. Critical because losing this key ring
    /// loses every stored vCenter password with no restore path; once, because
    /// repeating it every cycle is how principle 4 says a product trains people
    /// to filter it out. The path and a count, never a key.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Critical,
        Message = "The Data Protection key ring is in a losable location: {Path} — {Reason}. " +
                  "It holds the only copy of the key that decrypts every vCenter password " +
                  "entered in the product, and deleting it makes all of them permanently " +
                  "unrecoverable. The keys are readable right now, so this is still fixable: " +
                  "set Storage:KeyRingPath to a durable directory, move the {KeyCount} key " +
                  "file(s) there, restart, and back that directory up separately from the " +
                  "database. See ADR-0015 and ADR-0020.")]
    public static partial void KeyRingInLosableLocation(
        ILogger logger, string path, string reason, int keyCount);

    /// <summary>The key ring was deleted, and the passwords went with it.</summary>
    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Critical,
        Message = "The Data Protection key ring at {Path} holds no keys, but {Connections} " +
                  "connection(s) entered in the product are stored encrypted. Those passwords " +
                  "were encrypted with a key that is no longer here and cannot be recovered — " +
                  "not from a backup of the database, which never held the key. Each affected " +
                  "vCenter will report as unreachable until an administrator opens Connections " +
                  "and re-enters its password. See ADR-0015 and ADR-0020.")]
    public static partial void KeyRingLostItsKeys(ILogger logger, string path, int connections);

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
        EventId = 1017,
        Level = LogLevel.Debug,
        Message = "Event collection: {Recorded} events recorded, {Pruned} aged out.")]
    public static partial void EventCycle(ILogger logger, int recorded, int pruned);

    /// <summary>A source's events could not be read, or could not be kept.</summary>
    /// <remarks>
    /// Its own id rather than 1013, because this is not a cycle failing: the
    /// inventory went on, and the mark did not move, so the next read asks for
    /// the same window again.
    /// </remarks>
    [LoggerMessage(
        EventId = 1018,
        Level = LogLevel.Warning,
        Message = "Events from {Source} were not recorded: {Detail}. The next cycle asks for the " +
                  "same window again.")]
    public static partial void EventsNotRead(ILogger logger, string source, string detail);

    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Warning,
        Message = "Event read for {Sources} stopped before reaching the last recorded event. The " +
                  "newest events were kept; some between them and the previous read are missing.")]
    public static partial void EventGap(ILogger logger, string sources);

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
