using System.Net.Sockets;
using System.Security.Authentication;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// Turns a failed SimpliVity connect/login into something a person can act
/// on: the SSL exception .NET raises names only the outermost handshake
/// failure ("The SSL connection could not be established"); the actual
/// cause -- refused, timed out, or a reset mid-handshake -- lives in an
/// <see cref="Exception.InnerException"/> chain that the probe must not
/// swallow.
/// </summary>
internal static class TlsDiagnostics
{
    /// <summary>Every exception in the chain, outermost first, type and message only -- no secrets pass through here.</summary>
    public static IReadOnlyList<string> FormatChain(Exception exception)
    {
        var lines = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            lines.Add($"  {current.GetType().FullName}: {current.Message}");
        }

        return lines;
    }

    /// <summary>
    /// One of "TCP refused", "TCP timeout", "handshake failed", "server
    /// closed", or "unknown" -- whichever the deepest matching exception in
    /// the chain identifies.
    /// </summary>
    public static string Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused:
                        return "TCP refused";
                    case SocketError.TimedOut:
                        return "TCP timeout";
                    case SocketError.ConnectionReset:
                    case SocketError.ConnectionAborted:
                    case SocketError.Shutdown:
                        return "server closed";
                }
            }

            if (current is AuthenticationException)
            {
                return "handshake failed";
            }

            if (current is OperationCanceledException)
            {
                return "TCP timeout";
            }
        }

        return "unknown";
    }
}
