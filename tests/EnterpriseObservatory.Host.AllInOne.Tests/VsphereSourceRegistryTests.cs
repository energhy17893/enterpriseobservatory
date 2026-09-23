using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.State;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// When a connection changes, whose client may be thrown away, and when.
/// </summary>
/// <remarks>
/// <para>
/// Two loops share one registry. Inventory refreshes every five minutes and
/// observations every thirty seconds, so a refresh is not a cycle boundary —
/// it is a cycle boundary for <em>one</em> of them, and says nothing about
/// what the other is in the middle of. Retiring a client on one refresh and
/// disposing it on the next therefore disposes it while the other loop may
/// still be reading through it.
/// </para>
/// <para>
/// What that costs is not a tidy exception in a log. The read fails, the
/// runner counts a source failure, ConsecutiveFailures climbs and a
/// "Collector unreachable" alert is raised for a vCenter that is answering
/// perfectly well. A product whose one claim is that it does not lie about
/// what it can see cannot invent a fault out of its own bookkeeping.
/// </para>
/// <para>
/// So these hold a source the way a long inventory cycle holds one — across
/// refreshes it did not make — and ask whether it still works. And, because
/// "never dispose" would pass every such question while leaking an
/// <see cref="HttpClient"/> and a vSphere session on every password change,
/// they also pin down the moment disposal must have happened.
/// </para>
/// </remarks>
public class VsphereSourceRegistryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(T0);
    private readonly TestConnectionStore _connections = new();
    private readonly InMemoryEntityGraphStore _graphs = new();
    private readonly List<(string Instance, string Reason)> _logged = [];

    private VsphereSourceRegistry? _registry;

    /// <summary>The one registry both loops are handed, as the host resolves it.</summary>
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

    // --- a client in use is not taken away --------------------------------

    [Fact]
    public async Task A_source_held_by_one_loop_survives_two_refreshes_by_the_other()
    {
        // The reported defect, in the order it happens in production. The
        // inventory loop takes its sources and settles in for a long pass over
        // a large estate; an operator corrects the password ten seconds later;
        // the observation loop refreshes twice inside that pass. With
        // retirement counted in refreshes rather than in loops, the second of
        // those two disposes the client the inventory pass is still reading
        // through — ObjectDisposedException mid-read, counted as a source
        // failure, and a fabricated "Collector unreachable (inventory)" alert
        // against a healthy vCenter.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Replace(Connection("vc-1") with { Password = Secret.From("corrected") });

        _ = Registry().Observations;
        _ = Registry().Observations;

        Assert.False(
            await ReadWasRefusedByDisposalAsync(held),
            "the inventory loop's client was disposed while its cycle was still using it");
    }

    [Fact]
    public async Task A_source_held_by_one_loop_survives_the_other_loop_losing_the_connection()
    {
        // The same hazard down the other retirement path. A connection deleted
        // on the Connections screen retires its client too, and a deletion is
        // exactly as likely to land in the middle of a five-minute inventory
        // pass as a password edit is. Losing the connection should end the
        // reading, not corrupt the pass that is already under way and blame
        // the vCenter for it.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Remove("vc-1");

        _ = Registry().Observations;
        _ = Registry().Observations;

        Assert.False(
            await ReadWasRefusedByDisposalAsync(held),
            "the inventory loop's client was disposed while its cycle was still using it");
    }

    // --- but it is taken away ---------------------------------------------

    [Fact]
    public async Task A_replaced_client_is_disposed_once_both_loops_have_moved_on()
    {
        // The other half, and the reason waiting is not the same as leaking.
        // Every retired client holds a socket pool and a logged-in vSphere
        // session, and vCenter counts sessions: a service that kept one per
        // password edit would spend months looking fine and then start being
        // refused logins by the estate it is supposed to watch. Once each loop
        // has begun a refresh after the retirement, neither can still be
        // holding the old source, and it must go.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Replace(Connection("vc-1") with { Password = Secret.From("corrected") });

        // The observation loop retires it, then goes round again. The
        // inventory loop starting its next pass is the proof that the pass
        // holding the old client has ended.
        _ = Registry().Observations;
        _ = Registry().Observations;
        _ = Registry().Inventory;

        Assert.True(
            await ReadWasRefusedByDisposalAsync(held),
            "the replaced client outlived both loops moving past it");
    }

    [Fact]
    public async Task A_client_for_a_removed_connection_is_disposed_once_both_loops_have_moved_on()
    {
        // A connection somebody deleted must stop holding anything at all. It
        // will never be rebuilt, so a client kept back here is kept for the
        // life of the process — the one leak with no upper bound, because
        // nothing later in the run can collect it.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Remove("vc-1");

        _ = Registry().Observations;
        _ = Registry().Observations;
        _ = Registry().Inventory;

        Assert.True(
            await ReadWasRefusedByDisposalAsync(held),
            "the removed connection's client outlived both loops moving past it");
    }

    [Fact]
    public async Task A_client_is_disposed_when_only_one_loop_ever_asks_for_sources()
    {
        // Waiting for both loops must not mean waiting for a loop that does not
        // exist. Anything driving the registry from one side only — a host
        // running a single cadence, or a test — would otherwise accumulate a
        // client per change forever, which is the leak this fix was explicitly
        // not allowed to trade the defect for.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Replace(Connection("vc-1") with { Password = Secret.From("corrected") });

        _ = Registry().Inventory;
        _ = Registry().Inventory;

        Assert.True(
            await ReadWasRefusedByDisposalAsync(held),
            "a client was kept back waiting for a loop that never refreshes");
    }

    [Fact]
    public async Task Disposing_the_registry_disposes_what_it_was_still_holding_back()
    {
        // Shutdown is the one moment with no next refresh to wait for. If the
        // retirement queue were only drained by refreshes, stopping the service
        // would strand the sockets and the vSphere sessions in it — which is
        // how a restart loop turns one session leak into a steady climb.
        _connections.Add(Connection("vc-1"));

        var held = Registry().Inventory.Single();

        _connections.Replace(Connection("vc-1") with { Password = Secret.From("corrected") });

        _ = Registry().Observations;

        Registry().Dispose();

        Assert.True(
            await ReadWasRefusedByDisposalAsync(held),
            "a retired client survived the registry being disposed");
    }

    // --- and it is actually replaced --------------------------------------

    [Fact]
    public void A_corrected_connection_is_read_through_a_new_source()
    {
        // The reason retirement exists at all. The shape a client is built from
        // includes the credential, deliberately: an operator who fixes a
        // password has to be read with the new one on the next cycle, not after
        // a restart. A fix that made retirement never happen would leave a
        // failing session in place and the alert standing with nothing left to
        // clear it.
        _connections.Add(Connection("vc-1"));

        var before = Registry().Inventory.Single();

        _connections.Replace(Connection("vc-1") with { Password = Secret.From("corrected") });

        Assert.NotSame(before, Registry().Inventory.Single());
    }

    [Fact]
    public void An_unchanged_connection_is_read_through_the_same_source()
    {
        // The promise the interface makes, and the one the two loops rely on:
        // they must share one client per vCenter. Building a new one per
        // refresh would open a fresh vSphere session every thirty seconds and
        // exhaust the socket pool slowly enough that nobody connects the
        // eventual failures back to here.
        _connections.Add(Connection("vc-1"));

        var first = Registry().Inventory.Single();

        Assert.Same(first, Registry().Inventory.Single());
        Assert.Same(first, Registry().Inventory.Single());
    }

    // --- two connections of the same kind stay separate (M6.0a) ------------

    [Fact]
    public void Two_vsphere_connections_produce_two_independent_sources()
    {
        // SimpliVity turns out to live under its own vCenter, which is added
        // as a second vsphere connection rather than a new kind. Nothing here
        // is new behaviour -- EntityId has refused to collapse two sources'
        // ids since EntityIdTests.An_id_carries_the_source_that_named_it --
        // but this is the level where the registry could still do it by
        // accident, by keying a source on Kind instead of InstanceId. It does
        // not: each connection gets its own source and its own client.
        _connections.Add(Connection("vc-1"));
        _connections.Add(Connection("vc-2"));

        var inventory = Registry().Inventory;

        Assert.Equal(
            ["vc-1", "vc-2"],
            inventory.Select(s => s.InstanceId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.NotSame(
            inventory.Single(s => s.InstanceId == "vc-1"),
            inventory.Single(s => s.InstanceId == "vc-2"));
    }

    // --- S3: a second kind, built through the same registry -----------------

    [Fact]
    public void A_simplivity_connection_is_read_for_inventory_only()
    {
        // Built by kind, not held back as "no collector for kind" any more.
        // SimpliVity has no metrics and no event stream of its own (its alarms
        // reach the product through vCenter's events), so it appears in the
        // inventory list and nowhere else.
        _connections.Add(Connection("vc-1"));
        _connections.Add(Connection("svt-1") with { Kind = ConnectionKinds.Simplivity });

        Assert.Equal(
            ["svt-1", "vc-1"],
            Registry().Inventory.Select(s => s.InstanceId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(["vc-1"], Registry().Events.Select(s => s.InstanceId));
        Assert.Empty(_logged);
    }

    [Fact]
    public async Task A_simplivity_observation_row_says_inventory_only_and_stays_not_polled()
    {
        // Live, the KBSVT Observation row kept "this build has no collector for
        // kind 'simplivity'" from before S3: nothing refreshed it any more.
        // And no role means no alarm (ADR-0026): the metrics stand-in raises no
        // "Collector unreachable (metrics)", while a vCenter whose metrics
        // read fails still does.
        _connections.Add(Connection("svt-1") with { Kind = ConnectionKinds.Simplivity });
        _connections.Add(Connection("vc-1"));

        var result = await new ObservationCollectionPipeline(_clock).RunAsync(
            Registry().Observations, [], CollectionPolicy.Default with { MaxRetries = 0 }, CancellationToken.None);

        Assert.Equal(
            ["vc-1"],
            result.CollectionAlerts
                .Where(a => a.Title == "Collector unreachable (metrics)")
                .Select(a => a.Fingerprint.Value.Contains("svt-1", StringComparison.Ordinal) ? "svt-1" : "vc-1"));

        var standIn = Registry().Observations.Single(s => s.InstanceId == "svt-1");
        var health = Assert.Single(result.Health, h => h.InstanceId == "svt-1");
        Assert.Equal(CollectionFailureKind.NotConfigured, health.LastFailureKind);
        Assert.Contains("SimpliVity is read for inventory only", health.LastFailureDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("no collector for kind", health.LastFailureDetail, StringComparison.Ordinal);
        Assert.Same(standIn, Registry().Observations.Single(s => s.InstanceId == "svt-1"));
        Assert.Empty(_logged);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// Whether a read through this source was refused because its client is gone.
    /// </summary>
    /// <remarks>
    /// The address points at a port nothing listens on, so a read always fails;
    /// what the tests are asking is <em>how</em>. A live client gets as far as
    /// the socket and comes back with a connection failure, which is the
    /// product working. A disposed one never leaves the process and comes back
    /// with <see cref="ObjectDisposedException"/>, which is the product
    /// inventing a fault for a vCenter that was never asked.
    /// </remarks>
    private static async Task<bool> ReadWasRefusedByDisposalAsync(IInventorySource source)
    {
        var thrown = await Record.ExceptionAsync(() => source.ReadAsync(CancellationToken.None));

        Assert.NotNull(thrown);

        for (var error = thrown; error is not null; error = error.InnerException)
        {
            if (error is ObjectDisposedException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A connection whose reads fail at the socket rather than hanging.
    /// </summary>
    /// <remarks>
    /// Port 1 on the loopback interface refuses immediately, so a read through
    /// a live client fails in milliseconds and for a reason that has nothing to
    /// do with disposal. A hostname would spend the test's time in DNS, and a
    /// reachable address would spend it talking to something that is not a
    /// vCenter.
    /// </remarks>
    private static SourceConnection Connection(string instanceId) => new()
    {
        InstanceId = instanceId,
        Kind = VsphereSourceRegistry.VsphereKind,
        BaseAddress = new Uri("https://127.0.0.1:1"),
        Username = "svc-observatory",
        Password = Secret.From("a working one"),
    };
}
