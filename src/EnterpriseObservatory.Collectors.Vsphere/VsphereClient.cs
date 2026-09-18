using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>A vCenter call failed in a way the caller should know about.</summary>
public sealed class VsphereApiException : Exception
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
    /// <summary>Objects per page. Bounded because an unbounded retrieval can be refused outright.</summary>
    private const int PageSize = 250;

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
        catch (VsphereApiException ex) when (
            ex.Kind is VsphereFaultKind.NoPermission or VsphereFaultKind.InvalidName)
        {
            // Two survivable cases, both seen against a live server. A read-only
            // account may not be granted Global.Settings; and the option itself
            // is absent until someone sets it, which vCenter reports as an
            // invalid name. Either costs us the exact limit and nothing else —
            // the batch sizer falls back to the documented default. Failing the
            // whole cycle over an optional reading would be worse.
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
        CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);
        var byId = catalog.ToDictionary(c => c.Id);

        var response = await SendAsync(
            VsphereSoapRequests.QueryAvailablePerfMetric(
                content.PerformanceManager,
                entityMoRef,
                entityType.ToString(),
                VsphereIntervals.IntervalSecondsFor(entityType)),
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

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "counterId"))
        {
            if (int.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                byId.TryGetValue(id, out var counter))
            {
                keys.Add(counter.Key);
            }
        }

        return [.. keys];
    }

    public async Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityMoRefs);
        ArgumentNullException.ThrowIfNull(counters);

        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var intervalSeconds = VsphereIntervals.IntervalSecondsFor(entityType);

        var response = await SendAsync(
            VsphereSoapRequests.QueryPerf(
                content.PerformanceManager, entityMoRefs, entityType.ToString(),
                counters, intervalSeconds, MaxSample),
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
        ],
        ["VirtualMachine"] =
        [
            "name",
            "summary.overallStatus",
            "runtime.powerState",
            "runtime.host",
            "config.instanceUuid",
            "datastore",
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
            "summary.type",
        ],
    };

    public async Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken)
    {
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<VsphereReadFailure>();

        var viewMoRef = await CreateViewAsync(content, cancellationToken).ConfigureAwait(false);

        try
        {
            var objects = await RetrieveAllPagesAsync(content, viewMoRef, cancellationToken)
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

            return new VsphereInventoryPayload
            {
                VCenterName = string.IsNullOrWhiteSpace(content.Name) ? InstanceId : content.Name,
                Hosts = [.. objects.Where(o => o.Type == "HostSystem").Select(ToHost)],
                VirtualMachines = [.. objects.Where(o => o.Type == "VirtualMachine").Select(ToVirtualMachine)],
                Clusters = [.. objects.Where(o => o.Type == "ClusterComputeResource").Select(ToCluster)],
                Datastores = [.. objects.Where(o => o.Type == "Datastore").Select(ToDatastore)],
                Failures = failures,
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
    private async Task<List<PropertyObject>> RetrieveAllPagesAsync(
        VsphereServiceContent content,
        string viewMoRef,
        CancellationToken cancellationToken)
    {
        var all = new List<PropertyObject>();

        var response = await SendAsync(
            VsphereSoapRequests.RetrievePropertiesEx(
                content.PropertyCollector, content.RootFolder, viewMoRef, InventoryProperties, PageSize),
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
        }

        return all;
    }

    private static VsphereHost ToHost(PropertyObject o) => new()
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
    };

    private static VsphereVirtualMachine ToVirtualMachine(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        InstanceUuid = PropertyCollectorParser.ReadString(o.Values, "config.instanceUuid"),
        PowerState = PropertyCollectorParser.ReadString(o.Values, "runtime.powerState"),
        OverallStatus = PropertyCollectorParser.ReadString(o.Values, "summary.overallStatus"),
        HostMoRef = PropertyCollectorParser.ReadString(o.Values, "runtime.host"),
        DatastoreMoRefs = PropertyCollectorParser.SplitValues(
            PropertyCollectorParser.ReadString(o.Values, "datastore")),
    };

    private static VsphereCluster ToCluster(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        // Null when unreadable, never false: "we could not read the HA setting"
        // and "HA is off" lead to opposite actions.
        HighAvailabilityEnabled = PropertyCollectorParser.ReadBoolean(o.Values, "configuration.dasConfig.enabled"),
        DrsEnabled = PropertyCollectorParser.ReadBoolean(o.Values, "configuration.drsConfig.enabled"),
    };

    private static VsphereDatastore ToDatastore(PropertyObject o) => new()
    {
        MoRef = o.MoRef,
        Name = PropertyCollectorParser.ReadString(o.Values, "name") ?? o.MoRef,
        CapacityBytes = PropertyCollectorParser.ReadLong(o.Values, "summary.capacity"),
        FreeSpaceBytes = PropertyCollectorParser.ReadLong(o.Values, "summary.freeSpace"),
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
                VsphereSoapRequests.Login(content.SessionManager, _options.Username, _options.Password),
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
