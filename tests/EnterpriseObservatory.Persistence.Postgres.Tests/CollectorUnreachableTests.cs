using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// "Collector unreachable" through the real cycle and the real tables.
/// </summary>
/// <remarks>
/// <para>
/// Written for the outage of 22 September 2026: a vCenter unreachable for four
/// hours, the notifier logging "[Raised] Collector unreachable" for each role,
/// and afterwards neither <c>alert_instance</c> nor <c>alert_transition</c>
/// holding a trace of it. The trace now lives in <c>alert_history</c>, which a
/// retired instance does not take with it. The question was whether the collection alert ever
/// reached the store. These drive the whole composition — pipeline, runner,
/// breaker, reconciler, store — for longer than the breaker threshold, across
/// a restart, and read the rows back with SQL rather than through the store's
/// own cache, which is the one thing that could hide a write that never
/// happened.
/// </para>
/// </remarks>
public sealed class CollectorUnreachableTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 7, 58, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();
    private readonly MovingClock _clock = new(T0);
    private readonly CountingNotifier _notifier = new();

    // What the live service runs with, apart from retries: a retry sleeps on
    // the real clock and changes nothing about which alert is observed.
    private static readonly MonitoringOptions Options = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    /// <summary>The cycle over fresh stores, as a service start builds it.</summary>
    private MonitoringCycle Start() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        new PostgresEntityGraphStore(_live.Database),
        new PostgresAlertStateStore(_live.Database),
        new PostgresCollectorHealthStore(_live.Database),
        new PostgresCoverageStore(_live.Database),
        _notifier,
        new PostgresObservationStore(_live.Database),
        new PostgresMaintenanceWindowStore(_live.Database),
        _clock,
        new PostgresEventStore(_live.Database));

    [SkippableFact]
    public async Task A_silent_metric_source_holds_one_open_row_through_a_restart_and_resolves_when_it_answers()
    {
        RequireDatabase();

        var source = new Source("vc-1");
        var cycle = Start();

        // Past the breaker threshold, so most of these cycles never ask the
        // source at all -- the path a four-hour outage spends its time on.
        for (var i = 0; i < 12; i++)
        {
            await cycle.RunObservationsAsync([source], Options, CancellationToken.None);

            if (i >= 1)
            {
                Assert.Equal(("Open", true, "None"), Row("collector-unreachable:metrics"));
            }

            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        // A restart in the middle of the outage.
        _live.Restart();
        cycle = Start();

        for (var i = 0; i < 12; i++)
        {
            await cycle.RunObservationsAsync([source], Options, CancellationToken.None);

            Assert.Equal(("Open", true, "None"), Row("collector-unreachable:metrics"));

            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(1, _notifier.Raised("Collector unreachable (metrics)"));

        // The source answers again, once the breaker lets it be asked.
        source.Answers = true;
        _clock.Advance(CollectionPolicy.Default.CircuitBreakerCooldown);

        await cycle.RunObservationsAsync([source], Options, CancellationToken.None);

        Assert.Equal("Resolved", Row("collector-unreachable:metrics")?.State);
        Assert.Contains("ConditionCleared", Transitions("collector-unreachable:metrics"));

        // This is why the tables were empty when they were read after the
        // outage: a resolved alert still absent on the next cycle retires, the
        // scope is rewritten without it, and its transitions used to go with it
        // by cascade -- thirty seconds after the vCenter came back, nothing in
        // the database said it had ever been gone. The instance row still
        // retires; its history no longer goes with it (migration 14,
        // alert_history, ADR-0026). This line was pinned so that the change
        // would have to be made on purpose, and this is it.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cycle.RunObservationsAsync([source], Options, CancellationToken.None);

        Assert.Null(Row("collector-unreachable:metrics"));
        var history = Transitions("collector-unreachable:metrics");
        Assert.Equal("Raised", history[0]);
        Assert.Equal("ConditionCleared", history[^1]);

        // And days later, across another restart, it is still there to read.
        _clock.Advance(TimeSpan.FromDays(3));
        _live.Restart();
        cycle = Start();
        await cycle.RunObservationsAsync([source], Options, CancellationToken.None);

        Assert.Null(Row("collector-unreachable:metrics"));
        Assert.Contains("ConditionCleared", Transitions("collector-unreachable:metrics"));
    }

    [SkippableFact]
    public async Task A_silent_inventory_source_holds_one_open_row_through_a_restart_and_resolves_when_it_answers()
    {
        RequireDatabase();

        var source = new Source("vc-1");
        var cycle = Start();

        for (var i = 0; i < 8; i++)
        {
            await cycle.RunInventoryAsync([source], Options, CancellationToken.None);

            if (i >= 1)
            {
                Assert.Equal(("Open", true, "None"), Row("collector-unreachable:inventory"));
            }

            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        _live.Restart();
        cycle = Start();

        for (var i = 0; i < 4; i++)
        {
            await cycle.RunInventoryAsync([source], Options, CancellationToken.None);

            Assert.Equal(("Open", true, "None"), Row("collector-unreachable:inventory"));

            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(1, _notifier.Raised("Collector unreachable (inventory)"));

        source.Answers = true;

        await cycle.RunInventoryAsync([source], Options, CancellationToken.None);

        Assert.Equal("Resolved", Row("collector-unreachable:inventory")?.State);
        Assert.Contains("ConditionCleared", Transitions("collector-unreachable:inventory"));
    }

    // --- reading the tables -------------------------------------------------

    private (string State, bool Confirmed, string Pending)? Row(string check) =>
        _live.Database.Read<(string, bool, string)?>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT state, is_confirmed, pending_notification FROM alert_instance
                WHERE fingerprint LIKE 'platform|collector unreachable|%|' || @check;
                """;
            command.Parameters.AddWithValue("@check", check);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            var row = (reader.GetString(0), reader.GetBoolean(1), reader.GetString(2));

            // One row per role, never two.
            Assert.False(reader.Read());

            return row;
        });

    private List<string> Transitions(string check) =>
        _live.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT reason FROM alert_history
                WHERE fingerprint LIKE 'platform|collector unreachable|%|' || @check
                ORDER BY episode_first_seen_utc, ordinal;
                """;
            command.Parameters.AddWithValue("@check", check);

            using var reader = command.ExecuteReader();
            var reasons = new List<string>();

            while (reader.Read())
            {
                reasons.Add(reader.GetString(0));
            }

            return reasons;
        });

    // --- doubles -----------------------------------------------------------

    private sealed class MovingClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>A vCenter whose name does not resolve until told otherwise.</summary>
    private sealed class Source(string instanceId) : IInventorySource, IObservationSource
    {
        public string InstanceId { get; } = instanceId;

        public bool Answers { get; set; }

        Task<InventorySnapshot> IInventorySource.ReadAsync(CancellationToken cancellationToken) =>
            Answers
                ? Task.FromResult(new InventorySnapshot { SourceInstanceId = InstanceId, ReadAtUtc = T0 })
                : throw new HttpRequestException("No such host is known. (vcenter.example:443)");

        Task<ObservationBatch> IObservationSource.ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
            Answers
                ? Task.FromResult(new ObservationBatch { SourceInstanceId = InstanceId, ReadAtUtc = T0 })
                : throw new HttpRequestException("No such host is known. (vcenter.example:443)");
    }

    private sealed class CountingNotifier : IAlertNotifier
    {
        private readonly List<AlertInstance> _sent = [];

        public int Raised(string title) =>
            _sent.Count(a => a.Title == title && a.PendingNotification == AlertNotificationKind.Raised);

        public Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken)
        {
            _sent.AddRange(pending);
            return Task.CompletedTask;
        }
    }
}
