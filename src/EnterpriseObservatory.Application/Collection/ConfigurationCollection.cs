using EnterpriseObservatory.Application.Monitoring;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// A source's configuration tier: heavy, slowly changing properties, read on
/// their own cadence and carried by the source into its inventory reads.
/// </summary>
/// <remarks>
/// It returns no entities. What it reads reaches the graph through the next
/// inventory snapshot, so topology, relationships and vanish stay decided by
/// the inventory read alone, and <see cref="Domain.EntityGraph.Merge"/> never
/// sees a second writer for one entity.
/// </remarks>
public interface IConfigurationTierSource
{
    string InstanceId { get; }

    /// <summary>
    /// Reads the configuration. A read cut off by the token returns what it
    /// kept, as an incomplete <see cref="ConfigurationRead"/>, rather than
    /// throwing (ADR-0005: partial progress).
    /// </summary>
    Task<ConfigurationRead> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>What one configuration read reached.</summary>
public sealed record ConfigurationRead
{
    public int ObjectsRead { get; init; }

    public long BytesRead { get; init; }

    public bool Complete { get; init; } = true;

    /// <summary>
    /// Nothing was asked: the carry was younger than the interval. Not a
    /// failure — the collector is up and its configuration current.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>What could not be read, the cut-off included.</summary>
    public IReadOnlyList<CollectionFailure> Failures { get; init; } = [];
}

/// <summary>One configuration pass: each source's health, and what each read reached.</summary>
public sealed record ConfigurationCycleResult
{
    public IReadOnlyList<CollectorHealth> Health { get; init; } = [];

    public IReadOnlyList<(string InstanceId, ConfigurationRead Read)> Reads { get; init; } = [];
}

/// <summary>
/// Runs the configuration tier through <see cref="SourceRunner"/> in the
/// <see cref="CollectorRole.Configuration"/> role: the same budget, breaker,
/// one-strike rule and health record as every other read.
/// </summary>
/// <remarks>
/// Health only, like the event read: its "Collector unreachable" alert is not
/// raised, because the inventory role already raises one for a vCenter that
/// cannot be reached, and a configuration read that fails while inventory
/// answers is the collector_health row's to show. Held for the process's
/// life, so the runner's overlap guard (F2) spans cycles.
/// </remarks>
public sealed class ConfigurationCollectionPipeline(
    IClock clock, ICollectorHealthStore health, TimeProvider? timeProvider = null)
{
    private readonly SourceRunner _runner = new(clock ?? throw new ArgumentNullException(nameof(clock)), timeProvider);
    private readonly ICollectorHealthStore _health = health ?? throw new ArgumentNullException(nameof(health));

    public async Task<ConfigurationCycleResult> RunAsync(
        IReadOnlyList<IConfigurationTierSource> sources,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(policy);

        using var gate = new SemaphoreSlim(policy.MaxConcurrency, policy.MaxConcurrency);
        var prior = _health.Current;

        var outcomes = await Task.WhenAll(sources.Select(async source => (
            source.InstanceId,
            Outcome: await _runner.RunAsync(
                source.InstanceId,
                CollectorRole.Configuration,
                source.ReadAsync,
                static read => read.Failures,
                SourceRunner.Existing(prior, source.InstanceId, CollectorRole.Configuration),
                policy,
                gate,
                cancellationToken,
                extras: static read => new SelfMetricsExtras(ItemsRead: read.Skipped ? null : read.ObjectsRead))
                .ConfigureAwait(false))))
            .ConfigureAwait(false);

        IReadOnlyList<CollectorHealth> health = [.. outcomes.Select(o => o.Outcome.Health)];
        _health.Merge(health);

        return new ConfigurationCycleResult
        {
            Health = health,
            Reads =
            [
                .. outcomes
                    .Where(o => o.Outcome.Result is not null)
                    .Select(o => (o.InstanceId, o.Outcome.Result!)),
            ],
        };
    }
}
