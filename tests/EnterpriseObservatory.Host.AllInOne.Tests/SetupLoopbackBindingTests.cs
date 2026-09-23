using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EnterpriseObservatory.Host.AllInOne.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The setup host on real Kestrel: bound to loopback and to nothing else, even
/// when configuration asks for every interface.
/// </summary>
/// <remarks>
/// <para>
/// A test server cannot answer this — it has no sockets. So the setup host is
/// composed with the same two calls Program.cs makes
/// (<see cref="FirstRunSetup.Configure"/> and <see cref="FirstRunSetup.Map"/>)
/// and started on real sockets, with configuration that tries both ways a
/// normal installation widens its binding: <c>urls</c> (<c>ASPNETCORE_URLS</c>)
/// and a <c>Kestrel:Endpoints</c> entry, each on 0.0.0.0.
/// </para>
/// <para>
/// Then a caller from another machine is simulated the only honest way a
/// single machine can: by connecting to this machine's own non-loopback
/// address, which is exactly the address another machine would use. It must
/// not connect at all.
/// </para>
/// </remarks>
public sealed class SetupLoopbackBindingTests : IAsyncLifetime
{
    private readonly string _keyRing = SetupTestSupport.TempDirectory("eo-setup-bind-keys-");
    private readonly int _port = FreePort();
    private readonly int _configuredEndpointPort = FreePort();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = _keyRing,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://0.0.0.0:{_port}",
            ["Kestrel:Endpoints:Everywhere:Url"] = $"http://0.0.0.0:{_configuredEndpointPort}",
        });

        FirstRunSetup.Configure(builder, _keyRing);

        _app = builder.Build();
        FirstRunSetup.Map(_app);

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        SetupTestSupport.DeleteQuietly(_keyRing);
    }

    private ICollection<string> BoundAddresses() =>
        _app!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;

    [Fact]
    public void Setup_listens_on_the_configured_port_on_loopback_and_nowhere_else()
    {
        var bound = BoundAddresses();

        Assert.NotEmpty(bound);
        Assert.Contains($"http://127.0.0.1:{_port}", bound);
        Assert.All(bound, address => Assert.True(
            FirstRunSetup.IsLoopbackAddress(address), $"Setup is bound to {address}."));
        Assert.DoesNotContain(bound, address => address.Contains($":{_configuredEndpointPort}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_local_browser_reaches_setup()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        var response = await client.GetAsync("/api/setup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_caller_from_another_machine_cannot_connect_at_all()
    {
        var external = NonLoopbackAddress();

        Skip.If(external is null, "This machine has no non-loopback IPv4 address to connect through.");

        // The positive control first: the port is open, on loopback.
        using (var local = new TcpClient())
        {
            await local.ConnectAsync(IPAddress.Loopback, _port);
            Assert.True(local.Connected);
        }

        using var remote = new TcpClient();

        var refused = await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await remote.ConnectAsync(external!, _port, timeout.Token);
        });

        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    [Fact]
    public async Task The_endpoint_configuration_asked_for_is_not_bound_even_on_loopback()
    {
        using var client = new TcpClient();

        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(IPAddress.Loopback, _configuredEndpointPort, timeout.Token);
        });
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress? NonLoopbackAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
}
