using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Folds measurements down and throws away what has aged out.
/// </summary>
/// <remarks>
/// <para>
/// Its own loop rather than a step inside the metric cycle. Compaction is
/// bulk work over the whole database and collection is a deadline — a cycle
/// that had to wait for a retention sweep before sampling would be late for a
/// reason that has nothing to do with the estate.
/// </para>
/// <para>
/// Runs on the five-minute boundary it produces, which is the shortest useful
/// cadence: anything faster finds no complete bucket to fold.
/// </para>
/// <para>
/// Every sweep reconciles the compaction scope, including the ones that
/// worked. That is what lets the alerts resolve by themselves, and it is why
/// this loop talks to the alert store at all rather than only to the log.
/// See <see cref="GuardedCompaction"/>.
/// </para>
/// </remarks>
public sealed class CompactionWorker(
    IObservationStore store,
    IAlertStateStore alerts,
    IAlertNotifier notifier,
    IMaintenanceWindowStore maintenance,
    IClock clock,
    MonitoringOptions options,
    ILogger<CompactionWorker> logger) : BackgroundService
{
    private readonly IObservationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IAlertStateStore _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));

    private readonly IAlertNotifier _notifier =
        notifier ?? throw new ArgumentNullException(nameof(notifier));

    private readonly IMaintenanceWindowStore _maintenance =
        maintenance ?? throw new ArgumentNullException(nameof(maintenance));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly MonitoringOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<CompactionWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>How the sweeps have been going, across passes.</summary>
    /// <remarks>
    /// In memory rather than stored, which bounds what the stall alert can
    /// claim: a restart forgets that compaction was already failing, so the
    /// hour starts again. Persisting it would mean another table for a number
    /// that only decides when a warning becomes a critical, and the warning
    /// itself is raised from the very first failed sweep either way.
    /// </remarks>
    private CompactionSweepState _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CompactionInterval);

        // Waits before the first pass rather than running at startup. There is
        // nothing to fold that a moment's delay loses, and a service restart
        // should not begin with bulk disk work while collection is trying to
        // get its first samples.
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Justified: a failed sweep must not end the
            // loop. The next pass picks up where this one stopped, because
            // compaction is computed from its sources rather than accumulated.
            // Reaching here means the reporting itself failed — the sweep's own
            // failure is already an alert by then — so the log is all that is
            // left to say it with.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HostLog.CompactionFailed(_logger, ex);
            }
        }
    }

    /// <summary>
    /// One sweep, and everything the product should say about it.
    /// </summary>
    /// <remarks>
    /// Public so it can be run once, deterministically, without a timer. The
    /// alternative is a test that starts the service and waits, which decides
    /// whether this behaviour is correct by how busy the build agent is.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var outcome = GuardedCompaction.Run(
            () => _store.Compact(now, _options.Retention),
            _state,
            now,
            GuardedCompaction.StallsAfter);

        _state = outcome.State;

        if (outcome.Error is { } error)
        {
            HostLog.CompactionFailed(_logger, error);
        }
        else if (outcome.Report is { DidSomething: true } report)
        {
            HostLog.Compacted(
                _logger,
                report.BucketsWritten,
                report.SamplesDeleted,
                report.BucketsDeleted);
        }

        // Reconciled every pass, not only the failing ones. Reconciliation
        // treats what it is given as the whole truth, so a successful sweep
        // handing in an empty list is precisely what resolves yesterday's
        // failure alert — and skipping the call on success would leave it open
        // forever.
        var reconciliation = _alerts.Reconcile(
            GuardedCompaction.Scope,
            (stored, flaps) => AlertReconciler.Reconcile(new AlertReconciliationRequest
            {
                // The third scope, and the reason the reconciler takes this
                // rather than the two the monitoring cycle knows about: the
                // set is open, and a scope no store has an opinion about is
                // one a new worker cannot file wrongly.
                Scope = GuardedCompaction.Scope,
                Observed = outcome.Alerts,

                // No rules and no sources: compaction is a direct producer,
                // two-valued at N = 1, and its own evidence (ADR-0026 §1.2).
                Evaluations = [],
                Sources = EvidenceSources.None,
                RawRetention = _options.Retention.Raw,
                Stored = stored,
                FlapHistories = flaps,
                Hysteresis = _options.Hysteresis,
                Flap = _options.Flap,
                MaintenanceWindows = _maintenance.ActiveAt(now),
                NowUtc = now,
            }));

        if (reconciliation.ToNotify.Count == 0)
        {
            return;
        }

        await _notifier.DispatchAsync(reconciliation.ToNotify, cancellationToken).ConfigureAwait(false);

        // Marked only after the dispatcher returns, and marked at all because
        // the pending kind survives every later sweep: an alert never marked
        // notifies once every five minutes for as long as compaction is broken.
        _alerts.MarkNotified(
            GuardedCompaction.Scope, [.. reconciliation.ToNotify.Select(a => a.Fingerprint)]);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>How the compaction sweeps have been going, across passes.</summary>
/// <param name="LastCompletedUtc">
/// When a sweep last ran to the end. Null until one has, which on a fresh
/// service is indistinguishable from one that has never managed it — and the
/// alert says so rather than inventing a time.
/// </param>
/// <param name="FailingSinceUtc">
/// When the current run of failures began, or null when the last sweep worked.
/// The stall threshold is measured from here rather than from
/// <paramref name="LastCompletedUtc"/> so that an installation whose very
/// first sweep fails is still told, an hour later, that retention has never
/// run.
/// </param>
/// <param name="ConsecutiveFailures">
/// How many sweeps in a row have thrown. Reported to the operator because "it
/// failed once" and "it has failed eleven times" call for different actions.
/// </param>
public readonly record struct CompactionSweepState(
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? FailingSinceUtc,
    int ConsecutiveFailures);

/// <summary>What one guarded sweep produced.</summary>
/// <param name="Report">What the sweep did, or null when it threw.</param>
/// <param name="Error">What it threw, for the log. Null when it worked.</param>
/// <param name="State">The state to carry into the next sweep.</param>
/// <param name="Alerts">
/// What the product should be showing about compaction right now — empty after
/// a sweep that worked, which is what resolves whatever was open.
/// </param>
public readonly record struct CompactionSweepOutcome(
    CompactionReport? Report,
    Exception? Error,
    CompactionSweepState State,
    IReadOnlyList<AlertDefinition> Alerts);

/// <summary>
/// Runs one compaction sweep so that its failure is something the product says
/// rather than something the log mentions.
/// </summary>
/// <remarks>
/// <para>
/// The defect this exists for is invisible by construction. On a large estate
/// the five-minute fold can exceed the command timeout — the first pass after
/// a long outage covers the whole backlog in one statement — and then it does
/// so again every five minutes, forever. Folding precedes deleting, so nothing
/// is lost; but nothing is deleted either, the sample table grows without
/// bound, and the only evidence anywhere was one log line. The operator's first
/// symptom was a full disk.
/// </para>
/// <para>
/// So the failure becomes an alert, for the reason ADR-0005 gives and both
/// <c>StorageFailure</c> and <c>GuardedRule</c> follow: "we are not doing X"
/// belongs in the product, not in a file on the server. Being an ordinary alert
/// also means it is reconciled in the same scope as everything else, so it
/// resolves by itself on the first sweep that completes, and an operator can
/// acknowledge or silence it with the same buttons as anything else.
/// </para>
/// <para>
/// Two alerts rather than one, because they ask for different things. A sweep
/// that threw is a warning: one pass was skipped, the next will pick it up, and
/// on a busy estate that will occasionally just happen. A fold that has not
/// completed for an hour is a different statement — the watermark has not moved
/// in that time, so retention has not run in that time, and raw samples have
/// been accumulating with nothing to remove them. That one is critical, and
/// deliberately so where <c>GuardedRule</c> settles for a warning: a failed
/// rule means we have stopped looking at the estate and may not claim more,
/// whereas this is a statement about our own machine, where we do know what
/// happens next.
/// </para>
/// <para>
/// The two signals collapse into one measurement here only because of how the
/// store is built: <c>Fold</c> writes its watermark in the same transaction as
/// the buckets, so in this store the watermark advances exactly when the fold
/// completes. A watermark that has stopped advancing therefore is a sweep that
/// has stopped completing, and asking the database for it separately would be
/// asking it the same question twice.
/// </para>
/// </remarks>
public static class GuardedCompaction
{
    /// <summary>The evaluation that owns these alerts.</summary>
    /// <remarks>
    /// Its own scope, not the metric cycle's. Reconciliation resolves anything
    /// it was given but did not see, and the metric cycle runs every thirty
    /// seconds while compaction runs every five minutes — sharing a scope would
    /// have the faster loop clear this alert ten times before the slower one
    /// raised it again.
    /// </remarks>
    public const string Scope = "compaction";

    public const string Category = "Configuration";

    private const string Source = "platform";

    private const string FailedTitle = "Compaction failed";

    private const string StalledTitle = "Compaction has stopped";

    /// <summary>The thing the alerts are about, in fingerprint terms.</summary>
    /// <remarks>
    /// One object name for the whole sweep rather than one per resolution.
    /// The steps fail together by design — a fold that throws takes the deletes
    /// with it — so splitting them would raise five alerts describing one
    /// event.
    /// </remarks>
    private const string ObjectName = "compaction";

    /// <summary>
    /// How long a run of failures may last before it stops being a blip.
    /// </summary>
    /// <remarks>
    /// An hour is twelve passes at the default cadence, which is far past
    /// anything a busy moment explains, and far short of the two days of raw
    /// retention that the sweep is failing to enforce. Fixed rather than
    /// configurable until somebody has a reason: a threshold nobody has needed
    /// to change is a setting nobody has needed to understand.
    /// </remarks>
    public static readonly TimeSpan StallsAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Runs <paramref name="sweep"/>, or reports that it could not be run.
    /// </summary>
    /// <param name="sweep">The compaction pass itself.</param>
    /// <param name="state">How the previous sweeps went.</param>
    /// <param name="nowUtc">When this sweep is happening.</param>
    /// <param name="stallAfter">
    /// How long a run of failures may last before it is reported as a stall.
    /// </param>
    public static CompactionSweepOutcome Run(
        Func<CompactionReport> sweep,
        CompactionSweepState state,
        DateTimeOffset nowUtc,
        TimeSpan stallAfter)
    {
        ArgumentNullException.ThrowIfNull(sweep);

        try
        {
            return new CompactionSweepOutcome(
                sweep(),
                null,
                new CompactionSweepState(nowUtc, null, 0),

                // Nothing observed, which is how the reconciler is told the
                // problem has gone. An empty list here is doing work.
                []);
        }
#pragma warning disable CA1031 // Justified: see the remarks above. This is the
        // one place that can turn a broken sweep into something an operator
        // sees, so it has to survive whatever the database throws.
        //
        // OperationCanceledException included, unlike GuardedRule. A rule is
        // handed the cycle's token and a cancellation there means the service
        // is stopping; IObservationStore.Compact takes no token and cannot
        // observe one, so a cancellation surfacing from inside it is the
        // database giving up — which is the exact failure this exists to
        // report. Shutdown is handled by not starting a sweep at all: the
        // worker's timer wait returns false and the loop ends.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failing = state.FailingSinceUtc ?? nowUtc;

            var next = new CompactionSweepState(
                state.LastCompletedUtc, failing, state.ConsecutiveFailures + 1);

            var alerts = new List<AlertDefinition> { Failed(ex) };

            if (nowUtc - failing >= stallAfter)
            {
                alerts.Add(Stalled(next, nowUtc - failing));
            }

            return new CompactionSweepOutcome(null, ex, next, alerts);
        }
    }

    private static AlertDefinition Failed(Exception error) => new()
    {
        Fingerprint = AlertFingerprint.Create(
            Source, FailedTitle, Category, ObjectName, "compaction-failed"),

        // Warning, because one skipped pass is recoverable and the next one
        // resumes from the same watermark. What makes it recoverable is the
        // ordering: folding runs before deleting, so a sweep that died partway
        // deleted nothing it had not already summarised.
        Severity = AlertSeverity.Warning,
        Title = FailedTitle,
        Description =
            $"The retention sweep threw {error.GetType().Name} and did not finish: {error.Message} " +
            "Nothing was folded and nothing aged out on this pass, so measurements older than " +
            "their retention are still on disk. No history was lost — folding runs before " +
            "deleting — and the next pass resumes from the same point.",
        Category = Category,
        Source = Source,
        IsDerived = true,
    };

    private static AlertDefinition Stalled(CompactionSweepState state, TimeSpan failingFor) => new()
    {
        // A separate fingerprint, not an escalation of the one above. They
        // answer different questions and an operator may reasonably acknowledge
        // the first while still wanting to be paged by the second.
        Fingerprint = AlertFingerprint.Create(
            Source, StalledTitle, Category, ObjectName, "compaction-stalled"),

        Severity = AlertSeverity.Critical,
        Title = StalledTitle,
        Description =
            $"No compaction sweep has completed for {Describe(failingFor)} " +
            $"({state.ConsecutiveFailures} consecutive failures). " +
            $"{LastCompleted(state)} The fold watermark has not moved in that time, so retention " +
            "is not running and raw samples are accumulating with nothing to remove them. Left " +
            "alone this ends as a full disk, and the database stops accepting measurements at all.",
        Category = Category,
        Source = Source,
        IsDerived = true,
    };

    private static string LastCompleted(CompactionSweepState state) =>
        state.LastCompletedUtc is { } last
            ? $"The last sweep to finish was at {last:u}."
            : "No sweep has finished since this service started.";

    /// <summary>A duration an operator reads rather than parses.</summary>
    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m";
}
