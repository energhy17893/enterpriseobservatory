using EnterpriseObservatory.Host.AllInOne.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

public class DatabaseStartupRetryTests
{
    [Fact]
    public void A_call_that_succeeds_first_time_never_retries()
    {
        var attempts = 0;
        var retries = 0;

        var result = DatabaseStartupRetry.Open(
            open: () => { attempts++; return "ready"; },
            isTransient: _ => true,
            window: TimeSpan.FromSeconds(30),
            delay: TimeSpan.Zero,
            onRetry: (_, _, _) => retries++);

        Assert.Equal("ready", result);
        Assert.Equal(1, attempts);
        Assert.Equal(0, retries);
    }

    [Fact]
    public void A_transient_failure_is_retried_until_it_succeeds()
    {
        var attempts = 0;
        var retries = 0;

        var result = DatabaseStartupRetry.Open(
            open: () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new InvalidOperationException("connection refused");
                }

                return "ready";
            },
            isTransient: _ => true,
            window: TimeSpan.FromSeconds(30),
            delay: TimeSpan.Zero,
            onRetry: (attempt, _, ex) =>
            {
                retries++;
                Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
                Assert.Equal(retries, attempt);
            });

        Assert.Equal("ready", result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retries);
    }

    [Fact]
    public void A_non_transient_failure_is_never_retried()
    {
        // Bad configuration -- a missing password, a bad port -- is
        // permanent. Retrying it for the whole window only delays a message
        // that was already correct on the first attempt.
        var attempts = 0;

        var thrown = Assert.Throws<ArgumentException>(() => DatabaseStartupRetry.Open<string>(
            open: () => { attempts++; throw new ArgumentException("bad options"); },
            isTransient: ex => ex is not ArgumentException,
            window: TimeSpan.FromSeconds(30),
            delay: TimeSpan.Zero,
            onRetry: (_, _, _) => Assert.Fail("should not retry a permanent failure")));

        Assert.Equal(1, attempts);
        Assert.Equal("bad options", thrown.Message);
    }

    [Fact]
    public void A_failure_that_never_clears_gives_up_once_the_window_elapses_with_a_clear_message()
    {
        var attempts = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() => DatabaseStartupRetry.Open<string>(
            open: () =>
            {
                attempts++;
                throw new InvalidOperationException("connection refused");
            },
            isTransient: _ => true,
            window: TimeSpan.Zero,
            delay: TimeSpan.Zero,
            onRetry: (_, _, _) => Assert.Fail("the window is already exhausted on the first attempt")));

        Assert.Equal(1, attempts);
        Assert.Contains("PostgreSQL was not reachable", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1 attempt", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.InnerException);
    }

    [Fact]
    public void The_bound_is_wall_clock_not_attempt_count()
    {
        // A tiny window with a real (if tiny) delay: the second attempt's
        // elapsed time must already be past the window, so there is no third.
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => DatabaseStartupRetry.Open<string>(
            open: () => { attempts++; throw new InvalidOperationException("still down"); },
            isTransient: _ => true,
            window: TimeSpan.FromMilliseconds(20),
            delay: TimeSpan.FromMilliseconds(30),
            onRetry: (_, _, _) => { }));

        Assert.True(attempts is 1 or 2, $"expected 1 or 2 attempts, got {attempts}");
    }
}
