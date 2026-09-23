namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The configuration tier's last reading of each object, carried into every
/// fast inventory read with the time it was read.
/// </summary>
/// <remarks>
/// <para>
/// Why here, in the collector, rather than as an overlay on the graph:
/// <c>EntityGraph.Merge</c> replaces an entity wholesale, and what the slow
/// tier feeds is not only <c>Entity.Settings</c> — <c>scsiLun</c> names the
/// devices in <c>Entity.StoragePaths</c>, and <c>layoutEx</c> sizes the
/// snapshot alert. Overlaying the raw properties onto the fast read before it
/// is mapped gives every existing parser the same input it always had, so the
/// fast snapshot is whole and nothing downstream changes. Topology,
/// relationships and vanish still come from the fast read alone: an entry
/// whose object the fast read did not return is never used.
/// </para>
/// <para>
/// ADR-0027's carry-forward rule: a carried value keeps its read time, and
/// past <see cref="Limit"/> it is absent rather than stale. In memory only,
/// like the graph's own settings: a restart starts empty.
/// </para>
/// </remarks>
internal sealed class ConfigurationCarry
{
    /// <summary>
    /// How long a reading is carried without a newer one: ADR-0026's
    /// carry-forward limit, the same two days annotations use.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromDays(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PropertyObject> _byMoRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _readAt = new(StringComparer.Ordinal);

    /// <summary>Keeps what one page of the configuration read returned.</summary>
    public void Store(IEnumerable<PropertyObject> objects, DateTimeOffset readAtUtc)
    {
        lock (_gate)
        {
            foreach (var o in objects)
            {
                _byMoRef[o.MoRef] = Trim(o);
                _readAt[o.MoRef] = readAtUtc;
            }
        }
    }

    /// <summary>Whether nothing is carried that is still inside <see cref="Limit"/>.</summary>
    public bool IsEmpty(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return !_readAt.Values.Any(at => nowUtc - at <= Limit);
        }
    }

    /// <summary>The newest reading carried, or null when there is none.</summary>
    public DateTimeOffset? Newest
    {
        get
        {
            lock (_gate)
            {
                return _readAt.Count == 0 ? null : _readAt.Values.Max();
            }
        }
    }

    /// <summary>After a complete read: forgets objects vCenter no longer has.</summary>
    public void KeepOnly(IReadOnlySet<string> moRefs)
    {
        lock (_gate)
        {
            foreach (var gone in _byMoRef.Keys.Where(m => !moRefs.Contains(m)).ToList())
            {
                _byMoRef.Remove(gone);
                _readAt.Remove(gone);
            }
        }
    }

    /// <summary>
    /// The fast read's objects with the carried configuration added, and when
    /// each carried reading was taken.
    /// </summary>
    /// <remarks>
    /// The fast read's own values win a collision; there should be none, as
    /// the two tiers ask for disjoint paths. A carried reading's refusals
    /// (<see cref="PropertyObject.Missing"/>) are not carried: the
    /// configuration tier reports them under its own role, once.
    /// </remarks>
    public (IReadOnlyList<PropertyObject> Objects, IReadOnlyDictionary<string, DateTimeOffset> ReadAt) Overlay(
        IReadOnlyList<PropertyObject> fast, DateTimeOffset nowUtc)
    {
        var readAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var merged = new List<PropertyObject>(fast.Count);

        lock (_gate)
        {
            foreach (var o in fast)
            {
                if (!_byMoRef.TryGetValue(o.MoRef, out var carried) ||
                    !string.Equals(carried.Type, o.Type, StringComparison.Ordinal) ||
                    nowUtc - _readAt[o.MoRef] > Limit)
                {
                    merged.Add(o);
                    continue;
                }

                readAt[o.MoRef] = _readAt[o.MoRef];

                var values = new Dictionary<string, string>(o.Values, StringComparer.Ordinal);
                foreach (var (path, value) in carried.Values)
                {
                    values.TryAdd(path, value);
                }

                var structures = new Dictionary<string, IReadOnlyList<PropertyNode>>(o.Structures, StringComparer.Ordinal);
                foreach (var (path, nodes) in carried.Structures)
                {
                    structures.TryAdd(path, nodes);
                }

                merged.Add(o with { Values = values, Structures = structures });
            }
        }

        return (merged, readAt);
    }

    /// <summary>
    /// Keeps only the advanced settings anything reads: a host reports over a
    /// thousand (202 KB, measured) and <see cref="AdvancedSettings.Wanted"/>
    /// is a handful. The path itself stays, even with nothing kept, so "read,
    /// none wanted" is not mistaken for "not read".
    /// </summary>
    private static PropertyObject Trim(PropertyObject o) =>
        o.Structures.TryGetValue("config.option", out var options)
            ? o with
            {
                Structures = new Dictionary<string, IReadOnlyList<PropertyNode>>(o.Structures, StringComparer.Ordinal)
                {
                    ["config.option"] = [.. options.Where(n => AdvancedSettings.Wanted.Contains(n.TextOf("key")))],
                },
                Missing = [],
            }
            : o with { Missing = [] };
}
