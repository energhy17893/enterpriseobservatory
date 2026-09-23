namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The vCenter calls this collector makes.
/// </summary>
/// <remarks>
/// A port rather than a concrete client so the orchestration — batching, size
/// reduction, availability checking — can be tested without a vCenter or an
/// HTTP stack. Those decisions are where the bugs live; the transport is
/// mechanical.
/// </remarks>
/// <summary>
/// What the channel behind a vSphere API instance can say about itself, for
/// self-metrics the runner produces (F6, ADR-0025 §5) — never part of a
/// business read.
/// </summary>
/// <remarks>
/// Separate from <see cref="IVsphereApi"/> on purpose: these answer
/// <see cref="IObservationSource.SessionsHeld"/> and
/// <see cref="IObservationSource.GetServerTimeAsync"/>, which
/// <see cref="VsphereObservationSource"/> forwards without adding them to its
/// own read. A fake that implements only <see cref="IVsphereApi"/> — every
/// existing test fixture — simply does not offer this, and the source reports
/// nothing for either self-metric, exactly like a Redfish source that has not
/// implemented them yet.
/// </remarks>
public interface IVsphereChannelSelfMetrics
{
    /// <summary>Sessions this channel believes it holds — 0 or 1; see <see cref="VsphereSessionChannel"/>.</summary>
    int SessionsHeld { get; }

    /// <summary>The vCenter's own clock (<c>ServiceInstance.CurrentTime</c>), or null when it could not be read.</summary>
    Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken);
}

public interface IVsphereApi
{
    /// <summary>Identifies this vCenter, for provenance and health tracking.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Reads counter metadata.
    /// </summary>
    /// <remarks>
    /// Counter ids are assigned per vCenter, so this cannot be cached across
    /// installations and must be read per connection.
    /// </remarks>
    Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads <c>config.vpxd.stats.maxQueryMetrics</c>, or null if unreadable.
    /// </summary>
    /// <remarks>
    /// Null is a legitimate answer: the setting may be absent on older versions
    /// or the account may not be allowed to read it. The caller falls back to
    /// the documented default rather than failing.
    /// </remarks>
    Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks which counters this entity can actually supply right now.
    /// </summary>
    /// <remarks>
    /// The empirical way to detect an insufficient statistics level. Rather than
    /// reasoning about how level numbers map to counters across versions, ask
    /// the server what it has and compare against what we wanted.
    /// </remarks>
    Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
        string entityMoRef,
        VsphereEntityType entityType,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>Reads samples for a batch of entities of one type.</summary>
    /// <exception cref="VsphereQuerySizeRefusedException">
    /// When the server refuses the query for being too large.
    /// </exception>
    /// <param name="nowUtc">
    /// The caller's clock, used to bound a historical query. Passed in rather
    /// than read here so the transport has no opinion about time and the
    /// request is reproducible in a test.
    /// </param>
    Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>Reads samples for a batch of entities, each over its own window.</summary>
    /// <remarks>
    /// <para>
    /// Both ends always given, and no <c>maxSample</c>: with a window, vCenter
    /// keeps the newest samples and drops the oldest when the two disagree
    /// (docs/measurements/queryperf-sample-window.md, m1), so the window alone
    /// decides what comes back. <c>startTime</c> is exclusive and
    /// <c>endTime</c> inclusive (vim25 <c>PerfQuerySpec</c>), which is what
    /// lets a high-water mark be the start without reading its own sample again.
    /// </para>
    /// <para>
    /// The default ignores the windows and asks the older question, for
    /// implementations written before windows existed.
    /// </para>
    /// </remarks>
    /// <exception cref="VsphereQuerySizeRefusedException">
    /// When the server refuses the query for being too large.
    /// </exception>
    Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
        IReadOnlyList<PerfQueryTarget> targets,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        CancellationToken cancellationToken) =>
        QueryPerfAsync(
            [.. targets.Select(t => t.MoRef)],
            entityType,
            counters,
            targets.Count == 0 ? DateTimeOffset.UtcNow : targets.Max(t => t.EndInclusiveUtc),
            cancellationToken);
}

/// <summary>One entity and the window to read it over.</summary>
/// <param name="MoRef">The managed object.</param>
/// <param name="StartExclusiveUtc">vim25 <c>startTime</c>: samples strictly after this.</param>
/// <param name="EndInclusiveUtc">vim25 <c>endTime</c>: samples up to and including this.</param>
public sealed record PerfQueryTarget(string MoRef, DateTimeOffset StartExclusiveUtc, DateTimeOffset EndInclusiveUtc);

/// <summary>
/// The server refused a performance query for asking too much at once.
/// </summary>
/// <remarks>
/// Distinguished from other faults because it is the one failure the collector
/// can fix by itself, by asking for less. Treating it as a generic error would
/// leave the charts permanently empty — which is exactly what happens when the
/// batch size is a hard-coded guess.
/// </remarks>
public sealed class VsphereQuerySizeRefusedException : Exception
{
    public VsphereQuerySizeRefusedException(string message)
        : base(message)
    {
    }

    public VsphereQuerySizeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VsphereQuerySizeRefusedException()
        : base("The server refused the performance query for being too large.")
    {
    }
}

/// <summary>Which entities to sample, grouped by type.</summary>
/// <remarks>
/// Grouped because the sampling interval and the counter set both depend on the
/// type, and because a single query may only address one type.
/// </remarks>
public sealed record VsphereSampleTargets
{
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public IReadOnlyList<string> VirtualMachines { get; init; } = [];

    public IReadOnlyList<string> Datastores { get; init; } = [];

    public IEnumerable<(VsphereEntityType Type, IReadOnlyList<string> MoRefs)> ByType()
    {
        if (Hosts.Count > 0)
        {
            yield return (VsphereEntityType.HostSystem, Hosts);
        }

        if (VirtualMachines.Count > 0)
        {
            yield return (VsphereEntityType.VirtualMachine, VirtualMachines);
        }

        if (Datastores.Count > 0)
        {
            yield return (VsphereEntityType.Datastore, Datastores);
        }
    }

    public bool IsEmpty => Hosts.Count == 0 && VirtualMachines.Count == 0 && Datastores.Count == 0;
}
