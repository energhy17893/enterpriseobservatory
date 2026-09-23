using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// A read-only client for the HPE SimpliVity (OmniStack) REST API.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/version</c> needs no authentication (§10.7). Everything else
/// needs a bearer token from <c>POST /api/oauth/token</c>, Basic
/// <c>simplivity:</c> with <c>grant_type=password</c> and the read-only
/// account's own credentials -- "read-only user can perform GET"
/// (developer.hpe.com). The token is inactivity-limited to 10 minutes and
/// absolute-limited to 24 hours; this probe runs once and revokes when it is
/// done, so neither limit is exercised, and revoking is unconditional --
/// including when a later call fails -- so a probe run never leaves a token
/// alive past its own process.
/// </para>
/// <para>
/// The only POSTs this type makes are <c>oauth/token</c> and
/// <c>oauth/revoke</c>, both named in M6.0b's constraints as the sole
/// exceptions to "no PATCH, no PUT, no DELETE". Every other call is a GET.
/// </para>
/// </remarks>
internal sealed class SimplivityClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _accessToken;

    public SimplivityClient(Uri baseAddress, bool insecure)
    {
        var handler = new HttpClientHandler();
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<SimplivityRead> GetVersionAsync(CancellationToken cancellationToken) =>
        await GetAsync("/api/version", authenticated: false, cancellationToken);

    /// <summary>
    /// <c>POST /api/oauth/token</c>. Timed: the read budget for a probe run
    /// includes one token acquisition, and M6.0b's report asks for it.
    /// </summary>
    public async Task<(bool Ok, TimeSpan Elapsed, string? Error)> LoginAsync(
        string user, string password, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = user,
                ["password"] = password,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("simplivity:")));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return (false, stopwatch.Elapsed, $"HTTP {(int)response.StatusCode}");
            }

            using var parsed = JsonDocument.Parse(body);
            _accessToken = parsed.RootElement.TryGetProperty("access_token", out var token)
                ? token.GetString()
                : null;

            return (_accessToken is { Length: > 0 }, stopwatch.Elapsed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            stopwatch.Stop();
            return (false, stopwatch.Elapsed, ex.Message);
        }
    }

    public Task<SimplivityRead> GetAsync(string path, CancellationToken cancellationToken) =>
        GetAsync(path, authenticated: true, cancellationToken);

    private async Task<SimplivityRead> GetAsync(string path, bool authenticated, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authenticated && _accessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new SimplivityRead(path, null, (int)response.StatusCode, stopwatch.Elapsed, null);
            }

            try
            {
                return new SimplivityRead(
                    path, JsonDocument.Parse(body), (int)response.StatusCode, stopwatch.Elapsed, null);
            }
            catch (JsonException ex)
            {
                return new SimplivityRead(path, null, (int)response.StatusCode, stopwatch.Elapsed, ex.Message);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            stopwatch.Stop();
            return new SimplivityRead(path, null, null, stopwatch.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// <c>POST /api/oauth/revoke</c>. Called unconditionally by the runner,
    /// including on a failed run — the same discipline F3 uses for vCenter
    /// sessions, applied to a token instead.
    /// </summary>
    public async Task RevokeAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not { Length: > 0 } token)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/revoke")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("simplivity:")));

            using var response = await _http.SendAsync(request, cancellationToken);
            Console.WriteLine($"  POST /api/oauth/revoke                     {(int)response.StatusCode}");
        }
#pragma warning disable CA1031 // Justified: revoke is best-effort cleanup, never the reason a probe fails.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine($"  POST /api/oauth/revoke                     FAILED ({ex.GetType().Name})");
        }
        finally
        {
            _accessToken = null;
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record SimplivityRead(
    string Path, JsonDocument? Document, int? StatusCode, TimeSpan Elapsed, string? Error)
{
    public bool Ok => Document is not null;
}
