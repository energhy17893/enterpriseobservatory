using System.Globalization;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Simplivity;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>
/// "Test connection" for a SimpliVity federation: a token and one host row,
/// then the token is given back.
/// </summary>
public sealed class SimplivityConnectionProbe : IConnectionProbe
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

        var options = new SimplivityConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
            RequestTimeout = Timeout,
        };

        using var channel = new SimplivitySessionChannel(SimplivitySessionChannel.CreateHandler(options), options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            using var hosts = await channel.GetAsync("/api/hosts?limit=1", deadline.Token).ConfigureAwait(false);

            return new ConnectionProbeResult
            {
                Succeeded = true,
                Detail = "Took a token and read the host list. The credentials work and the account " +
                         "can read this federation.",
                Identified = hosts.RootElement.TryGetProperty("count", out var count)
                    ? string.Create(CultureInfo.InvariantCulture, $"{count.GetRawText()} hosts")
                    : null,
            };
        }
        catch (SimplivityApiException ex) when (ex.Kind == CollectionFailureKind.AuthenticationRejected)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                CredentialsRejected = true,
                Detail = "The OVC rejected the username or password. It is the vCenter account, so check " +
                         "both before trying again — vSphere locks an account after a few failed sign-ins.",
            };
        }
        catch (HttpRequestException ex)
        {
            var certificate = ex.InnerException is System.Security.Authentication.AuthenticationException;

            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = certificate
                    ? "The TLS handshake failed (certificate or cipher suite). Import the OVC's CA, or tick " +
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
        finally
        {
            // The live OVC answers the revoke 401; the token then idles out in
            // ten minutes. Nothing to report on a test button.
            await channel.RevokeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
