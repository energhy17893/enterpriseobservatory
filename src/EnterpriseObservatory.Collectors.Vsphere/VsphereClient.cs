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
public sealed class VsphereClient : IVsphereApi, IVsphereInventoryApi, IVsphereEventApi, IDisposable
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

            var value = VsphereXml.Parse(response)
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
            document = VsphereXml.Parse(response);
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
    /// <summary>
    /// The requested properties whose absence is always a collection problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberately short subset of <see cref="InventoryProperties"/>,
    /// because most requested properties are legitimately absent on a healthy
    /// object: a virtual machine with no snapshots reports no
    /// <c>snapshot</c>, and an object with nothing wrong reports no
    /// <c>triggeredAlarmState</c>. Counting those as missing coverage would
    /// report an estate as unreadable for being healthy.
    /// </para>
    /// <para>
    /// What is listed is what every object of that type carries in every
    /// working installation. The bar for adding a row is that its absence can
    /// only mean the product failed to read it — and the list is conservative
    /// on purpose, because a false "I cannot see this" is worse than an
    /// incomplete list. One is a gap; the other is the product being
    /// confidently wrong about its own sight, which is the failure the first
    /// principle exists to prevent.
    /// </para>
    /// <para>
    /// Left out despite being requested, and worth recording so nobody adds
    /// them without an argument: <c>config.fileSystemVolume.mountInfo</c>,
    /// <c>config.storageDevice.multipathInfo</c> and <c>scsiLun</c>, because a
    /// host's storage configuration is an estate decision rather than a
    /// guarantee; <c>summary.uncommitted</c>, which the vSphere API documents
    /// as optional; and every <c>triggeredAlarmState</c>, <c>snapshot</c> and
    /// <c>layoutEx</c> path, whose absence is the good outcome.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, IReadOnlyList<string>> AlwaysExpected = new(StringComparer.Ordinal)
    {
        ["HostSystem"] =
        [
            "name",
            "summary.overallStatus",
            "runtime.connectionState",
            "runtime.inMaintenanceMode",
            "hardware.systemInfo.uuid",
            "config.option",

            // Every host runs services and keeps a clock, so a connected host
            // that answered neither was not read. Deliberately not listed:
            // config.network.vswitch and .portgroup, because a host moved
            // wholly onto a distributed switch has no standard switch at all,
            // and config.lockdownMode, which the schema marks optional.
            "config.service",
            "config.dateTimeInfo",
        ],
        ["VirtualMachine"] =
        [
            "name",
            "summary.overallStatus",
            "runtime.powerState",
            "config.instanceUuid",
            "config.hardware.numCPU",
            "config.hardware.memoryMB",
        ],
        ["ClusterComputeResource"] =
        [
            "name",
            "configuration.dasConfig.enabled",
            "configuration.drsConfig.enabled",
        ],
        ["Datastore"] =
        [
            "name",
            "summary.capacity",
            "summary.freeSpace",
            "summary.accessible",
        ],
    };

    /// <summary>
    /// What this collector asks for about one object type.
    /// </summary>
    /// <remarks>
    /// Exposed so the expected list can be held to the requested one. An
    /// expected property nobody asks for is blind in every estate forever,
    /// and the finding would name a path the product never sent — the
    /// product being confidently wrong about its own sight, which is the
    /// failure this whole surface exists to prevent.
    /// </remarks>
    public static IReadOnlyList<string> RequestedProperties(string objectType) =>
        InventoryProperties.TryGetValue(objectType, out var paths) ? paths : [];

    /// <summary>
    /// Counts, per object type and property, how many objects answered.
    /// </summary>
    /// <remarks>
    /// Computed here because this is the only place that knows both halves:
    /// what was asked for and what came back. By the time a payload has been
    /// mapped into entities the question is unanswerable — an absent property
    /// and a property that mapped to a default look identical.
    /// </remarks>
    public static IReadOnlyList<PropertyCoverage> MeasureCoverage(
        IReadOnlyList<PropertyObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var coverage = new List<PropertyCoverage>();

        foreach (var (type, expected) in AlwaysExpected)
        {
            var ofType = objects.Where(o => string.Equals(o.Type, type, StringComparison.Ordinal))
                .ToList();

            if (ofType.Count == 0)
            {
                // Nothing of this type in the estate. No row, because "no
                // hosts" is not "hosts we could not read", and a zero here
                // would read as blindness to anyone scanning the list.
                continue;
            }

            coverage.AddRange(expected.Select(path => new PropertyCoverage
            {
                ObjectType = type,
                Property = path,
                Asked = ofType.Count,
                Answered = ofType.Count(o => o.Values.ContainsKey(path) ||
                                             o.Structures.ContainsKey(path)),
            }));
        }

        return coverage;
    }

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

            // Every advanced setting the host has, which is over a thousand
            // rows. Asking for the whole table and keeping a handful of it is
            // deliberate: vSphere has no way to request individual advanced
            // settings through the property collector, and the alternative —
            // one QueryOptions round trip per setting per host — is a far
            // worse trade than one property that arrives with the rest of the
            // inventory. What is kept is decided in AdvancedSettingKeys.
            "config.option",

            // Host hardening inputs for the compliance engine (roadmap M3.4),
            // carried rather than judged. Each is asked for whole because the
            // property collector cannot address inside an array: there is no
            // path to "the security policy of every vSwitch" short of the
            // switches themselves. Read in ReadServices, ReadTimeConfiguration
            // and ReadSecurityPolicies. None of these shapes has yet been seen
            // from a live vCenter; they follow the published vim25 schema.
            "config.service",
            "config.dateTimeInfo",
            "config.network.vswitch",
            "config.network.portgroup",

            // The three-valued mode, not the legacy adminDisabled boolean,
            // which cannot tell normal lockdown from strict.
            "config.lockdownMode",
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

            // Whole, because it cannot be asked for in parts. Measured against
            // a live vCenter: "configurationEx.dasConfig" is refused as
            // InvalidProperty, and that fault fails the entire retrieval, not
            // one property. The property is declared as the base
            // ComputeResourceConfigInfo; dasConfig, rule and group belong to
            // the ClusterConfigInfoEx it actually holds, and a property path
            // can only walk declared types.
            "configurationEx",
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
                Coverage = MeasureCoverage(objects),
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

        var moRef = VsphereXml.Parse(response)
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "returnval")?.Value?.Trim();

        return string.IsNullOrWhiteSpace(moRef)
            ? throw new VsphereApiException("vCenter did not return a container view.")
            : moRef;
    }

    private Task TryDestroyViewAsync(string viewMoRef, CancellationToken cancellationToken) =>
        TryCleanUpAsync(VsphereSoapRequests.DestroyView(viewMoRef), cancellationToken);

    /// <summary>
    /// Gives a server-side object back, including when the read that made it
    /// was cut off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the caller's token alone. A read is cut off by cancelling its token
    /// — that is how the runner's timeout arrives — and cleanup sent on a
    /// cancelled token throws before it sends anything. The view was the one
    /// place that did exactly that, so every inventory read that timed out
    /// left a view behind, on a session the metric loop keeps alive and so
    /// never recycles. A short grace is cheaper than the leak.
    /// </para>
    /// <para>
    /// Never throws. The session ending collects whatever this could not, and
    /// failing a read over a tidy-up would lose what the read did collect.
    /// </para>
    /// </remarks>
    private async Task TryCleanUpAsync(string request, CancellationToken cancellationToken)
    {
        try
        {
            using var grace = new CancellationTokenSource(CleanupGrace);

            await SendAsync(
                request,
                cancellationToken.IsCancellationRequested ? grace.Token : cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(10);

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

        // The token the server is still holding results against. Each page
        // replaces it, and the last page clears it.
        var open = page.ContinuationToken;

        try
        {
            while (page.HasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                response = await SendAsync(
                    VsphereSoapRequests.ContinueRetrievePropertiesEx(
                        content.PropertyCollector, page.ContinuationToken!),
                    cancellationToken).ConfigureAwait(false);

                page = PropertyCollectorParser.ParsePage(response);
                open = page.ContinuationToken;
                all.AddRange(page.Objects);
                pages++;
            }
        }
        finally
        {
            // Only reached with a token when the walk stopped early: cut off,
            // faulted, or handed a page it could not read.
            if (!string.IsNullOrEmpty(open))
            {
                await TryCleanUpAsync(
                    VsphereSoapRequests.CancelRetrievePropertiesEx(content.PropertyCollector, open),
                    cancellationToken).ConfigureAwait(false);
            }
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
    /// <summary>
    /// The advanced settings worth carrying, keyed by their vSphere name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allowlist rather than the whole table, and the reason is volume: a
    /// host reports over a thousand advanced settings, so keeping all of them
    /// would write five figures of rows per cycle to answer a handful of
    /// questions. Nothing here is a judgement about the values — that belongs
    /// to a rule — only about which ones anything asks about.
    /// </para>
    /// <para>
    /// A setting that is absent is absent; it is not recorded as empty. The
    /// difference matters to every consumer: an unset <c>Syslog.global.logHost</c>
    /// and a host that never reported the setting at all are different facts,
    /// and only the first one is a finding.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ReadAdvancedSettings(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.Structures.TryGetValue("config.option", out var options))
        {
            return AdvancedSettings.None;
        }

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            var key = option.TextOf("key");

            if (key.Length == 0 || !AdvancedSettings.Wanted.Contains(key))
            {
                continue;
            }

            // Recorded even when empty, because an empty value is the answer
            // to most of these: a syslog target nobody set reads as "". The
            // absence that means "not reported" is the missing dictionary
            // entry, not an empty string.
            kept[key] = option.TextOf("value");
        }

        return kept;
    }

    /// <summary>
    /// What this vCenter actually returned, by name only: which keys arrived as
    /// values, which as structures and which were refused, and what a storage
    /// path's transport looks like.
    /// </summary>
    /// <remarks>
    /// For the probe. Several readers here were written from the published
    /// schema and say so; this is how they get pointed at a server. It reports
    /// names and counts, never a value.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DescribeInventoryShapeAsync(CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var viewMoRef = await CreateViewAsync(content, cancellationToken).ConfigureAwait(false);

        try
        {
            var (objects, _) = await RetrieveAllPagesAsync(content, viewMoRef, cancellationToken)
                .ConfigureAwait(false);

            var lines = new List<string>();

            foreach (var byType in objects.GroupBy(o => o.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var total = byType.Count();
                lines.Add($"{byType.Key} ({total})");

                var keys = byType
                    .SelectMany(o => o.Values.Keys.Select(k => (Key: k, As: "value"))
                        .Concat(o.Structures.Keys.Select(k => (Key: k, As: "structure")))
                        .Concat(o.Missing.Select(m => (Key: m.Path, As: "MISSING " + m.FaultType))))
                    .GroupBy(k => k)
                    .OrderBy(g => g.Key.Key, StringComparer.Ordinal);

                foreach (var key in keys)
                {
                    lines.Add($"  {key.Key.Key,-44} {key.Key.As,-22} {key.Count()}/{total}");
                }
            }

            var transports = objects
                .Where(o => o.Structures.ContainsKey("config.storageDevice.multipathInfo"))
                .SelectMany(o => o.Structures["config.storageDevice.multipathInfo"])
                .Where(n => string.Equals(n.Name, "lun", StringComparison.Ordinal))
                .SelectMany(lun => lun.All("path"))
                .Select(path => path.Child("transport"))
                .GroupBy(t => t is null
                    ? "(no transport element)"
                    : $"{(t.Type.Length == 0 ? "(untyped)" : t.Type)}: {string.Join(", ", t.Children.Select(c => c.Name).Distinct(StringComparer.Ordinal))}")
                .OrderByDescending(g => g.Count());

            lines.Add("path.transport");
            foreach (var transport in transports)
            {
                lines.Add($"  {transport.Count(),5} x {transport.Key}");
            }

            return lines;
        }
        finally
        {
            await TryDestroyViewAsync(viewMoRef, cancellationToken).ConfigureAwait(false);
        }
    }

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
        AdvancedSettings = ReadAdvancedSettings(o),
        Services = HostConfigurationParser.ReadServices(o),
        TimeConfiguration = HostConfigurationParser.ReadTimeConfiguration(o),
        VirtualSwitchSecurity = HostConfigurationParser.ReadVirtualSwitchSecurity(o),
        PortGroupSecurity = HostConfigurationParser.ReadPortGroupSecurity(o),
        LockdownMode = HostConfigurationParser.ReadLockdownMode(o),
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

    // --- IVsphereEventApi -------------------------------------------------

    /// <summary>Events per page, for the latest page and each backwards read.</summary>
    public const int EventPageSize = 200;

    /// <summary>
    /// The most pages one read will walk before stopping short of the mark.
    /// </summary>
    /// <remarks>
    /// Ten thousand events per vCenter per cycle, <strong>this product's
    /// choice</strong>. It exists for an event storm or a first contact with a
    /// busy estate; stopping keeps the newest events and reports the gap,
    /// which is the opposite of what QueryEvents does under the same load.
    /// </remarks>
    public const int MaxEventPages = 50;

    /// <summary>How far back the first read of a vCenter looks. This product's choice.</summary>
    public static readonly TimeSpan FirstEventLookBack = TimeSpan.FromHours(24);

    /// <summary>
    /// How far before the mark each window starts.
    /// </summary>
    /// <remarks>
    /// Events are filtered by key afterwards, so the overlap costs a few
    /// re-read events and buys safety against same-second events and a vCenter
    /// clock that stepped backwards a little.
    /// </remarks>
    public static readonly TimeSpan EventWindowOverlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads the events written since the mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collector pattern, and never <c>QueryEvents</c>: that call returns
    /// the <em>oldest</em> thousand events of its window, so on a busy vCenter
    /// it silently misses the most recent hours. Here a collector is opened
    /// over a window starting just before the mark, its <c>latestPage</c> read
    /// first, and <c>ReadPreviousEvents</c> walked backwards until the mark is
    /// reached or the window runs out. The collector is destroyed whatever
    /// happens: vCenter bounds how many one session may hold, and a leaked one
    /// per cycle eventually stops every read on the session.
    /// </para>
    /// <para>
    /// A vCenter whose keys have gone backwards — rebuilt, or its database
    /// reset — is recognised by the newest key being below the mark; "newer"
    /// then falls back to creation time, because the alternative is ignoring
    /// every event it will ever write again.
    /// </para>
    /// <para>
    /// <strong>Not yet verified against a live vCenter.</strong> The request
    /// shapes follow the published schema; the initial position of the
    /// scrollable view and the order of events within a page are handled
    /// defensively (merged by key, sorted) rather than assumed.
    /// </para>
    /// </remarks>
    public async Task<EventRead> ReadEventsAsync(
        EventMark? since,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        if (content.EventManager is not { } eventManager)
        {
            return EventRead.CouldNotAsk(
                "vCenter did not offer an event manager, so its events cannot be read.");
        }

        var begin = since is null ? nowUtc - FirstEventLookBack : since.CreatedAtUtc - EventWindowOverlap;

        var created = await SendAsync(
            VsphereSoapRequests.CreateCollectorForEvents(eventManager, begin),
            cancellationToken).ConfigureAwait(false);

        var collector = VsphereEventParser.ParseCollector(created)
            ?? throw new VsphereApiException("vCenter did not return an event collector.");

        try
        {
            await SendAsync(
                VsphereSoapRequests.SetCollectorPageSize(collector, EventPageSize),
                cancellationToken).ConfigureAwait(false);

            var latest = VsphereEventParser.ParseLatestPage(await SendAsync(
                    VsphereSoapRequests.RetrieveLatestEventPage(content.PropertyCollector, collector),
                    cancellationToken).ConfigureAwait(false))
                ?? throw new VsphereApiException("The latest page of events could not be read.");

            var reset = since is not null && latest.Count > 0 && latest.Max(e => e.Key) < since.Key;

            bool IsNew(SourceEvent e) =>
                since is null || (reset ? e.CreatedAtUtc > since.CreatedAtUtc : e.Key > since.Key);

            var byKey = new Dictionary<long, SourceEvent>();
            foreach (var e in latest)
            {
                byKey.TryAdd(e.Key, e);
            }

            // A latest page that is not full holds everything the window has.
            var exhausted = latest.Count < EventPageSize;
            var reached = since is not null && latest.Any(e => !IsNew(e));
            var pages = 1;

            while (!reached && !exhausted && pages < MaxEventPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var older = VsphereEventParser.ParseEvents(await SendAsync(
                        VsphereSoapRequests.ReadPreviousEvents(collector, EventPageSize),
                        cancellationToken).ConfigureAwait(false))
                    ?? throw new VsphereApiException("A page of older events could not be read.");

                pages++;

                if (older.Count == 0)
                {
                    exhausted = true;
                    break;
                }

                foreach (var e in older)
                {
                    byKey.TryAdd(e.Key, e);
                }

                reached = since is not null && older.Any(e => !IsNew(e));
            }

            return new EventRead
            {
                Events = [.. byKey.Values.Where(IsNew).OrderBy(e => e.Key)],
                Complete = reached || exhausted,
            };
        }
        finally
        {
            await TryDestroyCollectorAsync(collector, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task TryDestroyCollectorAsync(string collector, CancellationToken cancellationToken) =>
        TryCleanUpAsync(VsphereSoapRequests.DestroyCollector(collector), cancellationToken);

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

        // Headers first, body by hand: the default would buffer whatever the
        // far end sends, up to 2 GB, before this code saw a byte of it.
        // HttpClient.Timeout then stops at the headers, so it is applied here to
        // the body as well: a reply that drips forever is as bad as a huge one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(_http.Timeout);
        }

        string content;
        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            try
            {
                content = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The shape HttpClient itself gives a timeout.
            throw new TaskCanceledException(
                $"The vCenter call did not complete within {_http.Timeout}.", new TimeoutException(ex.Message, ex));
        }

        using var owned = response;

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

        // Checked after the status, so that a proxy's HTML error page is still
        // reported by its status rather than by its <!DOCTYPE html>.
        if (VsphereXml.DeclaresDocumentType(content))
        {
            throw new VsphereApiException(
                "vCenter's reply declared a DTD. vim25 never sends one, so the reply was refused unread.");
        }

        return content;
    }

    /// <summary>
    /// The largest reply body read from vCenter, in bytes.
    /// </summary>
    /// <remarks>
    /// Well above any real page (inventory and metric reads are paged far below
    /// this) and far below what would exhaust the host if a hostile or
    /// intercepted endpoint streamed without end.
    /// </remarks>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw TooLarge();
        }

        using var buffer = new MemoryStream();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        var encoding = EncodingFor(content.Headers.ContentType?.CharSet);
        using var reader = new StreamReader(
            buffer, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        static VsphereApiException TooLarge() => new(
            $"vCenter's reply exceeded {MaxResponseBytes / (1024 * 1024)} MB and was abandoned unread.");
    }

    private static Encoding EncodingFor(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
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

    /// <summary>
    /// Who is signed in to this vCenter, as far as this account may see.
    /// </summary>
    /// <remarks>
    /// For the probe, and for answering one question against a real server:
    /// does this product leave sessions behind. Not called by collection.
    /// </remarks>
    public async Task<VsphereSessions> ReadSessionsAsync(CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendAsync(
            VsphereSoapRequests.RetrieveSessions(content.PropertyCollector, content.SessionManager),
            cancellationToken).ConfigureAwait(false);

        return VsphereSessions.Parse(response);
    }

    /// <summary>
    /// Logs out, then asks the vCenter whether it agrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the probe. A read-only account cannot list sessions, so "did the
    /// session end" cannot be read off a list; but the server will say so
    /// itself. After a logout that worked, a call carrying the same session
    /// cookie is refused as <c>NotAuthenticated</c>. After one that did not,
    /// it is answered.
    /// </para>
    /// <para>
    /// The second call is posted directly, because <c>SendAsync</c> would meet
    /// that refusal by signing in again and hide the very answer being sought.
    /// </para>
    /// </remarks>
    /// <returns>
    /// True when the vCenter refused the old session; false when it still
    /// honoured it; null when this client never had a session to end.
    /// </returns>
    public async Task<bool?> LogoutAndConfirmAsync(CancellationToken cancellationToken)
    {
        if (!_loggedIn || _serviceContent is not { } content)
        {
            return null;
        }

        await LogoutAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reply = await PostAsync(
                VsphereSoapRequests.RetrieveSessions(content.PropertyCollector, content.SessionManager),
                cancellationToken).ConfigureAwait(false);

            // Answered, but an answer is not yet "still signed in". The
            // property collector can reply to a session it no longer knows
            // and refuse each property instead of the call — so the only
            // thing that proves the session survived is the session itself
            // coming back.
            return VsphereSessions.Parse(reply).Current is null;
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.NotAuthenticated)
        {
            return true;
        }
    }

    /// <summary>
    /// Ends this client's session on the vCenter, if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing called this. A session is a bounded thing on a vCenter and an
    /// abandoned one stays until the idle timeout collects it, so every
    /// restart, every edited or removed connection and every press of Test
    /// left one behind.
    /// </para>
    /// <para>
    /// Posted directly rather than through <c>SendAsync</c>: that path answers
    /// an expired session by logging in again, and logging in so as to log out
    /// is a directory login spent on nothing. A client that never logged in
    /// sends nothing at all, for the same reason. Never throws — this runs
    /// while a connection is being taken down, when there is nobody left to
    /// tell and the idle timeout is still the backstop.
    /// </para>
    /// </remarks>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_loggedIn || _serviceContent is not { } content)
        {
            return;
        }

        // Cleared first, so a second caller asks nothing and a call racing
        // this one logs in afresh rather than using a session being closed.
        _loggedIn = false;

        try
        {
            using var grace = new CancellationTokenSource(CleanupGrace);

            await PostAsync(
                VsphereSoapRequests.Logout(content.SessionManager),
                cancellationToken.IsCancellationRequested ? grace.Token : cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
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
