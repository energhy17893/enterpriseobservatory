using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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

        Assert.Equal(builtIn.IntervalShare, fromConfig.IntervalShare);
        Assert.Equal(builtIn.MinimumSourceTimeout, fromConfig.MinimumSourceTimeout);
        Assert.Equal(builtIn.MaxRetries, fromConfig.MaxRetries);
        Assert.Equal(builtIn.RetryBaseDelay, fromConfig.RetryBaseDelay);
        Assert.Equal(builtIn.CircuitBreakerThreshold, fromConfig.CircuitBreakerThreshold);
        Assert.Equal(builtIn.CircuitBreakerCooldown, fromConfig.CircuitBreakerCooldown);
        Assert.Equal(builtIn.MaxConcurrency, fromConfig.MaxConcurrency);
        Assert.Equal(builtIn.MaxRequestsPerSource, fromConfig.MaxRequestsPerSource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    public void An_interval_share_outside_zero_to_one_is_refused(double share)
    {
        // The share of the interval a read may spend: zero reads nothing,
        // more than the whole makes every slow cycle a late one.
        var options = new CollectionOptions { IntervalShare = share };

        Assert.Contains(options.Validate(), p => p.Contains("IntervalShare", StringComparison.Ordinal));
    }

    [Fact]
    public void The_whole_interval_is_an_allowed_share()
    {
        Assert.Empty(new CollectionOptions { IntervalShare = 1 }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-3)]
    public void A_minimum_timeout_below_one_second_is_refused(double seconds)
    {
        var options = new CollectionOptions { MinimumSourceTimeoutSeconds = seconds };

        Assert.Contains(
            options.Validate(), p => p.Contains("MinimumSourceTimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void The_retired_fixed_timeout_key_refuses_to_start_and_names_its_replacements()
    {
        // The timeout is derived from the interval now (T1.1). A fixed value
        // left in a config file would be silently ignored, and an operator who
        // set it would believe it was in force.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:SourceTimeoutSeconds"] = "40" })
            .Build();

        var problem = Assert.Single(CollectionOptions.RetiredKeyProblems(configuration));
        Assert.Contains("Collection:SourceTimeoutSeconds", problem, StringComparison.Ordinal);
        Assert.Contains("Collection:IntervalShare", problem, StringComparison.Ordinal);
        Assert.Contains("Collection:MinimumSourceTimeoutSeconds", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_host_refuses_to_start_with_the_retired_key()
    {
        // Program.cs itself, not the helper: the refusal only protects anyone
        // if the composition root asks for it.
        using var host = new ObservatoryHost();
        using var withOldKey = host.WithWebHostBuilder(b => b.UseSetting("Collection:SourceTimeoutSeconds", "25"));

        var refused = Assert.ThrowsAny<Exception>(() => withOldKey.CreateClient());

        Assert.Contains("Collection:SourceTimeoutSeconds", refused.ToString(), StringComparison.Ordinal);
        Assert.Contains("Collection:IntervalShare", refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_without_the_retired_key_has_no_retired_key_problem()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MaxRetries"] = "3" })
            .Build();

        Assert.Empty(CollectionOptions.RetiredKeyProblems(configuration));
    }

    [Fact]
    public void The_shipped_appsettings_does_not_carry_the_retired_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        Assert.Empty(CollectionOptions.RetiredKeyProblems(configuration));
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

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void A_per_source_request_limit_outside_one_to_eight_is_refused(int limit)
    {
        // F2: default 2, ceiling 8 (F note §8 decision 3). Out of range is
        // refused at startup, the same as every other Collection setting here
        // — not silently clamped to the nearest bound.
        var options = new CollectionOptions { MaxRequestsPerSource = limit };

        Assert.Contains(options.Validate(), p => p.Contains("MaxRequestsPerSource", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void A_per_source_request_limit_of_one_to_eight_is_accepted(int limit)
    {
        Assert.Empty(new CollectionOptions { MaxRequestsPerSource = limit }.Validate());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(1025)]
    public void A_store_queue_budget_outside_16_to_1024_mib_is_refused(int megabytes)
    {
        // F5: refused at startup, not clamped — the same convention as the rest.
        var options = new CollectionOptions { StoreQueueBudgetMegabytes = megabytes };

        Assert.Contains(options.Validate(), p => p.Contains("StoreQueueBudgetMegabytes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void A_store_queue_age_outside_1_to_60_minutes_is_refused(int minutes)
    {
        // Past an hour a dropped row is past vCenter's real-time retention and
        // could not be read again anyway.
        var options = new CollectionOptions { StoreQueueMaxAgeMinutes = minutes };

        Assert.Contains(options.Validate(), p => p.Contains("StoreQueueMaxAgeMinutes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(50_001)]
    public void A_store_write_chunk_outside_1000_to_50000_rows_is_refused(int rows)
    {
        // F5b: the chunk bounds one merge's time; refused, never clamped.
        var options = new CollectionOptions { StoreQueueMaxRowsPerWrite = rows };

        Assert.Contains(options.Validate(), p => p.Contains("StoreQueueMaxRowsPerWrite", StringComparison.Ordinal));
    }

    [Fact]
    public void The_store_queue_defaults_are_64_mib_30_minutes_and_the_measured_chunk()
    {
        var limits = new CollectionOptions().ToStoreQueueLimits();

        Assert.Equal(64L * 1024 * 1024, limits.BudgetBytes);
        Assert.Equal(TimeSpan.FromMinutes(30), limits.MaxAge);
        Assert.Equal(StoreQueueLimits.DefaultMaxRowsPerWrite, limits.MaxRowsPerWrite);
        Assert.Equal(StoreQueueLimits.Default, limits);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var options = new CollectionOptions
        {
            IntervalShare = 0,
            MinimumSourceTimeoutSeconds = 0,
            MaxRetries = -1,
            RetryBaseDelaySeconds = 0,
            CircuitBreakerThreshold = 0,
            CircuitBreakerCooldownSeconds = 0,
            MaxConcurrency = 0,
            MaxRequestsPerSource = 0,
            StoreQueueBudgetMegabytes = 0,
            StoreQueueMaxAgeMinutes = 0,
            StoreQueueMaxRowsPerWrite = 0,
        };

        Assert.Equal(11, options.Validate().Count);
    }

    [Fact]
    public void A_valid_customisation_binds_onto_the_policy_unchanged()
    {
        var options = new CollectionOptions
        {
            IntervalShare = 0.5,
            MinimumSourceTimeoutSeconds = 15,
            MaxRetries = 4,
            RetryBaseDelaySeconds = 2,
            CircuitBreakerThreshold = 3,
            CircuitBreakerCooldownSeconds = 600,
            MaxConcurrency = 16,
            MaxRequestsPerSource = 4,
        };

        Assert.Empty(options.Validate());

        var policy = options.ToPolicy();

        Assert.Equal(0.5, policy.IntervalShare);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.MinimumSourceTimeout);
        Assert.Equal(4, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.RetryBaseDelay);
        Assert.Equal(3, policy.CircuitBreakerThreshold);
        Assert.Equal(TimeSpan.FromSeconds(600), policy.CircuitBreakerCooldown);
        Assert.Equal(16, policy.MaxConcurrency);
        Assert.Equal(4, policy.MaxRequestsPerSource);
    }
}
