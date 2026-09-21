using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Planned work, and what it does to alerting.
/// </summary>
/// <remarks>
/// The domain has had all of this since the lifecycle work and nothing ever
/// passed a window to the reconciler, so the feature existed and did nothing.
/// These run through the whole cycle because that is exactly where the gap was.
/// </remarks>
public class MaintenanceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityId Host = new("vc-1:host-1");

    private readonly TestClock _clock = new(T0);

    private readonly InMemoryAlertStateStore _alerts;
    private readonly InMemoryMaintenanceWindowStore _windows;
    private readonly MaintenanceService _maintenance;
    private readonly RecordingNotifier _notifier = new();

    public MaintenanceTests()
    {
        _alerts = new InMemoryAlertStateStore();
        _windows = new InMemoryMaintenanceWindowStore();
        _maintenance = new MaintenanceService(_windows, _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private static OperatorIdentity Ertugrul { get; } = OperatorIdentity.Verified("ertugrul");

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private MonitoringCycle Cycle() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        new InMemoryEntityGraphStore(),
        _alerts,
        new InMemoryCollectorHealthStore(),
        new InMemoryCoverageStore(),
        _notifier,
        new InMemoryObservationStore(),
        _windows,
        _clock,
        new InMemoryEventStore());

    // --- what a window does -----------------------------------------------

    [Fact]
    public async Task A_window_stops_the_notification_and_not_the_alert()
    {
        // The whole rule. Hiding the alert would make the window useless
        // afterwards as an account of what actually broke during the work.
        Declare(T0.AddMinutes(-1), T0.AddHours(2));

        var result = await RunCycle();

        var alert = Assert.Single(result.Visible);

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.NotNull(alert.SuppressedByWindowId);
        Assert.False(alert.ShouldNotify);
        Assert.Empty(_notifier.Dispatched);
    }

    [Fact]
    public async Task Without_a_window_the_same_alert_notifies()
    {
        // The control. Without it the test above could pass because nothing
        // ever notifies.
        var result = await RunCycle();

        Assert.Single(result.Visible);
        Assert.Null(result.Visible[0].SuppressedByWindowId);
        Assert.Single(_notifier.Dispatched);
    }

    [Fact]
    public async Task The_window_that_silenced_it_is_named()
    {
        // So the operator can judge whether it should have, rather than being
        // told only that something did.
        var window = Declare(T0.AddMinutes(-1), T0.AddHours(2));

        var result = await RunCycle();

        Assert.Equal(window.Id, result.Visible[0].SuppressedByWindowId);
    }

    [Fact]
    public async Task A_problem_still_there_when_the_window_ends_notifies_then()
    {
        // The case that matters most. Something broke during the work and is
        // still broken afterwards; the window deferred the page, it did not
        // cancel it.
        Declare(T0.AddMinutes(-1), T0.AddHours(1));

        await RunCycle();
        Assert.Empty(_notifier.Dispatched);

        _clock.Advance(TimeSpan.FromHours(2));
        var result = await RunCycle();

        Assert.Single(_notifier.Dispatched);
        Assert.Null(result.Visible[0].SuppressedByWindowId);
    }

    [Fact]
    public async Task A_window_that_has_not_started_suppresses_nothing()
    {
        Declare(T0.AddHours(1), T0.AddHours(2));

        await RunCycle();

        Assert.Single(_notifier.Dispatched);
    }

    [Fact]
    public async Task Ending_a_window_early_takes_effect_on_the_next_cycle()
    {
        // Suppression is recomputed every cycle rather than stamped on once,
        // so cancelling planned work that finished early does what it looks
        // like it does.
        var window = Declare(T0.AddMinutes(-1), T0.AddHours(4));

        await RunCycle();
        Assert.Empty(_notifier.Dispatched);

        _maintenance.End(window.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));

        await RunCycle();

        Assert.Single(_notifier.Dispatched);
    }

    [Fact]
    public async Task A_window_covering_other_entities_does_not_cover_this_one()
    {
        Declare(T0.AddMinutes(-1), T0.AddHours(2), new EntityId("vc-1:host-9"));

        await RunCycle();

        Assert.Single(_notifier.Dispatched);
    }

    [Fact]
    public async Task A_window_naming_this_entity_covers_it()
    {
        Declare(T0.AddMinutes(-1), T0.AddHours(2), Host);

        await RunCycle();

        Assert.Empty(_notifier.Dispatched);
    }

    // --- declaring ---------------------------------------------------------

    [Fact]
    public void Who_declared_it_is_recorded()
    {
        // "There was a window" is half an answer; the other half is who decided
        // there should be.
        var window = Declare(T0, T0.AddHours(1));

        Assert.Equal("ertugrul", window.DeclaredBy);
        Assert.Equal(T0, window.DeclaredAtUtc);
    }

    [Fact]
    public void A_window_that_has_already_ended_is_refused()
    {
        // It suppresses nothing and is only a confusing row.
        var result = _maintenance.Declare(
            "Past", string.Empty, T0.AddHours(-3), T0.AddHours(-1), [], Ertugrul);

        Assert.Equal(MaintenanceFailure.BadSchedule, result.Failure);
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_refused()
    {
        var result = _maintenance.Declare(
            "Backwards", string.Empty, T0.AddHours(3), T0.AddHours(1), [], Ertugrul);

        Assert.Equal(MaintenanceFailure.BadSchedule, result.Failure);
    }

    [Fact]
    public void A_window_longer_than_the_maximum_is_refused()
    {
        // Beyond a fortnight it is not planned work, it is switching the
        // product off, and somebody should have to say so again.
        var result = _maintenance.Declare(
            "Forever",
            string.Empty,
            T0,
            T0 + MaintenanceService.MaximumDuration + TimeSpan.FromDays(1),
            [],
            Ertugrul);

        Assert.Equal(MaintenanceFailure.TooLong, result.Failure);
    }

    // --- durability --------------------------------------------------------

    [Fact]
    public void A_finished_window_is_kept_as_a_record()
    {
        // It is the answer to "why was nobody paged last Tuesday", asked long
        // after the work is done.
        Declare(T0, T0.AddHours(1));

        _clock.Advance(TimeSpan.FromDays(7));

        Assert.Single(_maintenance.All());
        Assert.Empty(_windows.ActiveAt(_clock.UtcNow));
    }

    [Fact]
    public void A_window_old_enough_to_be_history_is_forgotten()
    {
        Declare(T0, T0.AddHours(1));

        _clock.Advance(MaintenanceService.Retention + TimeSpan.FromDays(1));

        Assert.Equal(1, _maintenance.Forget());
        Assert.Empty(_maintenance.All());
    }

    // --- fixtures ----------------------------------------------------------

    private MaintenanceWindow Declare(
        DateTimeOffset start, DateTimeOffset end, params EntityId[] entities)
    {
        var result = _maintenance.Declare(
            "Firmware", "HBA firmware campaign", start, end, entities, Ertugrul);

        Assert.True(result.Applied, $"Declaring refused: {result.Failure}");

        return result.Window!;
    }

    private Task<MonitoringCycleResult> RunCycle()
    {
        var source = new FakeInventorySource("vc-1")
        {
            Behaviour = () => new InventorySnapshot
            {
                SourceInstanceId = "vc-1",
                ReadAtUtc = _clock.UtcNow,
                Alerts =
                [
                    new AlertDefinition
                    {
                        Fingerprint = AlertFingerprint.Create(
                            "vc-1", "Power supply failed", "Hardware", "psu-1", "psu"),
                        Severity = AlertSeverity.Critical,
                        Title = "Power supply failed",
                        Description = "PSU 1 reports a fault.",
                        Category = "Hardware",
                        Source = "vc-1",
                        Entity = Host,
                    },
                ],
            },
        };

        return Cycle().RunInventoryAsync([source], Options, CancellationToken.None);
    }
}
