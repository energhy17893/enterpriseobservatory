using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Host.AllInOne.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

public class CollectionOptionsTests
{
    [Fact]
    public void Untouched_defaults_have_nothing_wrong_with_them()
    {
        Assert.Empty(new CollectionOptions().Validate());
    }

    [Fact]
    public void Untouched_defaults_produce_the_same_policy_as_CollectionPolicy_Default()
    {
        // The whole point of T2.4: an installation that sets nothing under
        // Collection behaves exactly as it did before the section existed.
        var fromConfig = new CollectionOptions().ToPolicy();
        var builtIn = CollectionPolicy.Default;

        Assert.Equal(builtIn.SourceTimeout, fromConfig.SourceTimeout);
        Assert.Equal(builtIn.MaxRetries, fromConfig.MaxRetries);
        Assert.Equal(builtIn.RetryBaseDelay, fromConfig.RetryBaseDelay);
        Assert.Equal(builtIn.CircuitBreakerThreshold, fromConfig.CircuitBreakerThreshold);
        Assert.Equal(builtIn.CircuitBreakerCooldown, fromConfig.CircuitBreakerCooldown);
        Assert.Equal(builtIn.MaxConcurrency, fromConfig.MaxConcurrency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_source_timeout_is_refused(int seconds)
    {
        var options = new CollectionOptions { SourceTimeoutSeconds = seconds };

        Assert.Contains(options.Validate(), p => p.Contains("SourceTimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void A_negative_retry_count_is_refused()
    {
        var options = new CollectionOptions { MaxRetries = -1 };

        Assert.Contains(options.Validate(), p => p.Contains("MaxRetries", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2.5)]
    public void A_non_positive_retry_delay_is_refused(double seconds)
    {
        var options = new CollectionOptions { RetryBaseDelaySeconds = seconds };

        Assert.Contains(options.Validate(), p => p.Contains("RetryBaseDelaySeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void A_circuit_breaker_threshold_below_one_is_refused()
    {
        var options = new CollectionOptions { CircuitBreakerThreshold = 0 };

        Assert.Contains(options.Validate(), p => p.Contains("CircuitBreakerThreshold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_positive_cooldown_is_refused()
    {
        var options = new CollectionOptions { CircuitBreakerCooldownSeconds = 0 };

        Assert.Contains(
            options.Validate(), p => p.Contains("CircuitBreakerCooldownSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void A_concurrency_below_one_is_refused()
    {
        var options = new CollectionOptions { MaxConcurrency = 0 };

        Assert.Contains(options.Validate(), p => p.Contains("MaxConcurrency", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var options = new CollectionOptions
        {
            SourceTimeoutSeconds = 0,
            MaxRetries = -1,
            RetryBaseDelaySeconds = 0,
            CircuitBreakerThreshold = 0,
            CircuitBreakerCooldownSeconds = 0,
            MaxConcurrency = 0,
        };

        Assert.Equal(6, options.Validate().Count);
    }

    [Fact]
    public void A_valid_customisation_binds_onto_the_policy_unchanged()
    {
        var options = new CollectionOptions
        {
            SourceTimeoutSeconds = 40,
            MaxRetries = 4,
            RetryBaseDelaySeconds = 2,
            CircuitBreakerThreshold = 3,
            CircuitBreakerCooldownSeconds = 600,
            MaxConcurrency = 16,
        };

        Assert.Empty(options.Validate());

        var policy = options.ToPolicy();

        Assert.Equal(TimeSpan.FromSeconds(40), policy.SourceTimeout);
        Assert.Equal(4, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.RetryBaseDelay);
        Assert.Equal(3, policy.CircuitBreakerThreshold);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.CircuitBreakerCooldown);
        Assert.Equal(16, policy.MaxConcurrency);
    }
}
