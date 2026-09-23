using System.Runtime.CompilerServices;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Reads a source the way the runner does: through one
/// <see cref="ObservationSourceSlot"/> per source object, so what the source
/// learns in one read is there for the next (F5 — the state is the runner's,
/// not the collector's).
/// </summary>
/// <remarks>
/// No gap store: a test that exercises the gap record builds its own slot
/// with one. A batch's marks move only when a test says it was kept
/// (<see cref="Accept"/>), exactly as the cycle does after the store queue
/// accepts it.
/// </remarks>
internal static class RunnerSlots
{
    private static readonly ConditionalWeakTable<IObservationSource, ObservationSourceSlot> Slots = new();

    public static ObservationSourceSlot SlotOf(IObservationSource source) =>
        Slots.GetValue(source, s => new ObservationSourceSlot(s.InstanceId));

    public static Task<ObservationBatch> ReadAsync(this IObservationSource source, CancellationToken cancellationToken) =>
        SlotOf(source).ReadAsync(source, cancellationToken);

    /// <summary>What the cycle does once the store queue has accepted the batch.</summary>
    public static void Accept(this IObservationSource source, ObservationBatch batch) =>
        SlotOf(source).Accept(batch);
}
