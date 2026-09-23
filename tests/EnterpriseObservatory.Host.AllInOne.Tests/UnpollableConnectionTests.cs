using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.State;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// A configured connection the product cannot read has to say so, in the product.
/// </summary>
/// <remarks>
/// <para>
/// The case these exist for is the one ADR-0015 deliberately allows: a database
/// restored onto a new host without the Data Protection key ring beside it.
/// Every stored password becomes undecryptable at once, so every connection is
/// skipped, so no collector object is built, so there is no health record to be
/// unhealthy and no collector to fail. The estate goes dark and the dashboard
/// stays green — the single worst moment for the product to be quiet.
/// </para>
/// <para>
/// So these run the real registry into the real cycle and the real read model,
/// and ask what an operator would see, rather than asking the registry what it
/// returns. The defect lived entirely in the space between those two questions.
/// </para>
/// </remarks>
public class UnpollableConnectionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Long enough for the breaker to let a source be asked again.</summary>
    private static readonly TimeSpan PastTheCooldown =
        CollectionPolicy.Default.CircuitBreakerCooldown + TimeSpan.FromMinutes(1);

    private readonly TestClock _clock = new(T0);
    private readonly TestConnectionStore _connections = new();
    private readonly InMemoryEntityGraphStore _graphs = new();
    private readonly InMemoryAlertStateStore _alerts = new();
    private readonly InMemoryCollectorHealthStore _health = new();
    private readonly InMemoryCoverageStore _coverage = new();
    private readonly InMemoryObservationStore _observations = new();
    private readonly InMemoryMaintenanceWindowStore _maintenance = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly List<(string Instance, string Reason)> _logged = [];

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private VsphereSourceRegistry? _registry;

    /// <summary>
    /// The one registry, as the composition root resolves it.
    /// </summary>
    /// <remarks>
    /// Kept for the whole test rather than made per call, because what these
    /// are about is how it behaves across cycles — whether a connection that
    /// has been unusable once is still reported as unusable after it is fixed.
    /// A fresh one each time would have no memory to get that wrong with.
    /// </remarks>
    private VsphereSourceRegistry Registry() => _registry ??= new(
        new SourceConnectionCatalogue(_connections, [], _clock),
        _graphs,
        _clock,
        (instance, reason) => _logged.Add((instance, reason)));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry?.Dispose();
    }

    private MonitoringCycle Cycle() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        _graphs,
        _alerts,
        _health,
        _coverage,
        _notifier,
        _observations,
        _maintenance,
        _clock,
        new InMemoryEventStore());

    private ReadModel Screens() => new(
        _graphs, _alerts, _health, _coverage, _observations, Options, _clock);

    // --- the connection is still there ------------------------------------

    [Fact]
    public void A_connection_whose_password_cannot_be_read_is_not_silently_dropped()
    {
        // The registry used to answer this with an empty list and a log line.
        // Nothing downstream can report a source that was never handed to it:
        // no collector object means no health record, which means the
        // collectors screen is empty and the failing count is zero while the
        // entire estate is going unread.
        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });

        var registry = Registry();

        Assert.Equal(["vc-1"], registry.Inventory.Select(s => s.InstanceId));
        Assert.Equal(["vc-1"], registry.Observations.Select(s => s.InstanceId));
    }

    [Fact]
    public async Task A_connection_that_cannot_be_polled_is_counted_among_the_failing_collectors()
    {
        // The overview's failing count is the number an operator glances at to
        // decide whether to look further. If a connection nobody can read is
        // not in it, the one screen that is supposed to summarise "is the
        // monitoring working" answers yes while the answer is no.
        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });

        await RunBothCyclesAsync();

        var collectors = Screens().Collectors();

        Assert.Equal(2, Screens().Overview().FailingCollectors);
        Assert.Equal(["Inventory", "Observation"], collectors.Select(c => c.Role));

        foreach (var collector in collectors)
        {
            Assert.Equal("vc-1", collector.InstanceId);

            // Unknown, not Critical and certainly not Healthy: we know we did
            // not look, and we know nothing at all about the estate behind it.
            Assert.Equal(HealthState.Unknown, collector.Health);

            // Never succeeded. A null here is what stops the overview claiming
            // an oldest successful read that never happened.
            Assert.Null(collector.LastSuccessUtc);
        }
    }

    [Fact]
    public async Task A_connection_that_cannot_be_polled_reaches_the_alert_inbox()
    {
        // The failing count is on a screen somebody has to open. The inbox is
        // what pages at 3am, and it is the only channel that reaches a person
        // who is not already looking — which is the whole situation here,
        // because nothing else about the product looks wrong.
        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });

        await RunBothCyclesAsync();

        var visible = Screens().Alerts().Items;

        Assert.Equal(2, visible.Count);

        foreach (var alert in visible)
        {
            // Warning, not Critical, for the reason GuardedRule gives: we have
            // not observed a failure of the estate, only our own blindness.
            // Claiming more would be fabricating an outage.
            Assert.Equal(AlertSeverity.Warning, alert.Severity);
            Assert.Equal("Configuration", alert.Category);
            Assert.Contains("vc-1", alert.Description, StringComparison.Ordinal);
            Assert.Contains(
                "Everything this source reports on is Unknown, not healthy.",
                alert.Description,
                StringComparison.Ordinal);
        }

        // And it was pushed, not merely displayed. An alert that only exists on
        // a screen nobody has open is the defect again in a smaller form.
        Assert.NotEmpty(_notifier.Dispatched);
    }

    [Fact]
    public async Task The_reason_a_connection_cannot_be_polled_reaches_the_operator()
    {
        // The three reasons are three different errands, and only two of them
        // are the operator's to fix. Lost key material sends someone to their
        // backup procedure and a blank password sends them to a form -- both
        // stay alarms until they do. A kind this build cannot read sends them
        // to whoever deploys the service instead, and paging about a gap in
        // the product nobody here can close is what N1 (ADR-0026) stops: it
        // still reaches the collectors screen and the log, just not the inbox.
        _connections.Add(Connection("no-key-ring") with { PasswordUnreadable = true });
        _connections.Add(Connection("never-entered") with { Password = Secret.Empty });
        _connections.Add(Connection("wrong-kind") with { Kind = "netapp" });

        await RunBothCyclesAsync();

        var described = Screens().Alerts().Items
            .ToLookup(a => a.Description.Split('\'')[1], StringComparer.Ordinal);

        Assert.Contains(
            "key ring", described["no-key-ring"].First().Description, StringComparison.Ordinal);
        Assert.Contains(
            "no password has been entered",
            described["never-entered"].First().Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-kind", described.SelectMany(g => g).Select(a => a.Description));

        var collectors = Screens().Collectors();
        Assert.Contains(
            collectors,
            c => c.InstanceId == "wrong-kind" &&
                 c.LastFailureDetail != null &&
                 c.LastFailureDetail.Contains("no collector for kind 'netapp'", StringComparison.Ordinal));
    }

    // --- it clears itself --------------------------------------------------

    [Fact]
    public async Task The_alert_clears_itself_once_the_connection_can_be_read_again()
    {
        // An alert that has to be cleared by hand after the key ring is
        // restored is a new problem, not a fix: the operator who restores it at
        // 4am is not the one who knows the inbox needs tidying, so a stale
        // "we are not looking" would sit there teaching everyone to ignore it.
        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });

        await RunBothCyclesAsync();
        Assert.NotEmpty(Screens().Alerts().Items);

        _connections.Replace(Connection("vc-1"));

        // The registry has to stop saying it first. If it kept handing back the
        // stand-in, the next cycle would raise the alert again and the two
        // halves would take turns forever — which looks exactly like an alert
        // that will not clear.
        _logged.Clear();
        _ = Registry().Inventory;
        Assert.Empty(_logged);

        // Far enough on that the breaker is willing to try again. The one
        // strike a NotConfigured failure earns holds the source off for a
        // cooldown, so the fix is picked up by itself within one — which is the
        // same way a corrected password is picked up, and the point is that
        // nobody has to do anything for it to happen.
        _clock.Advance(PastTheCooldown);

        // Standing in for the real collector, which would otherwise open a
        // socket to a vCenter that does not exist in a test.
        var cycle = Cycle();
        var reading = new FakeInventorySource("vc-1") { Behaviour = () => Snapshot() };
        var sampling = new FakeObservationSource("vc-1") { Behaviour = () => Batch() };

        await cycle.RunInventoryAsync([reading], Options, CancellationToken.None);
        await cycle.RunObservationsAsync([sampling], Options, CancellationToken.None);

        Assert.Empty(Screens().Alerts().Items);
        Assert.Equal(0, Screens().Overview().FailingCollectors);
    }

    [Fact]
    public void A_connection_that_becomes_usable_stops_being_reported_as_unpollable()
    {
        // The registry's half of the same promise. If it kept handing back the
        // stand-in after the password was entered, the alert above would be
        // re-raised on the next cycle for as long as the service ran, and the
        // real collector would never be built at all.
        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });

        var registry = Registry();

        _ = registry.Inventory;
        Assert.NotEmpty(_logged);

        _connections.Replace(Connection("vc-1"));
        _logged.Clear();

        Assert.Equal(["vc-1"], registry.Inventory.Select(s => s.InstanceId));
        Assert.Empty(_logged);
    }

    // --- what it must not do ----------------------------------------------

    [Fact]
    public async Task A_connection_that_cannot_be_polled_does_not_make_its_estate_vanish()
    {
        // The stand-in fails rather than returning an empty snapshot, and this
        // is why. A snapshot is a claim about what exists, so an empty one from
        // a vCenter nobody contacted would mark every host and VM it ever
        // reported as gone. Losing the key ring would then not merely stop the
        // monitoring, it would erase the estate from the product on the next
        // cycle — and the alert would arrive next to a screen showing nothing
        // left to alert about.
        var cycle = Cycle();
        var healthy = new FakeInventorySource("vc-1") { Behaviour = () => Snapshot(Host("vc-1")) };

        await cycle.RunInventoryAsync([healthy], Options, CancellationToken.None);
        Assert.Single(_graphs.Current.Active);

        _connections.Add(Connection("vc-1") with { PasswordUnreadable = true });
        _clock.Advance(TimeSpan.FromMinutes(5));

        var registry = Registry();
        await cycle.RunInventoryAsync(registry.Inventory, Options, CancellationToken.None);

        Assert.Single(_graphs.Current.Active);
        Assert.Empty(_graphs.Current.Vanished);
    }

    [Fact]
    public async Task A_disabled_connection_raises_nothing()
    {
        // Disabling is a decision somebody made, and alerting about a decision
        // being honoured is how an inbox becomes something people filter away.
        // An estate being decommissioned is disabled long before anyone will
        // throw its history away, so this is the common case, not the odd one.
        _connections.Add(Connection("retired") with { IsEnabled = false, Password = Secret.Empty });

        await RunBothCyclesAsync();

        Assert.Empty(Screens().Alerts().Items);
        Assert.Equal(0, Screens().Overview().FailingCollectors);
        Assert.Empty(_logged);
    }

    // --- N1: not polled is not an alarm (ADR-0026) -------------------------

    [Fact]
    public async Task Disabling_a_connection_resolves_the_unreachable_alert_it_left_open()
    {
        // The live bug: CLS-Vcenter and SVT_Vcenter, disabled, still carried
        // "Collector unreachable" because the registry stops handing them to
        // the cycle the moment they are disabled -- nothing was left to sign
        // for their fingerprint, so it stayed open and stale forever instead
        // of resolving.
        _connections.Add(Connection("vc-1"));
        var flaky = new FakeInventorySource("vc-1"); // throws every read
        var cycle = Cycle();

        // Twice: a Warning confirms on its second consecutive observation.
        await cycle.RunInventoryAsync([flaky], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await cycle.RunInventoryAsync([flaky], Options, CancellationToken.None);

        Assert.Single(Screens().Alerts().Items, a => a.Title == "Collector unreachable (inventory)");

        _connections.Replace(Connection("vc-1") with { IsEnabled = false });
        _clock.Advance(TimeSpan.FromMinutes(5));

        var registry = Registry();
        Assert.Empty(registry.Inventory); // the registry builds nothing for it once disabled

        await cycle.RunInventoryAsync(registry.Inventory, Options, CancellationToken.None, DisabledIds());

        Assert.Empty(Screens().Alerts().Items);
    }

    [Fact]
    public async Task A_kind_with_no_collector_never_alerts_and_resolves_what_was_open()
    {
        // The live bug's other half: KibarHolding-alhcesx04-ilo, kind
        // 'redfish', with no collector built for it yet
        // (CollectionFailureKind.NotConfigured). Modelled here with any kind
        // this build cannot read, the same shape the registry gives redfish.
        _connections.Add(Connection("ilo-1"));
        var flaky = new FakeInventorySource("ilo-1");
        var cycle = Cycle();

        await cycle.RunInventoryAsync([flaky], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await cycle.RunInventoryAsync([flaky], Options, CancellationToken.None);

        Assert.Single(Screens().Alerts().Items, a => a.Title == "Collector unreachable (inventory)");

        // Turns out to be a kind this build has no collector for.
        _connections.Replace(Connection("ilo-1") with { Kind = "redfish" });
        _clock.Advance(TimeSpan.FromMinutes(5));

        var registry = Registry();
        await cycle.RunInventoryAsync(registry.Inventory, Options, CancellationToken.None);
        await cycle.RunObservationsAsync(registry.Observations, Options, CancellationToken.None);

        Assert.Empty(Screens().Alerts().Items);

        // And it never comes back while the registry keeps standing in for it.
        _clock.Advance(TimeSpan.FromHours(1));
        await cycle.RunInventoryAsync(registry.Inventory, Options, CancellationToken.None);
        Assert.Empty(Screens().Alerts().Items);
    }

    [Fact]
    public async Task An_enabled_connection_that_really_fails_still_raises_unreachable()
    {
        // KBVc01: a DNS failure on an enabled, polled vSphere connection is
        // exactly what "Collector unreachable" exists to report. N1 narrows
        // the alarm to sources deliberately not polled; it must not touch
        // this one.
        _connections.Add(Connection("kbvc01"));
        var dnsFailure = new FakeInventorySource("kbvc01"); // default: throws, unclassified
        var cycle = Cycle();

        await cycle.RunInventoryAsync([dnsFailure], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var result = await cycle.RunInventoryAsync([dnsFailure], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Collector unreachable (inventory)");
    }

    /// <summary>What <see cref="MonitoringWorker"/> would compute and pass in, live.</summary>
    private IReadOnlyCollection<string> DisabledIds() =>
        [.. _connections.All.Where(c => !c.IsEnabled).Select(c => c.InstanceId)];

    [Fact]
    public async Task Nothing_said_about_an_unpollable_connection_says_the_password()
    {
        // Everything written here is written because somebody has to read it:
        // it goes to the inbox, to the notifier and to the log. That is three
        // new places for a credential to come to rest, and the previous
        // product's leak was exactly this — a password carried into a field
        // nobody thought of as a password field. See SecretTests.
        _connections.Add(Connection("vc-1") with
        {
            // Unusable for a reason that is not the password, so there is a
            // real credential in hand while the message is being written.
            Kind = "netapp",
            Password = Secret.From("hunter2"),
        });

        await RunBothCyclesAsync();

        var said = string.Join(
            "\n",
            [
                .. Screens().Alerts().Items.Select(a => $"{a.Title} {a.Description}"),
                .. Screens().Collectors().Select(c => c.LastFailureDetail ?? string.Empty),
                .. _logged.Select(l => $"{l.Instance} {l.Reason}"),
            ]);

        Assert.NotEmpty(said);
        Assert.DoesNotContain("hunter2", said, StringComparison.Ordinal);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// Runs each cycle twice against the registry's sources.
    /// </summary>
    /// <remarks>
    /// Twice because a warning needs two consecutive hits before it is
    /// confirmed, which is the hysteresis every other warning in the product
    /// goes through. One pass would test a visibility rule that does not exist.
    /// </remarks>
    private async Task RunBothCyclesAsync()
    {
        var registry = Registry();
        var cycle = Cycle();

        for (var pass = 0; pass < 2; pass++)
        {
            await cycle.RunInventoryAsync(registry.Inventory, Options, CancellationToken.None);
            await cycle.RunObservationsAsync(registry.Observations, Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }
    }

    private static SourceConnection Connection(string instanceId) => new()
    {
        InstanceId = instanceId,
        Kind = VsphereSourceRegistry.VsphereKind,
        BaseAddress = new Uri($"https://{instanceId}.example.invalid"),
        Username = "svc-observatory",
        Password = Secret.From("a working one"),
    };

    private InventorySnapshot Snapshot(params Entity[] entities) => new()
    {
        SourceInstanceId = "vc-1",
        ReadAtUtc = _clock.UtcNow,
        Entities = entities,
    };

    private ObservationBatch Batch() => new()
    {
        SourceInstanceId = "vc-1",
        ReadAtUtc = _clock.UtcNow,
    };

    private Entity Host(string source) => new()
    {
        Id = EntityId.For(source, "host-1"),
        Kind = EntityKind.EsxiHost,
        DisplayName = "esx-1",
        SourceInstanceId = source,
        LastSeenUtc = _clock.UtcNow,
    };
}

/// <summary>Stored connections, kept only as long as the test runs.</summary>
/// <remarks>
/// The catalogue is the real one. Only the durable half is replaced, because
/// what these tests are about is what the registry does with a connection it
/// cannot use, not how that connection came to be stored.
/// </remarks>
internal sealed class TestConnectionStore : ISourceConnectionStore
{
    private readonly List<SourceConnection> _connections = [];

    public IReadOnlyList<SourceConnection> All => [.. _connections];

    public SourceConnection? Find(string instanceId) =>
        _connections.FirstOrDefault(c =>
            string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal));

    public bool Add(SourceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Find(connection.InstanceId) is not null)
        {
            return false;
        }

        _connections.Add(connection);

        return true;
    }

    public bool Update(SourceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!Remove(connection.InstanceId))
        {
            return false;
        }

        _connections.Add(connection);

        return true;
    }

    public bool Remove(string instanceId) => _connections.RemoveAll(c =>
        string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal)) > 0;

    /// <summary>Puts a connection back the way it would be after it was fixed.</summary>
    public void Replace(SourceConnection connection) => Update(connection);
}
