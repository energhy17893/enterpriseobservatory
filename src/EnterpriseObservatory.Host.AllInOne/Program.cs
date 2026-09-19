using System.Text.Json.Serialization;
using EnterpriseObservatory.Api;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Host.AllInOne.Notifications;
using EnterpriseObservatory.Host.AllInOne.Security;
using EnterpriseObservatory.Host.AllInOne.State;
using EnterpriseObservatory.Persistence.Sqlite;
using Microsoft.AspNetCore.DataProtection;

// The composition root, and the only place in the product that knows a
// concrete collector exists. Everything below it is wired through ports, which
// is what lets this same code run as one process today and as a collector
// service plus a web host tomorrow without the application layer changing.
// See ADR-0001.
var builder = WebApplication.CreateBuilder(args);

var endpoints = builder.Configuration.GetSection("VCenters").Get<List<VsphereEndpointOptions>>() ?? [];

// Checked before anything is constructed. The previous product's credential
// leak was a password in a settings file; this refuses to start rather than
// carrying on with one. See CredentialSourceGuard.
CredentialSourceGuard.EnsureNotFromFiles(
    builder.Configuration,
    endpoints.Select((_, i) => $"VCenters:{i}:Password"),
    builder.Environment.ContentRootPath);

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

// State outlives the process. Losing it forgets every acknowledgement and
// re-notifies every still-firing problem on restart, which is how a product
// teaches people to ignore it. See ADR-0011.
builder.Services.AddSingleton(new ObservatoryDatabase(BuildStoreOptions(builder.Configuration)));
builder.Services.AddSingleton<IEntityGraphStore, SqliteEntityGraphStore>();
builder.Services.AddSingleton<IAlertStateStore, SqliteAlertStateStore>();
builder.Services.AddSingleton<ICollectorHealthStore, SqliteCollectorHealthStore>();

// Measurements live in their own file. They are append-heavy and swept by
// retention every few minutes, which churns a file over time; keeping alert
// state out of that means a large metric history cannot take alerting down
// with it. See ADR-0012.
builder.Services.AddSingleton(new MetricsDatabase(BuildMetricsOptions(builder.Configuration)));
builder.Services.AddSingleton<IObservationStore, SqliteObservationStore>();
builder.Services.AddSingleton<IAlertNotifier, LoggingAlertNotifier>();
builder.Services.AddSingleton<InventoryCollectionPipeline>();
builder.Services.AddSingleton<ObservationCollectionPipeline>();
builder.Services.AddSingleton<MonitoringCycle>();
builder.Services.AddSingleton<AlertOperations>();
builder.Services.AddSingleton<IMaintenanceWindowStore, SqliteMaintenanceWindowStore>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<ReadModel>();

// --- who may do what -----------------------------------------------------
//
// A cookie rather than a token in browser storage. The interface and the API
// share an origin precisely so this works: HttpOnly keeps a script that gets
// into the page from reading it, and SameSite keeps another site from making
// the browser send it. See ADR-0006 and ADR-0014.
builder.Services.AddSingleton<IUserAccountStore, SqliteUserAccountStore>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<AccountService>();

builder.Services
    .AddAuthentication(AuthenticationApi.Scheme)
    .AddCookie(AuthenticationApi.Scheme, options =>
    {
        options.Cookie.Name = "eo.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Secure when the request arrived over HTTPS, and not otherwise. Fixed
        // to Always would make the product unusable over plain http on a
        // closed management network without saying why; SameAsRequest at least
        // never downgrades a secure session.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);

        // An API answers with a status, never a redirect to a login page. A
        // 302 to HTML is what turns "your session expired" into a JSON parse
        // error somewhere far from the cause.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Checked on every request against the account as it stands now, not as
        // it stood when the cookie was issued. Without this, removing an
        // account does not remove anything: the session keeps working until the
        // cookie expires, which is hours. A demotion has the same problem in
        // the other direction, so the role is refreshed here too.
        //
        // The cost is one lookup of a small, primary-keyed table per request.
        options.Events.OnValidatePrincipal = context =>
        {
            var accounts = context.HttpContext.RequestServices.GetRequiredService<IUserAccountStore>();
            var (status, replacement) = AuthenticationApi.Revalidate(accounts, context.Principal);

            switch (status)
            {
                case AuthenticationApi.PrincipalStatus.Gone:
                    context.RejectPrincipal();

                    return Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions
                        .SignOutAsync(context.HttpContext, AuthenticationApi.Scheme);

                case AuthenticationApi.PrincipalStatus.Stale:
                    context.ReplacePrincipal(replacement!);
                    context.ShouldRenew = true;
                    break;
            }

            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    // Named policies rather than a role string at each call site, so that
    // adding a command cannot quietly add one anybody can run.
    .AddPolicy(ObservatoryApi.Policies.Operator, policy =>
        policy.RequireAssertion(context =>
            AuthenticationApi.RoleOf(context.User) >= Role.Operator))
    .AddPolicy(ObservatoryApi.Policies.Administrator, policy =>
        policy.RequireAssertion(context =>
            AuthenticationApi.RoleOf(context.User) >= Role.Administrator));

// Issued per run and never stored: nothing to steal from the database, and a
// restart invalidates whatever was in an old log. It is only usable while the
// installation has no accounts at all.
var setupToken = AuthenticationService.NewSetupToken();

// Enums travel as their names, not their numbers. A client reading
// "severity": 2 has to keep a copy of our enum ordering, and the day someone
// inserts a value into the middle, every client silently means something else.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Credentials entered in the product are encrypted at rest, and the key ring
// lives beside the database rather than inside it. Beside, not in: a backup
// that captures both is a backup that captures the passwords in usable form,
// and an operator has to be able to treat the two differently. See ADR-0015.
var keyRing = builder.Services.AddDataProtection()
    .SetApplicationName("EnterpriseObservatory")
    .PersistKeysToFileSystem(new DirectoryInfo(KeyRingPath(builder.Configuration)));

if (OperatingSystem.IsWindows())
{
    // Machine scope rather than user scope, and the trade is worth stating.
    //
    // User scope is the stronger of the two, but it needs a loaded user
    // profile, which a Windows service running as LocalSystem or a managed
    // service account does not reliably have — and a key ring that silently
    // fails to decrypt after an account change is an outage nobody can
    // diagnose from the symptom.
    //
    // So: this protects the files leaving the machine, which is the realistic
    // case (a backup, a copied folder, a restored VM). It does not protect
    // against another administrator on this same machine. Said plainly in
    // ADR-0015 rather than implied by the absence of a comment.
    keyRing.ProtectKeysWithDpapi(protectToLocalMachine: true);
}
else
{
    // Not silently weaker. Without DPAPI the key ring is XML on disk, and the
    // encryption of the database is then only as good as the file permissions
    // on the folder beside it — which is a different guarantee from the one
    // ADR-0015 describes, so it is said out loud at startup.
    builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Warning);
}

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<ISourceConnectionStore, SqliteSourceConnectionStore>();
builder.Services.AddSingleton<VsphereConnectionProbe>();
builder.Services.AddSingleton<IConnectionProbe>(p => p.GetRequiredService<VsphereConnectionProbe>());
builder.Services.AddSingleton<ISourceCapabilityReader>(
    p => p.GetRequiredService<VsphereConnectionProbe>());

// Configured connections keep working exactly as before. They are merged with
// the stored ones by the catalogue, which is the single place that decides
// which of the two wins — two copies of that rule is how the screen and the
// collector start disagreeing about what is being polled.
builder.Services.AddSingleton(provider => new SourceConnectionCatalogue(
    provider.GetRequiredService<ISourceConnectionStore>(),
    [.. endpoints.Select(AsConnection)],
    provider.GetRequiredService<IClock>()));

builder.Services.AddSingleton<ISourceRegistry>(provider => new VsphereSourceRegistry(
    provider.GetRequiredService<SourceConnectionCatalogue>(),
    provider.GetRequiredService<IEntityGraphStore>(),
    provider.GetRequiredService<IClock>(),
    (instance, why) => HostLog.ConnectionNotPolled(
        provider.GetRequiredService<ILogger<VsphereSourceRegistry>>(), instance, why)));

builder.Services.AddHostedService<MonitoringWorker>();
builder.Services.AddHostedService<CompactionWorker>();

var host = builder.Build();

host.UseAuthentication();
host.UseAuthorization();

host.MapAuthenticationApi(setupToken);
host.MapAccountsApi();
host.MapMaintenanceApi();
host.MapConnections();
host.MapObservatoryApi();

// The SPA's build output, when it has been built. Serving the interface from
// the same origin as the API is what lets authentication stay a cookie rather
// than a token in browser storage. See ADR-0006.
//
// Conditional because a backend-only checkout has no wwwroot, and the static
// file middleware's warning about it would be printed on every start until
// everyone learned to ignore it. A warning nobody reads is worse than none: it
// teaches people that warnings here do not matter.
if (Directory.Exists(host.Environment.WebRootPath))
{
    host.UseDefaultFiles();
    host.UseStaticFiles();

    // The SPA owns its own routes, so any path the API did not claim is handed
    // to it. Without this, a deep link — the URL an operator pastes into an
    // incident ticket — returns 404 while the same page reached by clicking
    // works, which is the kind of defect that is reported as "sometimes it
    // breaks".
    host.MapFallbackToFile("index.html");
}

// Logged after building, so what the service is actually about to do is on the
// record. The untrusted-certificate case gets its own warning rather than a
// clause in a sentence: it is a security posture someone chose, and it should
// be as easy to find in a log as it was to turn on.
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EnterpriseObservatory");

// Printed only while it is usable. A token in a log for an installation that
// already has an administrator is noise that looks like a secret.
if (host.Services.GetRequiredService<AuthenticationService>().NeedsBootstrap)
{
    HostLog.SetupTokenIssued(startupLog, setupToken);
}

foreach (var endpoint in endpoints)
{
    var address = new Uri(endpoint.BaseAddress);

    HostLog.VsphereConfigured(startupLog, endpoint.InstanceId, address, endpoint.Username);

    if (endpoint.AcceptUntrustedCertificate)
    {
        HostLog.UntrustedCertificateAccepted(startupLog, endpoint.InstanceId);
    }
}

await host.RunAsync();

// A file beside the service, not a server. ADR-0001 requires an MSI that
// installs without an appliance, and a database nobody has to provision is the
// difference between a product an operator installs in a maintenance window and
// one that needs a project. ProgramData rather than the install directory,
// because data that survives an upgrade must not sit where the upgrade writes.
static SqliteStoreOptions BuildStoreOptions(IConfiguration configuration)
{
    var configured = configuration["Storage:Path"];

    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EnterpriseObservatory",
            "observatory.db")
        : configured;

    return new SqliteStoreOptions { Path = path };
}

// Beside the state database by default, and separately configurable — the
// measurement file is the one that grows, and an installation with a small
// system disk needs to be able to put it somewhere else.
static MetricsStoreOptions BuildMetricsOptions(IConfiguration configuration)
{
    var configured = configuration["Storage:MetricsPath"];

    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EnterpriseObservatory",
            "metrics.db")
        : configured;

    return new MetricsStoreOptions { Path = path };
}

static MonitoringOptions BuildMonitoringOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Monitoring");
    var defaults = MonitoringOptions.Default;

    return new MonitoringOptions
    {
        InventoryInterval = Seconds(section["InventoryIntervalSeconds"], defaults.InventoryInterval),
        ObservationInterval = Seconds(section["ObservationIntervalSeconds"], defaults.ObservationInterval),
        CompactionInterval = Seconds(section["CompactionIntervalSeconds"], defaults.CompactionInterval),
        Retention = new SeriesRetentionPolicy
        {
            Raw = Days(section["Retention:RawDays"], defaults.Retention.Raw),
            FiveMinutes = Days(section["Retention:FiveMinuteDays"], defaults.Retention.FiveMinutes),
            OneHour = Days(section["Retention:HourlyDays"], defaults.Retention.OneHour),
        },
    };
}

static TimeSpan Seconds(string? value, TimeSpan fallback) =>
    int.TryParse(value, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : fallback;

static TimeSpan Days(string? value, TimeSpan fallback) =>
    int.TryParse(value, out var days) && days > 0 ? TimeSpan.FromDays(days) : fallback;

// A configured vCenter, as the rest of the product sees it. Configuration and
// a form now describe the same thing, so they become the same type as early as
// possible — the alternative is two parallel shapes that drift a field at a
// time until one of them silently stops being honoured.
static SourceConnection AsConnection(VsphereEndpointOptions endpoint) => new()
{
    InstanceId = endpoint.InstanceId,
    Kind = VsphereSourceRegistry.VsphereKind,
    BaseAddress = new Uri(endpoint.BaseAddress),
    Username = endpoint.Username,
    Password = endpoint.Password,
    AcceptUntrustedCertificate = endpoint.AcceptUntrustedCertificate,
    PageSize = endpoint.InventoryPageSize,
    Origin = ConnectionOrigin.Configuration,
};

// Beside the database, not inside it. The key ring protects what is in the
// database, so keeping it in the same file would be a lock stored in the box
// it locks; keeping it in the same backup is the same mistake spread over two
// files, which is why this is called out in ADR-0015 rather than left to
// whoever writes the backup job.
static string KeyRingPath(IConfiguration configuration)
{
    var database = configuration["Storage:Path"];

    var directory = string.IsNullOrWhiteSpace(database)
        ? AppContext.BaseDirectory
        : Path.GetDirectoryName(Path.GetFullPath(database)) ?? AppContext.BaseDirectory;

    var keys = Path.Combine(directory, "keys");
    Directory.CreateDirectory(keys);
    return keys;
}
