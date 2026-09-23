using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// What an inventory-source contract fixture must supply for the shared,
/// source-level half of the collector contract suite (ADR-0025, roadmap
/// T2.1).
/// </summary>
/// <remarks>
/// Unlike <see cref="ITransportContractFixture"/>, this drives the collector's
/// public <c>IInventorySource</c> boundary through the runner
/// (<c>InventoryCollectionPipeline</c>), because the guarantees these cases
/// check — a slow source is abandoned and counted, self-metrics are always
/// produced, long-run state stays bounded — are the runner's contract with
/// <em>any</em> source plugged into it, not something a source can get right
/// or wrong on its own. Running the real vSphere source through it is what
/// proves today's collector actually receives what the runner promises.
/// </remarks>
public interface IInventoryContractFixture
{
    string InstanceId { get; }

    /// <summary>A source that reads <paramref name="hostCount"/> hosts, all clean.</summary>
    IInventorySource CreateHealthy(int hostCount);

    /// <summary>
    /// A source whose read never returns in any reasonable policy timeout —
    /// an uncooperative vendor client that does not observe cancellation
    /// either, which is the case <c>SourceRunner</c>'s hard timeout exists for.
    /// </summary>
    IInventorySource CreateSlow();

    /// <summary>
    /// A source that reads <paramref name="hostCount"/> hosts successfully and
    /// separately reports <paramref name="failureCount"/> distinct read
    /// failures — for the produced = accepted + dropped balance rule.
    /// </summary>
    IInventorySource CreateWithFailures(int hostCount, int failureCount);

    /// <summary>
    /// The read targets <paramref name="snapshot"/> accepted, one per host,
    /// by a key unique within the source.
    /// </summary>
    /// <remarks>
    /// The fixture's to answer, not the suite's: a source that owns its
    /// entities accepts a host as an entity, and one that only annotates an
    /// entity another source owns (ADR-0027) accepts it as an annotation.
    /// Counting <c>EsxiHost</c> entities in the suite made it vSphere's.
    /// </remarks>
    IReadOnlyList<string> AcceptedTargets(InventorySnapshot snapshot);
}
