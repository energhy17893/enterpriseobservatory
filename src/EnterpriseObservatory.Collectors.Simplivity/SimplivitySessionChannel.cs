using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Simplivity;

/// <summary>
/// The one thing that talks to a SimpliVity federation: its HTTP transport
/// and its OAuth token (the F3 channel pattern, VsphereSessionChannel's
/// counterpart for REST).
/// </summary>
/// <remarks>
/// <para>
/// HPE OmniStack REST: <c>POST /api/oauth/token</c> with Basic
/// <c>simplivity:</c> and <c>grant_type=password</c> answers a bearer token
/// that dies after 10 minutes idle or 24 hours in all (§10.7). One token
/// serves every cycle: asking for a new one per cycle would be a login per
/// cycle against the account vCenter also authenticates. A 401 on a read
/// means the token died; it is replaced once — however many reads noticed
/// at the same moment — and the read asked again. A second 401 on a token
/// just issued is not a dead token, and is not retried.
/// </para>
/// <para>
/// Revoked only at shutdown or retirement (<see cref="RevokeAsync"/>). The
/// live OVC answers that call 401 (measured 23 September on Kibar), so a
/// refused revoke is reported as a warning and never as an error: the token
/// expires on its own ten minutes later.
/// </para>
/// </remarks>
public sealed class SimplivitySessionChannel : IDisposable
{
    /// <summary>Largest reply read; a 500-row backup page is well under a megabyte.</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(10);

    private static readonly AuthenticationHeaderValue ClientCredentials =
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("simplivity:")));

    private readonly HttpClient _http;
    private readonly SimplivityConnectionOptions _options;
    private readonly SourceRequestGate? _requestGate;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string? _token;
    private int _generation;
    private bool _disposed;

    public SimplivitySessionChannel(
        HttpMessageHandler handler, SimplivityConnectionOptions options, SourceRequestGate? requestGate = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestGate = requestGate;
        _http = new HttpClient(handler)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>The TLS decision, in one place — see VsphereSessionChannel.CreateHandler.</summary>
    public static HttpMessageHandler CreateHandler(SimplivityConnectionOptions options)
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

    /// <summary>Tokens issued since this channel was built. One per 24 hours is the healthy rate.</summary>
    public int TokensIssued => Volatile.Read(ref _generation);

    /// <summary>Tokens this channel believes it holds (F6's <c>sessionsHeld</c>).</summary>
    public int SessionsHeld => _token is null ? 0 : 1;

    /// <summary>GETs one path as JSON, with a token, replacing a dead token once.</summary>
    public async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (token, generation) = await TokenAsync(expiredGeneration: null, cancellationToken).ConfigureAwait(false);
        var (status, body) = await SendAsync(Get(pathAndQuery, token), cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.Unauthorized)
        {
            (token, _) = await TokenAsync(generation, cancellationToken).ConfigureAwait(false);
            (status, body) = await SendAsync(Get(pathAndQuery, token), cancellationToken).ConfigureAwait(false);
        }

        return status switch
        {
            HttpStatusCode.OK => Parse(pathAndQuery, body),
            HttpStatusCode.Unauthorized => throw new SimplivityApiException(
                CollectionFailureKind.AuthenticationRejected,
                $"GET {pathAndQuery} was refused with a token the OVC had just issued. Not retried."),
            HttpStatusCode.Forbidden => throw new SimplivityApiException(
                CollectionFailureKind.AuthorizationDenied,
                $"The account may not read {pathAndQuery}. It needs a role change, not a retry."),
            _ => throw new SimplivityApiException(
                CollectionFailureKind.ProtocolError, $"GET {pathAndQuery} answered {(int)status}."),
        };
    }

    /// <summary>
    /// <c>POST /api/oauth/revoke</c>. Never throws.
    /// </summary>
    /// <returns>Null when revoked (or nothing was held); otherwise a warning to log.</returns>
    public async Task<string?> RevokeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return null;
        }

        await _mutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_token is not { } token)
            {
                return null;
            }

            _token = null;

            using var grace = new CancellationTokenSource(CleanupGrace);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/revoke")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            };
            request.Headers.Authorization = ClientCredentials;

            var (status, _) = await SendAsync(
                request, cancellationToken.IsCancellationRequested ? grace.Token : cancellationToken)
                .ConfigureAwait(false);

            return status switch
            {
                HttpStatusCode.OK or HttpStatusCode.NoContent => null,
                HttpStatusCode.Unauthorized =>
                    "the OVC answered 401 to the token revoke (as measured on this API); " +
                    "the token expires on its own after 10 minutes idle",
                _ => $"the token revoke answered {(int)status}; the token expires on its own after 10 minutes idle",
            };
        }
#pragma warning disable CA1031 // Justified: revoke is cleanup; it reports, it never fails a shutdown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"the token revoke failed ({ex.GetType().Name}: {ex.Message}); " +
                   "the token expires on its own after 10 minutes idle";
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>The held token, or a new one once when <paramref name="expiredGeneration"/> is still current.</summary>
    /// <remarks>
    /// Two reads that notice one dead token pass the same generation; the
    /// first replaces it and the second finds the generation moved on and
    /// takes the new token — one refresh, not two (the F3 rule).
    /// </remarks>
    private async Task<(string Token, int Generation)> TokenAsync(
        int? expiredGeneration, CancellationToken cancellationToken)
    {
        if (expiredGeneration is null && _token is { } held)
        {
            return (held, _generation);
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expiredGeneration == _generation)
            {
                _token = null;
            }

            if (_token is { } current)
            {
                return (current, _generation);
            }

            _token = await IssueAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _generation);

            return (_token, _generation);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<string> IssueAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _options.Username,
                ["password"] = _options.Password.Reveal(),
            }),
        };
        request.Headers.Authorization = ClientCredentials;

        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new SimplivityApiException(
                CollectionFailureKind.AuthenticationRejected,
                "The OVC rejected the username or password. This is not retried, so that repeated " +
                "attempts cannot lock the account out (it is the vCenter account).");
        }

        if (status != HttpStatusCode.OK)
        {
            throw new SimplivityApiException(
                CollectionFailureKind.ProtocolError, $"POST /api/oauth/token answered {(int)status}.");
        }

        using var document = Parse("/api/oauth/token", body);

        return document.RootElement.TryGetProperty("access_token", out var token) &&
               token.GetString() is { Length: > 0 } value
            ? value
            : throw new SimplivityApiException(
                CollectionFailureKind.ProtocolError, "POST /api/oauth/token answered without an access_token.");
    }

    private static HttpRequestMessage Get(string pathAndQuery, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Sends and reads the whole reply, bounded, through the source's request gate (F2).</summary>
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var owned = request;
        using var permit = _requestGate is null
            ? null
            : await _requestGate.AcquireAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return (response.StatusCode, body);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The SimpliVity call did not complete within {_http.Timeout}.", new TimeoutException(ex.Message, ex));
        }
    }

    /// <summary>A reply that is not JSON is a failure, never an empty success.</summary>
    private static JsonDocument Parse(string what, string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new SimplivityApiException(
                CollectionFailureKind.ProtocolError, $"{what} answered something that is not JSON: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
        _http.Dispose();
        _requestGate?.Dispose();
    }
}

/// <summary>A SimpliVity failure that already knows whether it is worth retrying.</summary>
public sealed class SimplivityApiException : Exception, ICollectionFault
{
    public SimplivityApiException(CollectionFailureKind kind, string message)
        : base(message) => Kind = kind;

    public SimplivityApiException(string message)
        : this(CollectionFailureKind.ProtocolError, message)
    {
    }

    public SimplivityApiException(string message, Exception innerException)
        : base(message, innerException) => Kind = CollectionFailureKind.ProtocolError;

    public SimplivityApiException()
        : this("The SimpliVity call failed.")
    {
    }

    public CollectionFailureKind Kind { get; }
}
