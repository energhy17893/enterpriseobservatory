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
    /// It is the whole budget for one source in one cycle, retries included.
    /// The cycle derives it from the interval with <see cref="ForInterval"/>;
    /// the 25 seconds here applies only to a policy never given one.
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
    /// A cap on how many sources one pipeline reads in parallel, not on how
    /// many requests any single source sees at once — that is
    /// <see cref="MaxRequestsPerSource"/>. The two are easy to conflate because
    /// they are both called "concurrency": this one is "sources read in
    /// parallel per pipeline" (F note §3.2).
    /// </remarks>
    public int MaxConcurrency { get; init; } = 8;

    /// <summary>
    /// How many requests one source may have in flight against it at once.
    /// </summary>
    /// <remarks>
    /// Per source, not per cycle (F2, docs/proposals/f-invert-collector-authority.md
    /// §3.2, §8 decision 3). Today one vCenter sees at most a handful of
    /// in-flight requests in normal operation, because each read is sequential
    /// inside the collector; this bounds the pathological case where an
    /// abandoned or overrunning read stacks a second, concurrent one on top of
    /// it against the same vCenter. Default 2, matching the measured basis in
    /// the decision (package A's live timing and the estate's size); the
    /// ceiling is <see cref="SourceRequestGate.MaximumLimit"/>.
    /// </remarks>
    public int MaxRequestsPerSource { get; init; } = SourceRequestGate.DefaultLimit;

    /// <summary>
    /// The share of a collection interval one source may spend being read.
    /// </summary>
    /// <remarks>
    /// Less than the whole: the cycle still has to evaluate and store what was
    /// read before the next one is due, and a read allowed the full interval
    /// makes every slow cycle a late one.
    /// </remarks>
    public double IntervalShare { get; init; } = 0.8;

    /// <summary>The least any source is given, however short its interval.</summary>
    /// <remarks>ADR-0005 §5: derived from the poll interval, at least ten seconds.</remarks>
    public TimeSpan MinimumSourceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static CollectionPolicy Default { get; } = new();

    /// <summary>
    /// This policy, with <see cref="SourceTimeout"/> derived from the interval
    /// the source is read on.
    /// </summary>
    /// <remarks>
    /// T1.1. The timeout used to be one constant, 25 seconds, for metrics read
    /// every 30 seconds and inventory read every five minutes alike — the
    /// inventory of a large estate had a twelfth of its interval to page
    /// through everything. The cycle calls this once per pipeline with that
    /// pipeline's own interval.
    /// </remarks>
    public CollectionPolicy ForInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var share = TimeSpan.FromTicks((long)(interval.Ticks * IntervalShare));

        return this with { SourceTimeout = share > MinimumSourceTimeout ? share : MinimumSourceTimeout };
    }

    /// <summary>
    /// How long before the timeout a source is asked to stop, so what it has
    /// read can come back before the runner stops waiting for it.
    /// </summary>
    /// <remarks>
    /// A tenth of the timeout. Before, the request to stop and the runner
    /// giving up were the same instant, so a source that did stop and hand
    /// back its partial read was discarded anyway — which is how a 2000-VM
    /// read that needed 42 seconds against 25 stored nothing at all rather
    /// than 25 seconds' worth.
    /// </remarks>
    public TimeSpan ReturnGrace => SourceTimeout / 10;

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan RetryDelay(int attempt, double jitterFraction) =>
        TimeSpan.FromTicks((long)(RetryBaseDelay.Ticks * Math.Pow(2, attempt - 1) * (1 + jitterFraction)));
}
