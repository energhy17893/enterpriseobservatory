using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>What one inventory cycle produced.</summary>
public sealed record CollectionCycleResult
{
    /// <summary>What each source that answered had to say.</summary>
    public IReadOnlyList<InventorySnapshot> Snapshots { get; init; } = [];

    /// <summary>Source health after this cycle, to be carried into the next.</summary>
    public IReadOnlyList<CollectorHealth> Health { get; init; } = [];

    /// <summary>
    /// Alerts describing the collection itself.
    /// </summary>
    /// <remarks>
    /// Kept apart from the alerts a source reported. "The iLO says a PSU failed"
    /// and "we cannot reach the iLO" are different claims, and conflating them
    /// is how a monitoring system ends up reporting an outage it merely failed
    /// to observe.
    /// </remarks>
    public IReadOnlyList<AlertDefinition> CollectionAlerts { get; init; } = [];
}

/// <summary>
/// Runs inventory sources for one cycle, applying the resilience policy.
/// </summary>
/// <remarks>
/// One source failing never stops the others or the loop. This was right in the
/// previous product and is kept; what is new is that the policy lives in
/// <see cref="SourceRunner"/> rather than being re-decided by each collector.
/// </remarks>
public sealed class InventoryCollectionPipeline(IClock clock)
{
    private readonly SourceRunner _runner = new(clock ?? throw new ArgumentNullException(nameof(clock)));

    public async Task<CollectionCycleResult> RunAsync(
        IReadOnlyList<IInventorySource> sources,
        IReadOnlyList<CollectorHealth> priorHealth,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(priorHealth);
        ArgumentNullException.ThrowIfNull(policy);

        using var gate = new SemaphoreSlim(policy.MaxConcurrency, policy.MaxConcurrency);

        var outcomes = await Task.WhenAll(sources.Select(source =>
            _runner.RunAsync(
                source.InstanceId,
                CollectorRole.Inventory,
                source.ReadAsync,
                static snapshot => snapshot.Failures,
                SourceRunner.Existing(priorHealth, source.InstanceId, CollectorRole.Inventory),
                policy,
                gate,
                cancellationToken))).ConfigureAwait(false);

        return new CollectionCycleResult
        {
            Snapshots =
            [
                .. outcomes.Select(o => o.Result).OfType<InventorySnapshot>().Select(Attribute),
            ],
            Health = [.. outcomes.Select(o => o.Health)],
            CollectionAlerts =
            [
                .. outcomes.SelectMany(o => o.CollectionAlerts)
                    .Select(a => a with { Scope = AlertScopes.Inventory }),
            ],
        };
    }

    /// <summary>
    /// Stamps the provenance a collector cannot be trusted to remember.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both fields are load-bearing and both are invisible when wrong.
    /// <see cref="Entity.SourceInstanceId"/> decides whether an entity may be
    /// treated as vanished; left blank, no entity ever vanishes and a
    /// decommissioned host stays in the graph forever. The alert scope decides
    /// which evaluation may resolve an alert; left blank, the metric cycle
    /// clears every inventory alert thirty seconds after it is raised.
    /// </para>
    /// <para>
    /// Neither failure produces an error, a log line or a visibly odd screen,
    /// so asking each collector to set them correctly would be asking to be
    /// caught out later. The snapshot is already attributed to a source; this
    /// simply propagates that attribution to what is inside it.
    /// </para>
    /// </remarks>
    private static InventorySnapshot Attribute(InventorySnapshot snapshot) => snapshot with
    {
        Entities =
        [
            .. snapshot.Entities.Select(e => e with { SourceInstanceId = snapshot.SourceInstanceId }),
        ],
        Alerts = [.. snapshot.Alerts.Select(a => a with { Scope = AlertScopes.Inventory })],
    };
}
