using System.Net;
using System.Net.Http.Json;
using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The Database card's endpoints (G-DB), on the real composition root.
/// </summary>
/// <remarks>
/// Outside setup mode, so ADR-0014's ordinary rules: an administrator's session
/// or nothing. Every refusal has the administrator's success beside it, for the
/// reason <see cref="CompositionRootSmokeTests"/> gives.
/// </remarks>
public sealed class DatabaseCardTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    // What ObservatoryHost puts in Database:Password. Searched for in every
    // response: the card shows the connection and never its password.
    private const string DatabasePassword = "smoke-test-no-database-is-reached";

    private readonly ObservatoryHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient Client() => _host.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private async Task<HttpClient> SignedIn(string username, Role role)
    {
        Assert.True(_host.Accounts.TryAdd(new UserAccount
        {
            Username = username,
            Password = PasswordHash.Create(Secret.From(Password), iterations: 1_000),
            Role = role,
            CreatedUtc = DateTimeOffset.UtcNow,
        }));

        var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/signin", new { username, password = Password });

        Assert.True(response.IsSuccessStatusCode, $"Sign-in as '{username}' failed with {(int)response.StatusCode}.");

        return client;
    }

    private static async Task<HttpStatusCode[]> AllThree(HttpClient client) =>
    [
        (await client.GetAsync("/api/database")).StatusCode,
        (await client.PostAsJsonAsync("/api/database/test", new { })).StatusCode,
        (await client.PostAsJsonAsync("/api/database/rotate", new { })).StatusCode,
    ];

    [Fact]
    public async Task An_anonymous_caller_is_refused_every_database_action_with_401()
    {
        Assert.All(await AllThree(Client()), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    [Theory]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Operator)]
    public async Task A_session_below_administrator_is_refused_every_database_action_with_403(Role role)
    {
        var client = await SignedIn("not-admin", role);

        Assert.All(await AllThree(client), status => Assert.Equal(HttpStatusCode.Forbidden, status));
    }

    [Fact]
    public async Task An_administrator_sees_the_connection_and_never_its_password()
    {
        var client = await SignedIn("root", Role.Administrator);

        var response = await client.GetAsync("/api/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(DatabasePassword, body, StringComparison.Ordinal);
        Assert.DoesNotContain("password\":", body.Replace("passwordSetUtc\":", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        var view = await response.Content.ReadFromJsonAsync<DatabaseView>();
        Assert.Equal("Configuration", view!.Source);
        Assert.Equal("127.0.0.1", view.Host);
        Assert.Equal("observatory", view.Username);
        Assert.False(view.CanRotate);
    }

    [Fact]
    public async Task An_administrator_reaches_the_test_and_it_reports_rather_than_throws()
    {
        // The smoke host's database throws on purpose; reaching the handler and
        // getting a sentence back is what this proves, not a working server.
        var client = await SignedIn("root", Role.Administrator);

        var response = await client.PostAsJsonAsync("/api/database/test", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DatabaseActionView>();
        Assert.False(result!.Succeeded);
        Assert.DoesNotContain(DatabasePassword, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotation_is_refused_when_the_password_comes_from_configuration()
    {
        // User secrets live where the product cannot write. Rotating on the
        // server alone would lock the service out at its next restart.
        var client = await SignedIn("root", Role.Administrator);

        var response = await client.PostAsJsonAsync("/api/database/rotate", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DatabaseActionView>();
        Assert.Equal(DatabaseApi.ConfigurationRefusal, result!.Detail);
    }
}
