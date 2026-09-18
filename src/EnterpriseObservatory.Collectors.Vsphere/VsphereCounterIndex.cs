namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Indexes a counter catalogue by its portable key.
/// </summary>
/// <remarks>
/// <para>
/// A counter key — <c>group.name.rollup</c> — is how humans and the metric
/// contract name a counter, but it is <strong>not unique</strong>. A live
/// vCenter 8 with 732 counters defines
/// <c>disk.scsiReservationCnflctsPct.average</c> twice, differing only in
/// <see cref="VsphereCounter.StatsType"/>. Building a dictionary directly from
/// the key throws, which is how the first live run of this collector ended.
/// </para>
/// <para>
/// This exists so that the choice between duplicates is made once, in the open,
/// and deterministically — rather than depending on which definition the server
/// happened to return first.
/// </para>
/// </remarks>
public static class VsphereCounterIndex
{
    /// <summary>
    /// Builds a lookup from counter key to the definition we will use.
    /// </summary>
    /// <remarks>
    /// When a key is defined more than once, the lowest statistics level wins,
    /// because that is the definition most installations will actually be
    /// collecting. The lowest counter id breaks a remaining tie, purely so the
    /// result does not depend on response ordering.
    /// </remarks>
    public static IReadOnlyDictionary<string, VsphereCounter> ByKey(IEnumerable<VsphereCounter> counters)
    {
        ArgumentNullException.ThrowIfNull(counters);

        return counters
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(c => c.Level).ThenBy(c => c.Id).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds keys the catalogue defines more than once.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden: a duplicate means the product is choosing
    /// on the operator's behalf, and that is worth being able to see.
    /// </remarks>
    public static IReadOnlyList<DuplicateCounterKey> FindDuplicates(IEnumerable<VsphereCounter> counters)
    {
        ArgumentNullException.ThrowIfNull(counters);

        return
        [
            .. counters
                .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => new DuplicateCounterKey
                {
                    Key = group.Key,
                    Definitions = [.. group.OrderBy(c => c.Level).ThenBy(c => c.Id)],
                }),
        ];
    }
}

/// <summary>A counter key the server defines more than once.</summary>
public sealed record DuplicateCounterKey
{
    public required string Key { get; init; }

    /// <summary>All definitions, in the order preference was applied.</summary>
    public required IReadOnlyList<VsphereCounter> Definitions { get; init; }

    /// <summary>The one that will be used.</summary>
    public VsphereCounter Chosen => Definitions[0];
}
