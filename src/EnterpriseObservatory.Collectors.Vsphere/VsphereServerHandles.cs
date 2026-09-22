using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Collects the <see cref="IServerHandle"/>s one read creates, so they can all
/// be given back together once the read is over — however it ended.
/// </summary>
/// <remarks>
/// ADR-0025 §3's "register / dispose" contract, kept inside the vSphere
/// collector (#80 §4): a read registers a handle the instant it creates the
/// server-side object behind it, and <see cref="DisposeAllAsync"/> is the one
/// place that gives every one of them back, each with the same fresh, bounded
/// token — never the read's own, which may already be cancelled.
/// <c>VsphereClient</c> used to send a destroy or cancel request inline, one
/// call at a time, from its own try/finally blocks; this formalises that into
/// one contract so the client itself no longer sends one directly.
/// </remarks>
internal sealed class ServerHandleScope
{
    private readonly List<IServerHandle> _handles = [];

    public void Register(IServerHandle handle) => _handles.Add(handle);

    /// <summary>
    /// Gives every registered handle back, with a fresh token bounded by
    /// <paramref name="grace"/> when the caller's own token is already
    /// cancelled.
    /// </summary>
    /// <remarks>
    /// Never throws: one handle that could not be reached must not stop the
    /// rest from being asked for back, and the caller — a read that may
    /// already be unwinding from its own exception — must not gain a new one
    /// from cleanup.
    /// </remarks>
    public async Task DisposeAllAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        if (_handles.Count == 0)
        {
            return;
        }

        using var freshSource = new CancellationTokenSource(grace);
        var freshToken = cancellationToken.IsCancellationRequested ? freshSource.Token : cancellationToken;

        foreach (var handle in _handles)
        {
#pragma warning disable CA1031 // Justified: see the remarks above -- one failed give-back must not stop the rest.
            try
            {
                await handle.DisposeAsync(freshToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        _handles.Clear();
    }
}

/// <summary>Gives a container view back to its <c>ViewManager</c>.</summary>
internal sealed class VsphereViewHandle(VsphereClient client, string viewMoRef) : IServerHandle
{
    public async ValueTask DisposeAsync(CancellationToken freshToken)
    {
        try
        {
            await client.SendAsync(VsphereSoapRequests.DestroyView(viewMoRef), freshToken).ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Gives a child <c>EventHistoryCollector</c> back to its <c>EventManager</c>.</summary>
internal sealed class VsphereEventCollectorHandle(VsphereClient client, string collectorMoRef) : IServerHandle
{
    public async ValueTask DisposeAsync(CancellationToken freshToken)
    {
        try
        {
            await client.SendAsync(VsphereSoapRequests.DestroyCollector(collectorMoRef), freshToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// Releases a <c>RetrievePropertiesEx</c> paging token the server is still
/// holding a result against.
/// </summary>
internal sealed class VsphereContinuationTokenHandle(
    VsphereClient client, string propertyCollectorMoRef, string token) : IServerHandle
{
    public async ValueTask DisposeAsync(CancellationToken freshToken)
    {
        try
        {
            await client.SendAsync(
                VsphereSoapRequests.CancelRetrievePropertiesEx(propertyCollectorMoRef, token), freshToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
