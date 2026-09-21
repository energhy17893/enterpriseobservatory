using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// How the <c>Collection</c> section of appsettings maps onto
/// <see cref="CollectionPolicy"/>.
/// </summary>
/// <remarks>
/// A separate shape for the same reason <see cref="VsphereEndpointOptions"/>
/// is one: configuration is seconds and counts, not a <see cref="TimeSpan"/>,
/// and validation needs somewhere to live that is not the domain record —
/// <see cref="CollectionPolicy"/> is owned by the collection pipeline's own
/// decision logic (see ADR-0005) and this type only ever reads it for its
/// defaults, never changes how its values are used. See roadmap T2.4.
/// </remarks>
public sealed class CollectionOptions
{
    private static readonly CollectionPolicy Defaults = CollectionPolicy.Default;

    /// <summary>How long one source may take before the cycle moves on without it.</summary>
    public int SourceTimeoutSeconds { get; set; } = (int)Defaults.SourceTimeout.TotalSeconds;

    /// <summary>Attempts after the first, for failures that might be temporary.</summary>
    public int MaxRetries { get; set; } = Defaults.MaxRetries;

    /// <summary>Base delay between attempts; doubled each time, plus jitter.</summary>
    public double RetryBaseDelaySeconds { get; set; } = Defaults.RetryBaseDelay.TotalSeconds;

    /// <summary>Consecutive failed cycles before a source is given a rest.</summary>
    public int CircuitBreakerThreshold { get; set; } = Defaults.CircuitBreakerThreshold;

    /// <summary>How long a source is left alone once its breaker opens.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = (int)Defaults.CircuitBreakerCooldown.TotalSeconds;

    /// <summary>How many sources may be read at once.</summary>
    public int MaxConcurrency { get; set; } = Defaults.MaxConcurrency;

    /// <summary>What is wrong with these values, or empty if nothing is.</summary>
    /// <remarks>
    /// Returns every problem rather than the first, the same reasoning as
    /// <see cref="VsphereEndpointOptions.Validate"/>: starting a service
    /// several times to be told about one bad setting at a time is a small
    /// cruelty that costs nothing to avoid.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SourceTimeoutSeconds <= 0)
        {
            problems.Add("Collection:SourceTimeoutSeconds must be positive.");
        }

        if (MaxRetries < 0)
        {
            problems.Add("Collection:MaxRetries must not be negative.");
        }

        if (RetryBaseDelaySeconds <= 0)
        {
            problems.Add("Collection:RetryBaseDelaySeconds must be positive.");
        }

        if (CircuitBreakerThreshold < 1)
        {
            problems.Add("Collection:CircuitBreakerThreshold must be at least 1.");
        }

        if (CircuitBreakerCooldownSeconds <= 0)
        {
            problems.Add("Collection:CircuitBreakerCooldownSeconds must be positive.");
        }

        if (MaxConcurrency < 1)
        {
            problems.Add("Collection:MaxConcurrency must be at least 1.");
        }

        return problems;
    }

    /// <summary>The domain policy these values describe.</summary>
    public CollectionPolicy ToPolicy() => new()
    {
        SourceTimeout = TimeSpan.FromSeconds(SourceTimeoutSeconds),
        MaxRetries = MaxRetries,
        RetryBaseDelay = TimeSpan.FromSeconds(RetryBaseDelaySeconds),
        CircuitBreakerThreshold = CircuitBreakerThreshold,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds),
        MaxConcurrency = MaxConcurrency,
    };
}
