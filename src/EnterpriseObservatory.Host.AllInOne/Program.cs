using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Host.AllInOne;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Host.AllInOne.Notifications;
using EnterpriseObservatory.Host.AllInOne.State;

// The composition root, and the only place in the product that knows a
// concrete collector exists. Everything below it is wired through ports, which
// is what lets this same code run as one process today and as a collector
// service plus a web host tomorrow without the application layer changing.
// See ADR-0001.
var builder = Host.CreateApplicationBuilder(args);

var endpoints = builder.Configuration.GetSection("VCenters").Get<List<VsphereEndpointOptions>>() ?? [];

// Checked before anything is constructed. The previous product's credential
// leak was a password in a settings file; this refuses to start rather than
// carrying on with one. See CredentialSourceGuard.
CredentialSourceGuard.EnsureNotFromFiles(
    builder.Configuration,
    endpoints.Select((_, i) => $"VCenters:{i}:Password"));

var problems = endpoints.SelectMany((e, i) => e.Validate(i)).ToList();

if (problems.Count > 0)
{
    // Refusing to start beats starting half-configured. A collector that was
    // silently skipped looks exactly like an estate with nothing wrong in it.
    throw new InvalidOperationException(
        "Configuration is not usable:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
}

builder.Services.AddSingleton(BuildMonitoringOptions(builder.Configuration));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IEntityGraphStore, InMemoryEntityGraphStore>();
builder.Services.AddSingleton<IAlertStateStore, InMemoryAlertStateStore>();
builder.Services.AddSingleton<ICollectorHealthStore, InMemoryCollectorHealthStore>();
builder.Services.AddSingleton<IAlertNotifier, LoggingAlertNotifier>();
builder.Services.AddSingleton<InventoryCollectionPipeline>();
builder.Services.AddSingleton<ObservationCollectionPipeline>();
builder.Services.AddSingleton<MonitoringCycle>();

foreach (var endpoint in endpoints)
{
    AddVsphere(builder.Services, endpoint);
}

builder.Services.AddHostedService<MonitoringWorker>();

var host = builder.Build();

// Logged after building, so what the service is actually about to do is on the
// record. The untrusted-certificate case gets its own warning rather than a
// clause in a sentence: it is a security posture someone chose, and it should
// be as easy to find in a log as it was to turn on.
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EnterpriseObservatory");

foreach (var endpoint in endpoints)
{
    var address = new Uri(endpoint.BaseAddress);

    HostLog.VsphereConfigured(startupLog, endpoint.InstanceId, address, endpoint.Username);

    if (endpoint.AcceptUntrustedCertificate)
    {
        HostLog.UntrustedCertificateAccepted(startupLog, endpoint.InstanceId);
    }
}

host.Run();

static MonitoringOptions BuildMonitoringOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Monitoring");
    var defaults = MonitoringOptions.Default;

    return new MonitoringOptions
    {
        InventoryInterval = Seconds(section["InventoryIntervalSeconds"], defaults.InventoryInterval),
        ObservationInterval = Seconds(section["ObservationIntervalSeconds"], defaults.ObservationInterval),
    };
}

static TimeSpan Seconds(string? value, TimeSpan fallback) =>
    int.TryParse(value, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : fallback;

// One vCenter becomes two collectors sharing one client: they read the same
// server with the same session, but on different schedules and failing
// independently. See ADR-0005.
static void AddVsphere(IServiceCollection services, VsphereEndpointOptions endpoint)
{
    var connection = new VsphereConnectionOptions
    {
        InstanceId = endpoint.InstanceId,
        BaseAddress = new Uri(endpoint.BaseAddress),
        Username = endpoint.Username,
        Password = endpoint.Password,
        AcceptUntrustedCertificate = endpoint.AcceptUntrustedCertificate,
        InventoryPageSize = endpoint.InventoryPageSize,
    };

    services.AddSingleton(_ => new VsphereClient(CreateHttpClient(connection), connection));

    services.AddSingleton<IInventorySource>(provider => new VsphereInventorySource(
        Client(provider, connection.InstanceId),
        provider.GetRequiredService<IClock>()));

    services.AddSingleton<IObservationSource>(provider => new VsphereObservationSource(
        Client(provider, connection.InstanceId),
        new GraphSampleTargetProvider(
            provider.GetRequiredService<IEntityGraphStore>(), connection.InstanceId),
        provider.GetRequiredService<IClock>()));
}

static VsphereClient Client(IServiceProvider provider, string instanceId) =>
    provider.GetServices<VsphereClient>().First(c =>
        string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal));

static HttpClient CreateHttpClient(VsphereConnectionOptions connection)
{
    var handler = new HttpClientHandler
    {
        // vim25 authenticates with a session cookie, so the handler must keep
        // one. Without this every call would be unauthenticated and the
        // symptom would be a login loop rather than an obvious error.
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer(),
    };

    if (connection.AcceptUntrustedCertificate)
    {
        // Scoped to this one vCenter's handler, never to the process. A global
        // callback would silently relax validation for every other outbound
        // call the product ever makes, including ones added years from now by
        // someone who never saw this line.
        handler.ServerCertificateCustomValidationCallback =
            static (_, _, _, _) => true;
    }

    return new HttpClient(handler) { BaseAddress = connection.BaseAddress };
}
