using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>Reads the certificate an HTTPS endpoint presents.</summary>
public interface IEndpointCertificateReader
{
    /// <summary>
    /// The certificate the endpoint presented in its TLS handshake, or null
    /// when none could be read. Never throws for an endpoint that does not
    /// answer; only the caller's cancellation escapes.
    /// </summary>
    Task<X509Certificate2?> ReadAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a vCenter's certificate by a TLS handshake with its endpoint (M8.7).
/// </summary>
/// <remarks>
/// <para>
/// Read-only and credential-free: a TCP connection and a client handshake
/// with the endpoint's host name as SNI. The validation callback keeps the
/// certificate presented and then <strong>refuses</strong> it, so the
/// handshake never completes and no application byte can ever be sent on
/// this connection. That is also why an expired or self-signed certificate —
/// exactly the one the radar must read — is read whatever
/// <see cref="VsphereConnectionOptions.AcceptUntrustedCertificate"/> says:
/// nothing is trusted here, only looked at. The SOAP connection keeps its own
/// validation, untouched.
/// </para>
/// <para>
/// Measured live (collection-pr1-shapes.md): the handshake presents the
/// certificate and its <c>notAfter</c> reads. The vim25 API does not offer
/// the vCenter's own certificate to a read-only account.
/// </para>
/// </remarks>
public sealed class TlsEndpointCertificateReader(TimeSpan timeout) : IEndpointCertificateReader
{
    private const int HttpsPort = 443;

    public async Task<X509Certificate2?> ReadAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        X509Certificate2? presented = null;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint.Host, endpoint.IsDefaultPort ? HttpsPort : endpoint.Port, limit.Token)
                .ConfigureAwait(false);

            await using var tls = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    presented ??= certificate is null
                        ? null
                        : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                    // Looked at, never trusted: see the remarks.
                    return false;
                });

            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = endpoint.Host }, limit.Token)
                .ConfigureAwait(false);

            return presented;
        }
        // The refusal above ends every handshake here, with the certificate
        // kept; one that failed before any certificate arrived read nothing.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return presented;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            return presented;
        }
    }
}
