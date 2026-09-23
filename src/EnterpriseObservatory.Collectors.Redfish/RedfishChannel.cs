using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Collectors.Redfish;

/// <summary>How to reach one iLO's Redfish service.</summary>
public sealed record RedfishConnectionOptions
{
    public required string InstanceId { get; init; }

    public required Uri BaseAddress { get; init; }

    /// <summary>An iLO account with the <c>ReadOnly</c> role (LoginPriv only, §10.7).</summary>
    public required string Username { get; init; }

    public required Secret Password { get; init; }

    /// <summary>Honoured until G-TLS: iLOs ship self-signed certificates.</summary>
    public bool AcceptUntrustedCertificate { get; init; }

    /// <summary>Per request. Measured 0.3–1.0 s per call, 3.4 s for a full IML, on Kibar's iLO 5.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The one thing that talks to an iLO: its HTTP transport (the F3 channel
/// pattern). GET only.
/// </summary>
/// <remarks>
/// Basic auth on every request, no session — HPE's own recommendation
/// (SMP <c>managingilosessionswithredfish</c>, reference-approaches §10.7):
/// nothing to log in, refresh or give back, and no iLO session slot spent.
/// </remarks>
public sealed class RedfishChannel : IDisposable
{
    /// <summary>Requests in flight per iLO — Telegraf's <c>collect_concurrency</c> (§10.7).</summary>
    public const int Parallelism = 8;

    /// <summary>Largest reply read; the full IML measured 270 KB.</summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly SourceRequestGate? _requestGate;
    private readonly AuthenticationHeaderValue _basic;
    private bool _disposed;

    public RedfishChannel(HttpMessageHandler handler, RedfishConnectionOptions options, SourceRequestGate? requestGate = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        _requestGate = requestGate;
        _basic = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password.Reveal()}")));
        _http = new HttpClient(handler)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>The TLS decision, in one place — see VsphereSessionChannel.CreateHandler.</summary>
    public static HttpMessageHandler CreateHandler(RedfishConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler();

        if (options.AcceptUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <summary>GETs one path as JSON through the source's request gate (F2).</summary>
    public async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var permit = _requestGate is null
            ? null
            : await _requestGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Authorization = _basic;

        HttpStatusCode status;
        string body;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The iLO call did not complete within {_http.Timeout}.", new TimeoutException(ex.Message, ex));
        }

        if (status != HttpStatusCode.OK)
        {
            throw status switch
            {
                HttpStatusCode.Unauthorized => new RedfishApiException(
                    CollectionFailureKind.AuthenticationRejected,
                    "The iLO rejected the username or password. Not retried, so that repeated attempts " +
                    "cannot lock the account out."),
                HttpStatusCode.Forbidden => new RedfishApiException(
                    CollectionFailureKind.AuthorizationDenied,
                    $"The account may not read {pathAndQuery}. It needs a role change, not a retry."),
                _ => new RedfishApiException(
                    CollectionFailureKind.ProtocolError, $"GET {pathAndQuery} answered {(int)status}."),
            };
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new RedfishApiException(
                CollectionFailureKind.ProtocolError, $"GET {pathAndQuery} answered something that is not JSON: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _requestGate?.Dispose();
    }
}

/// <summary>A Redfish failure that already knows whether it is worth retrying.</summary>
public sealed class RedfishApiException : Exception, ICollectionFault
{
    public RedfishApiException(CollectionFailureKind kind, string message)
        : base(message) => Kind = kind;

    public RedfishApiException(string message)
        : this(CollectionFailureKind.ProtocolError, message)
    {
    }

    public RedfishApiException(string message, Exception innerException)
        : base(message, innerException) => Kind = CollectionFailureKind.ProtocolError;

    public RedfishApiException()
        : this("The Redfish call failed.")
    {
    }

    public CollectionFailureKind Kind { get; }
}
