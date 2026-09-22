using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// M8.7: the vCenter endpoint's certificate, read by a TLS handshake during
/// the inventory read — read-only, no credentials, nothing sent — and kept
/// as its expiry and fingerprint only.
/// </summary>
public class VCenterCertificateTests
{
    private static X509Certificate2 SelfSigned(DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        using var ephemeral = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(notAfter.AddYears(-1), notAfter);

        // SChannel will not serve an ephemeral key; a PKCS#12 round trip gives it one it can.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private sealed class FixedReader(X509Certificate2? certificate) : IEndpointCertificateReader
    {
        public List<Uri> Asked { get; } = [];

        public Task<X509Certificate2?> ReadAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            Asked.Add(endpoint);
            return Task.FromResult(certificate);
        }
    }

    private static VsphereClient Client(IEndpointCertificateReader? reader)
    {
        var options = new VsphereConnectionOptions
        {
            BaseAddress = new Uri("https://vc.invalid"),
            Username = "svc-readonly@vsphere.local",
            Password = Secret.From("not-a-real-password"),
            InstanceId = "vc-test",
        };

        var channel = new VsphereSessionChannel(new VsphereSessionCleanupTests.ScriptedVcenter(), options);

        return new VsphereClient(channel, options) { CertificateReader = reader };
    }

    [Fact]
    public async Task The_inventory_read_carries_the_vcenter_certificate_expiry_and_fingerprint()
    {
        var notAfter = new DateTimeOffset(2027, 1, 31, 8, 0, 0, TimeSpan.Zero);
        using var certificate = SelfSigned(notAfter);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var reader = new FixedReader(certificate);

        // The client owns what the reader hands it and disposes it once read.
        var payload = await Client(reader).RetrieveInventoryAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://vc.invalid"), Assert.Single(reader.Asked));
        Assert.Equal(notAfter, DateTimeOffset.Parse(
            payload.VCenterVerdicts[InventoryVerdicts.CertificateNotAfter], CultureInfo.InvariantCulture));
        Assert.Equal(fingerprint, payload.VCenterVerdicts[InventoryVerdicts.CertificateSha256]);
        Assert.Equal(2, payload.VCenterVerdicts.Count);
    }

    [Fact]
    public async Task No_certificate_read_leaves_no_expiry_and_the_inventory_still_arrives()
    {
        var unread = await Client(new FixedReader(null)).RetrieveInventoryAsync(CancellationToken.None);
        var notAsked = await Client(null).RetrieveInventoryAsync(CancellationToken.None);

        Assert.Empty(unread.VCenterVerdicts);
        Assert.Single(unread.Datastores);
        Assert.Empty(notAsked.VCenterVerdicts);
    }

    [Fact]
    public async Task The_tls_reader_takes_the_certificate_from_the_handshake_without_trusting_it()
    {
        using var served = SelfSigned(new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(tcp.GetStream());

            try
            {
                await tls.AuthenticateAsServerAsync(served);

                // Were the handshake to complete, nothing may follow it.
                return await tls.ReadAsync(new byte[1]);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // The reader refuses the certificate it has just kept.
                return 0;
            }
        });

        using var read = await new TlsEndpointCertificateReader(TimeSpan.FromSeconds(10))
            .ReadAsync(new Uri($"https://127.0.0.1:{port}/sdk"), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(served.Thumbprint, read.Thumbprint);
        Assert.Equal(0, await server);
    }

    [Fact]
    public async Task An_endpoint_that_does_not_answer_gives_no_certificate_rather_than_an_error()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var read = await new TlsEndpointCertificateReader(TimeSpan.FromSeconds(5))
            .ReadAsync(new Uri($"https://127.0.0.1:{port}"), CancellationToken.None);

        Assert.Null(read);
    }
}
