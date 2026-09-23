using System.Net.Http.Headers;
using System.Text;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Owns the transport and the vCenter session for one connection: the
/// <see cref="HttpClient"/>, the handler policy (cookies, TLS), the F2
/// per-source request ceiling, and when to log in, retry and log out.
/// </summary>
/// <remarks>
/// <para>
/// Package F (docs/proposals/f-invert-collector-authority.md, F3) moves this
/// out of <see cref="VsphereClient"/> so the collector is reduced to building
/// SOAP envelopes and parsing replies through an already-guarded channel; it
/// no longer decides when it is logged in or for how long.
/// </para>
/// <para>
/// The session model takes its shape from the reference approach documented
/// in <c>docs/reference-approaches.md</c> §10.3 (Telegraf's
/// <c>inputs/vsphere/client.go</c>): one mutex around every session decision,
/// a login failure gets exactly one more attempt (rebuilding the
/// <see cref="HttpClient"/> once first), and logout runs exactly once. It
/// deliberately does <em>not</em> add Telegraf's proactive
/// <c>CurrentTime</c> probe ahead of a cached session's every use: that would
/// spend one extra round trip per top-level call for a session that is, in
/// the overwhelming case, still good, and this product already has a cheap
/// way to notice a dead one — the real call fails with
/// <c>NotAuthenticated</c>, which is what drives the retry below. There is
/// also no background keep-alive timer (govmomi refreshes every 10 minutes);
/// both are left to a follow-up if this estate's sessions turn out to expire
/// under load rather than between cycles, because neither is needed to close
/// the races this package targets and both change either request volume or
/// disposal ordering in ways worth reviewing on their own.
/// </para>
/// <para>
/// The generation counter #53's fix needed moves here from
/// <c>VsphereClient</c> rather than disappearing: it still answers "is the
/// session my failed call saw the one that is still current, or did somebody
/// already replace it" — but the check and the replacement now happen under
/// the same mutex as logout, which is what actually closes the race. Before,
/// the "signed in" flag was cleared outside that lock (<c>VsphereClient.cs</c>,
/// what was line 2244), so a slower caller could clear it after a faster one
/// had already signed in again and sign in a second time, leaving the first
/// replacement on the vCenter with nobody holding it.
/// </para>
/// </remarks>
public sealed class VsphereSessionChannel : IDisposable
{
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The largest reply body read from vCenter, in bytes.
    /// </summary>
    /// <remarks>
    /// Well above any real page (inventory and metric reads are paged far
    /// below this) and far below what would exhaust the host if a hostile or
    /// intercepted endpoint streamed without end.
    /// </remarks>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    private readonly HttpMessageHandler _handler;
    private readonly VsphereConnectionOptions _options;
    private readonly SourceRequestGate? _requestGate;

    /// <summary>The one lock every session decision and logout goes through.</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private HttpClient _http;
    private VsphereServiceContent? _content;

    /// <summary>
    /// Cleared and set only under <see cref="_mutex"/>. This is the shape #53
    /// was: <c>VsphereClient.cs:2244</c> used to clear the equivalent flag
    /// outside the lock, so a slower caller could sign in again just after a
    /// faster one had already logged out, leaving the replacement session on
    /// the vCenter with nobody holding it. Under one mutex, whichever of a
    /// concurrent login and a concurrent logout gets there first decides
    /// what the other one sees; there is no window between "read" and
    /// "write" for a second caller to land in.
    /// </summary>
    private bool _loggedIn;

    /// <summary>Which sign-in the current session came from; see <see cref="Generation"/>.</summary>
    private int _generation;

    private bool _disposed;

    public VsphereSessionChannel(
        HttpMessageHandler handler, VsphereConnectionOptions options, SourceRequestGate? requestGate = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestGate = requestGate;
        _http = BuildHttpClient();
    }

    private HttpClient BuildHttpClient() => new(_handler, disposeHandler: false)
    {
        BaseAddress = _options.BaseAddress,
        Timeout = _options.RequestTimeout,
    };

    /// <summary>
    /// Builds a handler configured for this connection.
    /// </summary>
    /// <remarks>
    /// Certificate validation is only relaxed when the options say so. vCenter
    /// ships with a self-signed certificate that many installations never
    /// replace, so this has to be expressible — but as a decision someone
    /// recorded, not something the collector does quietly on their behalf.
    /// Moved here from <c>VsphereClient</c> by F3: handler policy belongs to
    /// whoever owns the transport's whole lifetime. TLS thumbprint pinning
    /// (package G) is deliberately not added here yet — it lands once this
    /// package has finished moving the handler, so it is written once.
    /// </remarks>
    public static HttpMessageHandler CreateHandler(VsphereConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler
        {
            // vCenter tracks the session with the vmware_soap_session cookie.
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };

        if (options.AcceptUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <summary>
    /// Which sign-in the current session came from. A caller that sees
    /// <c>NotAuthenticated</c> reads this before the call goes out and hands
    /// it back to <see cref="EnsureSessionAsync(int?, CancellationToken)"/>,
    /// so the channel can tell "the session my failed call used is still
    /// current, nobody has fixed it yet" from "somebody already replaced it
    /// while my failure was in flight" — the second case asks for nothing.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Sessions this channel believes it holds right now — 0 or 1, since one
    /// channel ever opens one session at a time (F6, ADR-0025 §5).
    /// </summary>
    /// <remarks>
    /// "Believes", not "vCenter confirms": <c>_loggedIn</c> is this channel's
    /// own bookkeeping, not a read of the server's session list — the
    /// read-only account this product runs as cannot list it at all
    /// (measured: <c>NoPermission</c>, docs/reference-approaches.md §10.6). A
    /// session vCenter's idle timeout already collected without this channel
    /// finding out would still read as 1 here; nothing on a read-only account
    /// can catch that.
    /// </remarks>
    /// <remarks>
    /// Read without the mutex, the same way <see cref="EnsureSessionAsync(int?, CancellationToken)"/>'s
    /// own fast path already reads <c>_loggedIn</c>: a self-metric a health
    /// endpoint polls every cycle must not contend with every real call for a
    /// number that is 0 or 1 either way.
    /// </remarks>
    public int SessionsHeld => _loggedIn ? 1 : 0;

    /// <summary>
    /// Ensures a usable session and returns its service content, logging in
    /// or rebuilding as needed. See the type remarks for the model.
    /// </summary>
    public Task<VsphereServiceContent> EnsureSessionAsync(CancellationToken cancellationToken) =>
        EnsureSessionAsync(expiredGeneration: null, cancellationToken);

    /// <param name="expiredGeneration">
    /// The generation of a session a caller was just told is not
    /// authenticated, or null when this is only asking for one, with no
    /// opinion on whether the cached one is still good.
    /// </param>
    /// <param name="cancellationToken"></param>
    public async Task<VsphereServiceContent> EnsureSessionAsync(
        int? expiredGeneration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (expiredGeneration is null && _content is { } fresh && _loggedIn)
        {
            return fresh;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Inside the lock, and only for the session the caller actually
            // used. By the time a refusal arrives another call may already
            // have replaced that session, and giving up the replacement on
            // the strength of news about its predecessor is how one expiry
            // becomes two logins.
            if (expiredGeneration == _generation)
            {
                _loggedIn = false;
            }

            if (_content is { } cached && _loggedIn)
            {
                return cached;
            }

            try
            {
                return await LoginAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only a transport-level failure earns a rebuild: the socket
                // or handshake itself was the problem, not vCenter's answer.
                // A well-formed but bad reply (a fault, an oversized or DTD
                // body) is not retried here — sending the same login twice
                // for a reply that will not change is a login spent on
                // nothing, and for a rejected credential it is exactly the
                // lockout risk the retry-once rule elsewhere exists to avoid.
                RebuildHttpClient();
                return await LoginAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<VsphereServiceContent> LoginAsync(CancellationToken cancellationToken)
    {
        var contentXml = await PostRawAsync(
            VsphereSoapRequests.RetrieveServiceContent(), cancellationToken, VsphereCallContext.General)
            .ConfigureAwait(false);

        var content = VsphereServiceContentParser.TryParse(contentXml)
            ?? throw new VsphereApiException(
                "vCenter did not return usable service content. The endpoint may not be a vSphere SDK.");

        await PostRawAsync(
            // The one place the credential leaves its wrapper. It goes
            // straight into the login request and nowhere else; see ADR-0010.
            VsphereSoapRequests.Login(content.SessionManager, _options.Username, _options.Password.Reveal()),
            cancellationToken,
            VsphereCallContext.General).ConfigureAwait(false);

        _content = content;
        _generation++;
        _loggedIn = true;
        return content;
    }

    /// <summary>
    /// Discards the current <see cref="HttpClient"/> and builds a fresh one
    /// over the same handler, the "rebuild once" step of the Telegraf model.
    /// </summary>
    /// <remarks>
    /// The handler itself is not recreated — this channel is handed one, not
    /// a factory for one, so that a caller (a test's fake transport, or the
    /// registry's real one) keeps a single handler instance for the
    /// connection's whole life. That means a rebuild does not get a fresh
    /// cookie jar the way a brand-new <c>HttpClientHandler</c> would; what it
    /// does get is a clean <see cref="HttpClient"/> with no state left over
    /// from whatever made the previous attempt fail. If a fresh cookie jar
    /// turns out to matter in practice, this is where a handler factory would
    /// go, as a follow-up.
    /// </remarks>
    private void RebuildHttpClient()
    {
        var old = _http;
        _http = BuildHttpClient();
        old.Dispose();
        _content = null;
        _loggedIn = false;
    }

    /// <summary>
    /// Sends one already-built SOAP envelope and returns the reply body.
    /// </summary>
    /// <remarks>
    /// No session logic here at all — a caller who receives
    /// <see cref="VsphereFaultKind.NotAuthenticated"/> asks this channel to
    /// <see cref="EnsureSessionAsync"/> again and resends. That is the "the
    /// runner retries" half of F3: the retry decision belongs to whoever
    /// calls this channel, not to the channel's own transport method.
    /// </remarks>
    public Task<string> SendAsync(
        string body, CancellationToken cancellationToken, VsphereCallContext context = VsphereCallContext.General)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return PostRawAsync(body, cancellationToken, context);
    }

    /// <summary>
    /// Sends one request and reads its reply, holding this source's request
    /// gate for exactly that exchange (F2).
    /// </summary>
    /// <remarks>
    /// The gate is acquired here and nowhere else, and released before this
    /// method returns either way. A login nested inside <c>EnsureSessionAsync</c>
    /// is a second, independent acquisition of the same gate, never a nested
    /// one, because the probe's or the previous attempt's permit was already
    /// released by the time the next call reaches here — so nothing here can
    /// deadlock against itself even at a gate limit of one.
    /// </remarks>
    private async Task<string> PostRawAsync(
        string body, CancellationToken cancellationToken, VsphereCallContext context = VsphereCallContext.General)
    {
        if (_requestGate is null)
        {
            return await PostCoreAsync(body, cancellationToken, context).ConfigureAwait(false);
        }

        using var permit = await _requestGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await PostCoreAsync(body, cancellationToken, context).ConfigureAwait(false);
    }

    private async Task<string> PostCoreAsync(
        string body, CancellationToken cancellationToken, VsphereCallContext context = VsphereCallContext.General)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sdk")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        // vCenter accepts an empty SOAPAction; sending one avoids a 500 from
        // intermediaries that insist on the header being present.
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"urn:vim25/8.0.0.0\"");

        // Headers first, body by hand: the default would buffer whatever the
        // far end sends, up to 2 GB, before this code saw a byte of it.
        // HttpClient.Timeout then stops at the headers, so it is applied here
        // to the body as well: a reply that drips forever is as bad as a huge one.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(_http.Timeout);
        }

        string content;
        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            try
            {
                content = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The shape HttpClient itself gives a timeout.
            throw new TaskCanceledException(
                $"The vCenter call did not complete within {_http.Timeout}.", new TimeoutException(ex.Message, ex));
        }

        using var owned = response;

        // vCenter returns faults as HTTP 500 with a SOAP fault body, so the
        // status code alone cannot tell "your credentials are wrong" from
        // "the server is broken". The body decides.
        if (VsphereSoapFaultReader.TryRead(content, context) is { } fault)
        {
            throw fault.Kind == VsphereFaultKind.QuerySizeRefused
                ? new VsphereQuerySizeRefusedException(fault.Message)
                : new VsphereApiException(fault.Kind, Describe(fault));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new VsphereApiException(
                $"vCenter returned {(int)response.StatusCode} {response.ReasonPhrase} with no SOAP fault.");
        }

        // Checked after the status, so that a proxy's HTML error page is
        // still reported by its status rather than by its <!DOCTYPE html>.
        if (VsphereXml.DeclaresDocumentType(content))
        {
            throw new VsphereApiException(
                "vCenter's reply declared a DTD. vim25 never sends one, so the reply was refused unread.");
        }

        return content;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw TooLarge();
        }

        using var buffer = new MemoryStream();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        var encoding = EncodingFor(content.Headers.ContentType?.CharSet);
        using var reader = new StreamReader(
            buffer, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        static VsphereApiException TooLarge() => new(
            $"vCenter's reply exceeded {MaxResponseBytes / (1024 * 1024)} MB and was abandoned unread.");
    }

    private static Encoding EncodingFor(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string Describe(VsphereSoapFault fault) =>
        fault.Kind switch
        {
            VsphereFaultKind.InvalidLogin =>
                "vCenter rejected the credentials. This is not retried, so that repeated attempts " +
                "cannot lock the monitoring account out.",
            VsphereFaultKind.NoPermission =>
                $"The account is authenticated but lacks a required privilege ({fault.Message}). " +
                "This needs a role change, not a retry.",
            _ => string.IsNullOrWhiteSpace(fault.Message) ? fault.FaultType : fault.Message,
        };

    /// <summary>
    /// Ends this channel's session on the vCenter, if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the registry on retirement and on shutdown. A session is a
    /// bounded thing on a vCenter (VMware KB 336100: 500 by default, with
    /// tightening timeouts above 70% and 90% of that) and an abandoned one
    /// stays until the idle timeout collects it, so every restart, every
    /// edited or removed connection and every probe left one behind before
    /// this was called from anywhere live.
    /// </para>
    /// <para>
    /// Runs under the same mutex as <see cref="EnsureSessionAsync"/>, and
    /// clears <c>_loggedIn</c> <em>inside</em> that lock — closing the defect
    /// this had before F3 (<c>VsphereClient.cs:2244</c>, the same shape as
    /// #53): the flag used to clear outside the lock, so a call racing this
    /// one could read the old value and sign back in believing the session
    /// it just replaced was still the one being closed. Whichever of a
    /// concurrent login and a concurrent logout takes the mutex first now
    /// decides what the session is when the other one runs.
    /// </para>
    /// <para>
    /// Posted directly rather than through the ensure-session path: that path
    /// answers an expired session by logging in again, and logging in so as
    /// to log out is a login spent on nothing. A channel that never logged in
    /// sends nothing at all, for the same reason. Never throws — this runs
    /// while a connection is being taken down, when there is nobody left to
    /// tell and the idle timeout is still the backstop.
    /// </para>
    /// </remarks>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _mutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_loggedIn || _content is not { } content)
            {
                return;
            }

            // Cleared first, inside the lock: a second caller sees this
            // immediately and asks nothing, rather than racing the network
            // call below.
            _loggedIn = false;

            try
            {
                using var grace = new CancellationTokenSource(CleanupGrace);

                await PostRawAsync(
                    VsphereSoapRequests.Logout(content.SessionManager),
                    cancellationToken.IsCancellationRequested ? grace.Token : cancellationToken,
                    VsphereCallContext.General).ConfigureAwait(false);
            }
            catch (VsphereApiException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Logs out, then asks the vCenter whether it agrees.
    /// </summary>
    /// <remarks>
    /// For the probe tool (<c>--sessions --confirm-logout</c>). A read-only
    /// account cannot list sessions, so "did the session end" cannot be read
    /// off a list; but the server will say so itself. After a logout that
    /// worked, a call carrying the same session cookie is refused as
    /// <c>NotAuthenticated</c>. After one that did not, it is answered.
    /// </remarks>
    /// <returns>
    /// True when the vCenter refused the old session; false when it still
    /// honoured it; null when this channel never had a session to end.
    /// </returns>
    public async Task<bool?> LogoutAndConfirmAsync(CancellationToken cancellationToken)
    {
        VsphereServiceContent content;

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loggedIn || _content is not { } current)
            {
                return null;
            }

            content = current;
        }
        finally
        {
            _mutex.Release();
        }

        await LogoutAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Posted directly: going through EnsureSessionAsync would meet
            // the refusal by signing in again and hide the very answer being
            // sought.
            var reply = await PostRawAsync(
                VsphereSoapRequests.RetrieveSessions(content.PropertyCollector, content.SessionManager),
                cancellationToken,
                VsphereCallContext.General).ConfigureAwait(false);

            // Answered, but an answer is not yet "still signed in". The
            // property collector can reply to a session it no longer knows
            // and refuse each property instead of the call — so the only
            // thing that proves the session survived is the session itself
            // coming back.
            return VsphereSessions.Parse(reply).Current is null;
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.NotAuthenticated)
        {
            return true;
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
        // F2's gate moves in with F3, as §3.2 of the design note said it
        // would: this channel is now the one thing that owns a source's
        // whole request lifetime, gate included.
        _requestGate?.Dispose();
    }
}
