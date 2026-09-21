namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// The sources to read, as they stand right now.
/// </summary>
/// <remarks>
/// <para>
/// Asked on every cycle rather than resolved once at startup. That is the whole
/// difference between a connection list that lives in a settings file and one a
/// person maintains in the product: the second changes while the service is
/// running, and a monitoring loop holding a list it captured at boot would
/// ignore every change until someone restarted it — silently, which is the
/// worst way for a product to disagree with its own screens.
/// </para>
/// <para>
/// An implementation is expected to hand back the same source objects while
/// nothing has changed. Building a fresh HTTP client every thirty seconds
/// exhausts sockets long before anyone connects the slow decay to this.
/// </para>
/// </remarks>
public interface ISourceRegistry
{
    /// <summary>Sources for the inventory cycle.</summary>
    IReadOnlyList<IInventorySource> Inventory { get; }

    /// <summary>Sources for the observation cycle.</summary>
    IReadOnlyList<IObservationSource> Observations { get; }

    /// <summary>Sources for event collection, read on the inventory rhythm.</summary>
    /// <remarks>
    /// Only connections that can actually be polled. One that cannot is already
    /// reported through the inventory cycle, and reporting it a second time from
    /// here would put two alerts in front of an operator for one fault.
    /// </remarks>
    IReadOnlyList<IEventSource> Events { get; }
}
