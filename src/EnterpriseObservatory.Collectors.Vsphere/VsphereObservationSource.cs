using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>Supplies which entities to sample this cycle.</summary>
/// <remarks>
/// The observation source does not discover inventory — that is the inventory
/// source's job, on its own rhythm (ADR-0005). It is told what exists and
/// samples it.
/// </remarks>
public interface IVsphereSampleTargetProvider
{
    VsphereSampleTargets Current { get; }

    /// <summary>Maps a managed object reference to the entity it resolved to.</summary>
    EntityId? ResolveEntity(string moRef);
}

/// <summary>
/// Reads vSphere performance counters for one vCenter.
/// </summary>
/// <remarks>
/// <para>
/// Everything hard about this is in the orchestration rather than the
/// transport: sizing queries so the server accepts them, telling "the platform
/// is not configured to give us this" apart from "the platform is broken", and
/// never returning a number whose meaning we cannot vouch for.
/// </para>
/// <para>
/// See docs/collectors/vsphere-metric-contract.md.
/// </para>
/// </remarks>
public sealed class VsphereObservationSource(
    IVsphereApi api,
    IVsphereSampleTargetProvider targets,
    IClock clock) : IObservationSource
{
    private readonly IVsphereApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IVsphereSampleTargetProvider _targets =
        targets ?? throw new ArgumentNullException(nameof(targets));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string InstanceId => _api.InstanceId;

    public async Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var observations = new List<Observation>();
        var failures = new List<CollectionFailure>();

        var catalog = await _api.GetCounterCatalogAsync(cancellationToken).ConfigureAwait(false);

        // Not ToDictionary: a counter key is not unique, and building one
        // directly throws on a real catalogue. See VsphereCounterIndex.
        var byKey = VsphereCounterIndex.ByKey(catalog);

        var maxQueryMetrics = await _api.GetMaxQueryMetricsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (entityType, moRefs) in _targets.Current.ByType())
        {
            await ReadTypeAsync(
                entityType, moRefs, byKey, maxQueryMetrics, now,
                observations, failures, cancellationToken).ConfigureAwait(false);
        }

        return new ObservationBatch
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Observations = observations,
            Failures = failures,
        };
    }

    private async Task ReadTypeAsync(
        VsphereEntityType entityType,
        IReadOnlyList<string> moRefs,
        IReadOnlyDictionary<string, VsphereCounter> catalog,
        int? maxQueryMetrics,
        DateTimeOffset now,
        List<Observation> observations,
        List<CollectionFailure> failures,
        CancellationToken cancellationToken)
    {
        var wanted = VsphereCounters.For(entityType);
        if (wanted.Count == 0 || moRefs.Count == 0)
        {
            return;
        }

        // Ask one representative entity what it can actually supply. Counter
        // availability is a property of the platform's statistics level, not of
        // the individual object, so one probe answers for the type — and one
        // probe per cycle is affordable where one per entity would not be.
        var available = await _api
            .GetAvailableCounterKeysAsync(moRefs[0], entityType, cancellationToken)
            .ConfigureAwait(false);

        var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        var usable = new List<VsphereCounter>();
        foreach (var key in wanted)
        {
            if (!catalog.TryGetValue(key, out var counter))
            {
                // The counter does not exist on this vCenter at all. Worth
                // saying whose problem that is: raising a statistics level
                // cannot conjure a counter the server has never heard of, so
                // an operator reading this should not go looking in settings.
                // It is either a version difference or — as it was for
                // datastore and virtual disk latency — a name this product got
                // wrong and never checked against a real catalogue.
                failures.Add(new CollectionFailure
                {
                    Kind = CollectionFailureKind.ProtocolError,
                    Target = key,
                    Detail =
                        $"This vCenter has no counter called '{key}' for {entityType}, so it is " +
                        "not being measured. Changing the statistics level will not help: either " +
                        "this vCenter version does not provide it, or the product is asking for " +
                        "the wrong name.",
                });
                continue;
            }

            if (!availableSet.Contains(key))
            {
                // Defined but not being collected: the statistics level is too
                // low. Reported rather than tolerated, because the affected
                // metrics are Unknown and a monitoring product that hides what
                // it cannot see is worse than one that is absent.
                failures.Add(new CollectionFailure
                {
                    Kind = CollectionFailureKind.InsufficientDetailLevel,
                    Target = key,
                    Detail =
                        $"Counter requires statistics level {counter.Level} for {entityType}; " +
                        "the platform is not currently collecting it.",
                });
                continue;
            }

            usable.Add(counter);
        }

        if (usable.Count == 0)
        {
            return;
        }

        var sizer = new AdaptiveBatchSizer(maxQueryMetrics, usable.Count);
        var fallbackInterval = TimeSpan.FromSeconds(VsphereIntervals.IntervalSecondsFor(entityType));

        var remaining = new Queue<string>(moRefs);
        while (remaining.Count > 0)
        {
            var batch = Dequeue(remaining, sizer.Current);

            try
            {
                var samples = await _api
                    .QueryPerfAsync(batch, entityType, usable, cancellationToken)
                    .ConfigureAwait(false);

                observations.AddRange(ToObservations(samples, fallbackInterval, now));
            }
            catch (VsphereQuerySizeRefusedException ex)
            {
                if (!sizer.Reduce())
                {
                    // Already down to one entity: the size is not the problem,
                    // so retrying forever would be a loop rather than a fix.
                    failures.Add(new CollectionFailure
                    {
                        Kind = CollectionFailureKind.ProtocolError,
                        Target = $"{entityType} performance query",
                        Detail = $"Refused even for a single entity: {ex.Message}",
                    });
                    return;
                }

                // Put the batch back and try again smaller. The server told us
                // what it will accept; believing it is cheaper than guessing.
                Requeue(remaining, batch);
            }
        }

        if (sizer.WasReduced)
        {
            failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.ProtocolError,
                Target = $"{entityType} performance query",
                Detail =
                    $"The server refused the initial query size; reduced to {sizer.Current} entities " +
                    "per query. Samples were collected, but more slowly than planned.",
            });
        }
    }

    private IEnumerable<Observation> ToObservations(
        IReadOnlyList<PerfEntitySamples> samples,
        TimeSpan fallbackInterval,
        DateTimeOffset now)
    {
        foreach (var entity in samples)
        {
            // A sample we cannot attribute to an entity is dropped rather than
            // attached to a guess. An unattributed number is worse than no
            // number: it looks like knowledge.
            if (_targets.ResolveEntity(entity.EntityMoRef) is not { } entityId)
            {
                continue;
            }

            foreach (var value in entity.Values)
            {
                yield return new Observation
                {
                    Entity = entityId,
                    Value = value.Interval > TimeSpan.Zero ? value : value with { Interval = fallbackInterval },
                    SampledAtUtc = now,
                    Source = InstanceId,
                };
            }
        }
    }

    private static List<string> Dequeue(Queue<string> queue, int count)
    {
        var batch = new List<string>(Math.Min(count, queue.Count));
        while (batch.Count < count && queue.Count > 0)
        {
            batch.Add(queue.Dequeue());
        }

        return batch;
    }

    private static void Requeue(Queue<string> queue, List<string> batch)
    {
        var rest = queue.ToList();
        queue.Clear();

        foreach (var item in batch.Concat(rest))
        {
            queue.Enqueue(item);
        }
    }
}
