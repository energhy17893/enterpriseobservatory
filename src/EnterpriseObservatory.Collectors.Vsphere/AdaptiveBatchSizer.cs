using System.Globalization;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Chooses how many entities to put in one <c>QueryPerf</c> call.
/// </summary>
/// <remarks>
/// <para>
/// vCenter caps how much one performance query may return, via
/// <c>config.vpxd.stats.maxQueryMetrics</c> (256 by default since 6.5).
/// Exceeding it does not degrade gracefully: the query is refused and the
/// charts simply come back empty.
/// </para>
/// <para>
/// The previous product used a fixed <c>Chunk(32)</c> whose origin is not
/// recorded. With ten counters per entity that is 320 metrics — over the
/// default limit — so under the stricter reading of that setting those queries
/// were failing silently in the field.
/// </para>
/// <para>
/// Sources disagree on whether the limit counts metrics (entities × counters)
/// or entities. Rather than betting on an interpretation, this divides by the
/// counter count — correct under the strict reading, and merely conservative
/// under the loose one — and then halves on refusal until the server accepts
/// it. Being wrong in that direction costs a little throughput; being wrong in
/// the other costs all the data.
/// </para>
/// </remarks>
public sealed class AdaptiveBatchSizer
{
    /// <summary>Assumed when the server will not tell us. vCenter 6.5+ default.</summary>
    public const int DefaultMaxQueryMetrics = 256;

    /// <summary>Headroom, because the limit is not the only thing bounding a query.</summary>
    private const double SafetyFactor = 0.8;

    private readonly int _counterCount;

    public AdaptiveBatchSizer(int? serverMaxQueryMetrics, int counterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(counterCount);

        _counterCount = counterCount;
        Current = Initial(serverMaxQueryMetrics, counterCount);
    }

    /// <summary>Entities per query right now.</summary>
    public int Current { get; private set; }

    /// <summary>Whether the size has been reduced because the server refused a query.</summary>
    public bool WasReduced { get; private set; }

    /// <summary>
    /// Halves the batch after a refusal.
    /// </summary>
    /// <returns>False when already at one entity, meaning the size is not the problem.</returns>
    public bool Reduce()
    {
        if (Current <= 1)
        {
            return false;
        }

        Current = Math.Max(1, Current / 2);
        WasReduced = true;
        return true;
    }

    private static int Initial(int? serverMaxQueryMetrics, int counterCount)
    {
        // -1 disables the limit entirely. Treated as "generous" rather than
        // "unlimited": an unbounded query is still a way to make vCenter
        // unresponsive for everyone else using it.
        var budget = serverMaxQueryMetrics switch
        {
            null => DefaultMaxQueryMetrics,
            < 0 => DefaultMaxQueryMetrics * 8,
            0 => DefaultMaxQueryMetrics,
            var limit => limit,
        };

        return Math.Max(1, (int)(budget * SafetyFactor / counterCount));
    }

    /// <summary>
    /// Whether a server error is the query-size limit rather than something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vCenter reports this as a generic fault whose message is the only
    /// distinguishing feature, so matching on text is unavoidable. It should
    /// only be applied to a performance query — see
    /// <see cref="VsphereCallContext"/>.
    /// </para>
    /// <para>
    /// The patterns are deliberately narrow. An earlier version also matched
    /// "exceeds the maximum" and a bare "maxquerymetrics", which a live vCenter
    /// triggered with <c>'config.vpxd.stats.maxQueryMetrics' is invalid or
    /// exceeds the maximum number of characters permitted</c> — an unset option,
    /// read as a size refusal. A missed match costs a retry that fails the same
    /// way; a false match sends the collector shrinking batches forever against
    /// a problem that has nothing to do with size.
    /// </para>
    /// </remarks>
    public static bool IsQuerySizeRefusal(string? serverMessage)
    {
        if (string.IsNullOrWhiteSpace(serverMessage))
        {
            return false;
        }

        var message = serverMessage.ToLowerInvariant();

        // An "invalid" anything is a name or argument problem, never a size one.
        if (message.Contains("is invalid", StringComparison.Ordinal))
        {
            return false;
        }

        return message.Contains("restricted by administrator", StringComparison.Ordinal)
            || message.Contains("too many metrics", StringComparison.Ordinal)
            || message.Contains("number of metrics exceeds", StringComparison.Ordinal)
            || message.Contains("exceeds the maximum number of metrics", StringComparison.Ordinal);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Current} entities x {_counterCount} counters{(WasReduced ? " (reduced)" : string.Empty)}");
}
