namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// Opens the database with backoff instead of crashing on the first refused
/// connection.
/// </summary>
/// <remarks>
/// <para>
/// The service was found dead twice this week because PostgreSQL was still
/// starting when this process was — the two are started by the same
/// mechanism and nothing orders one before the other. A bounded retry here is
/// the difference between "came up thirty seconds after the database did"
/// and "needs a human to restart it every reboot." See roadmap T2.4.
/// </para>
/// <para>
/// Bounded, not endless: a database that is not coming back at all — wrong
/// host, wrong credentials, the server itself gone — must still fail loudly
/// rather than retry forever and never say why the product is not running.
/// </para>
/// </remarks>
public static class DatabaseStartupRetry
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Calls <paramref name="open"/>, retrying with <paramref name="delay"/>
    /// between attempts for up to <paramref name="window"/> when it throws
    /// something <paramref name="isTransient"/> says might clear on its own.
    /// </summary>
    /// <remarks>
    /// Synchronous and blocking on purpose: this runs once, before the host
    /// starts doing anything else, from a plain DI factory — there is nothing
    /// else for this thread to do while it waits, and giving it an async
    /// signature would only move the wait, not remove it.
    /// </remarks>
    public static T Open<T>(
        Func<T> open,
        Func<Exception, bool> isTransient,
        TimeSpan window,
        TimeSpan delay,
        Action<int, TimeSpan, Exception> onRetry)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentNullException.ThrowIfNull(onRetry);

        var attempt = 0;
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            attempt++;

            try
            {
                return open();
            }
            catch (Exception ex) when (isTransient(ex))
            {
                var elapsed = DateTimeOffset.UtcNow - started;

                if (elapsed >= window)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL was not reachable after {attempt} attempt(s) over " +
                        $"{elapsed.TotalSeconds:0} seconds. The last error was: {ex.Message}", ex);
                }

                onRetry(attempt, elapsed, ex);

                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }
        }
    }
}
