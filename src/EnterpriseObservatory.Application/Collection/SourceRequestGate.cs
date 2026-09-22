namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// Bounds how many requests one source has outstanding against it at once.
/// </summary>
/// <remarks>
/// <para>
/// F2 (docs/proposals/f-invert-collector-authority.md §3.2, §8 decision 3).
/// One instance per source, created once when the source is built and held for
/// its lifetime — <strong>not per cycle</strong>. A gate scoped to a cycle
/// cannot do what this exists for: an abandoned read from a previous cycle is
/// still inside the collector, still holding requests against the vCenter,
/// when the next cycle starts, and a fresh per-cycle gate would let the new
/// cycle's requests stack on top of it without limit.
/// </para>
/// <para>
/// Acquired <strong>around one HTTP exchange only</strong> — never across a
/// whole read, and never across a login nested inside one. A login triggered
/// by <c>NotAuthenticated</c> mid-read is its own acquisition, released before
/// the retried call acquires again, so nothing can deadlock on itself by
/// holding a permit while waiting for another one from the same gate.
/// </para>
/// <para>
/// Distinct from <see cref="CollectionPolicy.MaxConcurrency"/>, which bounds
/// how many <em>sources</em> a pipeline reads in parallel. This bounds how
/// many <em>requests</em> one source sees at once, regardless of how many
/// pipelines or cycles are asking.
/// </para>
/// </remarks>
public sealed class SourceRequestGate : IDisposable
{
    /// <summary>
    /// The default limit: two in-flight requests per source (F note §8
    /// decision 3). Normal operation, where each read is sequential inside the
    /// collector, never reaches this; it bounds the pathological case of a
    /// stacked, abandoned or overrunning read.
    /// </summary>
    public const int DefaultLimit = 2;

    /// <summary>
    /// The highest limit a source may be configured with. Opening this many
    /// simultaneous requests against one vCenter risks being throttled by it,
    /// or becoming the reason it is slow — the same reasoning
    /// <see cref="CollectionPolicy.MaxConcurrency"/> is capped for.
    /// </summary>
    public const int MaximumLimit = 8;

    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public SourceRequestGate(int limit)
    {
        if (limit < 1 || limit > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"A source's request limit must be between 1 and {MaximumLimit}.");
        }

        Limit = limit;
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    /// <summary>How many requests this source may have outstanding at once.</summary>
    public int Limit { get; }

    /// <summary>
    /// Waits for a permit, then hands back something that releases it.
    /// </summary>
    /// <remarks>
    /// The caller must dispose what this returns as soon as the one HTTP
    /// exchange it wraps is done — see the type remarks on why "one exchange,
    /// no more" is the rule that keeps this from being able to deadlock.
    /// </remarks>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Permit(_semaphore);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semaphore.Dispose();
    }

    /// <summary>The held permit; disposing it releases exactly once, even if disposed twice.</summary>
    private sealed class Permit(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
