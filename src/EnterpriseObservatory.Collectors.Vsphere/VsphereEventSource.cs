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
/// that it can never be mistaken for an empty window.
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
        catch (VsphereApiException ex)
        {
            return EventRead.CouldNotAsk(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return EventRead.CouldNotAsk("vCenter could not be reached: " + ex.Message);
        }
    }
}
