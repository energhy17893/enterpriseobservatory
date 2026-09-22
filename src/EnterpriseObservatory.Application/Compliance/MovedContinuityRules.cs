namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The four M8 alarm rules K2 moved into <c>eo-continuity</c> checks, by the
/// rule ids their alarms' fingerprints still carry.
/// </summary>
/// <remarks>
/// Kept only so the alarms these rules opened before the move can be found
/// and resolved as moved, once, after the first continuity evaluation. No
/// rule raises them any more.
/// </remarks>
public static class MovedContinuityRules
{
    /// <summary>M8.1, the HA scorecard: <c>cluster-ha-scorecard-{finding}</c>.</summary>
    public const string HighAvailability = "cluster-ha-scorecard";

    /// <summary>M8.3, DRS rule violations.</summary>
    public const string Drs = "drs-rule-violation";

    /// <summary>M8.6, multipath single points of failure.</summary>
    public const string Multipath = "multipath-single-point-of-failure";

    /// <summary>M8.2, N+1 (and its <c>-history-unreadable</c> companion).</summary>
    public const string NPlusOne = "cluster-n-plus-one";

    public static IReadOnlyList<string> RuleIds { get; } = [HighAvailability, Drs, Multipath, NPlusOne];

    /// <summary>
    /// The rule id an alarm's fingerprint carries: its fifth <c>|</c>-separated
    /// segment, bare or as the prefix of a per-finding key.
    /// </summary>
    /// <returns>Which of <see cref="RuleIds"/> raised it, or null.</returns>
    public static string? RuleOf(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        var segments = fingerprint.Split('|');

        if (segments.Length != 5)
        {
            return null;
        }

        var checkId = segments[4];

        return RuleIds.FirstOrDefault(id =>
            string.Equals(checkId, id, StringComparison.Ordinal) ||
            checkId.StartsWith(id + "-", StringComparison.Ordinal));
    }
}
