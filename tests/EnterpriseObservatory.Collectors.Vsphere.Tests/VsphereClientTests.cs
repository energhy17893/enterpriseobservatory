using System.Net.Http;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// What the handler the collector talks to every vCenter through is actually
/// configured to do.
/// </summary>
/// <remarks>
/// VsphereEndpointOptionsTests pins the configuration field that says "accept
/// an untrusted certificate". Nothing pinned what the field does,
/// so the field could keep being validated while the code reading it was gone.
/// That is the previous product's failure shape exactly: the mechanism was
/// fine, it simply had a way around it, and nobody was told when it was taken.
/// </remarks>
public class VsphereClientTests
{
    /// <summary>
    /// A connection to a vCenter that does not exist, with a password that is
    /// not one. Nothing here is dialled and nothing is revealed.
    /// </summary>
    private static VsphereConnectionOptions Connection => new()
    {
        BaseAddress = new Uri("https://vc-not-a-real-host.invalid"),
        Username = "svc-readonly@vsphere.local",
        Password = Secret.From("not-a-real-password"),
        InstanceId = "vc-test",
    };

    private static HttpClientHandler Handler(VsphereConnectionOptions options) =>
        Assert.IsType<HttpClientHandler>(VsphereSessionChannel.CreateHandler(options));

    [Fact]
    public void Certificate_validation_stays_on_unless_the_connection_explicitly_asked_for_it()
    {
        // If this guard is ever dropped, every monitored vCenter is talked to
        // with no transport security at all — and silently, because the
        // startup warning about accepting an untrusted certificate is driven
        // by the same flag and would keep quiet for endpoints that never
        // asked. The log would then say the connections are verified while
        // none of them are. A null callback is the framework's own validation.
        using var handler = Handler(Connection with { AcceptUntrustedCertificate = false });

        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void A_connection_that_did_ask_for_it_gets_the_relaxed_validation_it_asked_for()
    {
        // The other direction matters just as much. vCenter ships with a
        // self-signed certificate that many installations never replace, so a
        // guard that quietly refused to honour the recorded decision would
        // make the product unusable against a normal estate — and the
        // pressure would go straight into turning validation off globally,
        // which is worse than the setting it replaced.
        using var handler = Handler(Connection with { AcceptUntrustedCertificate = true });

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void The_decision_is_taken_per_connection_and_not_once_for_the_estate()
    {
        // An estate usually has one vCenter with a replaced certificate and
        // one without. If the relaxation ever leaked from the connection that
        // recorded it to the ones that did not, ticking the box on a lab
        // vCenter would silently downgrade production, and nothing on screen
        // would distinguish the two.
        using var strict = Handler(Connection with { AcceptUntrustedCertificate = false });
        using var relaxed = Handler(Connection with { AcceptUntrustedCertificate = true });

        Assert.Null(strict.ServerCertificateCustomValidationCallback);
        Assert.NotNull(relaxed.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void The_handler_keeps_cookies_because_that_is_where_the_vCenter_session_lives()
    {
        // vCenter tracks an authenticated session with the vmware_soap_session
        // cookie and nothing else. With cookie handling off, every request
        // after Login arrives unauthenticated: the client would re-login on
        // each call, which is both a collection outage and a stream of failed
        // or churning sessions against the monitoring account.
        using var handler = Handler(Connection);

        Assert.True(handler.UseCookies);
        Assert.NotNull(handler.CookieContainer);
    }

    [Fact]
    public void Each_connection_gets_its_own_cookie_jar()
    {
        // One shared container across vCenters would let one instance's
        // session cookie be sent to another instance — a credential-bearing
        // token crossing a trust boundary it was never issued for, and
        // collection attributed to the wrong estate when it is accepted.
        using var first = Handler(Connection);
        using var second = Handler(Connection with { InstanceId = "vc-other" });

        Assert.NotSame(first.CookieContainer, second.CookieContainer);
    }

    [Fact]
    public void A_handler_cannot_be_built_without_a_connection()
    {
        // The alternative is a NullReferenceException deep inside the first
        // request, which reads as "the vCenter is unreachable" rather than
        // "nothing was configured" — and an endpoint that looks unreachable
        // gets ignored rather than fixed.
        Assert.Throws<ArgumentNullException>(() => VsphereSessionChannel.CreateHandler(null!));
    }
}
