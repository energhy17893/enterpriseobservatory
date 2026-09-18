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

    /// <summary>
    /// Returns the value expressed in the unit the counter claims.
    /// </summary>
    public static double Normalize(double raw, string? unit) =>
        IsPercent(unit) ? raw / PercentScale : raw;

    /// <summary>
    /// Whether values in this unit are scaled on the wire.
    /// </summary>
    /// <remarks>
    /// Exposed so a probe or a diagnostic view can show both the raw reading
    /// and the normalised one, rather than leaving someone to wonder why the
    /// number differs from what vCenter's own charts show.
    /// </remarks>
    public static bool IsScaled(string? unit) => IsPercent(unit);

    private static bool IsPercent(string? unit) =>
        string.Equals(unit?.Trim(), "percent", StringComparison.OrdinalIgnoreCase);
}
