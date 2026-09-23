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
        EventId = 1060,
        Level = LogLevel.Information,
        Message = "Fold slice to {Resolution}: {Buckets} buckets in {Milliseconds:0} ms.")]
    public static partial void FoldSliceCommitted(
        ILogger logger, EnterpriseObservatory.Domain.SeriesResolution resolution, int buckets, double milliseconds);

    [LoggerMessage(
        EventId = 1049,
        Level = LogLevel.Information,
        Message = "Retention delete took {Milliseconds} ms ({SamplesDeleted} samples, " +
                  "{BucketsDeleted} buckets).")]
    public static partial void RetentionDeleted(
        ILogger logger, int milliseconds, int samplesDeleted, int bucketsDeleted);

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
    private static partial void EventsNotReadCore(ILogger logger, string source, string detail);

    /// <summary>
    /// <see cref="EventsNotReadCore"/>, with the fault text vCenter wrote made
    /// unable to break the line; see <see cref="LogText"/>.
    /// </summary>
    public static void EventsNotRead(ILogger logger, string source, string detail)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            EventsNotReadCore(logger, LogText.Escape(source), LogText.Escape(detail));
        }
    }

    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Warning,
        Message = "Event read for {Sources} stopped before reaching the last recorded event. The " +
                  "newest events were kept; some between them and the previous read are missing.")]
    public static partial void EventGap(ILogger logger, string sources);

    [LoggerMessage(
        EventId = 1020,
        Message = "[{Kind}] {Severity} {Title}: {Description}")]
    private static partial void AlertNotificationCore(
        ILogger logger,
        LogLevel level,
        Domain.Alerts.AlertNotificationKind kind,
        Domain.Alerts.AlertSeverity severity,
        string title,
        string description);

    /// <summary>
    /// <see cref="AlertNotificationCore"/>, with the title and description
    /// escaped: both embed VM names, event messages and user names that vCenter
    /// controls. Only the log line is escaped, never the stored alert.
    /// </summary>
    public static void AlertNotification(
        ILogger logger,
        LogLevel level,
        Domain.Alerts.AlertNotificationKind kind,
        Domain.Alerts.AlertSeverity severity,
        string title,
        string description)
    {
        // Escaped only once the line is known to be written.
        if (logger.IsEnabled(level))
        {
            var safeTitle = LogText.Escape(title);
            var safeDescription = LogText.Escape(description);
            AlertNotificationCore(logger, level, kind, severity, safeTitle, safeDescription);
        }
    }

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

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Debug,
        Message = "Compliance: {Findings} findings against {Catalogue} release {Release}.")]
    public static partial void ComplianceEvaluated(
        ILogger logger, int findings, string catalogue, string release);

    /// <summary>
    /// The compliance evaluation threw. Its own id, because the inventory cycle
    /// that ran before it did not fail and must not be reported as if it had.
    /// </summary>
    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Error,
        Message = "Compliance evaluation failed; the findings shown are from the last evaluation " +
                  "that succeeded.")]
    public static partial void ComplianceFailed(ILogger logger, Exception exception);

    /// <summary>The M8 alarms resolved as moved to continuity findings (K2, once).</summary>
    [LoggerMessage(
        EventId = 1025,
        Level = LogLevel.Information,
        Message = "Continuity: {Moved} alarm(s) resolved as moved to compliance findings; " +
                  "{Matched} matched a finding by name. Silences were not carried over as acceptances.")]
    public static partial void AlarmsMovedToFindings(ILogger logger, int moved, int matched);

    /// <summary>The demand snapshot for N+1 could not be taken; N+1 is not evaluated this time.</summary>
    [LoggerMessage(
        EventId = 1026,
        Level = LogLevel.Warning,
        Message = "Continuity: the N+1 demand snapshot could not be taken; N+1 is not evaluated this cycle.")]
    public static partial void DemandSnapshotFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Warning,
        Message = "No compliance catalogue is loaded: {Problem}")]
    public static partial void ComplianceCatalogueUnavailable(ILogger logger, string problem);

    [LoggerMessage(
        EventId = 1024,
        Level = LogLevel.Information,
        Message = "Compliance catalogue {Catalogue} release {Release}: {Controls} non-default " +
                  "controls, {Evaluated} of them evaluated by this build.")]
    public static partial void ComplianceCatalogueLoaded(
        ILogger logger, string catalogue, string release, int controls, int evaluated);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "Scheduled reports: {Due} due, {Sent} sent, {Failed} failed.")]
    public static partial void ReportsDispatched(ILogger logger, int due, int sent, int failed);

    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Warning,
        Message = "Scheduled report {SubscriptionId} was not sent: {Detail}")]
    public static partial void ReportFailed(ILogger logger, string subscriptionId, string detail);

    [LoggerMessage(
        EventId = 1032,
        Level = LogLevel.Error,
        Message = "A scheduled-report dispatch pass threw; the next pass, one minute from now, " +
                  "will try again.")]
    public static partial void ReportDispatchPassFailed(ILogger logger, Exception exception);

    /// <summary>
    /// PostgreSQL refused a startup connection and the service is retrying
    /// rather than crashing. See <c>DatabaseStartupRetry</c>.
    /// </summary>
    [LoggerMessage(
        EventId = 1033,
        Level = LogLevel.Warning,
        Message = "PostgreSQL was not reachable on startup attempt {Attempt} " +
                  "({ElapsedSeconds:0}s since the first try); retrying.")]
    public static partial void DatabaseNotReachableRetrying(
        ILogger logger, Exception exception, int attempt, double elapsedSeconds);

    // --- first-run setup and the database card (G-DB) ----------------------
    //
    // None of these takes a credential, and none takes an exception: a driver
    // exception's text is the driver's to change, and the sentences passed in
    // here come from PostgresProvisioning.Explain, which has already checked
    // them against every secret the request held.

    [LoggerMessage(
        EventId = 1040,
        Level = LogLevel.Warning,
        Message = "No database is configured: the service is in setup mode, on loopback only. Open " +
                  "http://localhost:{Port}/setup in a browser on this machine.")]
    public static partial void SetupModeStarting(ILogger logger, int port);

    [LoggerMessage(
        EventId = 1041,
        Level = LogLevel.Information,
        Message = "Setup is listening on {Addresses} and nowhere else.")]
    public static partial void SetupListening(ILogger logger, string addresses);

    [LoggerMessage(
        EventId = 1042,
        Level = LogLevel.Critical,
        Message = "Setup found itself bound to a non-loopback address ({Addresses}) and is stopping. " +
                  "The setup page accepts a database administrator's credential and must never face " +
                  "the network.")]
    public static partial void SetupExposed(ILogger logger, string addresses);

    [LoggerMessage(
        EventId = 1043,
        Level = LogLevel.Warning,
        Message = "Setup refused a request from {Remote}: setup is only available from this machine.")]
    public static partial void SetupRefusedRemoteCaller(ILogger logger, string remote);

    [LoggerMessage(
        EventId = 1044,
        Level = LogLevel.Warning,
        Message = "Setup did not complete: {Detail}")]
    public static partial void SetupFailed(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 1045,
        Level = LogLevel.Information,
        Message = "Setup {Outcome} role {Role} and database {Database} on {Host}:{Port}; the connection is " +
                  "saved, protected, in {File}. Switching to normal mode.")]
    public static partial void SetupCompleted(
        ILogger logger, string outcome, string role, string database, string host, int port, string file);

    [LoggerMessage(
        EventId = 1046,
        Level = LogLevel.Information,
        Message = "Database: {Username}@{Host}:{Port}/{Database}, from {Source}.")]
    public static partial void DatabaseSourceChosen(
        ILogger logger, string username, string host, int port, string database, string source);

    [LoggerMessage(
        EventId = 1047,
        Level = LogLevel.Information,
        Message = "The database password was rotated by {Actor}.")]
    public static partial void DatabasePasswordRotated(ILogger logger, string actor);

    [LoggerMessage(
        EventId = 1048,
        Level = LogLevel.Error,
        Message = "Rotating the database password failed ({Actor}): {Detail}")]
    public static partial void DatabasePasswordRotationFailed(ILogger logger, string actor, string detail);

    /// <summary>
    /// A collector could not give its session or token back on close. A
    /// warning, not an error: the SimpliVity OVC answers every revoke 401
    /// (measured), and the token idles out ten minutes later anyway.
    /// </summary>
    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Warning,
        Message = "Connection {InstanceId} closed without giving its session back: {Detail}.")]
    public static partial void SessionNotReleased(ILogger logger, string instanceId, string detail);
}
