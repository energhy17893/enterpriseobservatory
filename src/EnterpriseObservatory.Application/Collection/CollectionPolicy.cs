namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// How hard, how often and how patiently to try each source.
/// </summary>
/// <remarks>
/// Written down as policy rather than scattered through collectors so that
/// every vendor integration behaves the same without each author deciding
/// again. See ADR-0005.
/// </remarks>
public sealed record CollectionPolicy
{
    /// <summary>
    /// How long one source may take before the cycle moves on without it.
    /// </summary>
    /// <remarks>
    /// The outer bound. A slow vendor must not be able to hold the whole cycle,
    /// because the cost of that is every other vendor's data going stale.
    /// </remarks>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Attempts after the first, for failures that might be temporary.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Base delay between attempts; doubled each time, plus jitter.</summary>
    /// <remarks>
    /// Jitter matters because every source is driven by the same loop. Without
    /// it, twenty iLOs that all failed together would all retry together, which
    /// is how a transient network blip becomes a synchronised stampede.
    /// </remarks>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive failed cycles before a source is given a rest.</summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// How long a source is left alone once its breaker opens.
    /// </summary>
    /// <remarks>
    /// An endpoint that has been down for hours will not be fixed by asking it
    /// again every 30 seconds; all that achieves is log noise and wasted
    /// timeout budget.
    /// </remarks>
    public TimeSpan CircuitBreakerCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many sources may be read at once.</summary>
    /// <remarks>
    /// A cap rather than unlimited parallelism: opening fifty simultaneous
    /// sessions against one vCenter is a good way to be throttled by it, or to
    /// become the reason it is slow.
    /// </remarks>
    public int MaxConcurrency { get; init; } = 8;

    public static CollectionPolicy Default { get; } = new();

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan RetryDelay(int attempt, double jitterFraction) =>
        TimeSpan.FromTicks((long)(RetryBaseDelay.Ticks * Math.Pow(2, attempt - 1) * (1 + jitterFraction)));
}
