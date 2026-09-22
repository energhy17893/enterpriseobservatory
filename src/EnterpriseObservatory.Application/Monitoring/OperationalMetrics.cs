namespace EnterpriseObservatory.Application.Monitoring;

/// <summary>
/// What one role's most recent cycle looked like, for the metrics endpoint
/// (Package D — see docs/feature-roadmap.md "D — Öz-izleme").
/// </summary>
/// <remarks>
/// Everything here is already measured by <see cref="MonitoringCycle"/> and
/// carried on <see cref="MonitoringCycleResult"/>; this only remembers the
/// latest one so a request between cycles has something to read. Per process,
/// not persisted: a restart loses the last cycle's numbers, which is fine —
/// they describe "just now", not history.
/// </remarks>
public sealed record CycleMetricsSnapshot
{
    public DateTimeOffset? AtUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public int TransitionsAppended { get; init; }

    public int AgeClampedToUnknown { get; init; }
}

/// <summary>
/// Holds the last cycle's self-monitoring numbers between requests.
/// </summary>
/// <remarks>
/// A single mutable slot per role rather than a series: the metrics endpoint
/// answers "what did the runner just do", not "chart this over time" — that
/// is what the observation store's own series are for.
/// </remarks>
public interface IOperationalMetricsStore
{
    CycleMetricsSnapshot Inventory { get; }

    CycleMetricsSnapshot Observation { get; }

    void RecordInventory(CycleMetricsSnapshot snapshot);

    void RecordObservation(CycleMetricsSnapshot snapshot);
}

/// <remarks>
/// Each slot is a single reference swapped with <see cref="Volatile"/> rather
/// than guarded by a lock: one worker writes each slot (the inventory loop
/// writes <see cref="Inventory"/>, the observation loop <see cref="Observation"/>,
/// per <see cref="EnterpriseObservatory.Host.AllInOne.MonitoringWorker"/>'s two
/// independent loops) and any number of requests only ever read, so there is
/// nothing to serialise — only a torn read to avoid, and a reference
/// assignment cannot tear.
/// </remarks>
public sealed class OperationalMetricsStore : IOperationalMetricsStore
{
    private CycleMetricsSnapshot _inventory = new();
    private CycleMetricsSnapshot _observation = new();

    public CycleMetricsSnapshot Inventory => Volatile.Read(ref _inventory);

    public CycleMetricsSnapshot Observation => Volatile.Read(ref _observation);

    public void RecordInventory(CycleMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _inventory, snapshot);
    }

    public void RecordObservation(CycleMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _observation, snapshot);
    }
}
