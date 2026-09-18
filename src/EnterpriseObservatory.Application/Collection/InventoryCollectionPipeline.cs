using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>What one collection cycle produced.</summary>
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
/// <para>
/// One source failing never stops the others or the loop. This was right in the
/// previous product and is kept; what is new is that the policy lives here
/// rather than being re-decided by each collector.
/// </para>
/// <para>
/// Sources report what they could not collect as <see cref="CollectionFailure"/>
/// values; those are taken at face value and not retried, because a source that
/// says "authentication rejected" knows, and asking again will not change the
/// answer. Exceptions are treated as possibly transient and retried, because an
/// exception is by definition something the source did not anticipate.
/// </para>
/// </remarks>
public sealed class InventoryCollectionPipeline(IClock clock)
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<CollectionCycleResult> RunAsync(
        IReadOnlyList<IInventorySource> sources,
        IReadOnlyList<CollectorHealth> priorHealth,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(priorHealth);
        ArgumentNullException.ThrowIfNull(policy);

        var health = priorHealth.ToDictionary(h => h.InstanceId, StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(policy.MaxConcurrency, policy.MaxConcurrency);

        var tasks = sources.Select(source =>
            RunOneAsync(source, Existing(health, source.InstanceId), policy, gate, cancellationToken));

        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        var snapshots = outcomes.Select(o => o.Snapshot).OfType<InventorySnapshot>().ToList();
        var alerts = outcomes.SelectMany(o => o.CollectionAlerts).ToList();

        return new CollectionCycleResult
        {
            Snapshots = snapshots,
            Health = [.. outcomes.Select(o => o.Health)],
            CollectionAlerts = alerts,
        };
    }

    private static CollectorHealth Existing(Dictionary<string, CollectorHealth> health, string instanceId) =>
        health.TryGetValue(instanceId, out var found)
            ? found
            : new CollectorHealth { InstanceId = instanceId, Health = HealthState.Unknown };

    private async Task<SourceOutcome> RunOneAsync(
        IInventorySource source,
        CollectorHealth prior,
        CollectionPolicy policy,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (IsBreakerOpen(prior, policy, now))
        {
            // Left alone deliberately. Reported rather than silently skipped so
            // an operator can tell "we are not looking" from "we looked and it
            // was fine" — those must never be confused.
            return new SourceOutcome(
                Snapshot: null,
                Health: prior with { IsBackingOff = true, Health = HealthState.Unknown },
                CollectionAlerts: [UnreachableAlert(source.InstanceId, prior, backingOff: true)]);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AttemptAsync(source, prior, policy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SourceOutcome> AttemptAsync(
        IInventorySource source,
        CollectorHealth prior,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= policy.MaxRetries + 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await ReadWithHardTimeoutAsync(source, policy.SourceTimeout, cancellationToken)
                    .ConfigureAwait(false);

                return new SourceOutcome(
                    snapshot,
                    Succeeded(prior, snapshot, _clock.UtcNow),
                    CollectionAlerts: []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The whole cycle is shutting down; not this source's fault.
                throw;
            }
#pragma warning disable CA1031 // Justified: a vendor client may throw anything,
            // and one misbehaving integration must not end the cycle for the rest.
            // The exception is turned into data rather than swallowed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                lastError = ex;

                if (attempt <= policy.MaxRetries)
                {
                    var jitter = Random.Shared.NextDouble();
                    await Task.Delay(policy.RetryDelay(attempt, jitter), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var failed = Failed(prior, lastError, _clock.UtcNow);

        return new SourceOutcome(
            Snapshot: null,
            Health: failed,
            CollectionAlerts: [UnreachableAlert(source.InstanceId, failed, backingOff: false)]);
    }

    /// <summary>
    /// Reads a source, giving up after the timeout whether or not it cooperates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requesting cancellation is not enough. A vendor client that ignores the
    /// token — a blocking socket read, a third-party SDK that never threads it
    /// through — would otherwise hold the cycle open indefinitely, which is the
    /// exact failure the timeout exists to prevent.
    /// </para>
    /// <para>
    /// A .NET task cannot be aborted, so an abandoned read keeps running in the
    /// background until it finishes on its own. Its result is discarded and its
    /// exception observed so it cannot resurface elsewhere. The trade is
    /// deliberate: a leaked background read is recoverable, a hung monitoring
    /// loop is not.
    /// </para>
    /// </remarks>
    private static async Task<InventorySnapshot> ReadWithHardTimeoutAsync(
        IInventorySource source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cooperative = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cooperative.CancelAfter(timeout);

        var read = source.ReadAsync(cooperative.Token);
        var expiry = Task.Delay(timeout, cancellationToken);

        if (await Task.WhenAny(read, expiry).ConfigureAwait(false) == read)
        {
            return await read.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        Forget(read);

        throw new TimeoutException(
            $"Source '{source.InstanceId}' did not respond within {timeout.TotalSeconds:0.#}s.");
    }

    private static void Forget(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool IsBreakerOpen(CollectorHealth prior, CollectionPolicy policy, DateTimeOffset now)
    {
        if (prior.ConsecutiveFailures < policy.CircuitBreakerThreshold)
        {
            return false;
        }

        // Never succeeded at all: keep it closed so a source that is merely
        // misconfigured at startup still gets retried once the config is fixed.
        if (prior.LastSuccessUtc is not { } lastSuccess)
        {
            return prior.IsBackingOff;
        }

        return now - lastSuccess < policy.CircuitBreakerCooldown;
    }

    private static CollectorHealth Succeeded(
        CollectorHealth prior,
        InventorySnapshot snapshot,
        DateTimeOffset now) =>
        prior with
        {
            // Reaching the source but not reading all of it is degraded, not
            // healthy. Anything named in Failures is Unknown, never fine.
            Health = snapshot.Failures.Count == 0 ? HealthState.Healthy : HealthState.Warning,
            LastSuccessUtc = now,
            ConsecutiveFailures = 0,
            IsBackingOff = false,
            LastFailureDetail = snapshot.Failures.Count == 0
                ? null
                : snapshot.Failures[0].Detail,
        };

    private static CollectorHealth Failed(
        CollectorHealth prior,
        Exception? error,
        DateTimeOffset now)
    {
        var failures = prior.ConsecutiveFailures + 1;

        return prior with
        {
            // Not Critical: we do not know that the estate is broken, only that
            // we cannot see it. Claiming more would be the fabrication
            // principle 1 forbids.
            Health = HealthState.Unknown,
            ConsecutiveFailures = failures,
            IsBackingOff = false,
            LastFailureDetail = error?.Message ?? "Collection failed.",
            LastSuccessUtc = prior.LastSuccessUtc,
        };
    }

    private static AlertDefinition UnreachableAlert(
        string instanceId,
        CollectorHealth health,
        bool backingOff)
    {
        var detail = backingOff
            ? $"Not being polled: {health.ConsecutiveFailures} consecutive failures, backing off. " +
              $"Last failure: {health.LastFailureDetail}"
            : $"Failed {health.ConsecutiveFailures} consecutive times. " +
              $"Last failure: {health.LastFailureDetail}";

        return new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create(
                "platform", "Collector unreachable", "Configuration", instanceId, "collector-unreachable"),
            Severity = AlertSeverity.Warning,
            Title = "Collector unreachable",
            Description =
                $"{detail} Everything this source reports on is Unknown, not healthy.",
            Category = "Configuration",
            Source = "platform",
            IsDerived = true,
        };
    }

    private sealed record SourceOutcome(
        InventorySnapshot? Snapshot,
        CollectorHealth Health,
        IReadOnlyList<AlertDefinition> CollectionAlerts);
}
