using EnterpriseObservatory.Application.Collection;
using Microsoft.Extensions.Configuration;

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

    /// <summary>
    /// The share of its collection interval one source may spend being read.
    /// </summary>
    /// <remarks>
    /// Replaces the fixed <c>SourceTimeoutSeconds</c>: the timeout is derived
    /// from the interval (T1.1, <see cref="CollectionPolicy.ForInterval"/>).
    /// In (0, 1].
    /// </remarks>
    public double IntervalShare { get; set; } = Defaults.IntervalShare;

    /// <summary>The least any source is given, however short its interval. At least 1 s.</summary>
    public double MinimumSourceTimeoutSeconds { get; set; } = Defaults.MinimumSourceTimeout.TotalSeconds;

    /// <summary>A key this section no longer reads, and what replaced it.</summary>
    private const string RetiredSourceTimeoutKey = "Collection:SourceTimeoutSeconds";

    /// <summary>
    /// Keys under <c>Collection</c> that no longer mean anything, as startup problems.
    /// </summary>
    /// <remarks>
    /// The fixed timeout is gone — it is derived from each pipeline's interval
    /// — and binding would silently drop it. An operator who set it would
    /// believe it was in force, so its presence refuses to start and says what
    /// to use instead.
    /// </remarks>
    public static IReadOnlyList<string> RetiredKeyProblems(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[RetiredSourceTimeoutKey] is null
            ? []
            :
            [
                $"{RetiredSourceTimeoutKey} is no longer read: the source timeout is derived from each " +
                "collection interval. Remove it and, if needed, set Collection:IntervalShare (share of the " +
                "interval, in (0, 1], default 0.8) and Collection:MinimumSourceTimeoutSeconds (floor, at " +
                "least 1, default 10) instead.",
            ];
    }

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

    /// <summary>
    /// How many requests one source may have in flight against it at once.
    /// </summary>
    /// <remarks>
    /// F2. Default 2, matching <see cref="CollectionPolicy.MaxRequestsPerSource"/>;
    /// at most <see cref="SourceRequestGate.MaximumLimit"/> — see
    /// <see cref="Validate"/> for why an out-of-range value refuses to start
    /// rather than being silently clamped to the nearest bound, the same
    /// convention every other setting here follows.
    /// </remarks>
    public int MaxRequestsPerSource { get; set; } = Defaults.MaxRequestsPerSource;

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

        if (!(IntervalShare > 0 && IntervalShare <= 1))
        {
            problems.Add("Collection:IntervalShare must be greater than 0 and at most 1.");
        }

        if (!(MinimumSourceTimeoutSeconds >= 1))
        {
            problems.Add("Collection:MinimumSourceTimeoutSeconds must be at least 1.");
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

        if (MaxRequestsPerSource is < 1 or > SourceRequestGate.MaximumLimit)
        {
            problems.Add(
                $"Collection:MaxRequestsPerSource must be between 1 and {SourceRequestGate.MaximumLimit}.");
        }

        return problems;
    }

    /// <summary>The domain policy these values describe.</summary>
    public CollectionPolicy ToPolicy() => new()
    {
        IntervalShare = IntervalShare,
        MinimumSourceTimeout = TimeSpan.FromSeconds(MinimumSourceTimeoutSeconds),
        MaxRetries = MaxRetries,
        RetryBaseDelay = TimeSpan.FromSeconds(RetryBaseDelaySeconds),
        CircuitBreakerThreshold = CircuitBreakerThreshold,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds),
        MaxConcurrency = MaxConcurrency,
        MaxRequestsPerSource = MaxRequestsPerSource,
    };
}
