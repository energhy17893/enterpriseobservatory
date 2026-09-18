using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>What running one source for one cycle produced.</summary>
internal readonly record struct SourceRunOutcome<TResult>(
    TResult? Result,
    CollectorHealth Health,
    IReadOnlyList<AlertDefinition> CollectionAlerts)
    where TResult : class;

/// <summary>
/// Applies the resilience policy to a single source read.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the inventory and observation pipelines. They read different
/// things on different rhythms (ADR-0005) but must fail identically: a vendor
/// that times out on metrics should behave exactly as one that times out on
/// inventory, and nobody should have to remember to keep two copies in step.
/// </para>
/// <para>
/// Reported failures are taken at face value and not retried — a source that
/// says "authentication rejected" knows, and asking again on every cycle is how
/// an account gets locked out. Exceptions are treated as possibly transient,
/// because an exception is by definition something the source did not
/// anticipate.
/// </para>
/// </remarks>
internal sealed class SourceRunner(IClock clock)
{
    private readonly IClock _clock = clock;

    public async Task<SourceRunOutcome<TResult>> RunAsync<TResult>(
        string instanceId,
        Func<CancellationToken, Task<TResult>> read,
        Func<TResult, IReadOnlyList<CollectionFailure>> reportedFailures,
        CollectorHealth prior,
        CollectionPolicy policy,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
        where TResult : class
    {
        if (IsBreakerOpen(prior, policy, _clock.UtcNow))
        {
            // Left alone deliberately. Reported rather than silently skipped so
            // an operator can tell "we are not looking" from "we looked and it
            // was fine" — those must never be confused.
            return new SourceRunOutcome<TResult>(
                null,
                prior with { IsBackingOff = true, Health = HealthState.Unknown },
                [UnreachableAlert(instanceId, prior, backingOff: true)]);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AttemptAsync(instanceId, read, reportedFailures, prior, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SourceRunOutcome<TResult>> AttemptAsync<TResult>(
        string instanceId,
        Func<CancellationToken, Task<TResult>> read,
        Func<TResult, IReadOnlyList<CollectionFailure>> reportedFailures,
        CollectorHealth prior,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
        where TResult : class
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= policy.MaxRetries + 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await ReadWithHardTimeoutAsync(
                    instanceId, read, policy.SourceTimeout, cancellationToken).ConfigureAwait(false);

                return new SourceRunOutcome<TResult>(
                    result,
                    Succeeded(prior, reportedFailures(result), _clock.UtcNow),
                    []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The whole cycle is shutting down; not this source's fault.
                throw;
            }
#pragma warning disable CA1031 // Justified: a vendor client may throw anything,
            // and one misbehaving integration must not end the cycle for the
            // rest. The exception becomes data rather than being swallowed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                lastError = ex;

                if (attempt <= policy.MaxRetries)
                {
                    // Jitter matters because every source is driven by the same
                    // loop. Without it, twenty iLOs that failed together would
                    // retry together, turning a blip into a stampede.
                    await Task.Delay(policy.RetryDelay(attempt, Random.Shared.NextDouble()), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var failed = Failed(prior, lastError);

        return new SourceRunOutcome<TResult>(
            null, failed, [UnreachableAlert(instanceId, failed, backingOff: false)]);
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
    /// A .NET task cannot be aborted, so an abandoned read keeps running until
    /// it finishes on its own. Its result is discarded and its exception
    /// observed so it cannot resurface elsewhere. The trade is deliberate: a
    /// leaked background read is recoverable, a hung monitoring loop is not.
    /// </para>
    /// </remarks>
    private static async Task<TResult> ReadWithHardTimeoutAsync<TResult>(
        string instanceId,
        Func<CancellationToken, Task<TResult>> read,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cooperative = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cooperative.CancelAfter(timeout);

        var reading = read(cooperative.Token);
        var expiry = Task.Delay(timeout, cancellationToken);

        if (await Task.WhenAny(reading, expiry).ConfigureAwait(false) == reading)
        {
            return await reading.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        Forget(reading);

        throw new TimeoutException(
            $"Source '{instanceId}' did not respond within {timeout.TotalSeconds:0.#}s.");
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
            return false;
        }

        return now - lastSuccess < policy.CircuitBreakerCooldown;
    }

    private static CollectorHealth Succeeded(
        CollectorHealth prior,
        IReadOnlyList<CollectionFailure> failures,
        DateTimeOffset now) =>
        prior with
        {
            // Reaching the source but not reading all of it is degraded, not
            // healthy. Anything named in failures is Unknown, never fine.
            Health = failures.Count == 0 ? HealthState.Healthy : HealthState.Warning,
            LastSuccessUtc = now,
            ConsecutiveFailures = 0,
            IsBackingOff = false,
            LastFailureDetail = failures.Count == 0 ? null : failures[0].Detail,
        };

    private static CollectorHealth Failed(CollectorHealth prior, Exception? error) =>
        prior with
        {
            // Not Critical: we do not know the estate is broken, only that we
            // cannot see it. Claiming more would be fabrication.
            Health = HealthState.Unknown,
            ConsecutiveFailures = prior.ConsecutiveFailures + 1,
            IsBackingOff = false,
            LastFailureDetail = error?.Message ?? "Collection failed.",
            LastSuccessUtc = prior.LastSuccessUtc,
        };

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
            Description = $"{detail} Everything this source reports on is Unknown, not healthy.",
            Category = "Configuration",
            Source = "platform",
            IsDerived = true,
        };
    }

    internal static CollectorHealth Existing(
        IReadOnlyList<CollectorHealth> health,
        string instanceId) =>
        health.FirstOrDefault(h => string.Equals(h.InstanceId, instanceId, StringComparison.Ordinal))
        ?? new CollectorHealth { InstanceId = instanceId, Health = HealthState.Unknown };
}
