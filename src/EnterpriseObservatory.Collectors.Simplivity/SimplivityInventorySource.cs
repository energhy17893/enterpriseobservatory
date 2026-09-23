using System.Globalization;
using System.Text.Json;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Simplivity;

/// <summary>
/// What the SimpliVity source needs to know about entities other sources
/// own, to fold onto them (ADR-0027 §4).
/// </summary>
/// <remarks>
/// Read-only, answered by the host from the graph — the same arrangement as
/// the vSphere sample-target provider. The collector never reads the graph
/// itself (ADR-0025).
/// </remarks>
public interface ISimplivityFoldingDirectory
{
    /// <summary>The source id of the vCenter whose <c>about.instanceUuid</c> this is, or null.</summary>
    string? VcenterFor(string instanceUuid);

    /// <summary>Whether that entity exists now and is not vanished.</summary>
    bool Contains(EntityId entity);

    /// <summary>The VM whose instance UUID mark this is, or null.</summary>
    EntityId? VirtualMachineByInstanceUuid(string instanceUuid);
}

/// <summary>
/// SimpliVity (OmniStack REST) state, folded onto the vSphere entities the
/// same hardware already is (ADR-0027). Inventory only; no metrics.
/// </summary>
/// <remarks>
/// <para>
/// No entity of its own. A SimpliVity host is an ESXi host, an OmniStack
/// cluster is a vSphere cluster, and a SimpliVity VM is a vSphere VM, so this
/// source annotates them under <c>simplivity.*</c> and raises alerts on them.
/// Folding is exact or not at all: <c>hypervisor_object_id</c> is
/// <c>&lt;vCenter instanceUuid&gt;:HostSystem:host-N</c> (26/26 on Kibar), the
/// UUID names the vCenter connection, <c>host-N</c> the entity. Anything that
/// does not resolve is a "could not fold" failure, never a guess by name.
/// </para>
/// <para>
/// Alerts are three-valued, not Centreon's "anything but SAFE warns"
/// (§10.8): DEGRADED warns, DEFUNCT is critical, SYNCING is transient and
/// OUT_OF_SCOPE informational — data only. Upgrade states are data only too
/// (their findings are S2's).
/// </para>
/// </remarks>
public sealed class SimplivityInventorySource(
    string instanceId,
    SimplivitySessionChannel channel,
    ISimplivityFoldingDirectory directory,
    IClock clock) : IInventorySource
{
    /// <summary>The annotation namespace (ADR-0027).</summary>
    public const string Namespace = "simplivity";

    /// <summary>The largest page the API serves (measured, API 1.28).</summary>
    public const int PageLimit = 500;

    private readonly SimplivitySessionChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    private readonly ISimplivityFoldingDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string InstanceId { get; } = instanceId ?? throw new ArgumentNullException(nameof(instanceId));

    public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var hosts = await ReadAllAsync("hosts", cancellationToken).ConfigureAwait(false);
        var clusters = await ReadAllAsync("omnistack_clusters", cancellationToken).ConfigureAwait(false);
        var vms = await ReadAllAsync("virtual_machines", cancellationToken).ConfigureAwait(false);
        var backups = await ReadAllAsync("backups", cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var read = new Read(this);
        read.Hosts(hosts);
        read.Clusters(clusters, hosts);
        read.VirtualMachines(vms, Newest(backups));

        return new InventorySnapshot
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Annotations = read.Annotations,
            Alerts = read.Alerts,
            Failures = read.Failures,
            ViewsHeld = 0,
        };
    }

    /// <summary>Every page of one collection, <c>limit</c> + <c>offset</c>, until <c>count</c>.</summary>
    /// <remarks>
    /// A reply without the collection's array is a failure, never an empty
    /// estate. ponytail: offset paging over a list that changes mid-read may
    /// skip or repeat a row; rows are keyed by id downstream, so a repeat is
    /// harmless and a skip is back next cycle.
    /// </remarks>
    private async Task<List<JsonElement>> ReadAllAsync(string collection, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();

        for (var offset = 0; ;)
        {
            var path = string.Create(
                CultureInfo.InvariantCulture, $"/api/{collection}?limit={PageLimit}&offset={offset}");

            using var document = await _channel.GetAsync(path, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(collection, out var page) ||
                page.ValueKind != JsonValueKind.Array)
            {
                throw new SimplivityApiException(
                    CollectionFailureKind.ProtocolError, $"GET {path} answered without a '{collection}' array.");
            }

            // One row that is not an object fails the whole read, never a
            // partial success that silently drops it.
            if (page.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object))
            {
                throw new SimplivityApiException(
                    CollectionFailureKind.ProtocolError, $"GET {path} answered a '{collection}' row that is not an object.");
            }

            items.AddRange(page.EnumerateArray().Select(e => e.Clone()));

            var rows = page.GetArrayLength();
            offset += rows;

            int? count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : null;

            if (rows == 0 || (count is { } total ? offset >= total : rows < PageLimit))
            {
                return items;
            }
        }
    }

    /// <summary>The newest PROTECTED backup per SimpliVity VM id, and its type.</summary>
    private static Dictionary<string, (DateTimeOffset At, string? Type)> Newest(List<JsonElement> backups)
    {
        var newest = new Dictionary<string, (DateTimeOffset At, string? Type)>(StringComparer.Ordinal);

        foreach (var backup in backups)
        {
            if (!string.Equals(Text(backup, "state"), "PROTECTED", StringComparison.Ordinal) ||
                Text(backup, "virtual_machine_id") is not { } vm ||
                !DateTimeOffset.TryParse(Text(backup, "created_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                continue;
            }

            if (!newest.TryGetValue(vm, out var seen) || at > seen.At)
            {
                newest[vm] = (at, Text(backup, "type"));
            }
        }

        return newest;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    /// <summary>One read's folding, annotations, alerts and failures.</summary>
    private sealed class Read(SimplivityInventorySource source)
    {
        public List<EntityAnnotation> Annotations { get; } = [];

        public List<AlertDefinition> Alerts { get; } = [];

        public List<CollectionFailure> Failures { get; } = [];

        private readonly HashSet<EntityId> _annotated = [];

        public void Hosts(List<JsonElement> hosts)
        {
            foreach (var host in hosts)
            {
                var name = Text(host, "name") ?? Text(host, "id") ?? "?";

                if (!Fold(Text(host, "hypervisor_object_id"), "HostSystem", $"host '{name}'", out var id))
                {
                    continue;
                }



                var state = Text(host, "state");
                Annotate(id, $"host '{name}'", new()
                {
                    ["state"] = state,
                    ["upgrade_state"] = Text(host, "upgrade_state"),
                    ["version"] = Text(host, "version"),
                    ["virtual_controller_name"] = Text(host, "virtual_controller_name"),
                });

                // HPE's svt-federation-show problem indicators: Faulty, Suspected.
                AlertSeverity? severity = state switch
                {
                    "FAULTY" => AlertSeverity.Critical,
                    "SUSPECTED" => AlertSeverity.Warning,
                    _ => null,
                };

                if (severity is { } s)
                {
                    Raise(id, s, "SimpliVity host not alive", "simplivity-host-state",
                        $"SimpliVity reports host '{name}' as {state}.");
                }
            }
        }

        public void Clusters(List<JsonElement> clusters, List<JsonElement> hosts)
        {
            var computeClusterOf = hosts
                .Where(h => Text(h, "id") is not null)
                .ToDictionary(h => Text(h, "id")!, h => Text(h, "compute_cluster_hypervisor_object_id"), StringComparer.Ordinal);

            foreach (var cluster in clusters)
            {
                var name = Text(cluster, "name") ?? Text(cluster, "id") ?? "?";
                var members = cluster.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                    ? [.. m.EnumerateArray().Select(x => x.GetString()).OfType<string>()]
                    : new List<string>();

                // Its own reference when it is the composite form; otherwise the
                // one vSphere cluster all its member hosts name (26/26 measured).
                var reference = Text(cluster, "hypervisor_object_id") is { } own && own.Contains(':', StringComparison.Ordinal)
                    ? own
                    : members.Select(id => computeClusterOf.GetValueOrDefault(id)).Distinct(StringComparer.Ordinal).ToList()
                        is [{ } single] ? single : null;

                if (!Fold(reference, "ClusterComputeResource", $"OmniStack cluster '{name}'", out var id))
                {
                    continue;
                }

                var connected = Text(cluster, "arbiter_connected");
                var required = Text(cluster, "arbiter_required");

                Annotate(id, $"OmniStack cluster '{name}'", new()
                {
                    ["name"] = name,
                    ["arbiter_required"] = required,
                    ["arbiter_configured"] = Text(cluster, "arbiter_configured"),
                    ["arbiter_connected"] = connected,
                    ["upgrade_state"] = Text(cluster, "upgrade_state"),
                    ["version"] = Text(cluster, "version"),
                    ["members"] = members.Count.ToString(CultureInfo.InvariantCulture),
                });

                if (connected == "false")
                {
                    Raise(id, required == "true" ? AlertSeverity.Critical : AlertSeverity.Warning,
                        "SimpliVity arbiter disconnected", "simplivity-arbiter",
                        $"OmniStack cluster '{name}' has lost its arbiter" +
                        (required == "true" ? ", which it requires." : "."));
                }
            }
        }

        public void VirtualMachines(
            List<JsonElement> vms, Dictionary<string, (DateTimeOffset At, string? Type)> newestBackup)
        {
            foreach (var vm in vms)
            {
                // A deleted or removed VM keeps its backups in SimpliVity but has
                // no vSphere VM to fold onto; that is not a failure.
                if (!string.Equals(Text(vm, "state"), "ALIVE", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = Text(vm, "name") ?? Text(vm, "id") ?? "?";
                var target = $"VM '{name}'";

                EntityId id;
                if (Text(vm, "hypervisor_object_id") is { } reference && reference.Contains(':', StringComparison.Ordinal))
                {
                    if (!Fold(reference, "VirtualMachine", target, out id))
                    {
                        continue;
                    }
                }
                else if (Text(vm, "hypervisor_instance_id") is { } uuid &&
                         source._directory.VirtualMachineByInstanceUuid(uuid) is { } byUuid)
                {
                    id = byUuid;
                }
                else
                {
                    CouldNotFold(target, "it carries neither a <vCenter instanceUuid>:VirtualMachine:vm-N " +
                                         "reference nor an instance UUID a vSphere VM is marked with");
                    continue;
                }

                var ha = Text(vm, "ha_status");
                var settings = new Dictionary<string, string?>
                {
                    ["ha_status"] = ha,
                    ["ha_resynchronization_progress"] = Text(vm, "ha_resynchronization_progress"),
                };

                if (Text(vm, "id") is { } svtId && newestBackup.TryGetValue(svtId, out var backup))
                {
                    settings[InventoryVerdictKeys.SimplivityBackupLastUtc[(Namespace.Length + 1)..]] = backup.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                    settings["backup.type"] = backup.Type;
                }

                Annotate(id, target, settings);

                AlertSeverity? severity = ha switch
                {
                    "DEGRADED" => AlertSeverity.Warning,
                    "DEFUNCT" => AlertSeverity.Critical,
                    _ => null,
                };

                if (severity is { } s)
                {
                    Raise(id, s, "SimpliVity storage HA not safe", "simplivity-vm-ha",
                        $"SimpliVity reports storage HA for VM '{name}' as {ha}.");
                }
            }
        }

        /// <summary>Resolves <c>&lt;uuid&gt;:&lt;type&gt;:&lt;moRef&gt;</c> to an entity that exists, or records why not.</summary>
        private bool Fold(string? reference, string type, string target, out EntityId id)
        {
            id = default;

            if (reference?.Split(':') is not [var uuid, var kind, var moRef] ||
                !string.Equals(kind, type, StringComparison.Ordinal))
            {
                CouldNotFold(target, $"'{reference}' is not <vCenter instanceUuid>:{type}:<moRef>");
                return false;
            }

            if (source._directory.VcenterFor(uuid) is not { } vcenter)
            {
                CouldNotFold(target, $"no vCenter connection here has instanceUuid {uuid}");
                return false;
            }

            id = EntityId.For(vcenter, moRef);

            if (!source._directory.Contains(id))
            {
                CouldNotFold(target, $"vCenter '{vcenter}' does not report {moRef}");
                return false;
            }

            return true;
        }

        /// <remarks>
        /// NotConfigured: the platform answered and was read in full; what is
        /// missing is a vCenter connection or entity this product has — an
        /// environment condition, which keeps <c>up</c> true while still
        /// making the collector Warning and naming each one.
        /// </remarks>
        private void CouldNotFold(string target, string why) => Failures.Add(new CollectionFailure
        {
            Kind = CollectionFailureKind.NotConfigured,
            Target = $"SimpliVity {target}",
            Detail = $"could not fold onto a vSphere entity: {why}",
        });

        private void Annotate(EntityId id, string target, Dictionary<string, string?> values)
        {
            // Two SimpliVity objects on one vSphere entity would be two writers
            // of one namespace (ADR-0027), so the second is a fold failure.
            if (!_annotated.Add(id))
            {
                CouldNotFold(target, $"{id} is already annotated by another SimpliVity object");
                return;
            }

            Annotations.Add(new EntityAnnotation
            {
                Entity = id,
                Namespace = Namespace,
                Settings = values
                    .Where(v => v.Value is not null)
                    .ToDictionary(v => $"{Namespace}.{v.Key}", v => v.Value!, StringComparer.OrdinalIgnoreCase),
            });
        }

        private void Raise(EntityId id, AlertSeverity severity, string title, string check, string description) =>
            Alerts.Add(new AlertDefinition
            {
                Fingerprint = AlertFingerprint.Create(source.InstanceId, title, "Availability", id.Value, check),
                Severity = severity,
                Title = title,
                Description = description,
                Category = "Availability",
                Source = source.InstanceId,
                Entity = id,
            });
    }
}
