using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>What one metric cycle produced.</summary>
public sealed record ObservationCycleResult
{
    public IReadOnlyList<ObservationBatch> Batches { get; init; } = [];

    public IReadOnlyList<CollectorHealth> Health { get; init; } = [];

    public IReadOnlyList<AlertDefinition> CollectionAlerts { get; init; } = [];

    /// <summary>Every sample from every source that answered.</summary>
    public IReadOnlyList<Domain.Observation> Observations =>
        [.. Batches.SelectMany(b => b.Observations)];

    /// <summary>Earlier samples to keep; see <see cref="ObservationBatch.Backfill"/>.</summary>
    public IReadOnlyList<Domain.Observation> Backfill =>
        [.. Batches.SelectMany(b => b.Backfill)];
}

/// <summary>
/// Runs observation sources for one cycle.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the inventory pipeline because the two are different rhythms,
/// not because they behave differently when things go wrong — the resilience
/// policy is shared (<see cref="SourceRunner"/>). Inventory is state and only
/// needs reading when it changes; metrics are a time series with a new sample
/// every interval. See ADR-0005.
/// </para>
/// <para>
/// This pipeline additionally turns an
/// <see cref="CollectionFailureKind.InsufficientDetailLevel"/> report into an
/// alert of its own, because it is a configuration problem an operator can fix
/// rather than a transient fault — see the vSphere metric contract §6.1.
/// </para>
/// </remarks>
public sealed class ObservationCollectionPipeline(IClock clock, TimeProvider? timeProvider = null)
{
    private readonly SourceRunner _runner =
        new(clock ?? throw new ArgumentNullException(nameof(clock)), timeProvider);

    public async Task<ObservationCycleResult> RunAsync(
        IReadOnlyList<IObservationSource> sources,
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
                CollectorRole.Observation,
                source.ReadAsync,
                static batch => batch.Failures,
                SourceRunner.Existing(priorHealth, source.InstanceId, CollectorRole.Observation),
                policy,
                gate,
                cancellationToken))).ConfigureAwait(false);

        var batches = outcomes.Select(o => o.Result).OfType<ObservationBatch>().ToList();

        var alerts = outcomes
            .SelectMany(o => o.CollectionAlerts)
            .Concat(batches.SelectMany(DetailLevelAlerts))
            // Stamped here rather than at each producer: an alert with no scope
            // belongs to no evaluation, and the reconciler would resolve it on
            // the next pass of whichever cycle ran. See AlertDefinition.Scope.
            .Select(a => a with { Scope = AlertScopes.Observation })
            .ToList();

        return new ObservationCycleResult
        {
            Batches = batches,
            Health = [.. outcomes.Select(o => o.Health)],
            CollectionAlerts = alerts,
        };
    }

    /// <summary>
    /// Raises an alert per source whose platform is not configured to expose the
    /// counters we asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case this exists for is the vSphere statistics level. At the default
    /// level 1 the disk latency triad — device, kernel and queue — is simply not
    /// collected, and those three are what let the product say <em>which</em>
    /// layer is slow rather than merely that something is. Without them it can
    /// say "disk is slow" but not "the array is slow", which is the blame game
    /// it exists to end.
    /// </para>
    /// <para>
    /// Reported rather than silently tolerated, because the affected metrics are
    /// Unknown and a monitoring product that hides what it cannot see is worse
    /// than one that is absent. This is product principle 1 applied to
    /// configuration.
    /// </para>
    /// </remarks>
    private static IEnumerable<AlertDefinition> DetailLevelAlerts(ObservationBatch batch)
    {
        var insufficient = batch.Failures
            .Where(f => f.Kind == CollectionFailureKind.InsufficientDetailLevel)
            .ToList();

        if (insufficient.Count == 0)
        {
            yield break;
        }

        var targets = string.Join(", ", insufficient.Select(f => f.Target).Distinct().Order());

        yield return new AlertDefinition
        {
            Fingerprint = DetailLevelFingerprint(batch.SourceInstanceId),
            Severity = AlertSeverity.Warning,
            Title = "Platform detail level too low",
            Description =
                $"'{batch.SourceInstanceId}' is not configured to expose: {targets}. " +
                "Those metrics are reported as Unknown until the platform's statistics level is raised. " +
                $"Detail: {insufficient[0].Detail}",
            Category = "Configuration",
            Source = "platform",
            IsDerived = true,
        };
    }

    /// <summary>The fingerprint of a source's "detail level too low" alert, for the cycle's signature (ADR-0026).</summary>
    internal static AlertFingerprint DetailLevelFingerprint(string sourceInstanceId) =>
        AlertFingerprint.Create(
            "platform",
            "Platform detail level too low",
            "Configuration",
            sourceInstanceId,
            "insufficient-detail-level");
}
