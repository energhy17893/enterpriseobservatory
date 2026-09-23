using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Redfish;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>"Test connection" for an iLO: one <c>GET Systems/1</c> with Basic auth.</summary>
public sealed class RedfishConnectionProbe : IConnectionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ConnectionProbeResult> ProbeAsync(
        SourceConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var problems = connection.Validate();

        if (problems.Count > 0)
        {
            return new ConnectionProbeResult { Succeeded = false, Detail = string.Join(" ", problems) };
        }

        var options = new RedfishConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
            RequestTimeout = Timeout,
        };

        using var channel = new RedfishChannel(RedfishChannel.CreateHandler(options), options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            using var system = await channel.GetAsync(RedfishInventorySource.SystemPath, deadline.Token).ConfigureAwait(false);

            return new ConnectionProbeResult
            {
                Succeeded = true,
                Detail = "Read Systems/1. The credentials work and the account can read this iLO.",
                Identified = system.RootElement.TryGetProperty("Model", out var model) ? model.GetString() : null,
            };
        }
        catch (RedfishApiException ex) when (ex.Kind == CollectionFailureKind.AuthenticationRejected)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                CredentialsRejected = true,
                Detail = "The iLO rejected the username or password. Check both before trying again — " +
                         "iLO delays logins after repeated failures.",
            };
        }
        catch (HttpRequestException ex)
        {
            var certificate = ex.InnerException is System.Security.Authentication.AuthenticationException;

            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = certificate
                    ? "The TLS handshake failed (certificate or cipher suite). Import the iLO's CA, or tick " +
                      $"'accept an untrusted certificate' if that is a considered decision. ({ex.Message})"
                    : $"Could not reach {connection.BaseAddress.Host}. ({ex.Message})",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = $"No answer within {Timeout.TotalSeconds:0} seconds.",
            };
        }
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ConnectionProbeResult { Succeeded = false, Detail = ex.Message };
        }
    }
}
