using System.Reflection;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Compaction, said out loud.
/// </summary>
/// <remarks>
/// <para>
/// The failure these are about leaves no trace an operator would ever look at.
/// A five-minute fold that exceeds the command timeout throws every five
/// minutes forever; folding precedes deleting, so nothing is lost and nothing
/// is removed either, and the sample table grows until the disk is full. Before
/// this, the only evidence anywhere was one log line, and the first symptom
/// anybody noticed was the database refusing writes.
/// </para>
/// <para>
/// So these check that it reaches the alert inbox — the same inbox the API
/// serves and the same lifecycle everything else uses. ADR-0005: "we are not
/// doing X" belongs in the product, not in a file on the server.
/// </para>
/// </remarks>
public class CompactionVisibilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A store whose retention sweep never finishes.</summary>
    /// <remarks>
    /// A timeout rather than an invented exception, because that is the failure
    /// this whole file exists for: the first pass after a long outage folds the
    /// entire backlog in one statement and exceeds <c>CommandTimeout</c>.
    /// </remarks>
    private sealed class UncompactableStore : IObservationStore
    {
        public bool Broken { get; set; } = true;

        public int Sweeps { get; private set; }

        public void Append(IReadOnlyList<Observation> observations)
        {
        }

        public SeriesResult Query(SeriesQuery query) => throw new NotSupportedException();

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

        public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy)
        {
            Sweeps++;

            return Broken
                ? throw new TimeoutException("the fold exceeded the 30s command timeout")
                : new CompactionReport { SamplesDeleted = 1 };
        }
    }

    private sealed class Harness
    {
        public UncompactableStore Store { get; } = new();

        public InMemoryAlertStateStore Alerts { get; } = new();

        public RecordingNotifier Notifier { get; } = new();

        public InMemoryMaintenanceWindowStore Maintenance { get; } = new();

        public TestClock Clock { get; } = new(T0);

        public CompactionWorker Worker { get; }

        public Harness() => Worker = new CompactionWorker(
            Store,
            Alerts,
            Notifier,
            Maintenance,
            Clock,
            MonitoringOptions.Default,
            NullLogger<CompactionWorker>.Instance);

        /// <summary>One sweep, at the clock's current time.</summary>
        public Task SweepAsync() => Worker.SweepAsync(CancellationToken.None);

        /// <summary>What an operator would see in the inbox right now.</summary>
        /// <remarks>
        /// Read the way the API reads it — <c>ReadModel</c> projects
        /// <c>IAlertStateStore.All</c> filtered by visibility — so a test
        /// passing here means the alert is genuinely on the screen, not merely
        /// somewhere in the store.
        /// </remarks>
        public IReadOnlyList<AlertInstance> Inbox => [.. Alerts.All.Where(a => a.IsVisible)];
    }

    private static bool Is(AlertInstance instance, string title) =>
        string.Equals(instance.Title, title, StringComparison.Ordinal);

    [Fact]
    public async Task A_sweep_prunes_the_history_of_ended_alerts_past_the_hourly_retention()
    {
        // The alert history is kept 90 days (ADR-0017's hourly tier) and swept
        // by the same pass as the measurements (ADR-0026, migration 14).
        var harness = new Harness { Store = { Broken = false } };
        var old = new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create("vc-1", "Host down", "Hardware", "esx-01"),
            Severity = AlertSeverity.Critical,
            Title = "Host down",
        };

        AlertReconciliationResult Cycle(IReadOnlyList<AlertDefinition> observed, DateTimeOffset now) =>
            harness.Alerts.Reconcile(AlertScopes.Observation, (stored, flaps) =>
                AlertReconciler.Reconcile(new AlertReconciliationRequest
                {
                    Scope = AlertScopes.Observation,
                    Observed = observed,

                    // The producer of "Host down" ran each cycle (ADR-0026).
                    ProducersRun = [ProducerRun.Where("vc-1", f => f.HasSource("vc-1"))],
                    Stored = stored,
                    FlapHistories = flaps,
                    NowUtc = now,
                    Evaluations = [],
                    Sources = EvidenceSources.None,
                    RawRetention = TimeSpan.FromDays(2),
                }));

        var then = T0.AddDays(-100);
        Cycle([old], then);
        Cycle([], then.AddMinutes(1));
        Cycle([], then.AddMinutes(2));
        Assert.Single(harness.Alerts.ResolvedBetween(then, then.AddHours(1)));

        await harness.SweepAsync();

        Assert.Empty(harness.Alerts.ResolvedBetween(then, then.AddHours(1)));
    }
    [Fact]
    public async Task A_sweep_that_keeps_failing_reaches_the_alert_inbox()
    {
        // The defect. Without this the product shows a healthy estate while
        // retention has not run for a week and the disk is filling; the
        // operator finds out when PostgreSQL stops accepting writes, which
        // takes collection down with it.
        var harness = new Harness();

        // Twice, because a warning is confirmed on its second consecutive
        // observation. One failed sweep genuinely is a blip.
        await harness.SweepAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        var alert = Assert.Single(harness.Inbox);

        Assert.Equal("Compaction failed", alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("TimeoutException", alert.Description, StringComparison.Ordinal);

        // The operator has to be told which way this fails, because the two
        // possibilities call for opposite responses: had the deletes run, the
        // next step would be restoring a backup.
        Assert.Contains("No history was lost", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_compaction_alert_is_notified_like_any_other()
    {
        // Reaching the inbox is not enough on its own. An alert that appears on
        // a screen nobody is looking at is how this failure stayed invisible in
        // the first place, so it goes through the same dispatcher as an
        // unreachable vCenter.
        var harness = new Harness();

        await harness.SweepAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        Assert.Contains(harness.Notifier.Dispatched, a => Is(a, "Compaction failed"));
    }

    [Fact]
    public async Task A_sweep_that_works_resolves_what_its_predecessors_raised()
    {
        // Self-resolving is the reason this is an alert rather than a health
        // flag somebody has to clear. A compaction alert that survived the
        // repair would train operators to ignore the one that means something.
        var harness = new Harness();

        await harness.SweepAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        Assert.NotEmpty(harness.Inbox);

        harness.Store.Broken = false;
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        Assert.Empty(harness.Inbox);
    }

    [Fact]
    public async Task A_fold_that_has_not_completed_for_an_hour_is_a_second_and_worse_alert()
    {
        // One skipped pass and an hour of skipped passes are different
        // incidents. The first resolves itself; the second means the watermark
        // has not moved for an hour, so nothing has aged out for an hour, and
        // an operator who reads "compaction failed" as the same routine warning
        // they saw last Tuesday will not act until the volume is full.
        var harness = new Harness();

        await harness.SweepAsync();
        harness.Clock.Advance(GuardedCompaction.StallsAfter);
        await harness.SweepAsync();

        var stalled = Assert.Single(harness.Inbox, a => Is(a, "Compaction has stopped"));

        // Critical, so it is confirmed on its first observation rather than
        // waiting another five minutes for a second one.
        Assert.Equal(AlertSeverity.Critical, stalled.Severity);
        Assert.Contains("full disk", stalled.Description, StringComparison.Ordinal);

        // Both, not one replacing the other: they are separate fingerprints so
        // that acknowledging the routine warning cannot silence this.
        Assert.Contains(harness.Inbox, a => Is(a, "Compaction failed"));
    }

    [Fact]
    public async Task A_single_failed_sweep_is_not_reported_as_a_stall()
    {
        // The threshold has to mean something. A critical raised by every
        // transient timeout is one that gets muted, and then the real stall
        // arrives into a muted alert.
        var harness = new Harness();

        await harness.SweepAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        Assert.DoesNotContain(harness.Inbox, a => Is(a, "Compaction has stopped"));
    }

    [Fact]
    public async Task Compaction_reconciles_its_own_scope_and_leaves_the_cycles_alone()
    {
        // Reconciliation treats what it is given as the whole truth. Sweeping
        // in the metric cycle's scope would resolve every collector and rule
        // alert it did not happen to re-observe — so a compaction pass would
        // clear the alert saying a vCenter is unreachable, once every five
        // minutes, and the inbox would flicker.
        var harness = new Harness();

        var elsewhere = new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create("vc-1", "Host down", "Hardware", "esx-01"),
            Severity = AlertSeverity.Critical,
            Title = "Host down",
        };

        harness.Alerts.Reconcile(AlertScopes.Observation, (stored, flaps) =>
            AlertReconciler.Reconcile(new AlertReconciliationRequest
            {
                Scope = AlertScopes.Observation,
                Observed = [elsewhere],
                Stored = stored,
                FlapHistories = flaps,
                NowUtc = T0,
                Evaluations = [],
                Sources = EvidenceSources.None,
                RawRetention = TimeSpan.FromDays(2),
            }));

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SweepAsync();

        Assert.Contains(harness.Inbox, a => Is(a, "Host down"));
        Assert.All(
            harness.Alerts.InstancesIn(GuardedCompaction.Scope),
            a => Assert.Equal(GuardedCompaction.Scope, a.Scope));
    }

    [Fact]
    public async Task A_sweep_that_worked_still_says_so_to_the_reconciler()
    {
        // The empty observation list is load-bearing. Returning early on a
        // successful sweep — an obvious optimisation, since there is nothing to
        // report — would mean nothing ever tells the reconciler the problem is
        // gone, and the alert stays open for the life of the installation.
        var harness = new Harness { Store = { Broken = false } };

        await harness.SweepAsync();

        Assert.Empty(harness.Inbox);
        Assert.Empty(harness.Alerts.InstancesIn(GuardedCompaction.Scope));
        Assert.Equal(1, harness.Store.Sweeps);
    }

    [Fact]
    public void No_two_host_log_messages_share_an_event_id()
    {
        // These ids are what a log pipeline filters and alerts on. 1013 once
        // covered both "the cycle failed" and "this connection is not being
        // polled" — precisely the two lines an operator searches for when the
        // product goes quiet, and with one id there was no query that could
        // separate them. Reflected over rather than eyeballed so that the next
        // message added cannot reintroduce it.
        var hostLog = typeof(CompactionWorker).Assembly
            .GetType("EnterpriseObservatory.Host.AllInOne.HostLog", throwOnError: true)!;

        var logger = new RecordingLogger();

        var seen = new List<(string Method, int Id)>();

        foreach (var method in hostLog
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters() is [{ ParameterType.Name: nameof(ILogger) }, ..]))
        {
            logger.Events.Clear();

            method.Invoke(
                null, [logger, .. method.GetParameters().Skip(1).Select(p => Sample(p.ParameterType))]);

            seen.Add((method.Name, Assert.Single(logger.Events).Id));
        }

        // Otherwise the assertion below passes by finding nothing, which is how
        // a structural test quietly stops testing anything.
        Assert.True(seen.Count >= 10, $"Only found {seen.Count} HostLog messages to check.");

        var collisions = seen
            .GroupBy(e => e.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Method))}")
            .ToList();

        Assert.True(collisions.Count == 0, $"Event ids shared: {string.Join("; ", collisions)}");
    }

    /// <summary>A stand-in argument for a log message parameter.</summary>
    /// <remarks>
    /// Throws on a type it does not know rather than skipping the message.
    /// Skipping would let a new log line with an unfamiliar parameter drop out
    /// of the collision check without anybody noticing.
    /// </remarks>
    private static object Sample(Type type) =>
        type == typeof(string) ? "sample"
        : type == typeof(int) ? 0
        : type == typeof(double) ? 0d
        : type == typeof(Uri) ? new Uri("https://vcenter.invalid")
        : typeof(Exception).IsAssignableFrom(type) ? new InvalidOperationException("sample")
        : type.IsEnum ? Enum.GetValues(type).GetValue(0)!
        : throw new NotSupportedException(
            $"HostLog has a {type.Name} parameter this test cannot supply a value for.");

    private sealed class RecordingLogger : ILogger
    {
        public List<EventId> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <remarks>
        /// Always enabled. Source-generated messages skip the call entirely at
        /// a disabled level, and this test would then find nothing to check for
        /// every Debug-level line.
        /// </remarks>
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add(eventId);
    }
}
