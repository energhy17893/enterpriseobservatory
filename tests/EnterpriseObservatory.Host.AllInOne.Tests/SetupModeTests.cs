using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseObservatory.Host.AllInOne.Setup;
using EnterpriseObservatory.Persistence.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Program.cs booted with no database source at all: first-run setup (G-DB).
/// </summary>
/// <remarks>
/// <para>
/// The real composition root, the same way <see cref="CompositionRootSmokeTests"/>
/// boots it, only with nothing to connect to — no protected file in the key ring
/// directory and an empty <c>Database:Password</c>. What comes up is the setup
/// host, and these tests ask it the questions the brief made the definition of
/// done: only <c>/setup</c> and <c>/api/setup</c> answer, nobody off the machine
/// gets anything, and the one-time administrator credential ends up nowhere.
/// </para>
/// <para>
/// The anonymous 401 and the viewer 403 the smoke suite asserts become 503 here.
/// That is a separate case, asserted here; the smoke suite is untouched.
/// </para>
/// <para>
/// A test server has no sockets, so the remote address a request arrives from is
/// set by a startup filter from a test header. Whether a non-loopback caller can
/// connect at all is a different question, answered against real Kestrel in
/// <see cref="SetupLoopbackBindingTests"/>.
/// </para>
/// </remarks>
public sealed class SetupModeTests : IDisposable
{
    // Not a real credential. A distinctive string, so that finding it anywhere
    // it should not be is unambiguous.
    private const string AdminPassword = "admin-sentinel-7c41e09b-once-only";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SetupModeHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient Client(string? remote = null)
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        if (remote is not null)
        {
            client.DefaultRequestHeaders.Add(SetupModeHost.RemoteHeader, remote);
        }

        return client;
    }

    private static object Command(string host, int port, string database, string username, string adminUsername, string adminPassword) => new
    {
        host,
        port,
        database,
        username,
        schema = "public",
        requireTls = false,
        adminUsername,
        adminPassword,
    };

    // --- what is open ------------------------------------------------------

    [Fact]
    public async Task The_setup_page_and_its_api_answer_a_local_caller()
    {
        var client = Client();

        var page = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);

        var state = await client.GetFromJsonAsync<SetupStateView>("/api/setup");
        Assert.NotNull(state);
        Assert.True(state.SetupRequired);
        Assert.Equal("127.0.0.1", state.Host);
    }

    [Fact]
    public async Task The_root_sends_a_local_caller_to_the_setup_page()
    {
        var response = await Client().GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.OriginalString);
    }

    // --- what is not -------------------------------------------------------

    [Fact]
    public async Task An_anonymous_caller_gets_503_where_normal_mode_answers_401()
    {
        // The smoke suite's first case, in setup mode. A separate case rather
        // than a change to that one: both are true, of different hosts.
        var response = await Client().GetAsync("/api/overview");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("setupRequired").GetBoolean());
        Assert.Contains("Setup required", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sign_in_surface_and_health_are_503_too()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/auth/state")).StatusCode);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/auth/signin", new { username = "a", password = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_the_normal_host_maps_answers_503_in_setup_mode()
    {
        // Asked of every route the product maps in normal mode, every verb,
        // rather than a hand-picked few: the endpoint that would leak is, by
        // definition, the one nobody thought to name.
        List<(string Method, string Path)> routes;

        using (var normal = new ObservatoryHost())
        {
            routes = normal.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText is { } raw && !raw.Contains('*', StringComparison.Ordinal))
                .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => (method, Fill(e.RoutePattern.RawText!))))
                .Distinct()
                .ToList();
        }

        Assert.True(routes.Count > 20, $"Only {routes.Count} routes were found; the sweep would prove little.");

        var client = Client();

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { });
            }

            var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"{method} {path} answered {(int)response.StatusCode} in setup mode.");
        }

        static string Fill(string pattern) =>
            "/" + Regex.Replace(pattern.TrimStart('/'), "{[^}]+}", "x");
    }

    // --- loopback only -----------------------------------------------------

    [Theory]
    [InlineData("10.20.30.40")]
    [InlineData("192.168.1.5")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public async Task A_caller_from_another_machine_is_refused_setup(string remote)
    {
        var client = Client(remote);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/setup")).StatusCode);

        var posted = await client.PostAsJsonAsync(
            "/api/setup", Command("127.0.0.1", 1, "eo_remote", "eo_remote", "postgres", AdminPassword));

        Assert.Equal(HttpStatusCode.Forbidden, posted.StatusCode);
        Assert.DoesNotContain(AdminPassword, await posted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The positive control: the same request from loopback gets through.
        Assert.Equal(HttpStatusCode.OK, (await Client("127.0.0.1").GetAsync("/api/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("::1").GetAsync("/api/setup")).StatusCode);
    }

    [Fact]
    public async Task A_caller_whose_address_is_unknown_is_refused()
    {
        var response = await Client(SetupModeHost.NoAddress).GetAsync("/api/setup");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- the one-time administrator credential -------------------------------

    [Fact]
    public async Task A_failed_setup_puts_the_admin_credential_in_no_response_no_log_and_no_file()
    {
        // Port 1 on loopback refuses at once: the failure path, which is the
        // one most likely to echo what it was given.
        var response = await Client().PostAsJsonAsync(
            "/api/setup", Command("127.0.0.1", 1, "eo_setup_fail", "eo_setup_fail", "postgres", AdminPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<SetupResultView>(body, Web);

        Assert.False(result!.Succeeded);
        Assert.Contains("could not be reached", result.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(AdminPassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminPassword, _host.Logs.All, StringComparison.Ordinal);
        Assert.Empty(SetupTestSupport.FilesContaining(_host.KeyRing, AdminPassword));

        // And nothing was written as though it had worked.
        Assert.False(File.Exists(Configuration.DatabaseCredentialFile.PathFor(_host.KeyRing)));

        // The positive control for the log check: the failure was logged.
        Assert.Contains("Setup did not complete", _host.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_existing_database_setup_does_not_repeat_the_roles_password()
    {
        // The DBA-made path: no administrator, the role's own password typed in.
        const string RolePassword = "role-sentinel-2d8f6a90-typed-by-dba";

        var response = await Client().PostAsJsonAsync("/api/setup", new
        {
            mode = "existing",
            host = "127.0.0.1",
            port = 1,
            database = "eo_existing",
            username = "eo_existing",
            schema = "public",
            requireTls = false,
            password = RolePassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", body, StringComparison.Ordinal);
        Assert.DoesNotContain(RolePassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RolePassword, _host.Logs.All, StringComparison.Ordinal);
        Assert.Empty(SetupTestSupport.FilesContaining(_host.KeyRing, RolePassword));
        Assert.False(File.Exists(Configuration.DatabaseCredentialFile.PathFor(_host.KeyRing)));
    }

    [Fact]
    public async Task An_unknown_mode_is_refused()
    {
        var response = await Client().PostAsJsonAsync("/api/setup", new { mode = "import" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_product_role_cannot_be_postgres()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/setup", Command("127.0.0.1", 1, "observatory", "postgres", "postgres", AdminPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not 'postgres'", body, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminPassword, body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Setup_creates_a_non_superuser_role_and_its_database_and_the_admin_credential_lands_nowhere()
    {
        Skip.If(SetupTestSupport.AdminSkipReason is not null, SetupTestSupport.AdminSkipReason);

        var admin = SetupTestSupport.Admin();
        var name = "eo_setup_" + Guid.NewGuid().ToString("N")[..12];
        var target = new PostgresOptions
        {
            Host = SetupTestSupport.TestHost,
            Port = SetupTestSupport.TestPort,
            Database = name,
            Username = name,
        };

        try
        {
            var response = await Client().PostAsJsonAsync(
                "/api/setup",
                Command(target.Host, target.Port, name, name, admin.Username, admin.Password.Reveal()));

            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Setup answered {(int)response.StatusCode}: {body}");

            var result = JsonSerializer.Deserialize<SetupResultView>(body, Web);
            Assert.True(result!.Succeeded);
            Assert.False(result.RestartRequired);
            Assert.NotNull(result.NextStep);

            // Nowhere: not the response, not a log line, not a file.
            var secret = admin.Password.Reveal();
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, _host.Logs.All, StringComparison.Ordinal);
            Assert.Empty(SetupTestSupport.FilesContaining(_host.KeyRing, secret));
            Assert.Contains("Setup created role", _host.Logs.All, StringComparison.Ordinal);

            // What was kept works, and is not a superuser.
            var (file, keyRing) = SetupTestSupport.OpenFile(_host.KeyRing);

            using (keyRing)
            {
                var saved = file.Read();

                Assert.Equal(name, saved.Username);
                Assert.Empty(SetupTestSupport.FilesContaining(_host.KeyRing, saved.Password.Reveal()));
                Assert.True(PostgresProvisioning.CanConnect(saved).Succeeded);

                await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
                {
                    Host = saved.Host,
                    Port = saved.Port,
                    Database = saved.Database,
                    Username = saved.Username,
                    Password = saved.Password.Reveal(),
                    Pooling = false,
                }.ConnectionString);

                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = current_user;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
            }
        }
        finally
        {
            PostgresProvisioning.Drop(admin, target);
        }
    }
}

/// <summary>
/// Program.cs with no database source: the setup host.
/// </summary>
internal sealed class SetupModeHost : WebApplicationFactory<Program>
{
    /// <summary>The remote address a request should appear to come from.</summary>
    public const string RemoteHeader = "X-Test-Remote-Address";

    /// <summary>A header value meaning "no remote address at all".</summary>
    public const string NoAddress = "none";

    public string KeyRing { get; } = Path.Combine(
        Path.GetTempPath(), "eo-setup-keys-" + Guid.NewGuid().ToString("N"));

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:KeyRingPath", KeyRing);

        // Explicitly empty, so nothing in the machine's environment can turn
        // this into a configured installation.
        builder.UseSetting("Database:Password", string.Empty);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SetupTestSupport.DeleteQuietly(KeyRing);
    }

    /// <summary>
    /// Stands in for the socket a test server does not have: loopback unless a
    /// test says otherwise.
    /// </summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                var header = context.Request.Headers[RemoteHeader].ToString();

                context.Connection.RemoteIpAddress = header switch
                {
                    "" => IPAddress.Loopback,
                    NoAddress => null,
                    _ => IPAddress.Parse(header),
                };

                await following(context);
            });

            next(app);
        };
    }
}
