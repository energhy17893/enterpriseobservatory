using System.Globalization;
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
public sealed partial class VsphereClient : IVsphereApi, IVsphereInventoryApi, IVsphereEventApi
{
    /// <summary>Samples per series; see <see cref="VsphereSoapRequests.QueryPerf"/>.</summary>
    private const int MaxSample = 3;

    /// <summary>
    /// The transport and session, owned and lived out by whoever built this
    /// client (F3) — the registry in production, a test's fixture otherwise.
    /// This client no longer builds an <see cref="HttpClient"/>, decides when
    /// it is logged in, or closes anything: it asks the channel for a session
    /// and sends already-built SOAP envelopes through it.
    /// </summary>
    private readonly VsphereSessionChannel _channel;

    private readonly VsphereConnectionOptions _options;
    private IReadOnlyList<VsphereCounter>? _counterCatalog;

    public VsphereClient(VsphereSessionChannel channel, VsphereConnectionOptions options)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string InstanceId => _options.InstanceId;

    /// <summary>
    /// Reads the vCenter endpoint's certificate during the inventory read
    /// (M8.7); none is read when null.
    /// </summary>
    /// <remarks>
    /// Set by the host to a <see cref="TlsEndpointCertificateReader"/>. Left
    /// out by default, so a client built for one question (the probe, a test)
    /// opens no second connection it was not asked to.
    /// </remarks>
    public IEndpointCertificateReader? CertificateReader { get; init; }

    /// <summary>
    /// The time zone a VM's last-backup attribute is read in (M8.8).
    /// </summary>
    /// <remarks>
    /// The backup product writes its server's local time with no offset; the
    /// collector's own zone is the default assumption, and the reading records
    /// which zone it used. Settable so a test is not at the mercy of the
    /// machine it runs on.
    /// </remarks>
    public TimeZoneInfo BackupTimeZone { get; init; } = TimeZoneInfo.Local;

    // --- IVsphereApi ------------------------------------------------------

    public async Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken)
    {
        if (_counterCatalog is { } cached)
        {
            return cached;
        }

        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Why <see cref="GetMaxQueryMetricsAsync"/> returned null, in one line, without the value.
    /// </summary>
    /// <remarks>
    /// For the read-only probe only. The collector treats every unreadable
    /// case the same (fall back to 256); an operator deciding whether to grant
    /// a privilege or set the option needs to know which case it is.
    /// </remarks>
    public async Task<string> DiagnoseMaxQueryMetricsAsync(CancellationToken cancellationToken)
    {
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        if (content.SettingManager is not { } settingManager)
        {
            return "no OptionManager (setting) in ServiceContent";
        }

        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.QueryMaxQueryMetrics(settingManager), cancellationToken)
                .ConfigureAwait(false);

            var value = VsphereXml.Parse(response)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "value")?.Value;

            return value is null
                ? "not present (empty result)"
                : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? "present and readable (value withheld)"
                    : "present but not an integer";
        }
        catch (VsphereApiException ex)
        {
            return $"fault {ex.Kind}";
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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
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

        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken)
    {
        await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendAsync(VsphereSoapRequests.CurrentTime(), cancellationToken).ConfigureAwait(false);

        return VsphereSoapRequests.ParseCurrentTime(response);
    }

    public async Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
        IReadOnlyList<PerfQueryTarget> targets,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(counters);

        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var intervalSeconds = VsphereIntervals.IntervalSecondsFor(entityType);

        var response = await SendAsync(
            VsphereSoapRequests.QueryPerfWindows(
                content.PerformanceManager, targets, entityType.ToString(), counters, intervalSeconds),
            cancellationToken,
            VsphereCallContext.PerformanceQuery).ConfigureAwait(false);

        return PerfResponseParser.ParseSamples(
            response, counters.ToDictionary(c => c.Id), TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>
    /// A performance query with the sample count and window chosen by the
    /// caller, returning the raw reply beside the parsed samples.
    /// </summary>
    /// <remarks>
    /// For the read-only probe's measurements only (how <c>maxSample</c>
    /// interacts with a window, and how large a long real-time read is). The
    /// collector's own reads go through <see cref="QueryPerfAsync"/>, whose
    /// shape is fixed on purpose.
    /// </remarks>
    public async Task<(string Body, IReadOnlyList<PerfEntitySamples> Samples)> QueryPerfForMeasurementAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        int maxSample,
        (DateTimeOffset From, DateTimeOffset To)? window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityMoRefs);
        ArgumentNullException.ThrowIfNull(counters);

        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var intervalSeconds = VsphereIntervals.IntervalSecondsFor(entityType);

        var response = await SendAsync(
            VsphereSoapRequests.QueryPerf(
                content.PerformanceManager, entityMoRefs, entityType.ToString(),
                counters, intervalSeconds, maxSample, window),
            cancellationToken,
            VsphereCallContext.PerformanceQuery).ConfigureAwait(false);

        return (response, PerfResponseParser.ParseSamples(
            response, counters.ToDictionary(c => c.Id), TimeSpan.FromSeconds(intervalSeconds)));
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

            // Collection PR 1, each seen on 145 of 145 live machines. Every
            // machine has a connection state, a consolidation flag and a
            // device list (a keyboard and a video card at the least), so an
            // absent one was not read.
            "runtime.connectionState",
            "runtime.consolidationNeeded",
            "config.hardware.device",

            // M8.8: 145 of 145 live; a machine with no custom values answers
            // with an empty array (59 did), never with nothing.
            BackupAttributeParser.CustomValuePath,
        ],
        ["ClusterComputeResource"] =
        [
            "name",
            "configuration.dasConfig.enabled",
            "configuration.drsConfig.enabled",

            // Every cluster has a summary (3 of 3 live). Its currentEVCModeKey
            // is legitimately absent when EVC is off, and is not a row.
            "summary",

            // Stands for its dasConfig child: configurationEx can only be
            // requested whole, and every cluster's carries dasConfig, so a
            // cluster without it had its HA configuration go unread. Its
            // rule and group children are deliberately not expected -- a
            // cluster with none is the common, valid answer.
            "configurationEx",
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

    // One invalid path here fails the entire inventory, not one property:
    // vCenter answers InvalidProperty for the whole RetrievePropertiesEx
    // (measured with "configurationEx.dasConfig"). A path must walk declared
    // types only. TODO(T2.1): the collector contract suite needs an "unknown
    // property path" case that pins this down.
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

            // Collection PR 1 -- every path below was read alone on a live
            // vCenter before entering this list
            // (docs/measurements/collection-pr1-shapes.md). Read in
            // InventoryVerdictParser.
            //
            // vCenter's own verdicts: its configuration issues (an empty
            // array on a healthy host) and the hardware sensors (749 on 10
            // hosts, 343 KB).
            InventoryVerdictParser.ConfigIssuePath,
            InventoryVerdictParser.HealthSystemRuntimePath,

            // M8.7: the ESXi certificate, whole, for its expiry. The
            // certificate manager's certificateInfo would be smaller and is
            // refused to the read-only role (NoPermission, measured); this is
            // ~55 KB a host.
            InventoryVerdictParser.CertificatePath,

            // Collection PR 2 (docs/measurements/collection-pr2-shapes.md):
            // the host's newest possible EVC mode, ~0.2 KB a host, so the EVC
            // check can tell an EVC-off cluster of one CPU generation from one
            // of several. A sub-path is safe here: HostSystem.summary is
            // declared as the concrete HostListSummary, and the path was read
            // alone live on 10 of 10 hosts without a fault.
            InventoryVerdictParser.HostMaxEvcModePath,
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

            // Collection PR 1 (measured live, see the host list). The device
            // list is the largest addition -- ~8 KB a machine -- and there is
            // no narrower path to a CD drive's backing and connection (M8.4).
            InventoryVerdictParser.ConfigIssuePath,
            InventoryVerdictParser.ConnectionStatePath,
            InventoryVerdictParser.ConsolidationNeededPath,
            InventoryVerdictParser.DevicePath,

            // M8.8 (docs/measurements/backup-freshness-shapes.md): the custom
            // attribute values, where a backup product leaves its last backup
            // time. Whole: declared CustomFieldValue[], its elements the
            // polymorphic CustomFieldStringValue, so no sub-path. Read alone
            // live on 145 of 145 machines without a fault, ~50 KB. The names
            // behind the keys come from CustomFieldsManager, in a call of
            // their own.
            BackupAttributeParser.CustomValuePath,
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

            // Collection PR 1. summary whole, for EVC (M8.4): its
            // currentEVCModeKey sub-path is InvalidProperty on a live
            // vCenter, like configurationEx's children.
            InventoryVerdictParser.ConfigIssuePath,
            InventoryVerdictParser.ClusterSummaryPath,
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

            // Collection PR 1: vCenter's verdicts, and which hosts mount it
            // (M8.4 -- one mounting host pins its machines to that host).
            InventoryVerdictParser.ConfigIssuePath,
            InventoryVerdictParser.MaintenanceModePath,
            InventoryVerdictParser.DatastoreHostPath,
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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
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

            var rootAlarms = await ReadRootFolderAlarmStatesAsync(
                content, failures, cancellationToken).ConfigureAwait(false);

            var alarms = await ReadTriggeredAlarmsAsync(
                content, objects, rootAlarms, failures, cancellationToken).ConfigureAwait(false);

            // Built once from every host, because a shared volume is mounted on
            // many and any one of them can say which device it sits on.
            var volumeDevices = ReadVolumeDevices(objects);

            var vCenterVerdicts = await ReadVCenterCertificateAsync(cancellationToken).ConfigureAwait(false);

            var backupFields = await ReadLastBackupFieldsAsync(content, failures, cancellationToken)
                .ConfigureAwait(false);

            return new VsphereInventoryPayload
            {
                VCenterName = string.IsNullOrWhiteSpace(content.Name) ? InstanceId : content.Name,
                VCenterVerdicts = vCenterVerdicts,
                RootFolderMoRef = content.RootFolder,
                Hosts = [.. objects.Where(o => o.Type == "HostSystem").Select(ToHost)],
                VirtualMachines =
                [
                    .. objects.Where(o => o.Type == "VirtualMachine")
                        .Select(o => WithBackup(ToVirtualMachine(o), o, backupFields)),
                ],
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

    /// <summary>
    /// The vCenter endpoint's certificate as its expiry and fingerprint, or
    /// nothing when no reader is set or the handshake read none.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadVCenterCertificateAsync(
        CancellationToken cancellationToken)
    {
        if (CertificateReader is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var certificate = await CertificateReader
            .ReadAsync(_options.BaseAddress, cancellationToken)
            .ConfigureAwait(false);

        return certificate is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : InventoryVerdictParser.CertificateVerdicts(certificate);
    }

    /// <summary>
    /// The custom field definitions that name a last backup time, key to name
    /// (M8.8), or null when they could not be read.
    /// </summary>
    /// <remarks>
    /// A call of its own, like the root folder's alarms: a refusal costs the
    /// backup reading and nothing else, and is recorded. Null — not an empty
    /// map — so a VM is then "not read", never "no backup attribute". A
    /// vCenter that offers no custom fields manager has no custom fields, and
    /// that is an empty map.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>?> ReadLastBackupFieldsAsync(
        VsphereServiceContent content,
        List<VsphereReadFailure> failures,
        CancellationToken cancellationToken)
    {
        const string target = "custom field definitions: field";

        if (content.CustomFieldsManager is not { Length: > 0 } manager)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.RetrieveObjectProperties(
                    content.PropertyCollector, "CustomFieldsManager", [manager], [BackupAttributeParser.FieldPath]),
                cancellationToken).ConfigureAwait(false);

            var objects = PropertyCollectorParser.ParsePage(response).Objects;

            if (objects.SelectMany(o => o.Missing).FirstOrDefault() is { } missing)
            {
                failures.Add(new VsphereReadFailure
                {
                    Target = target,
                    Detail = missing.FaultType.Length == 0 ? "unreadable" : missing.FaultType,
                    IsPermissionDenied = missing.IsPermissionDenied,
                });

                return null;
            }

            return BackupAttributeParser.LastBackupFields(objects.SelectMany(o =>
                o.Structures.TryGetValue(BackupAttributeParser.FieldPath, out var definitions) ? definitions : []));
        }
        catch (VsphereApiException ex)
        {
            failures.Add(new VsphereReadFailure
            {
                Target = target,
                Detail = $"backup freshness is not read: {ex.Message}",
                IsPermissionDenied = ex.Kind == VsphereFaultKind.NoPermission,
            });

            return null;
        }
    }

    private VsphereVirtualMachine WithBackup(
        VsphereVirtualMachine vm, PropertyObject o, IReadOnlyDictionary<string, string>? backupFields)
    {
        var backup = BackupAttributeParser.Read(o, backupFields, BackupTimeZone);
        if (backup.Count == 0)
        {
            return vm;
        }

        var merged = new Dictionary<string, string>(vm.Verdicts, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in backup)
        {
            merged.TryAdd(key, value);
        }

        return vm with { Verdicts = merged };
    }

    private Task<string> CreateViewAsync(VsphereServiceContent content, CancellationToken cancellationToken) =>
        CreateViewAsync(content, [.. InventoryProperties.Keys], cancellationToken);

    private async Task<string> CreateViewAsync(
        VsphereServiceContent content,
        IReadOnlyList<string> types,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            VsphereSoapRequests.CreateContainerView(
                content.ViewManager, content.RootFolder, types),
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
    private Task<(List<PropertyObject> Objects, int Pages)> RetrieveAllPagesAsync(
        VsphereServiceContent content,
        string viewMoRef,
        CancellationToken cancellationToken) =>
        RetrieveAllPagesAsync(content, viewMoRef, InventoryProperties, cancellationToken);

    private async Task<(List<PropertyObject> Objects, int Pages)> RetrieveAllPagesAsync(
        VsphereServiceContent content,
        string viewMoRef,
        IReadOnlyDictionary<string, IReadOnlyList<string>> properties,
        CancellationToken cancellationToken,
        Action<int>? onReply = null)
    {
        var all = new List<PropertyObject>();
        var pages = 1;

        var response = await SendAsync(
            VsphereSoapRequests.RetrievePropertiesEx(
                content.PropertyCollector, content.RootFolder, viewMoRef,
                properties, _options.InventoryPageSize),
            cancellationToken).ConfigureAwait(false);
        onReply?.Invoke(response.Length);

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

                onReply?.Invoke(response.Length);
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
        IReadOnlyList<PropertyNode> rootStates,
        List<VsphereReadFailure> failures,
        CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, VsphereTriggeredAlarm>(StringComparer.Ordinal);

        foreach (var state in objects
            .Where(o => o.Structures.ContainsKey(TriggeredAlarmStatePath))
            .SelectMany(o => o.Structures[TriggeredAlarmStatePath])
            .Concat(rootStates))
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

    private const string TriggeredAlarmStatePath = "triggeredAlarmState";

    /// <summary>
    /// Reads the root folder's triggered alarms, in a call of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collection PR 2. The container view holds what is <em>below</em> the
    /// root folder, never the folder itself, so an alarm vCenter raises on
    /// the whole vCenter — its own licence expiry, for one — was never read.
    /// Measured live (docs/measurements/collection-pr2-shapes.md): the root
    /// carried 4 alarm states, 3 raised on the folder itself and one on a
    /// host whose own list carried the same key; the root also carries every
    /// alarm the four collected types carry, because vCenter propagates an
    /// alarm up the tree. Keying on vCenter's key keeps each once.
    /// </para>
    /// <para>
    /// A separate call rather than a second object spec in the inventory
    /// retrieval, so that a refusal here costs these alarms and nothing
    /// else: in the inventory retrieval one fault fails all of it. A refusal
    /// is recorded and survived, like the alarm definitions below.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<PropertyNode>> ReadRootFolderAlarmStatesAsync(
        VsphereServiceContent content,
        List<VsphereReadFailure> failures,
        CancellationToken cancellationToken)
    {
        const string target = "root folder: triggeredAlarmState";

        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.RetrieveObjectProperties(
                    content.PropertyCollector, "Folder", [content.RootFolder], [TriggeredAlarmStatePath]),
                cancellationToken).ConfigureAwait(false);

            var root = PropertyCollectorParser.ParsePage(response).Objects;

            foreach (var missing in root.SelectMany(o => o.Missing))
            {
                failures.Add(new VsphereReadFailure
                {
                    Target = target,
                    Detail = missing.FaultType.Length == 0 ? "unreadable" : missing.FaultType,
                    IsPermissionDenied = missing.IsPermissionDenied,
                });
            }

            return
            [
                .. root.SelectMany(o =>
                    o.Structures.TryGetValue(TriggeredAlarmStatePath, out var states) ? states : []),
            ];
        }
        catch (VsphereApiException ex)
        {
            failures.Add(new VsphereReadFailure
            {
                Target = target,
                Detail = $"alarms raised on the vCenter as a whole are not reported: {ex.Message}",
                IsPermissionDenied = ex.Kind == VsphereFaultKind.NoPermission,
            });

            return [];
        }
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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
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

            // One level into configurationEx, then one more into the three
            // children this collector reads. Element names, xsi:types and
            // counts only: a rule's or group's name is estate data.
            var clusters = objects
                .Where(o => o.Structures.ContainsKey("configurationEx"))
                .ToList();

            lines.Add($"configurationEx ({clusters.Count} clusters)");
            foreach (var child in clusters
                .SelectMany(o => o.Structures["configurationEx"])
                .GroupBy(n => (n.Name, n.Type))
                .OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                var type = child.Key.Type.Length == 0 ? "(untyped)" : child.Key.Type;
                lines.Add($"  {child.Key.Name,-28} {type,-44} {child.Count(),5}");

                if (child.Key.Name is "dasConfig" or "rule" or "group")
                {
                    var grandchildren = child
                        .SelectMany(n => n.Children)
                        .GroupBy(n => n.Name, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => $"{g.Key}({g.Count()})");
                    lines.Add($"      {string.Join(", ", grandchildren)}");
                }
            }

            lines.Add("readers");
            foreach (var cluster in clusters)
            {
                var ha = ClusterConfigurationParser.ReadHaSettings(cluster);
                lines.Add(
                    $"  cluster: ha settings {(ha is null ? "NOT READ" : ha.Count.ToString(CultureInfo.InvariantCulture) + " keys")}, " +
                    $"groups {PropertyCollectorParser.ReadClusterGroups(cluster.Structures).Count}, " +
                    $"drs rules {PropertyCollectorParser.ReadDrsRules(cluster.Structures).Count}");
            }

            foreach (var byTransport in objects
                .Where(o => string.Equals(o.Type, "HostSystem", StringComparison.Ordinal))
                .SelectMany(ReadStoragePaths)
                .GroupBy(p => p.TransportType.Length == 0 ? "(none)" : p.TransportType)
                .OrderByDescending(g => g.Count()))
            {
                lines.Add(
                    $"  paths {byTransport.Key}: {byTransport.Count()}, " +
                    $"with target {byTransport.Count(p => p.Target is not null)}, " +
                    $"distinct targets {byTransport.Select(p => p.Target).OfType<string>().Distinct(StringComparer.Ordinal).Count()}");
            }

            // Collection PR 1: which verdict keys each object type yielded,
            // and on how many objects. Keys and counts only.
            lines.Add("verdicts");
            foreach (var byType in objects
                .GroupBy(o => o.Type, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                foreach (var key in byType
                    .SelectMany(o => InventoryVerdictParser.Read(o).Keys)
                    .GroupBy(k => k, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    lines.Add($"  {byType.Key,-24} {key.Key,-34} {key.Count()}/{byType.Count()}");
                }
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
                    TransportType = path.Child("transport")?.Type ?? string.Empty,
                    Target = TransportTarget(path.Child("transport")),
                });
            }
        }

        return paths;
    }

    /// <summary>
    /// The storage-side port a path's transport names, or null when it names
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fibre Channel (measured live, 1240 paths): <c>portWorldWideName</c> is
    /// an <c>xsd:long</c>, so a WWN with its top bit set — NAA type C, usual
    /// for virtual ports — arrives negative. It is reinterpreted unsigned and
    /// written as sixteen lowercase hex digits in colon-separated pairs,
    /// <c>50:06:01:60:3b:20:1f:3a</c>, the form switch and array WWPNs will be
    /// joined on. Zero is not a WWN and reads as none.
    /// </para>
    /// <para>
    /// iSCSI: <c>HostInternetScsiTargetTransport.iScsiName</c>, the target
    /// IQN. <strong>Not validated live</strong> — the measured estate had no
    /// iSCSI; this follows the published schema (<c>iScsiName</c>,
    /// <c>iScsiAlias</c>, <c>address[]</c>).
    /// </para>
    /// <para>
    /// SAS and PCIe transports arrived with no children (measured), and
    /// anything else is a transport this reader does not know. Both stay
    /// null rather than being given an invented identity.
    /// </para>
    /// </remarks>
    private static string? TransportTarget(PropertyNode? transport)
    {
        switch (transport?.Type)
        {
            case "HostFibreChannelTargetTransport":
                return long.TryParse(
                        transport.TextOf("portWorldWideName"),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var wwn) && wwn != 0
                    ? FormatWorldWideName(unchecked((ulong)wwn))
                    : null;

            case "HostInternetScsiTargetTransport":
                return transport.TextOf("iScsiName") is { Length: > 0 } iqn ? iqn : null;

            default:
                return null;
        }
    }

    /// <summary><c>50:06:01:60:3b:20:1f:3a</c>: sixteen lowercase hex digits, paired.</summary>
    public static string FormatWorldWideName(ulong wwn)
    {
        var hex = wwn.ToString("x16", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 2, 2)));
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
        Verdicts = InventoryVerdictParser.Read(o),
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
        Verdicts = InventoryVerdictParser.Read(o),
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
        HaSettings = ClusterConfigurationParser.ReadHaSettings(o) ?? ClusterHaSettings.None,
        Groups = PropertyCollectorParser.ReadClusterGroups(o.Structures),
        DrsRules = PropertyCollectorParser.ReadDrsRules(o.Structures),
        Verdicts = InventoryVerdictParser.Read(o),
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
        Verdicts = InventoryVerdictParser.Read(o),
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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

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

    // --- session (F3: owned by VsphereSessionChannel, not this client) ----

    /// <summary>
    /// Sends a request, asking the channel to sign in again once if the
    /// session has expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>NotAuthenticated</c> is retried, and only once. Repeating a
    /// rejected credential every cycle is how a monitoring account ends up
    /// locked out by its own retry loop.
    /// </para>
    /// <para>
    /// The channel, not this client, decides what a retry means: this only
    /// reads <see cref="VsphereSessionChannel.Generation"/> before the call
    /// goes out and hands it back on a refusal, so the channel can tell "the
    /// session my failed call used is still current" from "somebody already
    /// replaced it while my failure was in flight" — that second case asks
    /// for nothing, which is what closes the race #53 needed a lock-free
    /// generation check for. This client no longer has a session field, a
    /// login method or a lock; it only relays the generation it saw.
    /// </para>
    /// </remarks>
    private async Task<string> SendAsync(
        string body,
        CancellationToken cancellationToken,
        VsphereCallContext context = VsphereCallContext.General)
    {
        var sentOn = _channel.Generation;

        try
        {
            return await _channel.SendAsync(body, cancellationToken, context).ConfigureAwait(false);
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.NotAuthenticated)
        {
            await _channel.EnsureSessionAsync(sentOn, cancellationToken).ConfigureAwait(false);
            return await _channel.SendAsync(body, cancellationToken, context).ConfigureAwait(false);
        }
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
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendAsync(
            VsphereSoapRequests.RetrieveSessions(content.PropertyCollector, content.SessionManager),
            cancellationToken).ConfigureAwait(false);

        return VsphereSessions.Parse(response);
    }
}
