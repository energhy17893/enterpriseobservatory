using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>A vCenter call failed in a way the caller should know about.</summary>
public sealed class VsphereApiException : Exception, ICollectionFault
{
    public VsphereApiException(VsphereFaultKind kind, string message)
        : base(message) => Kind = kind;

    public VsphereApiException(string message)
        : base(message) => Kind = VsphereFaultKind.Other;

    public VsphereApiException(string message, Exception innerException)
        : base(message, innerException) => Kind = VsphereFaultKind.Other;

    public VsphereApiException()
        : base("The vCenter call failed.") => Kind = VsphereFaultKind.Other;

    public VsphereFaultKind Kind { get; }

    /// <summary>What this fault means to the collection runner.</summary>
    /// <remarks>
    /// The runner cannot know a vim25 fault from a Redfish one, and it must
    /// not retry a rejected login. So the translation happens here, where the
    /// vocabulary is already understood, rather than by the runner guessing
    /// from a message string.
    /// </remarks>
    CollectionFailureKind ICollectionFault.Kind => Kind switch
    {
        VsphereFaultKind.InvalidLogin => CollectionFailureKind.AuthenticationRejected,
        VsphereFaultKind.NoPermission => CollectionFailureKind.AuthorizationDenied,
        _ => CollectionFailureKind.ProtocolError,
    };
}

/// <summary>One series an entity will supply: a counter, for a device.</summary>
/// <remarks>
/// A counter and a series are not the same thing. <c>disk.deviceLatency</c> is
/// one counter and, on a host with six LUNs, seven series — one per device and
/// one aggregate. The instance is what tells them apart, and an empty instance
/// is the aggregate rather than a missing value.
/// </remarks>
public sealed record VsphereAvailableMetric
{
    public required VsphereCounter Counter { get; init; }

    /// <summary>The device, or empty for the aggregate across all of them.</summary>
    public required string Instance { get; init; }
}

/// <summary>
/// Talks vim25 SOAP to one vCenter.
/// </summary>
/// <remarks>
/// <para>
/// Targets vSphere 8. Everything that makes a decision — which counters to ask
/// for, how large a batch may be, what an absent counter means — lives in the
/// sources; this is transport, session handling and fault translation.
/// </para>
/// <para>
/// All operations are reads. The account this connects with needs no write
/// privilege and should not have one: product principle 5 says the monitoring
/// tool must not be able to break production, and a technical guarantee is
/// worth more than an intention.
/// </para>
/// <para>
/// <strong>Not yet verified against a live vCenter.</strong> The request shapes
/// follow the published schema and the previous product's working traffic, and
/// the parsers are covered by tests, but session cookie behaviour and namespace
/// handling are the kind of thing that only a real server settles.
/// </para>
/// </remarks>
public sealed class VsphereClient : IVsphereApi, IVsphereInventoryApi, IDisposable
{
    /// <summary>Samples per series; see <see cref="VsphereSoapRequests.QueryPerf"/>.</summary>
    private const int MaxSample = 3;

    private readonly HttpClient _http;
    private readonly VsphereConnectionOptions _options;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private VsphereServiceContent? _serviceContent;
    private IReadOnlyList<VsphereCounter>? _counterCatalog;
    private bool _loggedIn;
    private bool _disposed;

    public VsphereClient(HttpClient http, VsphereConnectionOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _http.BaseAddress ??= options.BaseAddress;
        _http.Timeout = options.RequestTimeout;
    }

    public string InstanceId => _options.InstanceId;

    // --- IVsphereApi ------------------------------------------------------

    public async Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken)
    {
        if (_counterCatalog is { } cached)
        {
            return cached;
        }

        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        // Level 4 is the whole catalogue. This asks what the server defines,
        // not what it is currently collecting; availability is a separate
        // question and a separate call.
        var response = await SendAsync(
            VsphereSoapRequests.QueryPerfCounterByLevel(content.PerformanceManager, level: 4),
            cancellationToken).ConfigureAwait(false);

        // Counter ids are per vCenter and stable for the life of a connection,
        // so this is cached: availability checks would otherwise re-read the
        // whole catalogue for every entity type on every cycle.
        _counterCatalog = PerfResponseParser.ParseCounters(response);
        return _counterCatalog;
    }

    public async Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        if (content.SettingManager is not { } settingManager)
        {
            return null;
        }

        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.QueryMaxQueryMetrics(settingManager), cancellationToken)
                .ConfigureAwait(false);

            var value = XDocument.Parse(response)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "value")?.Value;

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
        catch (VsphereApiException ex) when (VsphereFaults.IsSurvivable(ex.Kind))
        {
            // Which cases survive, and why, is VsphereFaults.IsSurvivable.
            // This used to spell the same rule out inline beside the named one
            // that nothing called.
            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
        string entityMoRef,
        VsphereEntityType entityType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var metrics = await GetAvailableMetricsAsync(
            entityMoRef, entityType, nowUtc, cancellationToken).ConfigureAwait(false);

        return [.. metrics.Select(m => m.Counter.Key).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Every series this entity will supply, counter and instance together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing is the information. A counter is not a series: the same
    /// counter appears once per device plus once as an aggregate, and which
    /// devices exist is the difference between "the host has slow storage" and
    /// "one LUN is slow". Reading the counter ids alone throws that away.
    /// </para>
    /// <para>
    /// It also answers a question that cost this product several wrong fixes:
    /// which <em>object</em> a measurement is collected on. Per-datastore
    /// latency is a host counter whose instance is the datastore, so asking a
    /// datastore for it returns nothing however correct the name — and the only
    /// way to know that is to look at what each entity offers.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<VsphereAvailableMetric>> GetAvailableMetricsAsync(
        string entityMoRef,
        VsphereEntityType entityType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);
        var byId = catalog.ToDictionary(c => c.Id);

        var window = VsphereIntervals.SupportsRealTime(entityType)
            ? default((DateTimeOffset, DateTimeOffset)?)
            : (nowUtc - VsphereIntervals.HistoricalWindow, nowUtc);

        var response = await SendAsync(
            VsphereSoapRequests.QueryAvailablePerfMetric(
                content.PerformanceManager,
                entityMoRef,
                entityType.ToString(),
                VsphereIntervals.IntervalSecondsFor(entityType),
                window),
            cancellationToken).ConfigureAwait(false);

        XDocument document;
        try
        {
            document = XDocument.Parse(response);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var metrics = new List<VsphereAvailableMetric>();

        // Walked as PerfMetricId elements rather than as loose counterId
        // descendants, so each counter keeps the instance it arrived with.
        foreach (var metric in document.Descendants()
                     .Where(e => e.Elements().Any(c => c.Name.LocalName == "counterId")))
        {
            var idText = metric.Elements()
                .First(c => c.Name.LocalName == "counterId").Value;

            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                !byId.TryGetValue(id, out var counter))
            {
                continue;
            }

            var instance = metric.Elements()
                .FirstOrDefault(c => c.Name.LocalName == "instance")?.Value ?? string.Empty;

            metrics.Add(new VsphereAvailableMetric { Counter = counter, Instance = instance });
        }

        return metrics;
    }

    public async Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityMoRefs);
        ArgumentNullException.ThrowIfNull(counters);

        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var intervalSeconds = VsphereIntervals.IntervalSecondsFor(entityType);

        // Real-time takes maxSample; a historical interval ignores it and needs
        // a range instead. Sending neither is what made every datastore in a
        // live estate come back empty without a single error.
        var window = VsphereIntervals.SupportsRealTime(entityType)
            ? default((DateTimeOffset, DateTimeOffset)?)
            : (nowUtc - VsphereIntervals.HistoricalWindow, nowUtc);

        var response = await SendAsync(
            VsphereSoapRequests.QueryPerf(
                content.PerformanceManager, entityMoRefs, entityType.ToString(),
                counters, intervalSeconds, MaxSample, window),
            cancellationToken,
            VsphereCallContext.PerformanceQuery).ConfigureAwait(false);

        var byId = counters.ToDictionary(c => c.Id);

        return PerfResponseParser.ParseSamples(
            response, byId, TimeSpan.FromSeconds(intervalSeconds));
    }

    // --- IVsphereInventoryApi ---------------------------------------------

    /// <summary>
    /// Properties read per managed object type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only what the entity model needs. Asking for everything is both slower
    /// and a privilege the read-only account would have to be granted for no
    /// reason.
    /// </para>
    /// <para>
    /// Host IP addresses are deliberately absent: they live at
    /// <c>config.network.vnic[].spec.ip.ipAddress</c>, which the property
    /// collector returns as a nested structure this flattener cannot render
    /// faithfully. Reporting a half-read address as an identity mark would be
    /// worse than reporting none, so hosts are matched on UUID and name until
    /// that is handled properly.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, IReadOnlyList<string>> InventoryProperties = new(StringComparer.Ordinal)
    {
        ["HostSystem"] =
        [
            "name",
            "summary.overallStatus",
            "runtime.connectionState",
            "runtime.inMaintenanceMode",
            "hardware.systemInfo.uuid",
            "parent",
            "triggeredAlarmState",

            // Read on the host because that is where it lives, and used to
            // describe the datastores. It says which storage device each
            // mounted volume occupies — the one link that joins a datastore's
            // VMFS UUID to the NAA every storage path and disk device is
            // named by.
            "config.fileSystemVolume.mountInfo",

            // The path table, and the second table that names its devices.
            // multipathInfo says which routes reach which device and what
            // state each is in; it names the device by an internal key, and
            // scsiLun is read solely to turn that key into the NAA every other
            // part of the product speaks. One without the other gives either
            // path states nobody can attribute or names nothing points at.
            "config.storageDevice.multipathInfo",
            "config.storageDevice.scsiLun",
        ],
        ["VirtualMachine"] =
        [
            "name",
            "summary.overallStatus",
            "runtime.powerState",
            "runtime.host",
            "config.instanceUuid",
            "datastore",
            "triggeredAlarmState",

            // Sizing. numCPU is the divisor cpu.ready.summation needs and has
            // never had, and the two allocation limits are what tell a
            // throttled machine from a contended one — they look identical
            // from inside the guest and lead to opposite fixes.
            "config.hardware.numCPU",
            "config.hardware.memoryMB",
            "config.cpuAllocation.limit",
            "config.memoryAllocation.limit",

            // Snapshots: the tree for age, the file layout for size. Asked for
            // as two sub-paths rather than as layoutEx whole, because the rest
            // of that structure is per-file detail nothing here reads.
            "snapshot",
            "layoutEx.file",
            "layoutEx.disk",
        ],
        ["ClusterComputeResource"] =
        [
            "name",
            "configuration.dasConfig.enabled",
            "configuration.drsConfig.enabled",
            "triggeredAlarmState",
        ],
        ["Datastore"] =
        [
            "name",
            "summary.capacity",
            "summary.freeSpace",
            "summary.accessible",
            "summary.type",
            "summary.url",

            // One more field on a call already being made. It is what says a
            // datastore is going to fill, while fullness only says whether it
            // has — see counter map §4.
            "summary.uncommitted",
            "triggeredAlarmState",
        ],
    };

    /// <summary>What is asked of one managed object type, for a test to check.</summary>
    /// <remarks>
    /// Exposed because a forgotten property path is not an error. vCenter
    /// returns nothing for one it was never asked for, the reading is null
    /// forever, and everything downstream carries on looking correct — so the
    /// asking has to be assertable, not only the parsing.
    /// </remarks>
    public static IReadOnlyList<string> InventoryPropertiesFor(string managedObjectType) =>
        InventoryProperties.TryGetValue(managedObjectType, out var paths) ? paths : [];

    public async Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<VsphereReadFailure>();

        var viewMoRef = await CreateViewAsync(content, cancellationToken).ConfigureAwait(false);

        try
        {
            var (objects, pages) = await RetrieveAllPagesAsync(content, viewMoRef, cancellationToken)
                .ConfigureAwait(false);

            foreach (var missing in objects.SelectMany(o => o.Missing.Select(m => (o, m))))
            {
                failures.Add(new VsphereReadFailure
                {
                    Target = $"{missing.o.Type} {missing.o.MoRef}: {missing.m.Path}",
                    Detail = missing.m.FaultType.Length == 0 ? "unreadable" : missing.m.FaultType,
                    IsPermissionDenied = missing.m.IsPermissionDenied,
                });
            }

            var alarms = await ReadTriggeredAlarmsAsync(
                content, objects, failures, cancellationToken).ConfigureAwait(false);

            // Built once from every host, because a shared volume is mounted on
            // many and any one of them can say which device it sits on.
            var volumeDevices = ReadVolumeDevices(objects);

            return new VsphereInventoryPayload
            {
                VCenterName = string.IsNullOrWhiteSpace(content.Name) ? InstanceId : content.Name,
                Hosts = [.. objects.Where(o => o.Type == "HostSystem").Select(ToHost)],
                VirtualMachines = [.. objects.Where(o => o.Type == "VirtualMachine").Select(ToVirtualMachine)],
                Clusters = [.. objects.Where(o => o.Type == "ClusterComputeResource").Select(ToCluster)],
                Datastores =
                [
                    .. objects.Where(o => o.Type == "Datastore")
                        .Select(o => ToDatastore(o, volumeDevices)),
                ],
                TriggeredAlarms = alarms,
                Failures = failures,
                PagesRetrieved = pages,
            };
        }
        finally
        {
            // Views are server-side resources with a session lifetime. Leaking
            // one per cycle would accumulate until the session is recycled.
            await TryDestroyViewAsync(viewMoRef, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> CreateViewAsync(VsphereServiceContent content, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            VsphereSoapRequests.CreateContainerView(
                content.ViewManager, content.RootFolder, [.. InventoryProperties.Keys]),
            cancellationToken).ConfigureAwait(false);

        var moRef = XDocument.Parse(response)
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "returnval")?.Value?.Trim();

        return string.IsNullOrWhiteSpace(moRef)
            ? throw new VsphereApiException("vCenter did not return a container view.")
            : moRef;
    }

    private async Task TryDestroyViewAsync(string viewMoRef, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(VsphereSoapRequests.DestroyView(viewMoRef), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
            // Cleanup only. The session ending will collect it anyway, and
            // failing the whole read over a tidy-up would be worse.
        }
        catch (HttpRequestException)
        {
        }
    }

    /// <summary>
    /// Walks every page of the property retrieval.
    /// </summary>
    /// <remarks>
    /// Stopping at the first page truncates the inventory at whatever the
    /// server chose to return, and the result looks like a small healthy estate
    /// rather than a bug. This is the single easiest way to under-report an
    /// environment.
    /// </remarks>
    private async Task<(List<PropertyObject> Objects, int Pages)> RetrieveAllPagesAsync(
        VsphereServiceContent content,
        string viewMoRef,
        CancellationToken cancellationToken)
    {
        var all = new List<PropertyObject>();
        var pages = 1;

        var response = await SendAsync(
            VsphereSoapRequests.RetrievePropertiesEx(
                content.PropertyCollector, content.RootFolder, viewMoRef,
                InventoryProperties, _options.InventoryPageSize),
            cancellationToken).ConfigureAwait(false);

        var page = PropertyCollectorParser.ParsePage(response);
        all.AddRange(page.Objects);

        while (page.HasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();

            response = await SendAsync(
                VsphereSoapRequests.ContinueRetrievePropertiesEx(
                    content.PropertyCollector, page.ContinuationToken!),
                cancellationToken).ConfigureAwait(false);

            page = PropertyCollectorParser.ParsePage(response);
            all.AddRange(page.Objects);
            pages++;
        }

        return (all, pages);
    }

    /// <summary>
    /// Gathers the alarms vCenter has raised, once each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things measured against a live vCenter shape this. First, the
    /// property comes back on every object, nearly always as an empty array —
    /// so its presence says nothing and only its contents do. Second, a
    /// triggered alarm is reported on the object it concerns <em>and</em> on
    /// every ancestor: one memory alarm on one host appeared on the host and on
    /// its cluster, byte for byte the same. Keying on vCenter's own
    /// <c>key</c> collapses those back into the single fact they are.
    /// </para>
    /// <para>
    /// The name needs a second call. An <c>AlarmState</c> carries a reference
    /// and no words, so "alarm-115" has to be turned into "Host memory status"
    /// by reading the definition. Only the references that actually appear are
    /// resolved, which on a healthy estate is none and on a troubled one is a
    /// handful — never the whole alarm catalogue.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<VsphereTriggeredAlarm>> ReadTriggeredAlarmsAsync(
        VsphereServiceContent content,
        IReadOnlyList<PropertyObject> objects,
        List<VsphereReadFailure> failures,
        CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, VsphereTriggeredAlarm>(StringComparer.Ordinal);

        foreach (var state in objects
            .Where(o => o.Structures.ContainsKey("triggeredAlarmState"))
            .SelectMany(o => o.Structures["triggeredAlarmState"]))
        {
            var key = state.TextOf("key");
            var entity = state.TextOf("entity");
            var alarm = state.TextOf("alarm");

            // Without these three there is nothing to report, nothing to
            // attribute it to and no way to tell one triggering from another.
            if (key.Length == 0 || entity.Length == 0 || alarm.Length == 0)
            {
                continue;
            }

            byKey.TryAdd(key, new VsphereTriggeredAlarm
            {
                Key = key,
                EntityMoRef = entity,
                EntityType = state.TypeOf("entity"),
                AlarmMoRef = alarm,
                OverallStatus = state.TextOf("overallStatus") is { Length: > 0 } status
                    ? status
                    : null,
                TriggeredAtUtc = DateTimeOffset.TryParse(
                    state.TextOf("time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var at) ? at : null,
                Acknowledged = bool.TryParse(state.TextOf("acknowledged"), out var ack) && ack,
                AcknowledgedByUser = state.TextOf("acknowledgedByUser") is { Length: > 0 } who
                    ? who
                    : null,
            });
        }

        if (byKey.Count == 0)
        {
            return [];
        }

        var definitions = await ReadAlarmDefinitionsAsync(
            content,
            [.. byKey.Values.Select(a => a.AlarmMoRef).Distinct(StringComparer.Ordinal)],
            failures,
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. byKey.Values.Select(a => definitions.TryGetValue(a.AlarmMoRef, out var definition)
                ? a with { AlarmName = definition.Name, AlarmDescription = definition.Description }
                : a),
        ];
    }

    /// <summary>Reads the names behind a set of alarm references.</summary>
    /// <remarks>
    /// One call for all of them. A failure here is recorded and survived: an
    /// alarm under its reference is worse to read than one under its name, and
    /// far better than one nobody is told about.
    /// </remarks>
    private async Task<Dictionary<string, (string? Name, string? Description)>>
        ReadAlarmDefinitionsAsync(
            VsphereServiceContent content,
            IReadOnlyList<string> alarmMoRefs,
            List<VsphereReadFailure> failures,
            CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, (string? Name, string? Description)>(
            StringComparer.Ordinal);

        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.RetrieveAlarmDefinitions(
                    content.PropertyCollector, alarmMoRefs),
                cancellationToken).ConfigureAwait(false);

            foreach (var o in PropertyCollectorParser.ParsePage(response).Objects)
            {
                resolved[o.MoRef] = (
                    PropertyCollectorParser.ReadString(o.Values, "info.name"),
                    PropertyCollectorParser.ReadString(o.Values, "info.description"));
            }
        }
        catch (VsphereApiException ex)
        {
            failures.Add(new VsphereReadFailure
            {
                Target = "alarm definitions",
                Detail =
                    $"{alarmMoRefs.Count} triggered alarms are reported under their references " +
                    $"rather than their names: {ex.Message}",
                IsPermissionDenied = ex.Kind == VsphereFaultKind.NoPermission,
            });
        }

        return resolved;
    }

    /// <summary>
    /// Maps each mounted volume to the storage devices it occupies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape, measured against a live vCenter rather than read from the
    /// schema: <c>config.fileSystemVolume.mountInfo</c> is an array of
    /// <c>HostFileSystemMountInfo</c>, each with a <c>mountInfo</c> child — path
    /// and access mode — and a sibling <c>volume</c> that carries the real
    /// content. For VMFS the volume has a <c>uuid</c> and one <c>extent</c> per
    /// storage device, each with a <c>diskName</c> like
    /// <c>naa.600508b1001cb736...</c>.
    /// </para>
    /// <para>
    /// Read from every host and merged. The same volume is mounted on every
    /// host that can see it, so one unreadable host costs nothing as long as
    /// another answered — and on a shared SAN they nearly always do.
    /// </para>
    /// <para>
    /// Non-VMFS volumes are skipped rather than guessed at. An NFS mount has no
    /// storage device in this sense, and inventing one would put a LUN
    /// identifier on something that is not a LUN.
    /// </para>
    /// </remarks>
    public static Dictionary<string, List<string>> ReadVolumeDevices(
        IReadOnlyList<PropertyObject> objects)
    {
        var byVolume = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in objects
            .Where(o => o.Type == "HostSystem")
            .Where(o => o.Structures.ContainsKey("config.fileSystemVolume.mountInfo"))
            .SelectMany(o => o.Structures["config.fileSystemVolume.mountInfo"]))
        {
            if (entry.Child("volume") is not { } volume)
            {
                continue;
            }

            var uuid = volume.TextOf("uuid");
            if (uuid.Length == 0)
            {
                continue;
            }

            var devices = volume.All("extent")
                .Select(e => e.TextOf("diskName"))
                .Where(d => d.Length > 0)
                .ToList();

            if (devices.Count == 0)
            {
                continue;
            }

            if (!byVolume.TryGetValue(uuid, out var known))
            {
                byVolume[uuid] = known = [];
            }

            foreach (var device in devices.Where(d =>
                !known.Contains(d, StringComparer.OrdinalIgnoreCase)))
            {
                known.Add(device);
            }
        }

        return byVolume;
    }

    /// <summary>
    /// Reads a host's storage paths and the device each one leads to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape, from the published vim25 schema and <strong>not</strong>
    /// measured against a live server:
    /// <c>config.storageDevice.multipathInfo</c> is a <c>HostMultipathInfo</c>
    /// whose <c>lun</c> children are each a
    /// <c>HostMultipathInfoLogicalUnit</c> — an <c>id</c>, a <c>lun</c>
    /// reference naming the device by its internal key, and a <c>path</c> per
    /// route, each with a runtime <c>name</c>, an <c>adapter</c> and a
    /// <c>pathState</c>.
    /// </para>
    /// <para>
    /// The device key is not the NAA. It arrives as
    /// <c>key-vim.host.ScsiDisk-0200...</c>, while every storage path counter,
    /// disk device counter and datastore mark in this product speaks
    /// <c>naa.6005...</c>. <c>config.storageDevice.scsiLun</c> is the only
    /// table that holds both, so it is read alongside and used for nothing
    /// else.
    /// </para>
    /// <para>
    /// A path whose device cannot be resolved is still reported, with an empty
    /// device. "This path is dead" is worth saying even when nobody can yet
    /// say which LUN it led to, and dropping it would under-count redundancy
    /// in exactly the case where something is already wrong.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<VsphereStoragePath> ReadStoragePaths(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.Structures.TryGetValue("config.storageDevice.multipathInfo", out var multipath))
        {
            return [];
        }

        var canonicalByKey = ReadScsiLunNames(host);
        var paths = new List<VsphereStoragePath>();

        foreach (var lun in multipath.Where(n => string.Equals(n.Name, "lun", StringComparison.Ordinal)))
        {
            // The reference's value is the ScsiLun key. Falling back to the
            // logical unit's own id keeps the paths grouped even when the
            // reference is absent, which is the whole point of carrying a key
            // that nothing else needs.
            var deviceKey = lun.TextOf("lun") is { Length: > 0 } reference
                ? reference
                : lun.TextOf("id");

            var device = canonicalByKey.TryGetValue(deviceKey, out var naa) ? naa : string.Empty;

            foreach (var path in lun.All("path"))
            {
                var name = path.TextOf("name");
                if (name.Length == 0)
                {
                    // Without a runtime name there is nothing a path counter
                    // could ever be joined to, so the row would be unusable.
                    continue;
                }

                paths.Add(new VsphereStoragePath
                {
                    Name = name,
                    // pathState is the current field; state is its predecessor
                    // and still the only one some versions populate. Taking
                    // whichever answered beats reporting an empty state that
                    // a redundancy rule would have to treat as unknown.
                    State = path.TextOf("pathState") is { Length: > 0 } current
                        ? current
                        : path.TextOf("state"),
                    Adapter = AdapterName(path.TextOf("adapter")),
                    DeviceKey = deviceKey,
                    StorageDeviceId = device,
                });
            }
        }

        return paths;
    }

    /// <summary>Maps each SCSI device's internal key to its canonical NAA.</summary>
    private static Dictionary<string, string> ReadScsiLunNames(PropertyObject host)
    {
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!host.Structures.TryGetValue("config.storageDevice.scsiLun", out var luns))
        {
            return byKey;
        }

        foreach (var lun in luns)
        {
            var key = lun.TextOf("key");
            var canonical = lun.TextOf("canonicalName");

            if (key.Length > 0 && canonical.Length > 0)
            {
                byKey[key] = canonical;
            }
        }

        return byKey;
    }

    /// <summary>
    /// Recovers the adapter's own name from its reference.
    /// </summary>
    /// <remarks>
    /// An adapter arrives as <c>key-vim.host.FibreChannelHba-vmhba2</c>, and
    /// the part worth showing somebody is <c>vmhba2</c> — it is what is
    /// printed on the card and what they will look for. Anything that does not
    /// match that shape is passed through rather than trimmed on a guess.
    /// </remarks>
    private static string AdapterName(string reference)
    {
        const string Marker = "key-vim.host.";

        if (!reference.StartsWith(Marker, StringComparison.Ordinal))
        {
            return reference;
        }

        var dash = reference.IndexOf('-', Marker.Length);
        return dash >= 0 && dash + 1 < reference.Length ? reference[(dash + 1)..] : reference;
    }

    /// <summary>
    /// Flattens a virtual machine's snapshot tree, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>snapshot</c> is a <c>VirtualMachineSnapshotInfo</c> whose
    /// <c>rootSnapshotList</c> entries are <c>VirtualMachineSnapshotTree</c>
    /// nodes, each with a <c>name</c>, a <c>createTime</c>, the snapshot's own
    /// reference and a recursive <c>childSnapshotList</c>.
    /// </para>
    /// <para>
    /// The recursion is genuinely renderable here. The property parser builds
    /// a tree rather than a flat field map — it had to, for the volume/extent
    /// nesting — and it keeps repeated children repeated, which is what a
    /// branching snapshot chain is made of. So this walks it rather than
    /// half-reading it.
    /// </para>
    /// <para>
    /// Flattened on the way out, because what the failure mode needs is the
    /// oldest creation time and the count. Depth is kept, since a chain thirty
    /// deep is its own kind of problem, but the parent links are not: nothing
    /// that could act on this needs to know which snapshot is whose child.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<VsphereSnapshot> ReadSnapshots(PropertyObject vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (!vm.Structures.TryGetValue("snapshot", out var snapshot))
        {
            return [];
        }

        var found = new List<VsphereSnapshot>();

        foreach (var root in snapshot.Where(n =>
            string.Equals(n.Name, "rootSnapshotList", StringComparison.Ordinal)))
        {
            Walk(root, depth: 1, found);
        }

        // Oldest first, so "how old is the oldest" is the first element rather
        // than a scan, and so an unreadable timestamp sorts last instead of
        // masquerading as the oldest snapshot in the estate.
        return [.. found.OrderBy(s => s.CreatedAtUtc ?? DateTimeOffset.MaxValue)];

        static void Walk(PropertyNode node, int depth, List<VsphereSnapshot> into)
        {
            var moRef = node.TextOf("snapshot");

            if (moRef.Length > 0)
            {
                into.Add(new VsphereSnapshot
                {
                    MoRef = moRef,
                    Name = node.TextOf("name") is { Length: > 0 } name ? name : moRef,
                    CreatedAtUtc = DateTimeOffset.TryParse(
                        node.TextOf("createTime"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var created) ? created : null,
                    Depth = depth,
                });
            }

            foreach (var child in node.All("childSnapshotList"))
            {
                Walk(child, depth + 1, into);
            }
        }
    }

    /// <summary>
    /// How much disk the snapshot chain occupies, or null when it cannot be said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two contributions, and leaving either out understates it badly. The
    /// obvious one is the snapshot's own files — the memory image and the
    /// metadata, which <c>layoutEx.file</c> labels <c>snapshotData</c> and
    /// <c>snapshotMemory</c>. The one that actually fills datastores is the
    /// delta disks, and those are labelled <c>diskExtent</c> exactly like the
    /// base disks they hang off; nothing in the file entry says which is
    /// which.
    /// </para>
    /// <para>
    /// <c>layoutEx.disk</c> is what separates them. Each disk carries a
    /// <c>chain</c> whose first link is the base disk and whose every later
    /// link is one snapshot level, so the delta files are precisely the file
    /// keys in links two onwards. That is the whole reason it is requested; on
    /// its own it is a list of integers.
    /// </para>
    /// <para>
    /// Null when <c>layoutEx.file</c> was not readable. A machine whose layout
    /// we could not read must not be reported as one carrying no snapshot at
    /// all, which is the same zero-looks-like-a-measurement trap the counter
    /// map calls this product's most dangerous number.
    /// </para>
    /// </remarks>
    public static long? ReadSnapshotBytes(PropertyObject vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (!vm.Structures.TryGetValue("layoutEx.file", out var files))
        {
            return null;
        }

        var sizeByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var snapshotOwn = 0L;

        foreach (var file in files)
        {
            var key = file.TextOf("key");
            var size = long.TryParse(
                file.TextOf("size"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0L;

            if (key.Length > 0)
            {
                sizeByKey[key] = size;
            }

            if (file.TextOf("type") is "snapshotData" or "snapshotMemory")
            {
                snapshotOwn += size;
            }
        }

        var deltas = 0L;

        if (vm.Structures.TryGetValue("layoutEx.disk", out var disks))
        {
            foreach (var disk in disks)
            {
                // Skip(1): the first link is the base disk, which exists
                // whether or not anybody ever took a snapshot. Counting it
                // would report every virtual machine in the estate as carrying
                // a snapshot the size of itself.
                foreach (var link in disk.All("chain").Skip(1))
                {
                    foreach (var fileKey in link.All("fileKey"))
                    {
                        if (sizeByKey.TryGetValue(fileKey.Text, out var size))
                        {
                            deltas += size;
                        }
                    }
                }
            }
        }

        return snapshotOwn + deltas;
    }

    public static VsphereHost ToHost(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        HardwareUuid = PropertyCollectorParser.ReadString(o.Values, "hardware.systemInfo.uuid"),
        Fqdn = PropertyCollectorParser.ReadString(o.Values, "name") is { } name && name.Contains('.', StringComparison.Ordinal)
            ? name
            : null,
        OverallStatus = PropertyCollectorParser.ReadString(o.Values, "summary.overallStatus"),
        ConnectionState = PropertyCollectorParser.ReadString(o.Values, "runtime.connectionState"),
        InMaintenanceMode = PropertyCollectorParser.ReadBoolean(o.Values, "runtime.inMaintenanceMode") ?? false,
        // A host's parent is its cluster only when it is in one; a standalone
        // host's parent is a ComputeResource, which is not a cluster we model.
        ClusterMoRef = PropertyCollectorParser.ReadString(o.Values, "parent") is { } parent &&
                       parent.StartsWith("domain-c", StringComparison.OrdinalIgnoreCase)
            ? parent
            : null,
        StoragePaths = ReadStoragePaths(o),
    };

    public static VsphereVirtualMachine ToVirtualMachine(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        InstanceUuid = PropertyCollectorParser.ReadString(o.Values, "config.instanceUuid"),
        PowerState = PropertyCollectorParser.ReadString(o.Values, "runtime.powerState"),
        OverallStatus = PropertyCollectorParser.ReadString(o.Values, "summary.overallStatus"),
        HostMoRef = PropertyCollectorParser.ReadString(o.Values, "runtime.host"),
        DatastoreMoRefs = PropertyCollectorParser.SplitValues(
            PropertyCollectorParser.ReadString(o.Values, "datastore")),
        VirtualCpuCount = ReadCount(o, "config.hardware.numCPU"),
        ConfiguredMemoryMb = PropertyCollectorParser.ReadLong(o.Values, "config.hardware.memoryMB"),
        // Not coerced. vCenter's -1 means "no limit" and is carried through as
        // it arrived, because null here means "we could not read it" and the
        // two send somebody to different places.
        CpuLimitMhz = PropertyCollectorParser.ReadLong(o.Values, "config.cpuAllocation.limit"),
        MemoryLimitMb = PropertyCollectorParser.ReadLong(o.Values, "config.memoryAllocation.limit"),
        Snapshots = ReadSnapshots(o),
        SnapshotBytes = ReadSnapshotBytes(o),
    };

    /// <summary>Reads a count that must fit in an int, or null.</summary>
    /// <remarks>
    /// Read through the long reader and then range-checked rather than parsed
    /// as an int directly, so that a value which does not fit becomes null —
    /// "we could not make sense of it" — instead of silently wrapping to a
    /// small number a rule would then divide by.
    /// </remarks>
    private static int? ReadCount(PropertyObject o, string path) =>
        PropertyCollectorParser.ReadLong(o.Values, path) is { } value &&
        value is > 0 and <= int.MaxValue
            ? (int)value
            : null;

    public static VsphereCluster ToCluster(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        // Null when unreadable, never false: "we could not read the HA setting"
        // and "HA is off" lead to opposite actions.
        HighAvailabilityEnabled = PropertyCollectorParser.ReadBoolean(o.Values, "configuration.dasConfig.enabled"),
        DrsEnabled = PropertyCollectorParser.ReadBoolean(o.Values, "configuration.drsConfig.enabled"),
    };

    public static VsphereDatastore ToDatastore(
        PropertyObject o, Dictionary<string, List<string>> volumeDevices) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        CapacityBytes = PropertyCollectorParser.ReadLong(o.Values, "summary.capacity"),
        FreeSpaceBytes = PropertyCollectorParser.ReadLong(o.Values, "summary.freeSpace"),
        UncommittedBytes = PropertyCollectorParser.ReadLong(o.Values, "summary.uncommitted"),
        Url = PropertyCollectorParser.ReadString(o.Values, "summary.url"),
        StorageDevices = VsphereVolume.IdentifierFrom(
                PropertyCollectorParser.ReadString(o.Values, "summary.url")) is { } volume &&
            volumeDevices.TryGetValue(volume, out var devices)
                ? devices
                : [],
        Accessible = PropertyCollectorParser.ReadBoolean(o.Values, "summary.accessible"),
        Type = PropertyCollectorParser.ReadString(o.Values, "summary.type"),
    };

    // --- session ----------------------------------------------------------

    private async Task<VsphereServiceContent> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_serviceContent is { } cached && _loggedIn)
        {
            return cached;
        }

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_serviceContent is { } existing && _loggedIn)
            {
                return existing;
            }

            var contentXml = await PostAsync(
                VsphereSoapRequests.RetrieveServiceContent(), cancellationToken).ConfigureAwait(false);

            var content = VsphereServiceContentParser.TryParse(contentXml)
                ?? throw new VsphereApiException(
                    "vCenter did not return usable service content. The endpoint may not be a vSphere SDK.");

            await PostAsync(
                // The one place the credential leaves its wrapper. It goes straight
                // into the login request and nowhere else; see ADR-0010.
                VsphereSoapRequests.Login(
                    content.SessionManager, _options.Username, _options.Password.Reveal()),
                cancellationToken).ConfigureAwait(false);

            _serviceContent = content;
            _loggedIn = true;
            return content;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Sends a request, logging in again once if the session has expired.
    /// </summary>
    /// <remarks>
    /// Only <c>NotAuthenticated</c> is retried, and only once. Repeating a
    /// rejected credential every cycle is how a monitoring account ends up
    /// locked out by its own retry loop.
    /// </remarks>
    private async Task<string> SendAsync(
        string body,
        CancellationToken cancellationToken,
        VsphereCallContext context = VsphereCallContext.General)
    {
        try
        {
            return await PostAsync(body, cancellationToken, context).ConfigureAwait(false);
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.NotAuthenticated)
        {
            _loggedIn = false;
            await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(body, cancellationToken, context).ConfigureAwait(false);
        }
    }

    private async Task<string> PostAsync(
        string body,
        CancellationToken cancellationToken,
        VsphereCallContext context = VsphereCallContext.General)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sdk")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        // vCenter accepts an empty SOAPAction; sending one avoids a 500 from
        // intermediaries that insist on the header being present.
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"urn:vim25/8.0.0.0\"");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // vCenter returns faults as HTTP 500 with a SOAP fault body, so the
        // status code alone cannot tell "your credentials are wrong" from "the
        // server is broken". The body decides.
        if (VsphereSoapFaultReader.TryRead(content, context) is { } fault)
        {
            throw fault.Kind == VsphereFaultKind.QuerySizeRefused
                ? new VsphereQuerySizeRefusedException(fault.Message)
                : new VsphereApiException(fault.Kind, Describe(fault));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new VsphereApiException(
                $"vCenter returned {(int)response.StatusCode} {response.ReasonPhrase} with no SOAP fault.");
        }

        return content;
    }

    private static string Describe(VsphereSoapFault fault) =>
        fault.Kind switch
        {
            VsphereFaultKind.InvalidLogin =>
                "vCenter rejected the credentials. This is not retried, so that repeated attempts " +
                "cannot lock the monitoring account out.",
            VsphereFaultKind.NoPermission =>
                $"The account is authenticated but lacks a required privilege ({fault.Message}). " +
                "This needs a role change, not a retry.",
            _ => string.IsNullOrWhiteSpace(fault.Message) ? fault.FaultType : fault.Message,
        };

    /// <summary>
    /// Builds a handler configured for this connection.
    /// </summary>
    /// <remarks>
    /// Certificate validation is only relaxed when the options say so. vCenter
    /// ships with a self-signed certificate that many installations never
    /// replace, so this has to be expressible — but as a decision someone
    /// recorded, not something the collector does quietly on their behalf.
    /// </remarks>
    public static HttpMessageHandler CreateHandler(VsphereConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler
        {
            // vCenter tracks the session with the vmware_soap_session cookie.
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };

        if (options.AcceptUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionGate.Dispose();
    }
}
