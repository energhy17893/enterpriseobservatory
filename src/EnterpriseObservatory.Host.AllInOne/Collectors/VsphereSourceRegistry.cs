using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>
/// Builds collectors from the connections that exist right now.
/// </summary>
/// <remarks>
/// <para>
/// The composition root used to resolve this once, at startup, from
/// configuration. That was right while a connection could only come from a
/// settings file — the file cannot change without a restart, so neither can the
/// list. It stops being right the moment a person can add a vCenter in the
/// product: a list captured at boot would ignore everything they did until
/// somebody restarted the service, and it would do so silently, which is the
/// worst way for a product to disagree with its own screens.
/// </para>
/// <para>
/// So the list is rebuilt from the store, and the objects behind it are kept.
/// Handing back a new <see cref="System.Net.Http.HttpClient"/> every thirty
/// seconds exhausts the socket pool slowly enough that nobody connects the
/// eventual failures to this.
/// </para>
/// <para>
/// A connection that cannot be polled still appears in the list, as an
/// <see cref="UnusableSource"/> that reports why on every read. It used to be
/// dropped here with a log line, and a dropped connection is invisible: no
/// collector is built for it, so there is no health record, so the collectors
/// screen has nothing to show and <c>FailingCollectors</c> has nothing to
/// count. Restore a database without the key ring beside it — which ADR-0015
/// deliberately allows — and every password becomes undecryptable, the whole
/// estate stops being read, and the inbox stays empty and green. "We are not
/// looking" has to be visible in the product, not in a file on the server.
/// </para>
/// </remarks>
public sealed class VsphereSourceRegistry : ISourceRegistry, IDisposable
{
    /// <summary>What decides whether a built collector is still the right one.</summary>
    /// <remarks>
    /// Only the fields that shape the connection itself. A rename of the
    /// person who created it, or a change to whether it is enabled, must not
    /// tear down a working session and start a new one.
    /// </remarks>
    private sealed record Shape(
        Uri BaseAddress,
        string Username,
        Secret Password,
        bool AcceptUntrustedCertificate,
        int PageSize);

    private sealed record Built(
        Shape Shape,
        HttpClient Http,
        VsphereClient Client,
        IInventorySource Inventory,
        IObservationSource Observation,
        IEventSource Events,
        SourceRequestGate RequestGate);

    private readonly SourceConnectionCatalogue _catalogue;
    private readonly IEntityGraphStore _graph;
    private readonly IClock _clock;
    private readonly Action<string, string> _reportUnusable;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Built> _built = new(StringComparer.Ordinal);

    /// <summary>The stand-ins for connections that cannot be polled, by name.</summary>
    /// <remarks>
    /// Kept for the same reason the built collectors are: the registry promises
    /// the same source objects back while nothing has changed. These hold no
    /// socket, so keeping them costs nothing — but handing back a new one every
    /// cycle would quietly make that promise false for the next thing that
    /// depends on it.
    /// </remarks>
    private readonly Dictionary<string, UnusableSource> _unusable = new(StringComparer.Ordinal);

    /// <summary>Which loop is asking for sources.</summary>
    /// <remarks>
    /// The registry has two readers on two cadences — five minutes and thirty
    /// seconds — and they are the only thing that can be holding a client.
    /// Knowing which one is asking is what turns "a refresh happened" into the
    /// far stronger "that loop finished the cycle it was in".
    /// </remarks>
    private enum Reader
    {
        Inventory,
        Observations,
    }

    /// <summary>A client that is no longer handed out, and who still might hold it.</summary>
    /// <param name="Http">The client to dispose once nobody can be reading through it.</param>
    /// <param name="AfterInventoryPass">The inventory loop's pass count when it was retired.</param>
    /// <param name="AfterObservationPass">The observation loop's pass count when it was retired.</param>
    /// <param name="Client">The session that goes with it, logged out before the client is closed.</param>
    private sealed record Retired(
        HttpClient Http,
        VsphereClient Client,
        SourceRequestGate RequestGate,
        long AfterInventoryPass,
        long AfterObservationPass);

    /// <summary>
    /// Clients replaced or removed, waiting for both loops to let go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be disposed one refresh late, on the reasoning that a
    /// refresh happens once per cycle and a source times out well inside one.
    /// That reasoning is false, and the way it is false is the defect: there
    /// are two loops, <see cref="Inventory"/> and <see cref="Observations"/>,
    /// refreshing five minutes and thirty seconds apart. Refresh N+1 from
    /// either one disposed what refresh N from the <em>other</em> retired. A
    /// password edited during a long inventory pass over a large estate had
    /// the observation loop dispose that pass's client two ticks later, mid
    /// read — an <see cref="ObjectDisposedException"/> counted as a source
    /// failure, ConsecutiveFailures climbing, and a "Collector unreachable"
    /// alert raised against a vCenter that was answering fine. A fabricated
    /// fault, in a product whose one claim is that it does not lie about what
    /// it can see.
    /// </para>
    /// <para>
    /// So retirement is counted per loop instead of per refresh, and the count
    /// rests on something structural rather than on a timing estimate: each
    /// loop is serial, so a loop asking for sources <em>proves</em> the cycle
    /// it was in has ended, and with it every read through what it was holding.
    /// A client is disposable once each loop has asked at least once since it
    /// was retired. A loop that had never asked at the moment of retirement —
    /// pass zero — was never handed the client and imposes no wait, which is
    /// what keeps a host or a test that drives only one cadence from
    /// accumulating clients forever.
    /// </para>
    /// <para>
    /// Waiting is not leaking. The wait is bounded by the slower loop's
    /// interval, <see cref="Dispose"/> drains whatever is still queued at
    /// shutdown, and nothing here is conditional on time — an
    /// <see cref="HttpClient"/> kept indefinitely is a socket pool and a
    /// logged-in vSphere session kept indefinitely, and vCenter counts
    /// sessions.
    /// </para>
    /// </remarks>
    private readonly List<Retired> _retired = [];

    /// <summary>How many times each loop has asked for its sources.</summary>
    private long _inventoryPasses;
    private long _observationPasses;

    /// <summary>The source-level gap record every observation source is given (T0.4).</summary>
    private readonly ICollectionGapStore? _gaps;

    /// <summary>
    /// How many requests one source may have in flight at once (F2). Applied
    /// to every <see cref="SourceRequestGate"/> this registry builds.
    /// </summary>
    private readonly int _maxRequestsPerSource;

    public VsphereSourceRegistry(
        SourceConnectionCatalogue catalogue,
        IEntityGraphStore graph,
        IClock clock,
        Action<string, string> reportUnusable,
        ICollectionGapStore? gaps = null,
        int maxRequestsPerSource = SourceRequestGate.DefaultLimit)
    {
        _gaps = gaps;
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reportUnusable = reportUnusable ?? throw new ArgumentNullException(nameof(reportUnusable));
        _maxRequestsPerSource = maxRequestsPerSource;
    }

    public IReadOnlyList<IInventorySource> Inventory
    {
        get
        {
            var (collectors, unusable) = Refresh(Reader.Inventory);

            return [.. collectors.Select(b => b.Inventory), .. unusable];
        }
    }

    public IReadOnlyList<IObservationSource> Observations
    {
        get
        {
            var (collectors, unusable) = Refresh(Reader.Observations);

            return [.. collectors.Select(b => b.Observation), .. unusable];
        }
    }

    /// <remarks>
    /// Counted as the inventory loop, because that is the loop that asks: events
    /// are read on the inventory rhythm, after its cycle. The loop is serial, so
    /// asking here proves the inventory read before it has finished — exactly
    /// what a pass count is taken to mean — and a second pass per cycle only
    /// lets a retired client go sooner, never while it is still being read.
    /// Unusable connections are left out; the inventory cycle already reports
    /// them, once.
    /// </remarks>
    public IReadOnlyList<IEventSource> Events
    {
        get
        {
            var (collectors, _) = Refresh(Reader.Inventory);

            return [.. collectors.Select(b => b.Events)];
        }
    }

    private (List<Built> Collectors, List<UnusableSource> Unusable) Refresh(Reader reader)
    {
        lock (_gate)
        {
            // Counted before anything is disposed, so that this loop's own
            // arrival is what releases the clients it was holding last pass.
            if (reader == Reader.Inventory)
            {
                _inventoryPasses++;
            }
            else
            {
                _observationPasses++;
            }

            DisposeWhatNobodyCanStillBeReading();

            var wanted = new List<SourceConnection>();
            var unusable = new List<UnusableSource>();

            // A disabled connection is the one case that stays quiet. Somebody
            // decided it should not be read, and alerting about a decision
            // being honoured is how an inbox becomes something people mute.
            foreach (var connection in _catalogue.All().Where(c => c.IsEnabled))
            {
                if (WhyUnusable(connection) is not { } reason)
                {
                    wanted.Add(connection);
                    continue;
                }

                _reportUnusable(connection.InstanceId, reason);
                unusable.Add(StandIn(connection.InstanceId, reason));
            }

            Forget(unusable);

            var names = wanted.Select(c => c.InstanceId).ToHashSet(StringComparer.Ordinal);

            foreach (var gone in _built.Keys.Where(name => !names.Contains(name)).ToList())
            {
                Retire(_built[gone]);
                _built.Remove(gone);
            }

            foreach (var connection in wanted)
            {
                var shape = new Shape(
                    connection.BaseAddress,
                    connection.Username,
                    connection.Password,
                    connection.AcceptUntrustedCertificate,
                    connection.PageSize);

                if (_built.TryGetValue(connection.InstanceId, out var existing))
                {
                    if (existing.Shape == shape)
                    {
                        continue;
                    }

                    Retire(existing);
                }

                _built[connection.InstanceId] = Build(connection, shape);
            }

            return ([.. _built.Values], unusable);
        }
    }

    /// <summary>Stops handing a client out, and records who might still hold it.</summary>
    /// <remarks>
    /// Both loops have been handed every client that was ever built, so both
    /// have to move on before it can be closed. The pass numbers taken here are
    /// what "move on" is measured against.
    /// </remarks>
    private void Retire(Built built) =>
        _retired.Add(new Retired(
            built.Http, built.Client, built.RequestGate, _inventoryPasses, _observationPasses));

    /// <summary>Ends the vCenter session, then closes the sockets under it.</summary>
    /// <remarks>
    /// <para>
    /// In that order, because the logout travels over the client being closed.
    /// Only the sockets used to be closed, which left the session itself on
    /// the vCenter until its idle timeout: one for every edited or removed
    /// connection and one for every restart, against a limit vCenter enforces.
    /// </para>
    /// <para>
    /// Not awaited by the caller, which holds the registry's lock and must not
    /// hold it across a network call. Nothing can be reading through a client
    /// by the time it reaches here — that is what retirement waited for — so
    /// there is nothing to race. A client that never logged in sends nothing
    /// and is closed before this returns.
    /// </para>
    /// </remarks>
    private static async Task CloseAsync(VsphereClient client, HttpClient http, SourceRequestGate requestGate)
    {
        try
        {
            await client.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
            http.Dispose();
            requestGate.Dispose();
        }
    }

    /// <summary>
    /// Closes every retired client both loops have moved past.
    /// </summary>
    /// <remarks>
    /// A loop asking for its sources has, by the fact of asking, finished the
    /// cycle it was in: the loops are serial. So a loop whose pass count has
    /// risen since a client was retired cannot still be reading through it. A
    /// loop still on the pass it was on — the long inventory sweep in the
    /// middle of which somebody corrected a password — can, and is the case
    /// the old one-refresh-late rule got wrong.
    /// </remarks>
    private void DisposeWhatNobodyCanStillBeReading()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            var retired = _retired[i];

            if (HasMovedOn(_inventoryPasses, retired.AfterInventoryPass) &&
                HasMovedOn(_observationPasses, retired.AfterObservationPass))
            {
                _ = CloseAsync(retired.Client, retired.Http, retired.RequestGate);
                _retired.RemoveAt(i);
            }
        }
    }

    /// <summary>Whether a loop can no longer be holding what was retired at <paramref name="at"/>.</summary>
    /// <remarks>
    /// Pass zero is a loop that had never asked for sources when the client was
    /// retired, so it was never handed one and never will be — the client is
    /// out of <c>_built</c> before it could be. Without that, anything driving
    /// only one of the two cadences would queue a client per change and never
    /// close any of them.
    /// </remarks>
    private static bool HasMovedOn(long passes, long at) => at == 0 || passes > at;

    /// <summary>Drops the stand-ins for connections that are no longer unusable.</summary>
    /// <remarks>
    /// The other half of self-resolution. A connection whose password has been
    /// entered again must stop being reported as unpollable, and it stops by
    /// no longer being in this list: the cycle observes no alert for it, and
    /// reconciliation resolves the one it raised.
    /// </remarks>
    private void Forget(List<UnusableSource> current)
    {
        var keep = current.Select(u => u.InstanceId).ToHashSet(StringComparer.Ordinal);

        foreach (var gone in _unusable.Keys.Where(name => !keep.Contains(name)).ToList())
        {
            _unusable.Remove(gone);
        }
    }

    private UnusableSource StandIn(string instanceId, string reason)
    {
        if (_unusable.TryGetValue(instanceId, out var existing) &&
            string.Equals(existing.Reason, reason, StringComparison.Ordinal))
        {
            return existing;
        }

        var fresh = new UnusableSource(instanceId, reason);
        _unusable[instanceId] = fresh;

        return fresh;
    }

    /// <summary>Why this connection cannot be polled, or null if it can.</summary>
    /// <remarks>
    /// <para>
    /// A connection with no usable password is never attempted. Attempting it
    /// would be a guaranteed rejected login on every cycle, which is the shape
    /// of failure that locks the monitoring account out — and being refused by
    /// the directory tells the operator nothing they did not already know from
    /// the blank password field.
    /// </para>
    /// <para>
    /// Each reason names its own fix, because the three are not the same
    /// errand. "The key material is gone" sends someone to their backup
    /// procedure, "you never entered one" sends them to a form, and "this
    /// build has no collector for that" sends them to whoever deploys the
    /// service. One generic "not collecting" would send all three to the wrong
    /// place at the worst moment, which is the distinction
    /// <see cref="SourceConnection.PasswordUnreadable"/> exists to preserve.
    /// </para>
    /// </remarks>
    private static string? WhyUnusable(SourceConnection connection)
    {
        if (!string.Equals(connection.Kind, VsphereKind, StringComparison.Ordinal))
        {
            return $"this build has no collector for kind '{connection.Kind}'; " +
                   "remove the connection, or deploy a build that reads that kind";
        }

        if (connection.PasswordUnreadable)
        {
            return "its stored password cannot be decrypted; restore the Data Protection " +
                   "key ring that belongs with this database, or enter the password again";
        }

        if (connection.Password.IsEmpty)
        {
            return "no password has been entered for it; enter one on the Connections screen";
        }

        return null;
    }

    /// <summary>
    /// Stands in for a connection that cannot be polled at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It fails on every read rather than returning an empty snapshot, and the
    /// difference is not cosmetic. A snapshot is a claim about what exists, so
    /// an empty one from a vCenter we never contacted would mark every entity
    /// it ever reported as vanished — the estate would not merely go quiet, it
    /// would disappear. Failing says the only true thing: we did not look.
    /// </para>
    /// <para>
    /// Failing also puts the connection back on the two screens an operator
    /// actually watches. The runner turns the failure into a health record —
    /// Unknown, never succeeded, which the collectors list shows and
    /// <c>FailingCollectors</c> counts — and into the same
    /// <c>Collector unreachable</c> alert every other unreadable source
    /// raises, so it reaches the inbox and the notifier without a second kind
    /// of alert to learn.
    /// </para>
    /// <para>
    /// The failure is <see cref="CollectionFailureKind.NotConfigured"/>, which
    /// is not worth retrying, so the runner tries once and the breaker holds it
    /// off afterwards. There is nothing to retry: no request leaves the
    /// process. It resolves itself — once the connection is usable this object
    /// is gone, the real collector answers in its place, and the alert nobody
    /// had to clear by hand is reconciled away.
    /// </para>
    /// </remarks>
    private sealed class UnusableSource(string instanceId, string reason)
        : IInventorySource, IObservationSource
    {
        public string InstanceId { get; } = instanceId;

        /// <summary>Why, in the operator's terms. Never anything password-shaped.</summary>
        public string Reason { get; } = reason;

        Task<InventorySnapshot> IInventorySource.ReadAsync(CancellationToken cancellationToken) =>
            throw Fault();

        Task<ObservationBatch> IObservationSource.ReadAsync(CancellationToken cancellationToken) =>
            throw Fault();

        /// <summary>
        /// The message an operator reads at 3am.
        /// </summary>
        /// <remarks>
        /// It becomes the alert's "Last failure", so it has to survive being
        /// read out of context: it names the connection, says it is not being
        /// polled, and says what would fix it.
        /// </remarks>
        private ConnectionNotUsableException Fault() =>
            new($"'{InstanceId}' is not being polled: {Reason}.");
    }

    /// <summary>A connection the product is configured to read but cannot.</summary>
    /// <remarks>
    /// Carries <see cref="ICollectionFault"/> so the runner treats it the way
    /// it treats a rejected password: report it, and do not ask again. An
    /// unclassified exception stays retryable, and retrying this one would burn
    /// the retry budget and the cycle's patience on a decision that cannot
    /// change until a person changes it.
    /// </remarks>
    private sealed class ConnectionNotUsableException(string message)
        : InvalidOperationException(message), ICollectionFault
    {
        public CollectionFailureKind Kind => CollectionFailureKind.NotConfigured;
    }

    /// <summary>The only collector kind this build knows.</summary>
    public const string VsphereKind = "vsphere";

    private Built Build(SourceConnection connection, Shape shape)
    {
        var options = new VsphereConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
            InventoryPageSize = connection.PageSize,
        };

        // The handler comes from the collector, not from here. How certificate
        // validation is relaxed is a security decision, and a second copy of it
        // is how the two drift until one of them is quietly wrong.
        var http = new HttpClient(VsphereClient.CreateHandler(options))
        {
            BaseAddress = options.BaseAddress,
        };

        // One gate per source instance, not per cycle (F2) — created here,
        // beside the client it bounds, and kept for the connection's whole
        // lifetime so it still applies to a read a previous cycle abandoned.
        var requestGate = new SourceRequestGate(_maxRequestsPerSource);

        var client = new VsphereClient(http, options, requestGate)
        {
            // M8.7: the vCenter certificate's expiry, by a handshake alone.
            CertificateReader = new TlsEndpointCertificateReader(options.RequestTimeout),
        };

        return new Built(
            shape,
            http,
            client,
            new VsphereInventorySource(client, _clock),
            new VsphereObservationSource(
                client, new GraphSampleTargetProvider(_graph, connection.InstanceId), _clock, _gaps),
            new VsphereEventSource(client, _clock),
            requestGate);
    }

    private static readonly TimeSpan ShutdownLogoutDeadline = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        lock (_gate)
        {
            // Shutdown is the one moment with no next pass to wait for, so
            // everything still queued goes now: a stranded client is a socket
            // pool and a vSphere session the estate keeps counting.
            //
            // Logged out as well as closed, together and against one short
            // deadline: a host that is stopping cannot wait on a vCenter that
            // is not answering, and the idle timeout is still the backstop.
            var closing = _retired.Select(r => CloseAsync(r.Client, r.Http, r.RequestGate))
                .Concat(_built.Values.Select(b => CloseAsync(b.Client, b.Http, b.RequestGate)))
                .ToArray();

            Task.WhenAll(closing).Wait(ShutdownLogoutDeadline);

            _retired.Clear();
            _built.Clear();
            _unusable.Clear();
        }
    }
}
