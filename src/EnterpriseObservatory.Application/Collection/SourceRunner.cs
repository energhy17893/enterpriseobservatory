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
/// Shared by the inventory, observation and (since F1) event pipelines. They read different
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
/// <para>
/// A read that ran out of time is the one exception to "an exception is worth
/// retrying", and for a reason that has nothing to do with the vendor: the
/// runner never stopped it. See
/// <see cref="ReadWithHardTimeoutAsync{TResult}"/> — the read is abandoned, not
/// cancelled, so it is still inside the source, still holding its vCenter
/// session, and may still return. Calling the source again would put two
/// threads inside one instance, which is what
/// <see cref="IObservationSource.ReadAsync"/> says callers must not do.
/// </para>
/// </remarks>
internal sealed class SourceRunner(IClock clock, TimeProvider? timeProvider = null)
{
    private readonly IClock _clock = clock;

    // Elapsed-time tracking (the retry budget, the hard timeout, the retry
    // delay) is a separate axis from IClock's wall-clock UtcNow, and defaults
    // to the system's real timer in production. Tests substitute a
    // FakeTimeProvider so a budget of, say, one second can be crossed without
    // an actual second of wall-clock delay — which is what made these tests
    // flaky under parallel load: real Task.Delay calls raced real timeouts,
    // and CPU contention from other tests skewed both by different amounts.
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

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

        // One budget for the source this cycle, shared by its attempts. Each
        // retry used to get a fresh SourceTimeout, and the cycle waits for every
        // source, so one slow vCenter held a 30-second metric cycle for three
        // timeouts and the retry delays between them.
        var budgetStart = _time.GetTimestamp();

        for (var attempt = 1; attempt <= policy.MaxRetries + 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var left = policy.SourceTimeout - _time.GetElapsedTime(budgetStart);
            if (attempt > 1 && left <= policy.ReturnGrace)
            {
                // Nothing worth starting: the attempt would be asked to stop
                // before it could have done anything.
                break;
            }

            try
            {
                var result = await ReadWithHardTimeoutAsync(
                    instanceId, read, left, policy.ReturnGrace, _time, cancellationToken).ConfigureAwait(false);

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

                if (ex is AbandonedReadException)
                {
                    // Not retried, and not because the vendor said so. The
                    // first read is still running inside the source — we gave
                    // up waiting, we did not stop it — so a second call would
                    // put two threads in one source instance, on the caches
                    // and connections it keeps between reads. Nothing is
                    // gained either: the abandoned read holds the session the
                    // new one would need, and if it was slow because the
                    // vCenter is slow, a second concurrent query is how a slow
                    // vCenter becomes an overloaded one. The cycle is only
                    // 30 seconds long; the next one asks again.
                    break;
                }

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

                if (ex is OutOfTimeException)
                {
                    // The source stopped when asked, so nothing of it is still
                    // running — but the budget it stopped for is spent.
                    break;
                }

                if (attempt <= policy.MaxRetries)
                {
                    // Jitter matters because every source is driven by the same
                    // loop. Without it, twenty iLOs that failed together would
                    // retry together, turning a blip into a stampede.
                    var delay = policy.RetryDelay(attempt, Random.Shared.NextDouble());
                    if (delay >= policy.SourceTimeout - _time.GetElapsedTime(budgetStart) - policy.ReturnGrace)
                    {
                        // Waiting would spend what is left of the budget.
                        break;
                    }

                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
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
    /// <para>
    /// Because the read is still running, giving up here is reported as
    /// <see cref="AbandonedReadException"/> rather than a plain
    /// <see cref="TimeoutException"/>. The caller has to be able to tell "the
    /// source stopped and told us it timed out" — which it may ask again —
    /// from "we walked away from a read that is still in there", which it may
    /// not. A source is free to throw <see cref="TimeoutException"/> itself,
    /// so the type alone cannot carry that distinction.
    /// </para>
    /// <para>
    /// The source is asked to stop <paramref name="grace"/> before the runner
    /// gives up (T1.1). A source that honours the request and returns what it
    /// has read gets that partial result through; before, both happened at
    /// the same instant and the partial result lost the race. A source that
    /// stops by throwing is reported as <see cref="OutOfTimeException"/> —
    /// "did not finish within", not "the operation was canceled".
    /// </para>
    /// </remarks>
    private static async Task<TResult> ReadWithHardTimeoutAsync<TResult>(
        string instanceId,
        Func<CancellationToken, Task<TResult>> read,
        TimeSpan timeout,
        TimeSpan grace,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var cooperative = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var askToStop = timeout - grace;

        // CancellationTokenSource has no instance-level "CancelAfter" that
        // takes a TimeProvider, so the cooperative-cancel deadline is driven
        // by a timer from the provider instead — the same mechanism a fake
        // provider lets a test fire instantly rather than after a real delay.
        //
        // The timer is its own object, separate from `cooperative`, so its
        // callback can still be in flight — already handed to a thread pool
        // thread — at the instant this method returns and the `using`s below
        // dispose `cooperative`. A read that finishes just before the grace
        // deadline hits exactly that: the callback fires a moment later and
        // calls Cancel() on an already-disposed source. That is not this
        // read's problem to report — there is nothing left to cancel — so it
        // is swallowed here rather than crashing the process.
        using var askToStopTimer = time.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            cooperative,
            askToStop > TimeSpan.Zero ? askToStop : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);

        var reading = read(cooperative.Token);
        var expiry = Task.Delay(timeout, time, cancellationToken);

        if (await Task.WhenAny(reading, expiry).ConfigureAwait(false) == reading)
        {
            try
            {
                return await reading.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                cooperative.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new OutOfTimeException(
                    $"Source '{instanceId}' did not finish within {timeout.TotalSeconds:0.#}s.", ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        Forget(reading);

        throw new AbandonedReadException(
            $"Source '{instanceId}' did not respond within {timeout.TotalSeconds:0.#}s.");
    }

    /// <summary>A read the runner walked away from while it was still running.</summary>
    /// <remarks>
    /// Private on purpose. It is not something a source can raise and not
    /// something a caller can handle — it means only "this runner gave up on a
    /// read it could not stop", which nobody outside this class is in a
    /// position to say. It derives from <see cref="TimeoutException"/> so that
    /// the health record and the operator-facing message read exactly as they
    /// did before: from the outside this is still a source that ran out of
    /// time.
    /// </remarks>
    private sealed class AbandonedReadException(string message) : TimeoutException(message);

    /// <summary>A read that stopped when the runner asked it to, having run out of budget.</summary>
    /// <remarks>
    /// Not retried, for the budget's sake rather than for safety: the read has
    /// finished, but the time it would be retried in is gone.
    /// </remarks>
    private sealed class OutOfTimeException(string message, Exception inner) : TimeoutException(message, inner);

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
        var what = RoleLabel(role);

        var detail = backingOff
            ? $"Not being polled: {health.ConsecutiveFailures} consecutive failures, backing off. " +
              $"Last failure: {health.LastFailureDetail}"
            : $"Failed {health.ConsecutiveFailures} consecutive times. " +
              $"Last failure: {health.LastFailureDetail}";

        return new AlertDefinition
        {
            Fingerprint = UnreachableFingerprint(instanceId, role),
            Severity = AlertSeverity.Warning,
            Title = $"Collector unreachable ({what})",
            Description = $"{detail} Everything this source reports on is Unknown, not healthy.",
            Category = "Configuration",
            Source = "platform",
            IsDerived = true,
        };
    }

    /// <summary>The fingerprint of a source's "Collector unreachable" alert in one role.</summary>
    /// <remarks>
    /// The role is part of the identity, not decoration. Reading inventory and
    /// reading metrics fail independently, and one fingerprint for both would
    /// let each cycle resolve the other's alert on every pass. Public to the
    /// cycle so the runner can sign, for every source it attempted, that it
    /// looked (ADR-0026).
    /// </remarks>
    internal static AlertFingerprint UnreachableFingerprint(string instanceId, CollectorRole role) =>
        AlertFingerprint.Create(
            "platform",
            "Collector unreachable",
            "Configuration",
            instanceId,
            $"collector-unreachable:{RoleLabel(role)}");

    /// <summary>How a role reads in an alert title and fingerprint.</summary>
    /// <remarks>
    /// A switch, not "inventory or else metrics": with a third role (events,
    /// F1) the else branch labelled an event read failure as metrics, and gave
    /// it the observation role's fingerprint, so each would resolve the other.
    /// </remarks>
    private static string RoleLabel(CollectorRole role) => role switch
    {
        CollectorRole.Inventory => "inventory",
        CollectorRole.Observation => "metrics",
        CollectorRole.Events => "events",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown collector role."),
    };

    internal static CollectorHealth Existing(
        IReadOnlyList<CollectorHealth> health,
        string instanceId,
        CollectorRole role) =>
        health.FirstOrDefault(h =>
            string.Equals(h.InstanceId, instanceId, StringComparison.Ordinal) && h.Role == role)
        ?? new CollectorHealth { InstanceId = instanceId, Role = role, Health = HealthState.Unknown };
}
