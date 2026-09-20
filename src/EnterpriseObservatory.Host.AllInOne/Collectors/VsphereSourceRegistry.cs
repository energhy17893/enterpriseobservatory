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
        IInventorySource Inventory,
        IObservationSource Observation);

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

    /// <summary>
    /// Clients replaced or removed by the previous refresh.
    /// </summary>
    /// <remarks>
    /// Disposed one refresh late, deliberately. A cycle that is running right
    /// now is holding the source it was handed, and disposing its client out
    /// from under it would abort a read that was going fine. A refresh happens
    /// once per cycle and the source timeout is shorter than a cycle, so by the
    /// next refresh nothing is still using these.
    /// </remarks>
    private readonly List<HttpClient> _retired = [];

    public VsphereSourceRegistry(
        SourceConnectionCatalogue catalogue,
        IEntityGraphStore graph,
        IClock clock,
        Action<string, string> reportUnusable)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reportUnusable = reportUnusable ?? throw new ArgumentNullException(nameof(reportUnusable));
    }

    public IReadOnlyList<IInventorySource> Inventory
    {
        get
        {
            var (collectors, unusable) = Refresh();

            return [.. collectors.Select(b => b.Inventory), .. unusable];
        }
    }

    public IReadOnlyList<IObservationSource> Observations
    {
        get
        {
            var (collectors, unusable) = Refresh();

            return [.. collectors.Select(b => b.Observation), .. unusable];
        }
    }

    private (List<Built> Collectors, List<UnusableSource> Unusable) Refresh()
    {
        lock (_gate)
        {
            foreach (var old in _retired)
            {
                old.Dispose();
            }

            _retired.Clear();

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
                _retired.Add(_built[gone].Http);
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

                    _retired.Add(existing.Http);
                }

                _built[connection.InstanceId] = Build(connection, shape);
            }

            return ([.. _built.Values], unusable);
        }
    }

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

        var client = new VsphereClient(http, options);

        return new Built(
            shape,
            http,
            new VsphereInventorySource(client, _clock),
            new VsphereObservationSource(
                client, new GraphSampleTargetProvider(_graph, connection.InstanceId), _clock));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _retired.Concat(_built.Values.Select(b => b.Http)))
            {
                client.Dispose();
            }

            _retired.Clear();
            _built.Clear();
            _unusable.Clear();
        }
    }
}
