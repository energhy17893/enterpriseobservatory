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

    /// <summary>
    /// Maps a storage volume identifier to the datastore entity that owns it.
    /// </summary>
    /// <remarks>
    /// A host reports datastore latency with the volume's identifier as the
    /// counter instance, and nothing else in the sample says which datastore
    /// that is. Null means the volume is not one this source has inventoried —
    /// a datastore mounted on a host outside the collected estate, or one
    /// added since the last inventory cycle — and the measurement is dropped
    /// rather than attached to a guess.
    /// </remarks>
    EntityId? ResolveVolume(string volumeIdentifier);

    /// <summary>The name to show for a managed object, or null when unknown.</summary>
    /// <remarks>
    /// Used to label a datastore's series with the host that measured it, so
    /// "slow from one host" and "slow from all of them" can be told apart on
    /// screen. A managed object reference would technically serve and would be
    /// unreadable — nobody diagnoses storage by recognising host-3615.
    /// </remarks>
    string? DisplayNameOf(string moRef);
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

    /// <summary>How long a "nothing is available" answer is taken at its word.</summary>
    /// <remarks>
    /// Long enough that the re-check costs nothing measurable, short enough
    /// that a statistics level someone raised this morning starts producing
    /// data the same day rather than at the next restart.
    /// </remarks>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    /// <summary>When each type the probe dismissed may be tried again.</summary>
    private readonly Dictionary<VsphereEntityType, DateTimeOffset> _nextRecheck = [];

    /// <summary>
    /// Types where the probe said "nothing" and the data disagreed.
    /// </summary>
    /// <remarks>
    /// In memory rather than stored, deliberately. It is an observation about
    /// this session's conversation with one server, and a restart re-reads the
    /// connection anyway — persisting it would mean a vCenter that was fixed
    /// yesterday is still being second-guessed today on the strength of a
    /// disagreement nobody can see.
    /// </remarks>
    private readonly HashSet<VsphereEntityType> _probeProvedWrong = [];

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
            .GetAvailableCounterKeysAsync(moRefs[0], entityType, now, cancellationToken)
            .ConfigureAwait(false);

        var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        // The source supplied nothing at all for this entity type. Reported as
        // one fact rather than as one complaint per counter, because it is one
        // fact: against a live vCenter this produced two separate messages both
        // claiming a counter "requires statistics level 1", which is not a
        // thing that can be true — level 1 is the floor every installation
        // collects at. Two impossible sentences in place of one true one.
        // The probe is trusted, but not indefinitely and not on its own word.
        //
        // When it reports nothing for a whole entity type we stop querying —
        // asking every thirty seconds for an answer that will not change is a
        // monitoring tool making work for the system it monitors. But the probe
        // is also the thing that has been wrong all day, and a plain skip makes
        // that self-confirming: never ask, never learn it was available. So the
        // skip expires. Once an hour the query goes out regardless, and if data
        // comes back the probe has been proven wrong for this type and is not
        // consulted for it again.
        var trustTheProbe = availableSet.Count == 0 && !_probeProvedWrong.Contains(entityType);

        if (trustTheProbe)
        {
            failures.Add(new CollectionFailure
            {
                Kind = CollectionFailureKind.NotConfigured,
                Target = entityType.ToString(),
                Detail =
                    $"This vCenter reports no performance data at all for {entityType} at the " +
                    $"{VsphereIntervals.IntervalSecondsFor(entityType)}s interval, so nothing of " +
                    "that kind can be measured. This is about what the platform keeps, not about " +
                    "which counters the product asked for. The product re-checks hourly rather " +
                    "than taking the answer as permanent.",
            });

            if (_nextRecheck.TryGetValue(entityType, out var due) && now < due)
            {
                return;
            }

            _nextRecheck[entityType] = now + RecheckInterval;
        }

        // A type the probe has been caught out on, or an hourly re-check: use
        // every counter this vCenter actually defines and let the data answer.
        var ignoreTheProbe = availableSet.Count == 0;

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

            if (!ignoreTheProbe && !availableSet.Contains(key))
            {
                // Defined but not being collected. Usually the statistics
                // level — but only when the counter needs a level above the
                // floor. A level-1 counter that is unavailable cannot be
                // explained by the statistics level, and saying so anyway
                // sends an operator to raise a setting that is already high
                // enough. Reported either way: the affected metrics are
                // Unknown, and a product that hides what it cannot see is
                // worse than one that is absent.
                var raisingTheLevelCouldHelp = counter.Level > 1;

                failures.Add(new CollectionFailure
                {
                    Kind = raisingTheLevelCouldHelp
                        ? CollectionFailureKind.InsufficientDetailLevel
                        : CollectionFailureKind.NotConfigured,
                    Target = key,
                    Detail = raisingTheLevelCouldHelp
                        ? $"Counter requires statistics level {counter.Level} for {entityType}; " +
                          "the platform is collecting at a lower level, so this is not measured."
                        : $"This vCenter defines '{key}' but is not keeping it for {entityType}. " +
                          "It needs no statistics level above the default, so raising the level " +
                          "will not help — the platform simply holds no data of this kind.",
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
                    .QueryPerfAsync(batch, entityType, usable, now, cancellationToken)
                    .ConfigureAwait(false);

                var read = ToObservations(samples, fallbackInterval, now).ToList();

                // Data arrived for a type the probe said had none. The probe is
                // wrong here — recorded so the hourly re-check becomes a
                // permanent one, rather than this measuring an hour apart
                // forever while the data was there all along.
                if (ignoreTheProbe && read.Count > 0)
                {
                    _probeProvedWrong.Add(entityType);
                    _nextRecheck.Remove(entityType);
                }

                observations.AddRange(read);
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

            var measuredBy = _targets.DisplayNameOf(entity.EntityMoRef);

            foreach (var value in entity.Values)
            {
                var withInterval = value.Interval > TimeSpan.Zero
                    ? value
                    : value with { Interval = fallbackInterval };

                // Measured on this object, about a different one. The sample
                // arrived under a host because that is where vSphere keeps
                // datastore counters; the instance says which datastore, and
                // filing it under the host would put storage latency on the
                // wrong page entirely.
                if (VsphereCounters.InstanceNamesAnEntity(value.CounterName))
                {
                    if (Reattribute(withInterval, entityId, measuredBy) is { } moved)
                    {
                        yield return moved;
                    }

                    continue;
                }

                yield return new Observation
                {
                    Entity = entityId,
                    Value = withInterval,
                    SampledAtUtc = now,
                    Source = InstanceId,
                };
            }
        }

        Observation? Reattribute(CounterValue value, EntityId measuredOn, string? measuredBy)
        {
            // No instance, nothing to attribute it to. vCenter's aggregate
            // across every volume a host can see is already dropped by the
            // parser; anything else reaching here is a shape we did not expect,
            // and inventing a subject for it would be the worst of the options.
            if (value.Instance.Length == 0 ||
                _targets.ResolveVolume(value.Instance) is not { } datastore)
            {
                return null;
            }

            return new Observation
            {
                Entity = datastore,

                // The instance stops being the volume — that is now the entity
                // — and becomes the host that took the reading. One datastore
                // therefore carries one series per host, which is what lets the
                // product distinguish a volume that is slow from everywhere
                // from one that is slow from a single host. The first is the
                // array or the fabric; the second is that host's HBA, cable or
                // path.
                Value = value with { Instance = measuredBy ?? measuredOn.Value },
                SampledAtUtc = now,
                Source = InstanceId,
            };
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
