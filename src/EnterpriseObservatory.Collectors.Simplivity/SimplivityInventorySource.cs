using System.Collections.Concurrent;
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

    /// <summary>The page size asked for; fixed, never probed at runtime.</summary>
    /// <remarks>
    /// Measured on Kibar (24 September 2026, RedfishProbe --backup-paging):
    /// limit=2000 answered all 1481 backups in 1.9 s, limit=5000 was refused
    /// with HTTP 400. 500 is only the default when no limit is sent.
    /// </remarks>
    public const int PageLimit = 2000;

    /// <summary>Rows each backup page repeats of the one before it.</summary>
    /// <remarks>
    /// created_at is not a total order (513 backups shared 04:00 on Kibar),
    /// so a tie cut by a page boundary may come back in another order on the
    /// next page. Overlapping pages, de-duplicated by id, cover a tie up to
    /// this size; a larger one shows as distinct &lt; count, a partial read.
    /// </remarks>
    public const int PageOverlap = 50;

    /// <summary>
    /// The order backups are paged in: oldest first, so a backup created
    /// mid-read lands on the last page instead of shifting every page.
    /// </summary>
    private const string BackupOrder = "&sort=created_at&order=ascending";

    private readonly SimplivitySessionChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    private readonly ISimplivityFoldingDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string InstanceId { get; } = instanceId ?? throw new ArgumentNullException(nameof(instanceId));

    public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var (hosts, _) = await ReadAllAsync("hosts", cancellationToken).ConfigureAwait(false);
        var (clusters, _) = await ReadAllAsync("omnistack_clusters", cancellationToken).ConfigureAwait(false);
        var (vms, _) = await ReadAllAsync("virtual_machines", cancellationToken).ConfigureAwait(false);
        var (backups, counted) = await ReadAllAsync("backups", cancellationToken, BackupOrder).ConfigureAwait(false);

        var read = new Read(this);

        // Newest() is a maximum over the rows that arrived: one missing row can
        // be a VM's newest backup, and an older one then reads as its last.
        // A list short of its count sends no backup date at all this cycle.
        var backupsComplete = counted is not { } total || backups.Count >= total;

        if (!backupsComplete)
        {
            read.Failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.ProtocolError,
                Target = "SimpliVity backups",
                Detail = $"Partial read: {backups.Count} distinct of {counted} backups counted, after sorted, " +
                         "overlapping pages. No backup date is sent this cycle rather than one judged on a partial list.",
            });
        }

        var folded = read.Hosts(hosts);
        var hardware = await ReadHardwareAsync(folded, cancellationToken).ConfigureAwait(false);
        read.Hardware(folded, hardware.Replies, hardware.Failures);
        read.Clusters(clusters, hosts);
        var now = _clock.UtcNow;
        read.VirtualMachines(vms, backupsComplete ? Newest(backups) : []);

        return new InventorySnapshot
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Annotations = read.Annotations,
            Alerts = read.Alerts,
            Failures = read.Failures,
            Coverage = read.Coverage(),
            ViewsHeld = 0,
        };
    }

    /// <summary>Every page of one collection, <c>limit</c> + <c>offset</c>, until <c>count</c>.</summary>
    /// <remarks>
    /// <para>
    /// <c>show_optional_fields=true</c> on every list: HPE leaves its
    /// "optional" fields out of the default reply. Measured live on Kibar
    /// (23 September 2026, RedfishProbe --fields, reference-approaches §10.7):
    /// without it ha_status, ha_resynchronization_progress and
    /// hypervisor_instance_id were on 0/699 VMs and upgrade_state on 0/13
    /// clusters, so the one DEGRADED VM read as nothing at all. Chosen over an
    /// explicit fields= list, which would silently drop any field read later.
    /// </para>
    /// <para>
    /// A reply without the collection's array is a failure, never an empty
    /// estate.
    /// </para>
    /// <para>
    /// A skipped row is not harmless. The OVC's default order is unstable
    /// between requests (Kibar, 24 September 2026: 1481 backups read as 1433
    /// distinct over three 500-row pages, 30 VMs' newest backup different
    /// between two back-to-back reads), and a value derived over the rows —
    /// Newest() — silently becomes an older backup when the newest row is the
    /// one skipped: backup freshness flapped Passing/Failing every cycle. So
    /// every list is de-duplicated by id, one page covers Kibar's lists, and
    /// a list beyond one page is read in <paramref name="order"/> with
    /// <see cref="PageOverlap"/> rows repeated across each boundary. The
    /// caller compares the distinct rows with the returned count.
    /// ponytail: hosts and VMs past 2000 still page in the default order, with
    /// no overlap; dedupe covers repeats, not skips. Sort them too if an
    /// estate that large appears.
    /// </para>
    /// </remarks>
    private async Task<(List<JsonElement> Rows, int? Count)> ReadAllAsync(
        string collection, CancellationToken cancellationToken, string? order = null)
    {
        var items = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var offset = 0; ;)
        {
            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/{collection}?show_optional_fields=true{order}&limit={PageLimit}&offset={offset}");

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

            items.AddRange(page.EnumerateArray()
                .Where(e => Text(e, "id") is not { } id || ids.Add(id))
                .Select(e => e.Clone()));

            var rows = page.GetArrayLength();
            int? count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : null;

            if (rows == 0 || (count is { } total ? offset + rows >= total : rows < PageLimit))
            {
                return (items, count);
            }

            offset += order is not null && rows > PageOverlap ? rows - PageOverlap : rows;
        }
    }

    /// <summary>Hardware reads in flight at once, per source.</summary>
    /// <remarks>
    /// Measured on Kibar (23 September 2026): 0.8–1.0 s a host, 26 hosts
    /// 19.6–21 s one after another; four at a time is about 6 s. The channel's
    /// <see cref="SourceRequestGate"/> still caps what is really in flight
    /// (Collection:MaxRequestsPerSource, default 2).
    /// </remarks>
    public const int HardwareParallelism = 4;

    /// <summary>
    /// <c>GET /api/hosts/{id}/hardware</c> for every folded host, at most
    /// <see cref="HardwareParallelism"/> at once.
    /// </summary>
    /// <remarks>
    /// One host's tree failing is that host's hardware Unknown and a named
    /// failure, never the whole read: the list reads above already proved the
    /// OVC answers. A rejected token still fails the read — that is not one host's.
    /// </remarks>
    private async Task<(Dictionary<string, JsonElement> Replies, List<CollectionFailure> Failures)> ReadHardwareAsync(
        List<FoldedHost> hosts, CancellationToken cancellationToken)
    {
        var replies = new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
        var failures = new ConcurrentBag<CollectionFailure>();

        await Parallel.ForEachAsync(
            hosts.Where(h => h.SvtId is not null),
            new ParallelOptions { MaxDegreeOfParallelism = HardwareParallelism, CancellationToken = cancellationToken },
            async (host, token) =>
            {
                var path = $"/api/hosts/{Uri.EscapeDataString(host.SvtId!)}/hardware?show_optional_fields=true";
                var target = $"SimpliVity host '{host.Name}' hardware";

                try
                {
                    using var document = await _channel.GetAsync(path, token).ConfigureAwait(false);

                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("host", out var tree) &&
                        tree.ValueKind == JsonValueKind.Object)
                    {
                        replies[host.SvtId!] = tree.Clone();
                    }
                    else
                    {
                        failures.Add(new CollectionFailure
                        {
                            Kind = CollectionFailureKind.ProtocolError,
                            Target = target,
                            Detail = $"GET {path} answered without a 'host' object.",
                        });
                    }
                }
                catch (Exception ex) when (
                    ex is SimplivityApiException { Kind: not CollectionFailureKind.AuthenticationRejected } or HttpRequestException ||
                    (ex is TaskCanceledException && !token.IsCancellationRequested))
                {
                    failures.Add(new CollectionFailure
                    {
                        Kind = ex switch
                        {
                            SimplivityApiException api => api.Kind,
                            HttpRequestException => CollectionFailureKind.Unreachable,
                            _ => CollectionFailureKind.Timeout,
                        },
                        Target = target,
                        Detail = ex.Message,
                    });
                }
            }).ConfigureAwait(false);

        return (new Dictionary<string, JsonElement>(replies, StringComparer.Ordinal),
            [.. failures.OrderBy(f => f.Target, StringComparer.Ordinal)]);
    }

    /// <summary>A host that folded, its annotation still open for the hardware read.</summary>
    private sealed record FoldedHost(EntityId Id, string Name, string? SvtId, Dictionary<string, string?> Settings);

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

    /// <summary>A shared key without its namespace, as <see cref="Read.Annotate"/> adds it back.</summary>
    private static string Key(string key) => key[(Namespace.Length + 1)..];

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

        /// <summary>Per (object type, field): objects judged, and how many carried the field.</summary>
        private readonly Dictionary<(string Type, string Field), (int Asked, int Answered)> _read = [];

        /// <summary>
        /// Every field a verdict rests on, as coverage (ADR-0026): one missing
        /// or null is "could not be evaluated", never SAFE and never an alert.
        /// A field no object carried at all trips the collection-coverage warning.
        /// </summary>
        public List<PropertyCoverage> Coverage() =>
        [
            .. _read.OrderBy(r => r.Key.Type, StringComparer.Ordinal).ThenBy(r => r.Key.Field, StringComparer.Ordinal)
                .Select(r => new PropertyCoverage
                {
                    ObjectType = r.Key.Type,
                    Property = r.Key.Field,
                    Asked = r.Value.Asked,
                    Answered = r.Value.Answered,
                }),
        ];

        /// <summary>Reads a field a verdict rests on, counting whether it was there.</summary>
        private string? Judged(JsonElement e, string type, string field)
        {
            var value = Text(e, field);
            var (asked, answered) = _read.GetValueOrDefault((type, field));
            _read[(type, field)] = (asked + 1, answered + (value is null ? 0 : 1));
            return value;
        }

        public List<FoldedHost> Hosts(List<JsonElement> hosts)
        {
            var folded = new List<FoldedHost>();

            foreach (var host in hosts)
            {
                var name = Text(host, "name") ?? Text(host, "id") ?? "?";

                if (!Fold(Text(host, "hypervisor_object_id"), "HostSystem", $"host '{name}'", out var id))
                {
                    continue;
                }

                var state = Judged(host, "hosts", "state");
                folded.Add(new FoldedHost(id, name, Text(host, "id"), new()
                {
                    [Key(InventoryVerdictKeys.SimplivityState)] = state,
                    [Key(InventoryVerdictKeys.SimplivityUpgradeState)] = Judged(host, "hosts", "upgrade_state"),
                    [Key(InventoryVerdictKeys.SimplivityVersion)] = Text(host, "version"),
                    // The OVC, by name only: the host carries no reference to
                    // its VM. It folds onto its vSphere VM only when
                    // /api/virtual_machines lists it with a
                    // <uuid>:VirtualMachine:vm-N reference (VirtualMachines
                    // below) — never by matching this name.
                    [Key(InventoryVerdictKeys.SimplivityVirtualControllerName)] = Text(host, "virtual_controller_name"),
                }));

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

            return folded;
        }

        /// <summary>The hardware tree onto each folded host, then its annotation (S4).</summary>
        /// <remarks>
        /// HPE's colours (svt-hardware-show, reference-approaches §10.8): green
        /// OK, yellow degraded/warning/rebuilding, red error/missing/offline;
        /// SSD life ≤10% warns, ≤5% is critical. Empty or missing is Unknown —
        /// absent, never GREEN, and never an alert (ADR-0026): 8 of Kibar's 26
        /// hosts answer an empty accelerator_card.status because they have none.
        /// One alert per host and condition, with the count (principle 4).
        /// </remarks>
        public void Hardware(
            List<FoldedHost> hosts, Dictionary<string, JsonElement> replies, List<CollectionFailure> failures)
        {
            Failures.AddRange(failures);

            foreach (var host in hosts)
            {
                var answered = host.SvtId is not null && replies.ContainsKey(host.SvtId);
                var (asked, got) = _read.GetValueOrDefault(("hosts", "hardware"));
                _read[("hosts", "hardware")] = (asked + 1, got + (answered ? 1 : 0));

                if (answered)
                {
                    HostHardware(host, replies[host.SvtId!]);
                }

                Annotate(host.Id, $"host '{host.Name}'", host.Settings);
            }
        }

        private void HostHardware(FoldedHost host, JsonElement tree)
        {
            var raid = Word(Child(tree, "raid_card"), "status");
            var battery = Child(tree, "battery");
            var batteryHealth = Word(battery, "health");
            var drives = Items(tree, "logical_drives")
                .SelectMany(l => Items(l, "drive_sets"))
                .SelectMany(d => Items(d, "physical_drives"))
                .ToList();
            var byStatus = CountBy(drives, "status");
            var ssdLife = drives
                .Where(d => Text(d, "media_type") == "SSD")
                .Select(d => Number(d, "life_remaining"))
                .Where(l => l is >= 0 and <= 100)
                .Min();
            var rebuilding = drives.Count(d => Number(d, "percent_rebuilt") is >= 0 and < 100);

            var s = host.Settings;
            s[Key(InventoryVerdictKeys.SimplivityHwStatus)] = Word(tree, "status");
            s[Key(InventoryVerdictKeys.SimplivityHwRaidStatus)] = raid;
            s[Key(InventoryVerdictKeys.SimplivityHwBatteryStatus)] = Word(battery, "status");
            s[Key(InventoryVerdictKeys.SimplivityHwBatteryHealth)] = batteryHealth;
            s[Key(InventoryVerdictKeys.SimplivityHwBatteryCharge)] = Number(battery, "percent_charged") is >= 0 and var c
                ? c.ToString(CultureInfo.InvariantCulture)
                : null;
            s[Key(InventoryVerdictKeys.SimplivityHwAcceleratorStatus)] = Word(Child(tree, "accelerator_card"), "status");
            s[Key(InventoryVerdictKeys.SimplivityHwDrives)] = drives.Count.ToString(CultureInfo.InvariantCulture);
            s[Key(InventoryVerdictKeys.SimplivityHwDriveStatus)] = Encode(byStatus);
            s[Key(InventoryVerdictKeys.SimplivityHwDriveHealth)] = Encode(CountBy(drives, "health"));
            s[Key(InventoryVerdictKeys.SimplivityHwLifeRemainingMin)] = ssdLife?.ToString(CultureInfo.InvariantCulture);
            s[Key(InventoryVerdictKeys.SimplivityHwDrivesRebuilding)] = rebuilding.ToString(CultureInfo.InvariantCulture);

            var red = byStatus.GetValueOrDefault("RED");
            var yellow = byStatus.GetValueOrDefault("YELLOW");
            if (red + yellow > 0)
            {
                Raise(host.Id, red > 0 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    "SimpliVity physical drives not green", "simplivity-hw-drives",
                    $"SimpliVity reports {red} RED and {yellow} YELLOW of {drives.Count} physical drives on host '{host.Name}'.");
            }

            if (raid == "RED")
            {
                Raise(host.Id, AlertSeverity.Critical, "SimpliVity RAID controller red", "simplivity-hw-raid",
                    $"SimpliVity reports the RAID controller on host '{host.Name}' as RED.");
            }

            if (batteryHealth is not null && batteryHealth != "HEALTHY")
            {
                Raise(host.Id, AlertSeverity.Warning, "SimpliVity RAID battery not healthy", "simplivity-hw-battery",
                    $"SimpliVity reports the RAID battery on host '{host.Name}' as {batteryHealth}.");
            }

            if (ssdLife is { } life && life <= 10)
            {
                Raise(host.Id, life <= 5 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    "SimpliVity SSD wearing out", "simplivity-hw-ssd-life",
                    $"An SSD on host '{host.Name}' has {life}% of its life left.");
            }

            static JsonElement? Child(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

            static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                    : [];

            // Empty is what HPE answers for a part that is not there: Unknown.
            static string? Word(JsonElement? e, string name) =>
                e is { } o && Text(o, name) is { Length: > 0 } w ? w : null;

            static int? Number(JsonElement? e, string name) =>
                e is { } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
                v.TryGetInt32(out var n) ? n : null;

            static SortedDictionary<string, int> CountBy(List<JsonElement> drives, string name) =>
                new(drives.Select(d => Word(d, name)).OfType<string>()
                    .GroupBy(w => w, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), StringComparer.Ordinal);

            static string? Encode(SortedDictionary<string, int> counts) =>
                counts.Count == 0 ? null
                    : string.Join(';', counts.Select(c => $"{c.Key}={c.Value.ToString(CultureInfo.InvariantCulture)}"));
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

                var connected = Judged(cluster, "omnistack_clusters", "arbiter_connected");
                var required = Text(cluster, "arbiter_required");

                Annotate(id, $"OmniStack cluster '{name}'", new()
                {
                    [Key(InventoryVerdictKeys.SimplivityName)] = name,
                    [Key(InventoryVerdictKeys.SimplivityArbiterRequired)] = required,
                    [Key(InventoryVerdictKeys.SimplivityArbiterConfigured)] = Text(cluster, "arbiter_configured"),
                    [Key(InventoryVerdictKeys.SimplivityArbiterConnected)] = connected,
                    [Key(InventoryVerdictKeys.SimplivityUpgradeState)] = Judged(cluster, "omnistack_clusters", "upgrade_state"),
                    [Key(InventoryVerdictKeys.SimplivityVersion)] = Text(cluster, "version"),
                    [Key(InventoryVerdictKeys.SimplivityMembers)] = members.Count.ToString(CultureInfo.InvariantCulture),
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

                var ha = Judged(vm, "virtual_machines", "ha_status");
                var settings = new Dictionary<string, string?>
                {
                    [Key(InventoryVerdictKeys.SimplivityHaStatus)] = ha,
                    [Key(InventoryVerdictKeys.SimplivityHaResyncProgress)] = Text(vm, "ha_resynchronization_progress"),
                };

                if (Text(vm, "id") is { } svtId && newestBackup.TryGetValue(svtId, out var backup))
                {
                    settings[Key(InventoryVerdictKeys.SimplivityBackupLastUtc)] = backup.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                    settings[Key(InventoryVerdictKeys.SimplivityBackupType)] = backup.Type;
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
