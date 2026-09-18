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
        CancellationToken cancellationToken);

    /// <summary>Reads samples for a batch of entities of one type.</summary>
    /// <exception cref="VsphereQuerySizeRefusedException">
    /// When the server refuses the query for being too large.
    /// </exception>
    Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        CancellationToken cancellationToken);
}

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
