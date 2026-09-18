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
                source.ReadAsync,
                static snapshot => snapshot.Failures,
                SourceRunner.Existing(priorHealth, source.InstanceId),
                policy,
                gate,
                cancellationToken))).ConfigureAwait(false);

        return new CollectionCycleResult
        {
            Snapshots = [.. outcomes.Select(o => o.Result).OfType<InventorySnapshot>()],
            Health = [.. outcomes.Select(o => o.Health)],
            CollectionAlerts = [.. outcomes.SelectMany(o => o.CollectionAlerts)],
        };
    }
}
