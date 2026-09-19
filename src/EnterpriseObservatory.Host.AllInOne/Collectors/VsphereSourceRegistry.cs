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

    public IReadOnlyList<IInventorySource> Inventory =>
        [.. Refresh().Select(b => b.Inventory)];

    public IReadOnlyList<IObservationSource> Observations =>
        [.. Refresh().Select(b => b.Observation)];

    private List<Built> Refresh()
    {
        lock (_gate)
        {
            foreach (var old in _retired)
            {
                old.Dispose();
            }

            _retired.Clear();

            var wanted = _catalogue.All().Where(Usable).ToList();
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

            return [.. _built.Values];
        }
    }

    /// <summary>Whether this connection can be polled at all.</summary>
    /// <remarks>
    /// A connection with no usable password is skipped rather than attempted.
    /// Attempting it would be a guaranteed rejected login on every cycle, which
    /// is the shape of failure that locks the monitoring account out — and
    /// being refused by the directory tells the operator nothing they did not
    /// already know from the blank password field.
    /// </remarks>
    private bool Usable(SourceConnection connection)
    {
        if (!connection.IsEnabled)
        {
            return false;
        }

        if (!string.Equals(connection.Kind, VsphereKind, StringComparison.Ordinal))
        {
            _reportUnusable(connection.InstanceId, $"no collector for kind '{connection.Kind}'");
            return false;
        }

        if (connection.PasswordUnreadable)
        {
            _reportUnusable(
                connection.InstanceId,
                "its stored password cannot be decrypted; enter it again");
            return false;
        }

        if (connection.Password.IsEmpty)
        {
            _reportUnusable(connection.InstanceId, "no password has been entered");
            return false;
        }

        return true;
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
        }
    }
}
