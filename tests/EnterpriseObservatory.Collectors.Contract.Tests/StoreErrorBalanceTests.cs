namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The one case from §10.2's "asgari sözleşme vakaları" this suite does not
/// exercise: "depo hatasında kayıp ve çift yok" (a store error loses nothing
/// and duplicates nothing).
/// </summary>
/// <remarks>
/// <para>
/// Not a defect in today's vSphere collector — there is nothing in this
/// codebase to point the case at yet. A source's contract ends at
/// <c>InventorySnapshot</c> / <c>ObservationBatch</c>; nothing between there
/// and a collector reads or calls a store synchronously. The bounded,
/// age-and-size-limited queue in front of the store port, and the runner
/// writing through it, are ADR-0025 §6 — a decision the ADR explicitly does
/// not make yet ("disk taşması ayrı bir karardır ve bu ADR onu vermiyor").
/// </para>
/// <para>
/// This is left as a documented, skipped placeholder rather than omitted
/// silently, so the case is not lost between now and whenever the queue
/// lands. Once <c>CollectionPorts</c> gains a bounded store queue, this case
/// belongs alongside <see cref="InventoryContractTests{TFixture}"/> /
/// <see cref="ObservationContractTests{TFixture}"/>'s balance-rule cases:
/// inject a store failure mid-write and assert every produced record is
/// still exactly once accepted or once dropped, never both, never neither.
/// </para>
/// </remarks>
public sealed class StoreErrorBalanceTests
{
    [Fact(Skip =
        "No store call exists inside the collector or pipeline boundary yet (ADR-0025 §6, " +
        "the bounded store queue, is not implemented). Nothing to inject a store failure into. " +
        "Revisit when the queue lands.")]
    public void A_store_error_loses_nothing_and_duplicates_nothing()
    {
    }
}
