using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne.State;

/// <summary>
/// Keeps the entity graph in memory.
/// </summary>
/// <remarks>
/// <para>
/// Correct for the single-process deployment and deliberately not durable:
/// restarting rebuilds the graph from the next collection. What is lost is the
/// vanished-entity history, which matters and is why a persistent store is
/// coming.
/// </para>
/// <para>
/// The graph itself is immutable, so a reader never sees a half-applied update
/// and no lock is needed around reads.
/// </para>
/// </remarks>
public sealed class InMemoryEntityGraphStore : IEntityGraphStore
{
    private volatile EntityGraph _current = EntityGraph.Empty;

    public EntityGraph Current => _current;

    public void Replace(EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _current = graph;
    }
}

/// <summary>
/// Keeps alert state in memory.
/// </summary>
/// <remarks>
/// Losing this on restart is worse than losing the graph: an acknowledgement or
/// an operator clear would be forgotten, and every still-firing problem would
/// notify again as if it were new. That is the strongest argument for the
/// persistent store, and it is why this type is small and easy to replace.
/// </remarks>
public sealed class InMemoryAlertStateStore : IAlertStateStore
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Dictionary<AlertFingerprint, AlertInstance>> _instances =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<AlertFingerprint, FlapHistory>> _flaps =
        new(StringComparer.Ordinal);

    public IReadOnlyList<AlertInstance> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances.Values.SelectMany(s => s.Values)];
            }
        }
    }

    public IReadOnlyList<AlertInstance> InstancesIn(string scope)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(scope, out var slice) ? [.. slice.Values] : [];
        }
    }

    public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope)
    {
        lock (_gate)
        {
            return _flaps.TryGetValue(scope, out var slice) ? [.. slice.Values] : [];
        }
    }

    public void Apply(string scope, AlertReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            // One scope replaced wholesale, the others untouched. Wholesale
            // because the reconciler already decided what survives and applying
            // its retirements by omission is how resolved alerts would
            // otherwise accumulate forever; one scope because it decided that
            // only for what it evaluated.
            _instances[scope] = result.Instances.ToDictionary(i => i.Fingerprint);
            _flaps[scope] = result.FlapHistories.ToDictionary(f => f.Fingerprint);
        }
    }

    public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        lock (_gate)
        {
            if (!_instances.TryGetValue(scope, out var slice))
            {
                return;
            }

            foreach (var fingerprint in fingerprints)
            {
                // Re-read rather than taking the dispatched copy: reconciliation
                // may have run again while the dispatcher was working, and
                // writing back a stale instance would undo it.
                if (slice.TryGetValue(fingerprint, out var current))
                {
                    slice[fingerprint] = AlertLifecycle.MarkNotified(current);
                }
            }
        }
    }
}

/// <summary>Keeps collector health in memory.</summary>
public sealed class InMemoryCollectorHealthStore : ICollectorHealthStore
{
    private readonly Lock _gate = new();

    // Keyed by source and role together. One vCenter is read by two collectors
    // on two schedules; keyed by address alone, the thirty-second cycle's
    // successes would keep clearing the five-minute cycle's failure count and a
    // persistently broken inventory read would never reach the breaker.
    private readonly Dictionary<(string InstanceId, CollectorRole Role), CollectorHealth> _health = [];

    public IReadOnlyList<CollectorHealth> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _health.Values];
            }
        }
    }

    public void Merge(IReadOnlyList<CollectorHealth> health)
    {
        ArgumentNullException.ThrowIfNull(health);

        lock (_gate)
        {
            // Merged rather than replaced: a cycle only reports on the sources
            // it ran, and the inventory and observation cycles run on different
            // schedules. Replacing would erase the other one's findings.
            foreach (var entry in health)
            {
                _health[(entry.InstanceId, entry.Role)] = entry;
            }
        }
    }
}

/// <summary>The system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
