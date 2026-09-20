using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Converts vSphere's wire representation into the unit it claims.
/// </summary>
/// <remarks>
/// <para>
/// vSphere reports percentage counters in <em>hundredths of a percent</em>: a
/// host at 26.69% CPU comes back as <c>2669</c> with <c>unitInfo.key</c> of
/// <c>percent</c>. Passing that through unchanged would report 2669% and trip
/// every threshold in the product on the first cycle.
/// </para>
/// <para>
/// Normalising here rather than downstream is the adapter doing its job. The
/// domain's <see cref="CounterValue.Raw"/> means "the value of this counter in
/// the stated unit", and it is the adapter that knows the vendor's encoding.
/// Leaving the scaling for a consumer to remember is how one consumer forgets.
/// </para>
/// <para>
/// Only conversions that are documented and confirmed are applied. A unit this
/// does not recognise is passed through untouched: silently rescaling something
/// on a guess would be worse than the problem it was trying to solve.
/// </para>
/// </remarks>
public static class VsphereUnitNormalizer
{
    /// <summary>vSphere's scale for percentage counters: 1% is reported as 100.</summary>
    private const double PercentScale = 100d;

    /// <summary>Microseconds in a millisecond.</summary>
    private const double MicrosecondsPerMillisecond = 1000d;

    /// <summary>The unit every latency this product stores is expressed in.</summary>
    public const string Millisecond = "millisecond";

    /// <summary>
    /// Returns the value expressed in the unit the counter claims.
    /// </summary>
    public static double Normalize(double raw, string? unit) => unit switch
    {
        _ when IsPercent(unit) => raw / PercentScale,
        _ when IsMicrosecond(unit) => raw / MicrosecondsPerMillisecond,
        _ => raw,
    };

    /// <summary>
    /// The unit a normalised value is actually in, which is not always the one
    /// vSphere stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vSphere measures most latency in milliseconds and a few counters in
    /// microseconds — <c>datastore.datastoreVMObservedLatency.latest</c> is one,
    /// sitting three lines from millisecond counters it is meant to be compared
    /// against. This product's central act is comparing them: VM-observed
    /// latency far above device latency is the difference between a queue
    /// problem and an array problem, and a comparison that is wrong by a
    /// thousand would answer confidently and incorrectly.
    /// </para>
    /// <para>
    /// So the value is converted and the unit converted with it, keeping the
    /// invariant that <see cref="CounterValue.Raw"/> is expressed in
    /// <see cref="CounterValue.Unit"/>. This is the same trade already made for
    /// percentages, where the stored number also differs from the wire. It does
    /// mean a reading here differs by a thousand from the same counter in
    /// vCenter's own charts; <see cref="IsScaled"/> exists so a diagnostic can
    /// say so rather than leaving someone to discover it.
    /// </para>
    /// </remarks>
    public static string NormalizedUnit(string? unit) =>
        IsMicrosecond(unit) ? Millisecond : unit ?? string.Empty;

    /// <summary>
    /// Whether values in this unit are scaled on the wire.
    /// </summary>
    /// <remarks>
    /// Exposed so a probe or a diagnostic view can show both the raw reading
    /// and the normalised one, rather than leaving someone to wonder why the
    /// number differs from what vCenter's own charts show.
    /// </remarks>
    public static bool IsScaled(string? unit) => IsPercent(unit) || IsMicrosecond(unit);

    private static bool IsPercent(string? unit) =>
        string.Equals(unit?.Trim(), "percent", StringComparison.OrdinalIgnoreCase);

    private static bool IsMicrosecond(string? unit) =>
        string.Equals(unit?.Trim(), "microsecond", StringComparison.OrdinalIgnoreCase);
}
