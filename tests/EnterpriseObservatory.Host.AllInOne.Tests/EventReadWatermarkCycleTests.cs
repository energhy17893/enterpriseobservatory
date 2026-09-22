using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The event read watermark through the real cycle and reconciler: an event
/// alert is not resolved on events that were not read.
/// </summary>
public class EventReadWatermarkCycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private const string Title = "Storage device connectivity lost";

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private readonly TestClock _clock = new(T0);
    private readonly InMemoryAlertStateStore _alerts = new();
    private readonly InMemoryEventStore _events = new();

    private MonitoringCycle Cycle() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        new InMemoryEntityGraphStore(),
        _alerts,
        new InMemoryCollectorHealthStore(),
        new InMemoryCoverageStore(),
        new RecordingNotifier(),
        new InMemoryObservationStore(),
        new InMemoryMaintenanceWindowStore(),
        _clock,
        _events);

    private FakeInventorySource Inventory() => new("vc-1")
    {
        Behaviour = () => new InventorySnapshot
        {
            SourceInstanceId = "vc-1",
            ReadAtUtc = _clock.UtcNow,
            Entities = [],
            Alerts = [],
            Relationships = [],
        },
    };

    private void RecordLoss() => _events.Record(
        "vc-1",
        [
            new SourceEvent
            {
                Key = 1,
                CreatedAtUtc = _clock.UtcNow.AddMinutes(-1),
                EventClass = "EventEx",
                TypeId = "esx.problem.storage.connectivity.lost",
                Severity = "error",
                Message = "Lost connectivity to storage device naa.600a0b80. Path vmhba64:C4:T0:L0 is down.",
                Host = new EventObjectRef { MoRef = "host-1", Name = "esx01" },
            },
        ],
        complete: true,
        _clock.UtcNow);

    private async Task<MonitoringCycle> OpenAsync(FakeInventorySource inventory)
    {
        var cycle = Cycle();
        RecordLoss();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var opened = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Single(opened.Visible, a => a.Title == Title);
        return cycle;
    }

    [Fact]
    public async Task An_open_event_alert_stays_open_while_its_source_events_are_not_read()
    {
        // Past the time to live the rule alone would call the condition over.
        // But no read has landed for a day: whether vCenter reported it again
        // is not known, so the alert is kept, marked, and not resolved.
        var inventory = Inventory();
        var cycle = await OpenAsync(inventory);

        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(10));
        var blind = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Single(blind.Visible, a => a.Title == Title);
        var held = Assert.Single(_alerts.All, a => a.Title == Title);
        Assert.True(held.IsStale);
        Assert.Equal(UnknownReason.SourceSilent, held.StaleReason);
    }

    [Fact]
    public async Task With_events_read_up_to_now_and_no_new_report_the_alert_resolves()
    {
        var inventory = Inventory();
        var cycle = await OpenAsync(inventory);

        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(10));
        _events.Record("vc-1", [], complete: true, _clock.UtcNow);
        var read = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.DoesNotContain(read.Visible, a => a.Title == Title);
    }
}
