using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// The names of the capacity series the product writes for a datastore.
/// </summary>
/// <remarks>
/// <para>
/// These are the product's names, not a platform's. They come from inventory
/// properties (<c>summary.capacity</c>, <c>summary.freeSpace</c>,
/// <c>summary.uncommitted</c> on vSphere) that every inventory cycle already
/// reads, not from performance counters, so no vendor counter namespace
/// applies to them. The <c>.bytes</c> suffix is what keeps them apart from
/// vSphere's own <c>group.name.rollup</c> names: every vSphere counter ends in
/// its rollup (<c>.latest</c>, <c>.average</c>, …) and none ends in a unit.
/// </para>
/// <para>
/// Declared in the application layer rather than in a collector because the
/// capacity forecast (roadmap M4.3) reads them by name, and a rule may not
/// name a vendor's counter. Any collector that can report a volume's size and
/// free space writes the same names, and the forecast works for it unchanged.
/// </para>
/// <para>
/// All five are <see cref="RollupType.Latest"/> in bytes. A capacity figure is
/// a level, not a rate: the average of a datastore's free space over an hour
/// is not a quantity anybody asked about, and the last reading in the bucket
/// is — which is the case <see cref="AggregatedSample.Last"/> was kept for.
/// The bucket's minimum and maximum survive downsampling alongside it, so the
/// hourly tier still says how close to full the volume came within the hour.
/// See docs/collectors/vsphere-counter-map.md §4.
/// </para>
/// </remarks>
public static class CapacityCounters
{
    /// <summary>What the volume can hold.</summary>
    public const string DatastoreCapacity = "datastore.capacity.bytes";

    /// <summary>What is left.</summary>
    public const string DatastoreFree = "datastore.free.bytes";

    /// <summary>Capacity minus free: what is occupied now.</summary>
    public const string DatastoreUsed = "datastore.used.bytes";

    /// <summary>
    /// Space promised to thin disks that they have not taken yet.
    /// </summary>
    public const string DatastoreUncommitted = "datastore.uncommitted.bytes";

    /// <summary>
    /// Used plus uncommitted: what the volume would hold if every thin disk
    /// grew into what it was given.
    /// </summary>
    /// <remarks>
    /// Above capacity means over-committed. The same arithmetic vSphere shows
    /// as "provisioned space".
    /// </remarks>
    public const string DatastoreProvisioned = "datastore.provisioned.bytes";

    /// <summary>The unit every capacity series is recorded in.</summary>
    public const string Unit = "bytes";

    /// <summary>
    /// One capacity reading, ready to be stored.
    /// </summary>
    /// <remarks>
    /// The interval is zero because a capacity figure is a reading at an
    /// instant rather than something accumulated over a period, and a period
    /// here would invite somebody to divide by it.
    /// </remarks>
    public static Observation Reading(
        EntityId entity, string counter, double bytes, DateTimeOffset atUtc, string source) => new()
    {
        Entity = entity,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = bytes,
            Rollup = RollupType.Latest,
            Interval = TimeSpan.Zero,
            Unit = Unit,
        },
        SampledAtUtc = atUtc,
        Source = source,
    };
}
