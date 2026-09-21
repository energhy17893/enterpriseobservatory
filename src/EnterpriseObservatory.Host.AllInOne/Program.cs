using System.Text.Json.Serialization;
using EnterpriseObservatory.Api;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Host.AllInOne.Mail;
using EnterpriseObservatory.Host.AllInOne.Notifications;
using EnterpriseObservatory.Host.AllInOne.Security;
using EnterpriseObservatory.Host.AllInOne.State;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.DataProtection;

// The framework has its own type called Secret, and it is not this one. Named
// explicitly rather than resolved by using-order, because the two are close
// enough in purpose that a silent bind to the wrong one would compile.
using Secret = EnterpriseObservatory.Application.Security.Secret;

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
    endpoints.Count,
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
// teaches people to ignore it.
//
// One database for state and measurements, where SQLite used two files. That
// separation was about file mechanics — a churning metric history should not
// be able to take alerting down with it — and a server has no shared file to
// contend for. See ADR-0016.
var database = BuildDatabaseOptions(builder.Configuration);
var databaseProblems = database.Validate();

if (databaseProblems.Count > 0)
{
    // Refusing to start is the point rather than a harshness. A monitoring
    // product that cannot store what it collects should stop and say so; one
    // that carries on is blind and looks healthy, which principle 1 forbids.
    throw new InvalidOperationException(
        "PostgreSQL is required and is not usable:" + Environment.NewLine +
        string.Join(Environment.NewLine, databaseProblems.Select(p => "  " + p)) +
        Environment.NewLine +
        "Configure it under Database: host, port, database, username, schema. The password " +
        "must come from user secrets, an environment variable or a secret store — never from " +
        "a settings file. See ADR-0010 and ADR-0016.");
}

// Built when first asked for rather than here, and the difference is not a
// performance one. Constructing it opens a connection and applies the schema,
// so building it eagerly meant the composition root reached PostgreSQL before
// anything could replace a store — which made the whole host untestable
// without a live server, and is why Program.cs went a week with no test while
// its endpoints could not coexist.
//
// Refusing to start is unchanged. The startup code below reads the stored
// connections before RunAsync, which resolves a store and therefore this, so a
// database that cannot be reached still stops the service at boot rather than
// on the first request.
builder.Services.AddSingleton(_ => new PostgresDatabase(database));
builder.Services.AddSingleton<IEntityGraphStore, PostgresEntityGraphStore>();
builder.Services.AddSingleton<IAlertStateStore, PostgresAlertStateStore>();
builder.Services.AddSingleton<ICollectorHealthStore, PostgresCollectorHealthStore>();
builder.Services.AddSingleton<ICoverageStore, PostgresCoverageStore>();
builder.Services.AddSingleton<IObservationStore, PostgresObservationStore>();
builder.Services.AddSingleton<IEventStore, PostgresEventStore>();

// The same store, read-only, for naming who took a stale snapshot (M2.4).
// Resolved through IEventStore rather than registered twice, so there is one
// instance and one cursor cache; a store that cannot answer history questions
// fails here at resolution rather than quietly reporting every creator unknown.
builder.Services.AddSingleton(sp => (IEventHistory)sp.GetRequiredService<IEventStore>());
builder.Services.AddSingleton<EventCollectionPipeline>();
builder.Services.AddSingleton<IAlertNotifier, LoggingAlertNotifier>();
builder.Services.AddSingleton<InventoryCollectionPipeline>();
builder.Services.AddSingleton<ObservationCollectionPipeline>();
builder.Services.AddSingleton<MonitoringCycle>();
builder.Services.AddSingleton<AlertOperations>();
builder.Services.AddSingleton<IMaintenanceWindowStore, PostgresMaintenanceWindowStore>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<ReadModel>();

// The compliance engine (roadmap M3). Its own store and its own screen, never
// the alert inbox: a finding does not close itself and is expected by the
// hundred on the first day. The catalogue is loaded once, as data, from the
// edition Compliance:Catalogue names -- see ComplianceCatalogueSource. One
// that cannot be loaded is carried with its reason rather than stopping the
// service, so monitoring never goes dark over a compliance file.
var complianceCatalogue = ComplianceCatalogueSource.Load(builder.Configuration);
builder.Services.AddSingleton(complianceCatalogue);
builder.Services.AddSingleton<IComplianceStore, PostgresComplianceStore>();
builder.Services.AddSingleton<ComplianceService>();

// --- who may do what -----------------------------------------------------
//
// A cookie rather than a token in browser storage. The interface and the API
// share an origin precisely so this works: HttpOnly keeps a script that gets
// into the page from reading it, and SameSite keeps another site from making
// the browser send it. See ADR-0006 and ADR-0014.
builder.Services.AddSingleton<IUserAccountStore, PostgresUserAccountStore>();
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
//
// Where it is going to live is checked before it is used, and the check runs
// here rather than after the first failed decryption because by then the keys
// are already gone. %TEMP% was a real setting on a real installation this
// morning; Windows emptied it and one vCenter's password stopped existing. The
// product reported that correctly and far too late. See ADR-0020.
var keyRingLocation = KeyRingDurabilityGuard.Inspect(KeyRingPath(builder.Configuration));

// The only refusal in this guard, and it is the same refusal the database makes
// above: a key ring that cannot be written is not a degraded product, it is one
// that cannot hold a credential at all.
KeyRingDurabilityGuard.EnsureUsable(keyRingLocation);

var keyRing = builder.Services.AddDataProtection()
    .SetApplicationName("EnterpriseObservatory")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingLocation.Path));

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

// Scheduled email reports (roadmap M5.4). IReportRenderer is a stub here --
// the placeholder text body -- until M5.1's CSV/PDF export replaces it; the
// scheduler and delivery pipe around it are otherwise the finished feature.
builder.Services.AddSingleton<ISmtpSettingsStore, PostgresSmtpSettingsStore>();
builder.Services.AddSingleton<IReportSubscriptionStore, PostgresReportSubscriptionStore>();
builder.Services.AddSingleton<IMailSender, MailKitMailSender>();
builder.Services.AddSingleton<IReportRenderer, PlaceholderReportRenderer>();
builder.Services.AddSingleton<ReportDispatchService>();
builder.Services.AddHostedService<ReportSchedulerWorker>();

builder.Services.AddSingleton<ISourceConnectionStore, PostgresSourceConnectionStore>();
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
host.MapComplianceApi();
host.MapEmailApi();
host.MapReportsApi();
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

// Said once, at Critical, and only when it is true. A key ring in ProgramData
// or in a durable directory an operator chose produces nothing here at all:
// this line exists to be believed the one time it appears, and a check that
// speaks on a correct installation spends exactly that.
//
// Not a refusal. The fix for a losable location is a setting and a restart, and
// the fix for a key ring that has already been emptied is an administrator
// signing in and re-typing passwords — neither is possible from a service that
// will not boot, and the estate that is still monitorable goes on being
// monitored in the meantime. The argument is set out in ADR-0020.
if (keyRingLocation.Risk is KeyRingRisk.Losable)
{
    HostLog.KeyRingInLosableLocation(
        startupLog, keyRingLocation.Path, keyRingLocation.Reason, keyRingLocation.KeyCount);
}

// Connections that came from configuration are excluded deliberately: their
// passwords never went through the key ring, so they are not evidence of
// anything and counting them would put a number in front of an operator that
// does not match the number of vCenters they have to go and fix.
var storedConnections = host.Services.GetRequiredService<ISourceConnectionStore>().All
    .Count(c => c.Origin is not ConnectionOrigin.Configuration);

if (KeyRingDurabilityGuard.CredentialsAreUnrecoverable(keyRingLocation, storedConnections))
{
    HostLog.KeyRingLostItsKeys(startupLog, keyRingLocation.Path, storedConnections);
}

if (complianceCatalogue.Problem is { } complianceProblem)
{
    // The full account, path and error included; the screen shows only the
    // edition's name and what kind of thing is wrong.
    HostLog.ComplianceCatalogueUnavailable(
        startupLog, complianceCatalogue.Diagnostic ?? complianceProblem);
}
else
{
    var evaluatedControls = ComplianceEvaluation.Bind(complianceCatalogue).Count(c => c.IsEvaluated);

    HostLog.ComplianceCatalogueLoaded(
        startupLog,
        complianceCatalogue.Name,
        complianceCatalogue.Release,
        complianceCatalogue.Controls.Count,
        evaluatedControls);
}

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
static PostgresOptions BuildDatabaseOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Database");

    return new PostgresOptions
    {
        Host = Text(section["Host"], "127.0.0.1"),
        Port = int.TryParse(section["Port"], out var port) ? port : 5432,
        Database = Text(section["Database"], "observatory"),
        Username = Text(section["Username"], "observatory"),

        // Bound through the ordinary configuration system so user secrets, an
        // environment variable and a key vault all work with no special
        // support — and then checked by CredentialSourceGuard, because the
        // previous product's leak was a password sitting in a settings file.
        Password = Secret.From(section["Password"]),

        // Named so one server can hold a lab beside a production installation.
        Schema = Text(section["Schema"], "public"),

        RequireTls = bool.TryParse(section["RequireTls"], out var tls) && tls,
    };

    static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
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
        EventReadDeadline = Seconds(section["EventReadDeadlineSeconds"], defaults.EventReadDeadline),
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
    // Its own setting now. It used to live beside the database file, which was
    // a convenient default while the database was a file; with a server there
    // is no such place, and the key ring is local to the machine while the
    // database may not be.
    //
    // ProgramData rather than the install directory, because material that
    // must survive an upgrade cannot sit where the upgrade writes. And still
    // not in the database's backup: ADR-0015 is explicit that a backup holding
    // both loses the protection entirely.
    var configured = configuration["Storage:KeyRingPath"];

    var keys = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EnterpriseObservatory",
            "keys")
        : configured;

    // Not created here any more. Creating it is now part of inspecting it,
    // because whether the directory can be created is one of the answers the
    // inspection has to give and there is no way to learn it except by trying.
    // See KeyRingDurabilityGuard.
    return keys;
}

// Exposed so the composition root can be booted by a test. Top-level
// statements compile to an internal Program class, and WebApplicationFactory
// needs a type it can name. It is only the class that becomes visible; nothing
// in it is public API.
public partial class Program;
