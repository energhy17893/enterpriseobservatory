using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// What an observation-source contract fixture must supply for the shared,
/// source-level half of the collector contract suite (ADR-0025, roadmap
/// T2.1). See <see cref="IInventoryContractFixture"/> for why this drives the
/// source through the runner rather than in isolation.
/// </summary>
public interface IObservationContractFixture
{
    string InstanceId { get; }

    /// <summary>A source that samples <paramref name="targetCount"/> hosts, all clean.</summary>
    IObservationSource CreateHealthy(int targetCount);

    /// <summary>
    /// A source whose read never returns in any reasonable policy timeout.
    /// See <see cref="IInventoryContractFixture.CreateSlow"/>.
    /// </summary>
    IObservationSource CreateSlow();

    /// <summary>
    /// A source sampling two entity types, one of which fails outright — the
    /// per-type granularity <c>VsphereObservationSource</c> actually reports
    /// failures at, for the produced = accepted + dropped balance rule.
    /// </summary>
    IObservationSource CreateWithOneFailingEntityType();
}
