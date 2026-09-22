using System.Globalization;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>
/// Tries a connection once and says what happened.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of how the first live run went. A password was supplied
/// through a shell, the shell altered it before the product ever saw it, and
/// what came back was "vCenter rejected the credentials" — true, unhelpful,
/// and indistinguishable from having typed the wrong password. Someone entering
/// a credential should be able to find out whether it works at the moment they
/// enter it, not four cycles later from an alert.
/// </para>
/// <para>
/// One attempt. No retries, no back-off, no second chance on a different code
/// path. A test button that retries is a faster way to lock the account out
/// than the collector could ever manage, and the collector at least waits
/// thirty seconds between tries.
/// </para>
/// </remarks>
public sealed class VsphereConnectionProbe : IConnectionProbe, ISourceCapabilityReader
{
    /// <summary>How long a test may take before it is called a failure.</summary>
    /// <remarks>
    /// Shorter than the collector's timeout. A person is watching a spinner,
    /// and a connection that takes twenty-five seconds to answer is one they
    /// need to know about anyway.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ConnectionProbeResult> ProbeAsync(
        SourceConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var problems = connection.Validate();

        if (problems.Count > 0)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = string.Join(" ", problems),
            };
        }

        var options = new VsphereConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
            InventoryPageSize = connection.PageSize,
            RequestTimeout = Timeout,
        };

        var handler = VsphereSessionChannel.CreateHandler(options);
        using var channel = new VsphereSessionChannel(handler, options);
        var client = new VsphereClient(channel, options);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            return await ProbeAsync(client, connection, deadline.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // A successful test is a session, and it used to be left on the
            // vCenter: one per press of the button, until the idle timeout.
            // A probe that never signed in sends nothing here.
            await channel.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ConnectionProbeResult> ProbeAsync(
        VsphereClient client,
        SourceConnection connection,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            // Logs in and reads one advanced setting: the cheapest call that
            // proves the credential works rather than merely that the address
            // resolves. Retrieving the counter catalogue would prove more and
            // cost hundreds of rows to say the same thing about the password.
            var maxQueryMetrics = await client
                .GetMaxQueryMetricsAsync(deadline)
                .ConfigureAwait(false);

            return new ConnectionProbeResult
            {
                Succeeded = true,
                Detail = "Signed in and read a setting. The credentials work and the account " +
                         "can read this vCenter.",
                Identified = maxQueryMetrics is { } limit
                    ? string.Create(
                        CultureInfo.InvariantCulture, $"maxQueryMetrics = {limit}")
                    : null,
            };
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.InvalidLogin)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                CredentialsRejected = true,

                // The lockout warning belongs here rather than in a tooltip.
                // The person reading this is one click away from doing it
                // again, and vSphere's default policy gives them five.
                Detail = "vCenter rejected the username or password. Check both before trying " +
                         "again — vSphere locks an account after a few failed sign-ins, and this " +
                         "counts as one of them.",
            };
        }
        catch (VsphereApiException ex) when (ex.Kind == VsphereFaultKind.NoPermission)
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = "The account signed in but is not allowed to read this vCenter. " +
                         $"It needs a read-only role at the top of the inventory. ({ex.Message})",
            };
        }
        catch (HttpRequestException ex)
        {
            // Certificate failures arrive here, and they are worth separating
            // from "the host is down" because the fix is completely different
            // and the message otherwise reads as a network problem.
            var certificate = ex.InnerException is System.Security.Authentication.AuthenticationException;

            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = certificate
                    ? "The TLS certificate was not trusted. Import the vCenter's root CA, or " +
                      $"tick 'accept an untrusted certificate' if that is a considered decision. ({ex.Message})"
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
        // Anything a vendor client can do becomes a sentence on the screen; an
        // unhandled exception here would be a 500 with nothing useful in it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ConnectionProbeResult
            {
                Succeeded = false,
                Detail = ex.Message,
            };
        }
    }

    /// <summary>
    /// Every counter this vCenter defines.
    /// </summary>
    /// <remarks>
    /// The catalogue is the authority on what a name means, and counter ids
    /// differ between installations, so this is read from the server rather
    /// than assumed. Had it been consulted once against a real vCenter, the
    /// product would not have spent its development asking for two counters
    /// that do not exist.
    /// </remarks>
    public async Task<IReadOnlyList<SourceCounter>> CountersAsync(
        SourceConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var options = new VsphereConnectionOptions
        {
            InstanceId = connection.InstanceId,
            BaseAddress = connection.BaseAddress,
            Username = connection.Username,
            Password = connection.Password,
            AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
            InventoryPageSize = connection.PageSize,
        };

        var handler = VsphereSessionChannel.CreateHandler(options);
        using var channel = new VsphereSessionChannel(handler, options);
        var client = new VsphereClient(channel, options);

        IReadOnlyList<VsphereCounter> catalogue;
        try
        {
            catalogue = await client.GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // As above: reading the catalogue signs in, so it signs out.
            await channel.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }

        return
        [
            .. catalogue
                .Select(c => new SourceCounter { Key = c.Key, Unit = c.Unit, Level = c.Level })
                .DistinctBy(c => c.Key)
                .OrderBy(c => c.Key, StringComparer.Ordinal),
        ];
    }
}
