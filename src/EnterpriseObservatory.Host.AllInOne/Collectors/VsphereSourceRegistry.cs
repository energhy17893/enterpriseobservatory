using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Redfish;
using EnterpriseObservatory.Collectors.Simplivity;
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

    /// <summary>One connection's collectors, whatever its kind.</summary>
    /// <param name="Close">
    /// Ends the session the kind holds, then closes its sockets. Called once,
    /// when nobody can still be reading through it; never throws.
    /// </param>
    /// <param name="Observation">Null for a kind that reads no metrics.</param>
    /// <param name="Events">Null for a kind that reads no event stream.</param>
    /// <param name="Configuration">Null for a kind with no configuration tier.</param>
    private sealed record Built(
        Shape Shape,
        Func<Task> Close,
        IInventorySource Inventory,
        IObservationSource? Observation,
        IEventSource? Events,
        IConfigurationTierSource? Configuration = null);

    private readonly SourceConnectionCatalogue _catalogue;
    private readonly IEntityGraphStore _graph;
    private readonly IClock _clock;
    private readonly Action<string, string> _reportUnusable;

    /// <summary>A session or token that could not be given back on close; a warning, not an error.</summary>
    private readonly Action<string, string> _reportCloseWarning;

    /// <summary>
    /// Instance, annotations, alerts, fold failures, VMs not SAFE — one per
    /// SimpliVity read (S3 follow-up diagnostic).
    /// </summary>
    private readonly Action<string, int, int, int, int, string> _reportSimplivityRead;
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
        Configuration,
    }

    /// <summary>A client that is no longer handed out, and who still might hold it.</summary>
    /// <param name="Close">Closes the transport and session once nobody can be reading through it.</param>
    /// <param name="AfterInventoryPass">The inventory loop's pass count when it was retired.</param>
    /// <param name="AfterObservationPass">The observation loop's pass count when it was retired.</param>
    /// <param name="AfterConfigurationPass">The configuration loop's pass count when it was retired.</param>
    private sealed record Retired(
        Func<Task> Close,
        long AfterInventoryPass,
        long AfterObservationPass,
        long AfterConfigurationPass);

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
    private long _configurationPasses;

    /// <summary>
    /// How many requests one source may have in flight at once (F2). Applied
    /// to every <see cref="SourceRequestGate"/> this registry builds.
    /// </summary>
    private readonly int _maxRequestsPerSource;

    /// <summary>A configuration pass skips a carry younger than this; see <see cref="VsphereConfigurationSource"/>.</summary>
    private readonly TimeSpan? _configurationInterval;

    public VsphereSourceRegistry(
        SourceConnectionCatalogue catalogue,
        IEntityGraphStore graph,
        IClock clock,
        Action<string, string> reportUnusable,
        int maxRequestsPerSource = SourceRequestGate.DefaultLimit,
        Action<string, string>? reportCloseWarning = null,
        Action<string, int, int, int, int, string>? reportSimplivityRead = null,
        TimeSpan? configurationInterval = null)
    {
        _configurationInterval = configurationInterval;
        _reportSimplivityRead = reportSimplivityRead ?? ((_, _, _, _, _, _) => { });
        _reportCloseWarning = reportCloseWarning ?? ((_, _) => { });
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

            return [.. collectors.Select(b => b.Observation).OfType<IObservationSource>(), .. unusable];
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

            return [.. collectors.Select(b => b.Events).OfType<IEventSource>()];
        }
    }

    /// <remarks>
    /// Its own loop and its own pass count: the configuration loop reads
    /// through the same client as the other two, on a third cadence, so a
    /// retired client waits for it as well. Unusable connections are left out,
    /// for the reason <see cref="Events"/> gives.
    /// </remarks>
    public IReadOnlyList<IConfigurationTierSource> Configuration
    {
        get
        {
            var (collectors, _) = Refresh(Reader.Configuration);

            return [.. collectors.Select(b => b.Configuration).OfType<IConfigurationTierSource>()];
        }
    }

    private (List<Built> Collectors, List<UnusableSource> Unusable) Refresh(Reader reader)
    {
        lock (_gate)
        {
            // Counted before anything is disposed, so that this loop's own
            // arrival is what releases the clients it was holding last pass.
            switch (reader)
            {
                case Reader.Inventory:
                    _inventoryPasses++;
                    break;
                case Reader.Observations:
                    _observationPasses++;
                    break;
                default:
                    _configurationPasses++;
                    break;
            }

            DisposeWhatNobodyCanStillBeReading();

            var wanted = new List<SourceConnection>();
            var unusable = new List<UnusableSource>();

            // A disabled connection is the one case that stays quiet. Somebody
            // decided it should not be read, and alerting about a decision
            // being honoured is how an inbox becomes something people mute.
            foreach (var connection in _catalogue.All().Where(c => c.IsEnabled))
            {
                if (WhyUnusable(connection) is not { } why)
                {
                    wanted.Add(connection);
                    continue;
                }

                _reportUnusable(connection.InstanceId, why.Reason);
                unusable.Add(StandIn(connection.InstanceId, why.Reason, why.Kind));
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
        _retired.Add(new Retired(built.Close, _inventoryPasses, _observationPasses, _configurationPasses));

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
    private static async Task CloseVsphereAsync(VsphereSessionChannel channel)
    {
        try
        {
            await channel.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            channel.Dispose();
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
                HasMovedOn(_observationPasses, retired.AfterObservationPass) &&
                HasMovedOn(_configurationPasses, retired.AfterConfigurationPass))
            {
                _ = retired.Close();
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

    private UnusableSource StandIn(string instanceId, string reason, CollectionFailureKind kind)
    {
        if (_unusable.TryGetValue(instanceId, out var existing) &&
            string.Equals(existing.Reason, reason, StringComparison.Ordinal))
        {
            return existing;
        }

        var fresh = new UnusableSource(instanceId, reason, kind);
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
    private static (string Reason, CollectionFailureKind Kind)? WhyUnusable(SourceConnection connection)
    {
        if (!BuiltKinds.Contains(connection.Kind, StringComparer.Ordinal))
        {
            // The estate describing itself, not a credential an operator forgot
            // (N1, ADR-0026): nothing was ever going to be polled here, so
            // nothing raises "Collector unreachable" for it either.
            return (
                $"this build has no collector for kind '{connection.Kind}'; " +
                "remove the connection, or deploy a build that reads that kind",
                CollectionFailureKind.NotConfigured);
        }

        if (connection.PasswordUnreadable)
        {
            return (
                "its stored password cannot be decrypted; restore the Data Protection " +
                "key ring that belongs with this database, or enter the password again",
                CollectionFailureKind.CredentialsUnavailable);
        }

        if (connection.Password.IsEmpty)
        {
            return (
                "no password has been entered for it; enter one on the Connections screen",
                CollectionFailureKind.CredentialsUnavailable);
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
    /// Failing also puts the connection back on the one screen an operator
    /// actually watches. The runner turns the failure into a health record —
    /// Unknown, never succeeded, which the collectors list shows and
    /// <c>FailingCollectors</c> counts.
    /// </para>
    /// <para>
    /// <see cref="Kind"/> decides whether that also raises
    /// <c>Collector unreachable</c> (N1, ADR-0026). No collector built for the
    /// kind (<see cref="CollectionFailureKind.NotConfigured"/>) is the estate
    /// describing itself, not a collector failing to reach it, and paging on a
    /// decision nobody can fix by being paged is what made the redfish
    /// placeholder's alert permanent on the live estate — so that one stays
    /// quiet. A missing or unreadable password
    /// (<see cref="CollectionFailureKind.CredentialsUnavailable"/>) is still an
    /// operator's fix waiting, and still pages until they make it.
    /// </para>
    /// <para>
    /// Either way the failure is not worth retrying (<see cref="CollectionFailures.IsWorthRetrying"/>),
    /// so the runner tries once and the breaker holds it off afterwards. There
    /// is nothing to retry: no request leaves the process. It resolves itself
    /// — once the connection is usable this object is gone, the real collector
    /// answers in its place, and the health record it left behind is the next
    /// one it writes.
    /// </para>
    /// </remarks>
    private sealed class UnusableSource(string instanceId, string reason, CollectionFailureKind kind)
        : IInventorySource, IObservationSource
    {
        public string InstanceId { get; } = instanceId;

        /// <summary>Why, in the operator's terms. Never anything password-shaped.</summary>
        public string Reason { get; } = reason;

        /// <summary>
        /// <see cref="CollectionFailureKind.NotConfigured"/> for a kind this
        /// build has no collector for — never an alarm (N1, ADR-0026) — or
        /// <see cref="CollectionFailureKind.CredentialsUnavailable"/> for a
        /// missing or unreadable password, which stays one until an operator
        /// fixes it.
        /// </summary>
        public CollectionFailureKind Kind { get; } = kind;

        Task<InventorySnapshot> IInventorySource.ReadAsync(CancellationToken cancellationToken) =>
            throw Fault();

        Task<ObservationBatch> IObservationSource.ReadAsync(
            ObservationReadContext context, CancellationToken cancellationToken) =>
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
            new($"'{InstanceId}' is not being polled: {Reason}.", Kind);
    }

    /// <summary>A connection the product is configured to read but cannot.</summary>
    /// <remarks>
    /// Carries <see cref="ICollectionFault"/> so the runner treats it the way
    /// it treats a rejected password: report it, and do not ask again. An
    /// unclassified exception stays retryable, and retrying this one would burn
    /// the retry budget and the cycle's patience on a decision that cannot
    /// change until a person changes it.
    /// </remarks>
    private sealed class ConnectionNotUsableException(string message, CollectionFailureKind kind)
        : InvalidOperationException(message), ICollectionFault
    {
        public CollectionFailureKind Kind => kind;
    }

    /// <summary>The vSphere collector's kind.</summary>
    public const string VsphereKind = ConnectionKinds.Vsphere;

    /// <summary>The kinds this build has a collector for; <see cref="Build"/> answers each.</summary>
    private static readonly string[] BuiltKinds = [ConnectionKinds.Vsphere, ConnectionKinds.Simplivity, ConnectionKinds.Redfish];

    /// <summary>Builds a connection's collectors by its kind.</summary>
    private Built Build(SourceConnection connection, Shape shape) => connection.Kind switch
    {
        ConnectionKinds.Vsphere => BuildVsphere(connection, shape),
        ConnectionKinds.Simplivity => BuildSimplivity(connection, shape),
        ConnectionKinds.Redfish => BuildRedfish(connection, shape),
        _ => throw new InvalidOperationException(
            $"No collector for kind '{connection.Kind}'; WhyUnusable should have held it back."),
    };

    private Built BuildVsphere(SourceConnection connection, Shape shape)
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

        // The handler policy (cookies, the TLS decision) and the session
        // itself — login, re-login, logout — belong to the channel, not to
        // VsphereClient (F3). How certificate validation is relaxed is a
        // security decision, and a second copy of it is how the two drift
        // until one of them is quietly wrong.
        var handler = VsphereSessionChannel.CreateHandler(options);

        // One gate per source instance, not per cycle (F2) — created here,
        // beside the channel it bounds, and kept for the connection's whole
        // lifetime so it still applies to a read a previous cycle abandoned.
        // It now lives inside the channel, not beside it (F3, §3.2).
        var requestGate = new SourceRequestGate(_maxRequestsPerSource);
        var channel = new VsphereSessionChannel(handler, options, requestGate);

        var client = new VsphereClient(channel, options)
        {
            // M8.7: the vCenter certificate's expiry, by a handshake alone.
            CertificateReader = new TlsEndpointCertificateReader(options.RequestTimeout),
        };

        return new Built(
            shape,
            () => CloseVsphereAsync(channel),
            new VsphereInventorySource(client, _clock),
            // No gap store: the runner keeps the gap record now (F5, ADR-0005 §3).
            new VsphereObservationSource(
                client, new GraphSampleTargetProvider(_graph, connection.InstanceId), _clock),
            new VsphereEventSource(client, _clock),
            new VsphereConfigurationSource(client, _configurationInterval));
    }

    /// <summary>
    /// SimpliVity: inventory only — no metrics, and its alarms reach the
    /// product through vCenter's event stream, not this source (§10.7).
    /// </summary>
    /// <remarks>
    /// One token for the connection's life (F3): the channel keeps it across
    /// cycles and replaces it once on a 401. Folding reads the graph through
    /// <see cref="GraphFoldingDirectory"/>, never the collector itself
    /// (ADR-0027 §4).
    /// </remarks>
    /// <summary>
    /// Counts a SimpliVity snapshot as the collector hands it over, before the
    /// runner or the cycle touch it (S3 follow-up diagnostic).
    /// </summary>
    private sealed class CountedRead(IInventorySource inner, Action<string, int, int, int, int, string> report)
        : IInventorySource
    {
        public string InstanceId => inner.InstanceId;

        public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            var snapshot = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);

            report(
                snapshot.SourceInstanceId,
                snapshot.Annotations.Count,
                snapshot.Alerts.Count,
                snapshot.Failures.Count,
                snapshot.Annotations.Count(a =>
                    a.Settings.TryGetValue("simplivity.ha_status", out var ha) &&
                    !string.Equals(ha, "SAFE", StringComparison.Ordinal)),
                Filled(snapshot.Coverage));

            return snapshot;
        }

        /// <summary>E.g. "virtual_machines.ha_status 699/699, hosts.upgrade_state 26/26".</summary>
        /// <remarks>
        /// The fields a verdict rests on: "0 VMs not SAFE" next to
        /// "ha_status 0/699" is the measurement that found the missing
        /// show_optional_fields (23 September 2026).
        /// </remarks>
        internal static string Filled(IReadOnlyList<PropertyCoverage> coverage) =>
            coverage.Count == 0
                ? "nothing measured"
                : string.Join(", ", coverage.Select(c => $"{c.ObjectType}.{c.Property} {c.Answered}/{c.Asked}"));
    }

    /// <summary>
    /// The stand-in for a role the kind does not have: NotPolled with a
    /// reason, and — unlike <see cref="UnusableSource"/> — no "Collector
    /// unreachable" alert (<see cref="IRoleNotApplicable"/>, ADR-0026).
    /// </summary>
    private sealed class RoleNotApplicable(string instanceId, string reason)
        : IObservationSource, IRoleNotApplicable
    {
        public string InstanceId { get; } = instanceId;

        public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
            throw new ConnectionNotUsableException(
                $"'{InstanceId}' is not being polled: {reason}.", CollectionFailureKind.NotConfigured);
    }

    internal const string SimplivityHasNoMetrics =
        "SimpliVity is read for inventory only; this build has no observation collector for it";

    private Built BuildSimplivity(SourceConnection connection, Shape shape)
    {
        var options = new SimplivityConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
        };

        var channel = new SimplivitySessionChannel(
            SimplivitySessionChannel.CreateHandler(options), options, new SourceRequestGate(_maxRequestsPerSource));

        return new Built(
            shape,
            () => CloseSimplivityAsync(connection.InstanceId, channel),
            new CountedRead(
                new SimplivityInventorySource(connection.InstanceId, channel, new GraphFoldingDirectory(_graph), _clock),
                _reportSimplivityRead),
            // A stand-in, not nothing: without it the Observation health row
            // kept the last "no collector for kind" message forever. It stays
            // NotConfigured, so /health keeps it NotPolled, and now says why.
            Observation: new RoleNotApplicable(connection.InstanceId, SimplivityHasNoMetrics),
            Events: null);
    }

    internal const string RedfishHasNoMetrics =
        "Redfish is read for inventory only; this build has no observation collector for it";

    /// <summary>
    /// One iLO (M6.1): inventory only, Basic auth per request — no session to
    /// close, so closing is just the sockets. Folding reads the graph through
    /// <see cref="GraphFoldingDirectory"/> (ADR-0027 §4).
    /// </summary>
    private Built BuildRedfish(SourceConnection connection, Shape shape)
    {
        var options = new RedfishConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
        };

        var channel = new RedfishChannel(
            RedfishChannel.CreateHandler(options), options, new SourceRequestGate(RedfishChannel.Parallelism));

        return new Built(
            shape,
            () =>
            {
                channel.Dispose();
                return Task.CompletedTask;
            },
            new RedfishInventorySource(connection.InstanceId, channel, new GraphFoldingDirectory(_graph), _clock),
            Observation: new RoleNotApplicable(connection.InstanceId, RedfishHasNoMetrics),
            Events: null);
    }

    /// <summary>
    /// Revokes the token, then closes the sockets. A refused revoke — the live
    /// OVC answers 401 — is a warning, never an error: the token idles out.
    /// </summary>
    private async Task CloseSimplivityAsync(string instanceId, SimplivitySessionChannel channel)
    {
        try
        {
            if (await channel.RevokeAsync(CancellationToken.None).ConfigureAwait(false) is { } warning)
            {
                _reportCloseWarning(instanceId, warning);
            }
        }
        finally
        {
            channel.Dispose();
        }
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
            var closing = _retired.Select(r => r.Close())
                .Concat(_built.Values.Select(b => b.Close()))
                .ToArray();

            Task.WhenAll(closing).Wait(ShutdownLogoutDeadline);

            _retired.Clear();
            _built.Clear();
            _unusable.Clear();
        }
    }
}
