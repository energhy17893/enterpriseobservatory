namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// How vSphere names a storage volume, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Its own type because two layers need the same answer and neither owns it.
/// The client reads a datastore's URL to find the volume its extents belong
/// to; the inventory source reads the same URL to mark the datastore with the
/// identifier its performance counters use. Hanging the rule off either one
/// made the other reach across a boundary for it — the transport calling into
/// the mapping, which is the wrong direction and the kind of thing that is
/// only ever cheap to fix before there is a third caller.
/// </para>
/// </remarks>
internal static class VsphereVolume
{
    /// <summary>
    /// The volume's own identifier, out of a datastore's URL.
    /// </summary>
    /// <param name="url">
    /// <c>summary.url</c>, e.g. <c>ds:///vmfs/volumes/5f2c8b1a-.../</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// The identifier is the URL's last segment — a VMFS UUID for block
    /// storage, a generated one for NFS. Taking the last segment rather than
    /// matching a UUID shape is deliberate: NFS identifiers are not UUIDs, and
    /// a pattern that only accepted one would silently exclude every NFS
    /// datastore in an estate.
    /// </para>
    /// <para>
    /// This is what joins host-measured latency to the datastore it is about.
    /// Verified against a live vCenter: 41 of 41 datastores carried a URL and
    /// all 30 instances one host reported resolved through it.
    /// </para>
    /// </remarks>
    public static string? IdentifierFrom(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var segment = url
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        // "ds:" alone would be left if the URL were nothing but a scheme, and
        // an identifier every datastore shares is worse than none: it would
        // attach every volume's latency to whichever one was read last.
        return segment is { Length: > 0 } && !segment.EndsWith(':') ? segment : null;
    }
}
