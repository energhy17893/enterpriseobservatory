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
    /// <param name="remembered">
    /// A size this session already found the server accepts, from
    /// <see cref="LearnedBatchSizes"/>. It caps the start and never raises it:
    /// what was learnt is that larger was refused, not that larger is safe.
    /// </param>
    public AdaptiveBatchSizer(int? serverMaxQueryMetrics, int counterCount, int? remembered = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(counterCount);

        _counterCount = counterCount;
        Planned = Initial(serverMaxQueryMetrics, counterCount);
        Current = remembered is > 0 and var known ? Math.Min(known, Planned) : Planned;
    }

    /// <summary>Entities per query right now.</summary>
    public int Current { get; private set; }

    /// <summary>What the server's stated limit alone would have allowed.</summary>
    public int Planned { get; }

    /// <summary>Whether the size has been reduced because the server refused a query.</summary>
    public bool WasReduced { get; private set; }

    /// <summary>Whether this read is running below the planned size, for whatever reason.</summary>
    public bool IsBelowPlan => Current < Planned;

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
    /// Whether a fault is the query-size limit rather than something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fault type decides (T1.3). vCenter enforces
    /// <c>maxQueryMetrics</c> with <c>vim.fault.RestrictedByAdministrator</c>
    /// (a <c>RuntimeFault</c>, vSphere API 6.0+), which arrives in the SOAP
    /// detail as <c>RestrictedByAdministratorFault</c>. Its message is the
    /// server's wording and is localised; the type is what vim25 promises.
    /// </para>
    /// <para>
    /// The message is read only when the reply names no specific type — no
    /// detail at all, or the generic <c>RuntimeFault</c>. That fallback stays
    /// until a live refusal has been seen: this estate's limit has never been
    /// hit, and provoking it on a production vCenter is not an acceptable way
    /// to find out. A fault that does name its own type is that type, whatever
    /// its wording.
    /// </para>
    /// <para>
    /// It should only be applied to a performance query — see
    /// <see cref="VsphereCallContext"/>.
    /// </para>
    /// </remarks>
    public static bool IsQuerySizeRefusal(string faultType, string? serverMessage)
    {
        ArgumentNullException.ThrowIfNull(faultType);

        if (faultType.Contains(RefusalFaultType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var namesNoSpecificType = faultType.Length == 0 ||
            string.Equals(faultType, "RuntimeFault", StringComparison.OrdinalIgnoreCase);

        return namesNoSpecificType && IsQuerySizeRefusal(serverMessage);
    }

    /// <summary>The vim25 fault vCenter answers an oversized performance query with.</summary>
    public const string RefusalFaultType = "RestrictedByAdministrator";

    /// <summary>
    /// Whether a server message reads like the query-size limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback for a fault that names no specific type; see the
    /// two-argument overload.
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

/// <summary>
/// Batch sizes the server has accepted after refusing larger ones, for the
/// life of one source (ADR-0005 §2, T1.2).
/// </summary>
/// <remarks>
/// <para>
/// Without it every cycle started at the planned size and was refused on the
/// way down again: two wasted queries a cycle at a hidden limit of 64, and the
/// same "reduced" report written as if it were news.
/// </para>
/// <para>
/// Keyed by counter count as well as type, because the size is entities per
/// query and the limit is on metrics: a cycle asking for fewer counters (a
/// statistics level changed, a counter went missing) has a different answer
/// and relearns it rather than inheriting a wrong one.
/// </para>
/// <para>
/// In memory only, like the probe's reputation beside it: a restart reads the
/// connection again, and a limit an administrator raised yesterday should not
/// still be second-guessed today. Locked because an abandoned read can still
/// be inside the source when the next one starts; see
/// <c>VsphereObservationSource</c>.
/// </para>
/// </remarks>
public sealed class LearnedBatchSizes
{
    private readonly Lock _padlock = new();
    private readonly Dictionary<(VsphereEntityType, int), int> _sizes = [];

    public int? For(VsphereEntityType entityType, int counterCount)
    {
        lock (_padlock)
        {
            return _sizes.TryGetValue((entityType, counterCount), out var size) ? size : null;
        }
    }

    public void Remember(VsphereEntityType entityType, int counterCount, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        lock (_padlock)
        {
            _sizes[(entityType, counterCount)] = size;
        }
    }
}