using System.Net.Sockets;
using System.Security.Authentication;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

/// <summary>
/// Proves <see cref="TlsDiagnostics"/> against constructed exception chains
/// -- entirely offline. A live SimpliVity run hit "The SSL connection could
/// not be established, see inner exception", with the inner exception never
/// printed; these tests are the exception-chain formatter's own coverage,
/// the constraint's stand-in for a live TLS failure this tool must not
/// require a network to reproduce.
/// </summary>
public sealed class TlsDiagnosticsTests
{
    [Fact]
    public void Classifies_connection_refused_as_tcp_refused()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);

        Assert.Equal("TCP refused", TlsDiagnostics.Classify(ex));
    }

    [Fact]
    public void Classifies_timed_out_as_tcp_timeout()
    {
        var ex = new SocketException((int)SocketError.TimedOut);

        Assert.Equal("TCP timeout", TlsDiagnostics.Classify(ex));
    }

    [Fact]
    public void Classifies_a_cancelled_operation_as_tcp_timeout()
    {
        var ex = new OperationCanceledException("connect timed out");

        Assert.Equal("TCP timeout", TlsDiagnostics.Classify(ex));
    }

    [Fact]
    public void Classifies_an_authentication_exception_as_handshake_failed()
    {
        var ex = new AuthenticationException("The SSL connection could not be established, see inner exception.");

        Assert.Equal("handshake failed", TlsDiagnostics.Classify(ex));
    }

    [Fact]
    public void Classifies_connection_reset_as_server_closed()
    {
        var ex = new SocketException((int)SocketError.ConnectionReset);

        Assert.Equal("server closed", TlsDiagnostics.Classify(ex));
    }

    [Fact]
    public void Classifies_via_the_deepest_matching_exception_in_the_chain()
    {
        var inner = new SocketException((int)SocketError.TimedOut);
        var outer = new HttpRequestException("wrapped", inner);

        Assert.Equal("TCP timeout", TlsDiagnostics.Classify(outer));
    }

    [Fact]
    public void Unrecognized_exceptions_classify_as_unknown()
    {
        Assert.Equal("unknown", TlsDiagnostics.Classify(new InvalidOperationException("no idea")));
    }

    [Fact]
    public void Formats_the_full_inner_exception_chain_with_types_and_messages_outermost_first()
    {
        var inner = new SocketException((int)SocketError.ConnectionRefused);
        var middle = new AuthenticationException("handshake message", inner);
        var outer = new HttpRequestException("outer message", middle);

        var lines = TlsDiagnostics.FormatChain(outer);

        Assert.Equal(3, lines.Count);
        Assert.Contains("HttpRequestException", lines[0], StringComparison.Ordinal);
        Assert.Contains("outer message", lines[0], StringComparison.Ordinal);
        Assert.Contains("AuthenticationException", lines[1], StringComparison.Ordinal);
        Assert.Contains("handshake message", lines[1], StringComparison.Ordinal);
        Assert.Contains("SocketException", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_a_single_exception_with_no_inner_exception_as_one_line()
    {
        var lines = TlsDiagnostics.FormatChain(new InvalidOperationException("solo"));

        Assert.Single(lines);
        Assert.Contains("solo", lines[0], StringComparison.Ordinal);
    }
}
