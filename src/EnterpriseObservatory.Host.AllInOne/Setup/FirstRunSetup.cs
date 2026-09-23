using System.Net;
using System.Net.Sockets;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Host.AllInOne.Security;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.HttpResults;

// The framework has its own type called Secret, and it is not this one.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.Host.AllInOne.Setup;

/// <summary>What the setup page is shown before anything is entered.</summary>
public sealed record SetupStateView
{
    public bool SetupRequired { get; init; } = true;

    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string Database { get; init; }

    public required string Username { get; init; }

    public required string Schema { get; init; }

    public required bool RequireTls { get; init; }

    public required string AdminUsername { get; init; }
}

/// <summary>What the setup form sends.</summary>
/// <remarks>
/// <see cref="AdminPassword"/> is the one-time administrator credential, and
/// <see cref="Password"/> the product role's own password on the
/// existing-database path. Both become a <see cref="Secret"/> on the first
/// lines of the handler; the administrator's lives for that request only. See
/// <see cref="FirstRunSetup"/>.
/// </remarks>
public sealed record SetupCommand
{
    /// <summary>
    /// <see cref="FirstRunSetup.CreateMode"/> (the default): create the role and
    /// database with a one-time administrator credential.
    /// <see cref="FirstRunSetup.ExistingMode"/>: use a role and database a DBA made.
    /// </summary>
    public string Mode { get; init; } = FirstRunSetup.CreateMode;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5432;

    public string Database { get; init; } = "observatory";

    public string Username { get; init; } = "observatory";

    public string Schema { get; init; } = "public";

    public bool RequireTls { get; init; }

    public string AdminUsername { get; init; } = "postgres";

    public string AdminPassword { get; init; } = string.Empty;

    /// <summary>The existing role's password; unused when creating.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Never the two passwords. A record prints every property by default, and
    /// this one should not be one careless log call away from the credential.
    /// </summary>
    public override string ToString() =>
        $"SetupCommand {{ Mode = {Mode}, Host = {Host}, Port = {Port}, Database = {Database}, " +
        $"Username = {Username}, Schema = {Schema}, RequireTls = {RequireTls}, AdminUsername = {AdminUsername} }}";
}

/// <summary>What setup did.</summary>
public sealed record SetupResultView
{
    public required bool Succeeded { get; init; }

    public required string Detail { get; init; }

    /// <summary>
    /// True when the service has to be restarted by hand before it runs
    /// normally. Said rather than implied: a setup that finished and then
    /// silently waited would look exactly like one that hung.
    /// </summary>
    public required bool RestartRequired { get; init; }

    public string? NextStep { get; init; }
}

/// <summary>
/// First-run setup: the service with no database, open on loopback only,
/// serving one page until that page has created the database.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0016 accepted a two-step install; this brings the second step into the
/// product the way pgAdmin and Grafana do — one page, once. It exists only while
/// the installation has no database source at all (<see cref="DatabaseSource"/>):
/// no protected file and no password in configuration.
/// </para>
/// <para>
/// **Loopback only, and that is the security decision rather than a
/// default.** The page is anonymous — there is no account table yet to sign in
/// against — and it accepts a database administrator's credential. So it must
/// never face the network. The product installs as a Windows service on
/// Windows Server, where a local browser (on the console or over RDP) always
/// exists; a headless install is out of scope. Enforced three ways, each
/// independent of the others:
/// </para>
/// <list type="number">
/// <item>Kestrel is given exactly two listeners, 127.0.0.1 and ::1. Every
/// endpoint from configuration (<c>Kestrel:Endpoints</c>) and every hosting URL
/// (<c>urls</c>, <c>ASPNETCORE_URLS</c>) is discarded, so nothing else is
/// bound.</item>
/// <item>Once started, the bound addresses are read back; a non-loopback one
/// stops the service.</item>
/// <item>Every request whose remote address is not loopback is refused with
/// 403 before it reaches anything.</item>
/// </list>
/// <para>
/// Everything but <c>/setup</c> and <c>/api/setup</c> answers 503 "setup
/// required" — including <c>/health</c>, which is true: there is nothing
/// monitoring yet.
/// </para>
/// <para>
/// The administrator credential is used once, inside the request that carries
/// it, and is then gone: not stored, not logged, not echoed in any response or
/// error. What is kept is the product's own role — generated password, never a
/// superuser — in the protected file beside the key ring.
/// </para>
/// </remarks>
public static class FirstRunSetup
{
    public const string PagePath = "/setup";

    public const string ApiPath = "/api/setup";

    public const string CreateMode = "create";

    public const string ExistingMode = "existing";

    /// <summary>The port used when configuration names none.</summary>
    public const int DefaultPort = 5000;

    /// <summary>
    /// Runs the setup host until setup completes or the service is stopped.
    /// </summary>
    /// <returns>True when setup completed and the normal host should start.</returns>
    public static async Task<bool> RunAsync(WebApplicationBuilder builder, string keyRingPath)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Configure(builder, keyRingPath);

        var app = builder.Build();
        Map(app);

        await app.RunAsync().ConfigureAwait(false);

        return app.Services.GetRequiredService<SetupSession>().Completed;
    }

    /// <summary>The setup host's services and its loopback-only listeners.</summary>
    public static void Configure(WebApplicationBuilder builder, string keyRingPath)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The same refusal the normal host makes: setup writes a credential
        // that is only as durable as this directory.
        var location = KeyRingDurabilityGuard.Inspect(keyRingPath);
        KeyRingDurabilityGuard.EnsureUsable(location);

        KeyRing.Add(builder.Services, location.Path);

        var credentialFile = DatabaseCredentialFile.PathFor(location.Path);

        builder.Services.AddSingleton(provider => new DatabaseCredentialFile(
            credentialFile,
            new DataProtectionSecretProtector(
                provider.GetRequiredService<IDataProtectionProvider>(),
                DataProtectionSecretProtector.DatabasePasswordPurpose)));

        builder.Services.AddSingleton(new SetupSession(
            DatabaseSource.FromConfiguration(builder.Configuration),
            NormalAddresses(builder.Configuration)));

        var port = Port(builder.Configuration);

        // Hosting URLs never win over the listeners below, whatever
        // configuration says about preferHostingUrls.
        builder.WebHost.PreferHostingUrls(false);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Replaces the loader the web defaults set up from the "Kestrel"
            // section, so no endpoint from configuration is bound.
            kestrel.Configure(new ConfigurationBuilder().Build());

            kestrel.Listen(IPAddress.Loopback, port);

            if (Socket.OSSupportsIPv6)
            {
                kestrel.Listen(IPAddress.IPv6Loopback, port);
            }
        });
    }

    /// <summary>The setup host's pipeline and its two endpoints.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EnterpriseObservatory.Setup");

        HostLog.SetupModeStarting(logger, Port(app.Configuration));

        app.Lifetime.ApplicationStarted.Register(() => EnsureLoopbackOnly(app, logger));

        app.Use(async (context, next) =>
        {
            if (!IsLoopback(context.Connection.RemoteIpAddress))
            {
                HostLog.SetupRefusedRemoteCaller(logger, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    detail = "Setup is only available from this machine (loopback). Open it in a browser on " +
                             "the server itself, on the console or over RDP.",
                }).ConfigureAwait(false);

                return;
            }

            await next(context).ConfigureAwait(false);
        });

        var hasInterface = Directory.Exists(app.Environment.WebRootPath);

        if (hasInterface)
        {
            // The page's own script and styles. Files only, never a directory
            // listing and never the fallback: anything not on disk falls
            // through to the refusal below.
            app.UseStaticFiles();
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            if (path.Equals(PagePath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments(ApiPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (path == "/" && HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Redirect(PagePath);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                detail = "Setup required: this installation has no database yet. Open /setup in a browser " +
                         "on this machine.",
                setupRequired = true,
            }).ConfigureAwait(false);
        });

        if (hasInterface)
        {
            app.MapFallbackToFile(PagePath, "index.html");
        }
        else
        {
            app.MapGet(PagePath, () => Results.Content(
                "<!doctype html><title>Setup</title><p>The web interface is not built into this " +
                "installation. POST the setup form to /api/setup.</p>",
                "text/html"));
        }

        app.MapGet(ApiPath, (SetupSession session) => Results.Ok(session.State()))
            .WithName("GetSetupState");

        app.MapPost(ApiPath, CreateAsync).WithName("RunSetup");
    }

    /// <summary>
    /// The port the setup page listens on: the one the normal host will use.
    /// </summary>
    /// <remarks>
    /// Read from the same places Kestrel reads it — <c>urls</c> (which is also
    /// <c>ASPNETCORE_URLS</c>), then <c>Kestrel:Endpoints</c> — so the operator
    /// opens the address they will keep using, only on loopback for now.
    /// </remarks>
    public static int Port(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var address in NormalAddresses(configuration))
        {
            try
            {
                var parsed = BindingAddress.Parse(address);

                if (parsed.Port > 0)
                {
                    return parsed.Port;
                }
            }
            catch (FormatException)
            {
                // Not an address this can read; the next one may be.
            }
        }

        return DefaultPort;
    }

    /// <summary>Whether a caller is on this machine.</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            // Unknown is refused: the safe reading of a caller nobody can place.
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    /// <summary>Whether a bound address, as Kestrel reports it, is loopback.</summary>
    public static bool IsLoopbackAddress(string address)
    {
        var host = BindingAddress.Parse(address).Host.Trim('[', ']');

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host, out var ip) && IsLoopback(ip));
    }

    private static List<string> NormalAddresses(IConfiguration configuration)
    {
        var addresses = new List<string>();

        if (configuration["urls"] is { Length: > 0 } urls)
        {
            addresses.AddRange(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url)
            {
                addresses.Add(url);
            }
        }

        return addresses;
    }

    private static void EnsureLoopbackOnly(WebApplication app, ILogger logger)
    {
        // Absent under a test server, which has no network to face.
        var bound = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

        var exposed = bound
            .Where(address => !IsLoopbackAddress(address))
            .ToList();

        if (exposed.Count > 0)
        {
            if (logger.IsEnabled(LogLevel.Critical))
            {
                var text = string.Join(", ", exposed);
                HostLog.SetupExposed(logger, text);
            }

            app.Lifetime.StopApplication();
            return;
        }

        if (bound.Count > 0 && logger.IsEnabled(LogLevel.Information))
        {
            var text = string.Join(", ", bound);
            HostLog.SetupListening(logger, text);
        }
    }

    private static async Task<Results<Ok<SetupResultView>, BadRequest<SetupResultView>, Conflict<SetupResultView>>> CreateAsync(
        HttpContext context,
        SetupCommand command,
        SetupSession session,
        DatabaseCredentialFile file,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggers)
    {
        // The one-time credential, as a Secret from here on. Nothing below
        // puts it in a field, a log call, an exception or a response.
        var admin = new PostgresAdminCredential
        {
            Username = command.AdminUsername.Trim(),
            Password = Secret.From(command.AdminPassword),
        };

        var logger = loggers.CreateLogger("EnterpriseObservatory.Setup");

        var existing = string.Equals(command.Mode, ExistingMode, StringComparison.OrdinalIgnoreCase);

        if (!existing && !string.Equals(command.Mode, CreateMode, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest(Failed($"Mode must be '{CreateMode}' or '{ExistingMode}'."));
        }

        var target = new PostgresOptions
        {
            Host = command.Host.Trim(),
            Port = command.Port,
            Database = command.Database.Trim(),
            Username = command.Username.Trim(),
            Schema = command.Schema.Trim(),
            RequireTls = command.RequireTls,

            // Created: generated here, never shown. Existing: the DBA's.
            Password = existing ? Secret.From(command.Password) : PostgresProvisioning.NewPassword(),
        };

        if (!await session.Gate.WaitAsync(TimeSpan.Zero, context.RequestAborted).ConfigureAwait(false))
        {
            return TypedResults.Conflict(Failed("Setup is already running from another request."));
        }

        try
        {
            if (session.Completed)
            {
                return TypedResults.Conflict(Failed("Setup has already completed."));
            }

            var provisioned = await Task.Run(() => existing
                    ? PostgresProvisioning.Adopt(target)
                    : PostgresProvisioning.Provision(admin, target))
                .ConfigureAwait(false);

            if (!provisioned.Succeeded)
            {
                HostLog.SetupFailed(logger, provisioned.Detail);
                return TypedResults.BadRequest(Failed(provisioned.Detail));
            }

            // Written, then proved: the password read back out of the file is
            // the one the server accepts, before anything says "done". A setup
            // that half-wrote itself must never report success.
            var proved = WriteAndProve(file, target);

            if (!proved.Succeeded)
            {
                file.Delete();

                string detail;

                if (existing)
                {
                    // Not ours to remove: the DBA made them.
                    detail = proved.Detail + " The saved connection was removed; the database was left as it was.";
                }
                else
                {
                    PostgresProvisioning.Drop(admin, target);
                    detail = proved.Detail + " Nothing was kept: the database and role were removed again.";
                }

                HostLog.SetupFailed(logger, detail);

                return TypedResults.BadRequest(Failed(detail));
            }

            session.Completed = true;

            HostLog.SetupCompleted(logger, existing ? "took over the existing" : "created", target.Username, target.Database, target.Host, target.Port, file.Path);

            // After the response has left, so the page hears about it. The
            // normal host then starts in this same process (Program.cs).
            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });

            return TypedResults.Ok(new SetupResultView
            {
                Succeeded = true,
                Detail = provisioned.Detail + " The connection was saved and verified.",
                RestartRequired = false,
                NextStep =
                    "The service is switching to normal mode now, listening on " +
                    session.NormalAddressText + ". This page reloads when it answers. " +
                    "Then create the first administrator with the one-time setup token printed in the " +
                    "service log. If nothing answers within a minute, restart the service.",
            });
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static ProvisioningResult WriteAndProve(DatabaseCredentialFile file, PostgresOptions target)
    {
        try
        {
            file.Write(target);
            return PostgresProvisioning.CanConnect(file.Read());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return ProvisioningResult.Failed(
                $"The connection could not be saved to {file.Path} ({ex.GetType().Name}).");
        }
    }

    private static SetupResultView Failed(string detail) => new()
    {
        Succeeded = false,
        Detail = detail,
        RestartRequired = false,
    };
}

/// <summary>The setup host's state: one run at a time, and whether it finished.</summary>
public sealed class SetupSession
{
    private readonly PostgresOptions _defaults;
    private readonly IReadOnlyList<string> _normalAddresses;
    private volatile bool _completed;

    public SetupSession(PostgresOptions defaults, IReadOnlyList<string> normalAddresses)
    {
        _defaults = defaults;
        _normalAddresses = normalAddresses;
    }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool Completed
    {
        get => _completed;
        set => _completed = value;
    }

    public string NormalAddressText => _normalAddresses.Count == 0
        ? $"http://localhost:{FirstRunSetup.DefaultPort}"
        : string.Join(", ", _normalAddresses);

    /// <summary>The form's starting values: <c>Database:*</c> from configuration, if any.</summary>
    public SetupStateView State() => new()
    {
        Host = _defaults.Host,
        Port = _defaults.Port,
        Database = _defaults.Database,
        Username = _defaults.Username,
        Schema = _defaults.Schema,
        RequireTls = _defaults.RequireTls,
        AdminUsername = "postgres",
    };
}
