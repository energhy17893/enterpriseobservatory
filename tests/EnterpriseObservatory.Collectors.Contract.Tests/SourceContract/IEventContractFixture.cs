using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// What an event-source contract fixture must supply for the source-level
/// half of the collector contract suite (ADR-0025, F1).
/// </summary>
/// <remarks>
/// Driven through <see cref="EventCollectionPipeline"/>, because since F1 the
/// event read takes the same guarded handle as inventory and observation
/// (ADR-0025 §1): the breaker, the one-strike rule and the hard timeout are
/// the runner's contract with any event source plugged into it.
/// </remarks>
public interface IEventContractFixture
{
    string InstanceId { get; }

    /// <summary>
    /// A source whose every read is refused because the credential is wrong,
    /// the way the vendor's own client reports it, and how many times it was
    /// actually asked.
    /// </summary>
    (IEventSource Source, Func<int> Calls) CreateRejectingLogin();

    /// <summary>
    /// A source whose read never returns within any reasonable deadline and
    /// does not observe cancellation either.
    /// </summary>
    IEventSource CreateSlow();
}
