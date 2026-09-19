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
        CollectorRole role,
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
                [UnreachableAlert(instanceId, role, prior, backingOff: true)]);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AttemptAsync(instanceId, role, read, reportedFailures, prior, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SourceRunOutcome<TResult>> AttemptAsync<TResult>(
        string instanceId,
        CollectorRole role,
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

                if (ex is ICollectionFault fault && !CollectionFailures.IsWorthRetrying(fault.Kind))
                {
                    // The source has told us asking again cannot help. Asking
                    // anyway is not merely wasteful: three rejected logins a
                    // cycle is how the monitoring account gets locked out, and
                    // the operator is being shown a message that promises this
                    // does not happen.
                    var fatal = FailedFatally(prior, ex, fault.Kind, _clock.UtcNow);

                    return new SourceRunOutcome<TResult>(
                        null, fatal, [UnreachableAlert(instanceId, role, fatal, backingOff: false)]);
                }

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

        var failed = Failed(prior, lastError, _clock.UtcNow);

        return new SourceRunOutcome<TResult>(
            null, failed, [UnreachableAlert(instanceId, role, failed, backingOff: false)]);
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

    /// <summary>Whether this source should be left alone for now.</summary>
    /// <remarks>
    /// <para>
    /// Two earlier readings of this were wrong in the same direction, and both
    /// were found against a live vCenter rather than in a test.
    /// </para>
    /// <para>
    /// It measured the cooldown from the last <em>success</em>, so a source
    /// down for longer than the cooldown was never held off — an endpoint
    /// broken for a day got asked every thirty seconds, which is the precise
    /// behaviour the cooldown was written to prevent. And it refused to open
    /// at all for a source that had never succeeded, so a fresh installation
    /// with a wrong password hammered the directory indefinitely.
    /// </para>
    /// <para>
    /// The cooldown now runs from the last attempt, which is the only clock
    /// that answers "how long since we last bothered them". A source that has
    /// never succeeded is treated no differently: it is still retried, once a
    /// cooldown, so a configuration fixed at noon is picked up by itself.
    /// </para>
    /// </remarks>
    private static bool IsBreakerOpen(CollectorHealth prior, CollectionPolicy policy, DateTimeOffset now)
    {
        if (prior.LastAttemptUtc is not { } lastAttempt)
        {
            return false;
        }

        // A failure the source says cannot clear by retrying gets one strike,
        // not five. Five rejected logins is already an account lockout at
        // vSphere SSO's default policy.
        var threshold = prior.LastFailureKind is { } kind && !CollectionFailures.IsWorthRetrying(kind)
            ? 1
            : policy.CircuitBreakerThreshold;

        if (prior.ConsecutiveFailures < threshold)
        {
            return false;
        }

        return now - lastAttempt < policy.CircuitBreakerCooldown;
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

            // Cleared, not overwritten. This one means "the last attempt failed
            // outright", and the attempt succeeded — leaving the old reason
            // behind would have a working collector still explaining why it
            // could not authenticate yesterday.
            LastFailureDetail = null,

            // All of them. Keeping only failures[0] hid every problem after the
            // first, and against a real estate that meant an entire class of
            // measurement was missing behind a message about something else.
            PartialFailures = Summarise(failures),
            LastAttemptUtc = now,

            // Cleared, or the one-strike rule would outlive the problem: a
            // password corrected at noon would still be treated as rejected.
            LastFailureKind = null,
        };

    private static CollectorHealth Failed(
        CollectorHealth prior, Exception? error, DateTimeOffset now) =>
        prior with
        {
            // Not Critical: we do not know the estate is broken, only that we
            // cannot see it. Claiming more would be fabrication.
            Health = HealthState.Unknown,
            ConsecutiveFailures = prior.ConsecutiveFailures + 1,
            IsBackingOff = false,
            LastFailureDetail = error?.Message ?? "Collection failed.",
            LastSuccessUtc = prior.LastSuccessUtc,
            LastAttemptUtc = now,

            // Not carried forward. These describe what one reachable source
            // could not read; the source was not reached at all this time, so
            // repeating them would be stating something we did not observe.
            PartialFailures = [],

            // An exception nobody classified might be anything, so it stays
            // retryable. Guessing in the other direction would silently stop
            // collecting from a source that was only briefly unwell.
            LastFailureKind = (error as ICollectionFault)?.Kind,
        };

    /// <summary>A failure the source itself says will not clear by retrying.</summary>
    private static CollectorHealth FailedFatally(
        CollectorHealth prior,
        Exception error,
        CollectionFailureKind kind,
        DateTimeOffset now) =>
        prior with
        {
            Health = HealthState.Unknown,
            ConsecutiveFailures = prior.ConsecutiveFailures + 1,
            IsBackingOff = false,
            LastFailureDetail = error.Message,
            LastSuccessUtc = prior.LastSuccessUtc,
            LastAttemptUtc = now,
            LastFailureKind = kind,
            PartialFailures = [],
        };

    /// <summary>
    /// Every distinct thing that could not be read, in a stable order.
    /// </summary>
    /// <remarks>
    /// Deduplicated because one missing counter across two hundred entities is
    /// one problem, not two hundred, and a list that says it two hundred times
    /// is one nobody reads. Ordered so that an unchanged set of problems does
    /// not appear to change every cycle.
    /// </remarks>
    private static IReadOnlyList<PartialFailure> Summarise(
        IReadOnlyList<CollectionFailure> failures) =>
        [.. failures
            .Select(f => new PartialFailure { Kind = f.Kind, Target = f.Target, Detail = f.Detail })
            .DistinctBy(f => (f.Kind, f.Target, f.Detail))
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)];

    private static AlertDefinition UnreachableAlert(
        string instanceId,
        CollectorRole role,
        CollectorHealth health,
        bool backingOff)
    {
        var what = role == CollectorRole.Inventory ? "inventory" : "metrics";

        var detail = backingOff
            ? $"Not being polled: {health.ConsecutiveFailures} consecutive failures, backing off. " +
              $"Last failure: {health.LastFailureDetail}"
            : $"Failed {health.ConsecutiveFailures} consecutive times. " +
              $"Last failure: {health.LastFailureDetail}";

        return new AlertDefinition
        {
            // The role is part of the identity, not decoration. Reading
            // inventory and reading metrics fail independently, and one
            // fingerprint for both would let each cycle resolve the other's
            // alert on every pass.
            Fingerprint = AlertFingerprint.Create(
                "platform",
                "Collector unreachable",
                "Configuration",
                instanceId,
                $"collector-unreachable:{what}"),
            Severity = AlertSeverity.Warning,
            Title = $"Collector unreachable ({what})",
            Description = $"{detail} Everything this source reports on is Unknown, not healthy.",
            Category = "Configuration",
            Source = "platform",
            IsDerived = true,
        };
    }

    internal static CollectorHealth Existing(
        IReadOnlyList<CollectorHealth> health,
        string instanceId,
        CollectorRole role) =>
        health.FirstOrDefault(h =>
            string.Equals(h.InstanceId, instanceId, StringComparison.Ordinal) && h.Role == role)
        ?? new CollectorHealth { InstanceId = instanceId, Role = role, Health = HealthState.Unknown };
}
