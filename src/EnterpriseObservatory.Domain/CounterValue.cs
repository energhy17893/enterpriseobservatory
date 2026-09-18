namespace EnterpriseObservatory.Domain;

/// <summary>
/// How a counter's samples were combined over its interval.
/// </summary>
/// <remarks>
/// This is not metadata — it is the meaning of the number. Treating a
/// <see cref="Summation"/> counter as an average produces a value that is
/// simply wrong, silently. See docs/collectors/vsphere-metric-contract.md §2.
/// </remarks>
public enum RollupType
{
    Unknown = 0,
    Average,
    Latest,
    Summation,
    Maximum,
    Minimum,
    None,
}

/// <summary>
/// A raw counter sample together with everything needed to interpret it.
/// </summary>
/// <remarks>
/// <para>
/// A bare <c>double</c> is not enough. <c>cpu.ready.summation</c> is a total in
/// milliseconds; converting it to the percentage an operator actually wants
/// requires the interval it was summed over. The same raw number means
/// different things over a 20-second and a 300-second interval.
/// </para>
/// <para>
/// In the previous product that division was performed in several places, so
/// this type exists to make the conversion impossible to get wrong in only some
/// of them.
/// </para>
/// </remarks>
public readonly record struct CounterValue
{
    public required string CounterName { get; init; }

    /// <summary>The number as the platform reported it, unconverted.</summary>
    public required double Raw { get; init; }

    public required RollupType Rollup { get; init; }

    /// <summary>The period this sample covers.</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>Unit of <see cref="Raw"/>, as the platform names it.</summary>
    public required string Unit { get; init; }

    /// <summary>
    /// Expresses a summed duration as a percentage of the interval it was
    /// accumulated over.
    /// </summary>
    /// <remarks>
    /// The canonical use is CPU ready time:
    /// <c>ready% = ready_ms / (interval_s * 1000) * 100</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the counter is not a summation of milliseconds, because the
    /// conversion would be meaningless.
    /// </exception>
    public double AsPercentageOfInterval()
    {
        if (Rollup != RollupType.Summation)
        {
            throw new InvalidOperationException(
                $"Counter '{CounterName}' has rollup {Rollup}; only a summation can be " +
                "expressed as a percentage of its interval.");
        }

        if (!string.Equals(Unit, "millisecond", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Counter '{CounterName}' is measured in '{Unit}'; only a duration in " +
                "milliseconds can be expressed as a percentage of its interval.");
        }

        var intervalMs = Interval.TotalMilliseconds;
        if (intervalMs <= 0)
        {
            throw new InvalidOperationException(
                $"Counter '{CounterName}' has a non-positive interval.");
        }

        return Raw / intervalMs * 100d;
    }
}

/// <summary>
/// A measurement attached to an entity.
/// </summary>
/// <remarks>
/// Observations cannot exist without an entity to hang from — that is what
/// makes the graph, rather than the metric table, the core model. See ADR-0003.
/// </remarks>
public sealed record Observation
{
    public required EntityId Entity { get; init; }

    public required CounterValue Value { get; init; }

    public required DateTimeOffset SampledAtUtc { get; init; }

    /// <summary>Which collector produced this, for provenance.</summary>
    public required string Source { get; init; }
}
