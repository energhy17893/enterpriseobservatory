using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// A read-only Redfish client for an iLO 5/6, one request at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Basic auth, per request, no session</b> -- HPE's own recommendation
/// (SMP <c>managingilosessionswithredfish</c>): "self-cleaning the session
/// list ... risk of reaching the maximum number of iLO sessions is very
/// low". F3's session channel exists for vim25's SOAP session model; it does
/// not apply here, and building one anyway would spend a session slot this
/// account does not need to spend.
/// </para>
/// <para>
/// Every call this type makes is a GET. No PATCH, PUT, POST or DELETE method
/// exists on it, on purpose: adding one later is a decision that should have
/// to touch this file, not slip in as a one-line addition to a switch.
/// </para>
/// </remarks>
internal sealed class RedfishClient : IDisposable
{
    private readonly HttpClient _http;

    public RedfishClient(Uri baseAddress, string user, string password, bool insecure)
    {
        var handler = new HttpClientHandler();
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>
    /// GETs one Redfish resource. Never throws for a non-2xx response -- a
    /// probe reports what the server said, including 404 for a path this
    /// generation does not have.
    /// </summary>
    public async Task<RedfishRead> GetAsync(string path, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new RedfishRead(path, null, (int)response.StatusCode, stopwatch.Elapsed, null);
            }

            try
            {
                return new RedfishRead(path, JsonDocument.Parse(body), (int)response.StatusCode, stopwatch.Elapsed, null);
            }
            catch (JsonException ex)
            {
                return new RedfishRead(path, null, (int)response.StatusCode, stopwatch.Elapsed, ex.Message);
            }
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new RedfishRead(path, null, null, stopwatch.Elapsed, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            return new RedfishRead(path, null, null, stopwatch.Elapsed, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One GET, timed, with whatever the server said instead of a value.</summary>
internal sealed record RedfishRead(
    string Path, JsonDocument? Document, int? StatusCode, TimeSpan Elapsed, string? Error)
{
    public bool Ok => Document is not null;
}
