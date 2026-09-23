using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Simplivity;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.State;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// ADR-0027: a source that annotates an entity it does not own may raise
/// alerts on it, and those alerts are the raising source's.
/// </summary>
public class AnnotatingSourceAlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(T0);
    private readonly InMemoryEntityGraphStore _graphs = new();
    private readonly InMemoryAlertStateStore _alerts = new();

    private static readonly EntityId Vm = EntityId.For("vc-1", "vm-103");

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private MonitoringCycle Cycle() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        _graphs,
        _alerts,
        new InMemoryCollectorHealthStore(),
        new InMemoryCoverageStore(),
        new RecordingNotifier(),
        new InMemoryObservationStore(),
        new InMemoryMaintenanceWindowStore(),
        _clock,
        new InMemoryEventStore());

    private FakeInventorySource Vsphere() => new("vc-1")
    {
        Behaviour = () => new InventorySnapshot
        {
            SourceInstanceId = "vc-1",
            ReadAtUtc = _clock.UtcNow,
            Entities =
            [
                new Entity
                {
                    Id = Vm,
                    Kind = EntityKind.VirtualMachine,
                    DisplayName = "db-vm-01",
                    Health = HealthState.Healthy,
                    LastSeenUtc = _clock.UtcNow,
                },
            ],
        },
    };

    /// <summary>Exactly what SimplivityInventorySource raises for a DEGRADED VM.</summary>
    private FakeInventorySource Simplivity(Func<bool> degraded) => new("svt-1")
    {
        Behaviour = () => new InventorySnapshot
        {
            SourceInstanceId = "svt-1",
            ReadAtUtc = _clock.UtcNow,
            Annotations =
            [
                new EntityAnnotation
                {
                    Entity = Vm,
                    Namespace = "simplivity",
                    Settings = new Dictionary<string, string>
                    {
                        ["simplivity.ha_status"] = degraded() ? "DEGRADED" : "SAFE",
                    },
                },
            ],
            Alerts = degraded()
                ?
                [
                    new AlertDefinition
                    {
                        Fingerprint = AlertFingerprint.Create(
                            "svt-1", "SimpliVity storage HA not safe", "Availability", Vm.Value, "simplivity-vm-ha"),
                        Severity = AlertSeverity.Warning,
                        Title = "SimpliVity storage HA not safe",
                        Category = "Availability",
                        Source = "svt-1",
                        Entity = Vm,
                    },
                ]
                : [],
        },
    };

    private AlertInstance? HaAlert() =>
        _alerts.All.SingleOrDefault(a => a.Title == "SimpliVity storage HA not safe");

    [Fact]
    public async Task An_annotating_source_s_alert_on_a_vm_it_does_not_own_opens_and_stays_open()
    {
        var cycle = Cycle();
        var sources = new IInventorySource[] { Vsphere(), Simplivity(() => true) };

        for (var i = 0; i < 3; i++)
        {
            await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        var alert = HaAlert();
        Assert.NotNull(alert);
        Assert.True(alert.IsConfirmed);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
        Assert.Equal(Vm, alert.Entity);
    }

    [Fact]
    public async Task The_raising_source_owns_the_alert_not_the_entity_s_owner()
    {
        // ADR-0027: the alert is SimpliVity's. With vCenter silent and
        // SimpliVity answering SAFE, SimpliVity has looked and the condition
        // is gone: it resolves. It used to go Unknown, "source 'vc-1' did not
        // report", because silence was judged by the entity's owner.
        var degraded = true;
        var vsphere = Vsphere();
        var sources = new IInventorySource[] { vsphere, Simplivity(() => degraded) };
        var cycle = Cycle();

        for (var i = 0; i < 2; i++)
        {
            await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(AlertLifecycleState.Open, HaAlert()!.State);

        vsphere.Behaviour = null; // vCenter stops answering
        degraded = false;
        await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);

        Assert.Equal(AlertLifecycleState.Resolved, HaAlert()!.State);
    }

    [Fact]
    public async Task An_annotating_source_s_alert_goes_unknown_when_that_source_is_silent()
    {
        var simplivity = Simplivity(() => true);
        var sources = new IInventorySource[] { Vsphere(), simplivity };
        var cycle = Cycle();

        for (var i = 0; i < 2; i++)
        {
            await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        simplivity.Behaviour = null; // SimpliVity stops answering; vCenter still does
        await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);

        var alert = HaAlert()!;
        Assert.NotEqual(AlertLifecycleState.Resolved, alert.State);
        Assert.True(alert.IsStale);
    }

    [Fact]
    public async Task Two_connections_to_one_federation_still_store_vsphere_and_raise_one_alert()
    {
        // Before: EntityGraph.Merge threw on two writers of 'simplivity' on
        // one VM, which stopped the whole inventory cycle, vSphere included.
        var second = Simplivity(() => true);
        var secondAsSvt2 = new FakeInventorySource("svt-2")
        {
            Behaviour = () => second.Behaviour!() with { SourceInstanceId = "svt-2", Alerts = [] },
        };
        var sources = new List<IInventorySource> { Vsphere(), Simplivity(() => true), secondAsSvt2 };
        var cycle = Cycle();

        for (var i = 0; i < 2; i++)
        {
            await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal("vc-1", _graphs.Current.Entities[Vm].SourceInstanceId);
        Assert.Equal("DEGRADED", _graphs.Current.Entities[Vm].Settings["simplivity.ha_status"]);
        var duplicate = Assert.Single(_alerts.All, a => a.Title.Contains("report the same", StringComparison.Ordinal));
        Assert.Equal("Connections svt-1 and svt-2 report the same SimpliVity federation", duplicate.Title);
        Assert.Equal(AlertLifecycleState.Open, duplicate.State);

        // One of them removed: the duplicate is over.
        sources.Remove(secondAsSvt2);
        await cycle.RunInventoryAsync(sources, Options, CancellationToken.None);

        Assert.Equal(
            AlertLifecycleState.Resolved,
            Assert.Single(_alerts.All, a => a.Title.Contains("report the same", StringComparison.Ordinal)).State);
    }

    [Fact]
    public async Task The_real_collector_s_degraded_vm_alert_opens_through_the_real_folding_port()
    {
        const string uuid = "5029a1b2-c3d4-4e5f-8a9b-0c1d2e3f4a5b";

        var vsphere = new FakeInventorySource("vc-1")
        {
            Behaviour = () => new InventorySnapshot
            {
                SourceInstanceId = "vc-1",
                ReadAtUtc = _clock.UtcNow,
                Entities =
                [
                    new Entity
                    {
                        Id = EntityId.For("vc-1", "vcenter"),
                        Kind = EntityKind.VCenter,
                        DisplayName = "vc",
                        LastSeenUtc = _clock.UtcNow,
                        Marks = [IdentityMark.Create(IdentityMarkKind.HardwareUuid, uuid, "vc-1")],
                    },
                    new Entity
                    {
                        Id = Vm,
                        Kind = EntityKind.VirtualMachine,
                        DisplayName = "db-vm-01",
                        LastSeenUtc = _clock.UtcNow,
                    },
                ],
            },
        };

        var ovc = new OneVmOvc($$"""
            { "virtual_machines": [ { "id": "svt-vm-3", "name": "db-vm-01", "state": "ALIVE",
              "ha_status": "DEGRADED", "hypervisor_object_id": "{{uuid}}:VirtualMachine:vm-103" } ], "count": 1 }
            """);
        var options = new SimplivityConnectionOptions
        {
            InstanceId = "svt-1",
            BaseAddress = new Uri("https://ovc.local"),
            Username = "u",
            Password = Secret.From("p"),
        };
        var simplivity = new SimplivityInventorySource(
            "svt-1",
            new SimplivitySessionChannel(ovc, options),
            new GraphFoldingDirectory(_graphs),
            _clock);

        var cycle = Cycle();

        for (var i = 0; i < 3; i++)
        {
            await cycle.RunInventoryAsync([vsphere, simplivity], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal("DEGRADED", _graphs.Current.Entities[Vm].Settings["simplivity.ha_status"]);
        var alert = HaAlert();
        Assert.NotNull(alert);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
    }

    private sealed class OneVmOvc(string vms) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/oauth/token" => """{ "access_token": "t" }""",
                "/api/virtual_machines" => vms,
                _ => $$"""{ "{{path["/api/".Length..]}}": [], "count": 0 }""",
            };

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
