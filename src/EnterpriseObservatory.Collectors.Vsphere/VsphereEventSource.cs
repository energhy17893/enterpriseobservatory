using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>The event calls this collector makes.</summary>
/// <remarks>
/// Separate from the inventory and metric calls for the same reason those two
/// are separate: a different rhythm, and a source that can be tested without a
/// server.
/// </remarks>
public interface IVsphereEventApi
{
    string InstanceId { get; }

    /// <summary>Reads events newer than the mark, or a bounded recent window.</summary>
    Task<EventRead> ReadEventsAsync(EventMark? since, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

/// <summary>One vCenter's event stream.</summary>
/// <remarks>
/// Thin on purpose. The protocol — collector, latest page, backwards to the
/// mark, destroy — lives in <see cref="VsphereClient"/> beside the rest of the
/// wire handling; what this adds is the clock and the translation of a failed
/// call into "could not ask", which is a value rather than an exception so
/// that it can never be mistaken for an empty window. A session or credential
/// fault is the exception: it is thrown, so the runner can apply its rules.
/// </remarks>
public sealed class VsphereEventSource(IVsphereEventApi api, IClock clock) : IEventSource
{
    private readonly IVsphereEventApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string InstanceId => _api.InstanceId;

    public async Task<EventRead> ReadAsync(EventMark? since, CancellationToken cancellationToken)
    {
        try
        {
            return await _api.ReadEventsAsync(since, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (VsphereApiException ex) when (!ReachesTheRunner(ex.Kind))
        {
            return EventRead.CouldNotAsk(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return EventRead.CouldNotAsk("vCenter could not be reached: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether a fault says something about the session or the account rather
    /// than about the event read, and so must reach the runner as a fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before F1 every <see cref="VsphereApiException"/> became "could not
    /// ask". A rejected login then reached the runner as a value, the runner
    /// never saw <see cref="ICollectionFault"/>, and its one-strike rule could
    /// not fire: the same wrong password was presented again next cycle.
    /// </para>
    /// <para>
    /// The same three the observation source lets escape for the same reason.
    /// <c>InvalidLogin</c> maps to <c>AuthenticationRejected</c> and
    /// <c>NoPermission</c> to <c>AuthorizationDenied</c>, both one strike.
    /// <c>NotAuthenticated</c> arrives here only after the client's own
    /// re-login failed; it stays retryable, counted by the breaker. Any other
    /// fault is about this read and is still "could not ask", which the
    /// pipeline counts as a failure.
    /// </para>
    /// </remarks>
    private static bool ReachesTheRunner(VsphereFaultKind kind) =>
        kind is VsphereFaultKind.InvalidLogin
            or VsphereFaultKind.NotAuthenticated
            or VsphereFaultKind.NoPermission;
}
