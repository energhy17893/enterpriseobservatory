using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which path states this rule treats as still carrying the device's I/O.
/// </summary>
/// <remarks>
/// The same vocabulary <see cref="StoragePathRedundancyPolicy"/> already
/// argued for, kept as a second copy rather than a shared one because the two
/// rules are pinned to the domain's own words independently — a collector
/// that renames its states would otherwise have to change two files anyway,
/// and merging them would make it look like one decision instead of two.
/// </remarks>
public sealed record MultipathSinglePointOfFailurePolicy
{
    /// <summary>The states meaning this route can still carry the device's I/O.</summary>
    public IReadOnlyList<string> WorkingStates { get; init; } = ["active", "standby"];

    public static MultipathSinglePointOfFailurePolicy Default { get; } = new();
}

/// <summary>
/// Says that a shared LUN's protection is an illusion: several paths on
/// paper, one point of failure in the hardware.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StoragePathRedundancy"/> answers "did this host just lose a
/// route it had". This rule answers a different and earlier question: "does
/// this LUN's path table actually protect against the failure it looks like
/// it protects against". Both are inventory facts read from the same table;
/// neither is a counter.
/// </para>
/// <para>
/// <strong>Why this is not the noise <see cref="StoragePathRedundancy"/>
/// refused to produce.</strong> That rule's own remarks are explicit that a
/// single-path device is deliberately silent, because every host in the
/// estate carries local disks and boot devices with exactly one path by
/// design, and reporting them would be a permanent alert nobody can clear.
/// This rule does not report those devices either — see <see cref="Devices"/>.
/// It is scoped to devices a <see cref="EntityKind.Datastore"/> entity of
/// type <c>VMFS</c> is actually built on, which a local disk with no VMFS on
/// it, an NFS export and a vSAN object are all excluded from by construction:
/// NFS and vSAN have no path table at all (they are not multipathed SCSI),
/// and a device nothing points a datastore at is not a shared LUN this
/// product can name. What remains is the population Broadcom's own
/// multipathing guidance is about — SAN-attached block storage a host reaches
/// over more than one physical route on purpose — and for that population "one
/// HBA" or "one path" is a real, fixable configuration mistake rather than an
/// expected fact about a boot drive.
/// </para>
/// <para>
/// <strong>Local VMFS is the gap left open, and it is left open rather than
/// guessed at.</strong> vim25's <c>HostScsiDisk</c> (part of
/// <c>config.storageDevice.scsiLun</c>) carries a <c>localDisk</c> flag that
/// would settle this outright, but this collector's
/// <c>ReadScsiLunNames</c> in <c>VsphereClient.cs</c> reads only <c>key</c>
/// and <c>canonicalName</c> from that table today, and <c>VsphereClient.cs</c>
/// is owned by a parallel session for the duration of this change — see the
/// roadmap note on M8.6. A host with a local VMFS datastore (some estates put
/// the scratch partition or a boot bank there) will still show as a
/// single-path or single-HBA finding here until that flag is read. Recorded
/// as a known gap rather than silently worked around with a naming guess,
/// which would be exactly the kind of confident wrong answer this codebase
/// treats as worse than silence.
/// </para>
/// <para>
/// <strong>The target-port finding this rule does not produce.</strong> The
/// roadmap step asks for a third case — every working path leaving through
/// the same storage-array target port — and it is not implemented here. The
/// evidence for it exists in vim25 but is not collected: a
/// <c>HostMultipathInfoPath</c> carries a <c>transport</c> field of type
/// <c>HostTargetTransport</c>, which for Fibre Channel is a
/// <c>HostFibreChannelTargetTransport</c> carrying <c>portWorldWideName</c>,
/// and for iSCSI a <c>HostInternetScsiTargetTransport</c> carrying
/// <c>iScsiName</c> (confirmed against the vSphere Web Services API
/// reference on developer.broadcom.com, September 2026). Nothing this
/// collector reads today parses <c>path.transport</c> at all — see
/// <c>VsphereClient.ReadStoragePaths</c> — and adding it means changing that
/// method's shape and <see cref="Domain.StoragePath"/>'s. Both are out of
/// reach here: <c>VsphereClient.cs</c> is owned by a parallel session for the
/// duration of this change, and changing the domain record underneath it
/// while that session is also touching the collector risks exactly the
/// merge collision the worktree split exists to avoid. Left as the next step
/// on this roadmap item rather than smuggled in as a partial read that could
/// not be tested against the real field.
/// </para>
/// <para>
/// <strong>Citations.</strong> Broadcom's official multipathing guidance
/// ("Managing Multiple Paths", vSphere Storage guide) states plainly that
/// path failover exists to survive the loss of one HBA, one cable, one switch
/// or one storage processor/target port, and that a host should have more
/// than one HBA and more than one path to each such component for that
/// promise to hold. vROps ships an equivalent metric,
/// <c>diskspace|multipathAvailable</c>/path-count checks, under its storage
/// health group — cited for intent, not for a number, exactly as
/// <see cref="StoragePathRedundancy"/> cites vROps's own classification.
/// </para>
/// </remarks>
public static class MultipathSinglePointOfFailure
{
    /// <summary>Distinct from <see cref="StoragePathRedundancy.Category"/>'s family only in name.</summary>
    public const string Category = "Storage path";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "multipath-single-point-of-failure";

    private const string SinglePathTitle = "Storage device has only one path";
    private const string SingleHbaTitle = "All working paths share one HBA";

    private const string Platform = "platform";

    /// <summary>
    /// Every shared LUN whose path table cannot actually survive the failure
    /// it looks like it can.
    /// </summary>
    /// <param name="entities">
    /// The estate as the graph currently holds it. Hosts supply the path
    /// table; datastore entities supply the VMFS-backed device set that scopes
    /// this rule to shared storage. See <see cref="Devices"/>.
    /// </param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Entity> entities,
        MultipathSinglePointOfFailurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var rules = policy ?? MultipathSinglePointOfFailurePolicy.Default;
        var sharedDevices = SharedVmfsDevices(entities);
        var alerts = new List<AlertDefinition>();

        foreach (var host in entities.Where(Judgeable))
        {
            var devices = host.StoragePaths
                .Where(p => Identify(p).Length > 0)
                .GroupBy(Identify, StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                // Local disks, boot devices, NFS and vSAN all fall out here:
                // nothing marked a VMFS datastore with this NAA, so nothing
                // names this a shared LUN. See the type's remarks on why that
                // is the right gate rather than a path count on its own.
                if (!sharedDevices.TryGetValue(device.Key, out var datastoreName))
                {
                    continue;
                }

                var paths = device.ToList();

                if (Verdict(host, device.Key, datastoreName, paths, rules) is { } found)
                {
                    alerts.Add(found);
                }
            }
        }

        return alerts;
    }

    /// <summary>
    /// Every VMFS device this vCenter has told us a datastore lives on,
    /// keyed by the NAA the path table also names it by.
    /// </summary>
    /// <remarks>
    /// <c>VsphereDatastore.StorageDevices</c> — carried into the graph as
    /// <see cref="IdentityMarkKind.StorageDeviceId"/> marks on the datastore
    /// entity — is the join that already exists between "this LUN" and "this
    /// volume". Restricting to <c>Settings["type"]</c> equal to <c>VMFS</c>
    /// (case-insensitive, matching how the collector writes it) is what
    /// excludes NFS and vSAN: neither is reached over a SCSI path table, so a
    /// multipathing finding about either would be a category error.
    /// </remarks>
    private static Dictionary<string, string> SharedVmfsDevices(IReadOnlyList<Entity> entities)
    {
        var byNaa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var datastore in entities.Where(IsVmfsDatastore))
        {
            foreach (var mark in datastore.Marks)
            {
                if (mark.Kind == IdentityMarkKind.StorageDeviceId && mark.Value.Length > 0)
                {
                    byNaa[mark.Value] = datastore.DisplayName;
                }
            }
        }

        return byNaa;
    }

    private static bool IsVmfsDatastore(Entity entity) =>
        entity.Kind == EntityKind.Datastore &&
        entity.Settings.TryGetValue("type", out var type) &&
        string.Equals(type, "VMFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An entity whose path table this rule is entitled to judge.
    /// </summary>
    /// <remarks>
    /// Identical gate to <see cref="StoragePathRedundancy.Judgeable"/> and for
    /// the same two reasons stated there: only a host carries a path table
    /// today, and only an actively observed host's table is current rather
    /// than the last one we happened to see.
    /// </remarks>
    private static bool Judgeable(Entity entity) =>
        entity.Kind == EntityKind.EsxiHost &&
        entity.ObservationState == ObservationState.Active &&
        entity.StoragePaths.Count > 0;

    private static string Identify(StoragePath path) =>
        path.StorageDeviceId.Length > 0 ? path.StorageDeviceId : path.DeviceKey;

    /// <summary>
    /// What this host's routes to one shared device add up to.
    /// </summary>
    /// <remarks>
    /// Single path is checked first and the two are exclusive: a device with
    /// exactly one path table entry cannot also have "all working paths on
    /// one HBA" said about it without repeating the same fact under a second
    /// title.
    /// </remarks>
    private static AlertDefinition? Verdict(
        Entity host,
        string device,
        string datastoreName,
        List<StoragePath> paths,
        MultipathSinglePointOfFailurePolicy rules)
    {
        if (paths.Count == 1)
        {
            return Alert(
                host, device, SinglePathTitle, AlertSeverity.Warning,
                DescribeSinglePath(datastoreName, device, paths[0]));
        }

        var workingAdapters = paths
            .Where(p => rules.WorkingStates.Contains(p.State, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Adapter)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Zero means either nothing is working right now — StoragePathRedundancy
        // already names that as the more urgent "no working path" fact — or
        // every working path's adapter went unreported, and a "single HBA"
        // claim needs a named HBA to be worth anything. More than one means
        // there genuinely is HBA-level redundancy. Only exactly one adapter,
        // named, is this rule's business.
        if (workingAdapters.Count != 1)
        {
            return null;
        }

        return Alert(
            host, device, SingleHbaTitle, AlertSeverity.Warning,
            DescribeSingleHba(datastoreName, device, workingAdapters[0], paths));
    }

    private static AlertDefinition Alert(
        Entity host, string device, string title, AlertSeverity severity, string description) =>
        new()
        {
            // Host and device together, for the reason StoragePathRedundancy's
            // Alert already gives: a device-only fingerprint would merge every
            // host reaching the same array volume into one alert nobody could
            // acknowledge per-host, and a host-only one would merge its whole
            // LUN inventory.
            Fingerprint = AlertFingerprint.Create(
                Attribution(host), title, Category,
                $"{host.Id.Value}/{device}", RuleId),
            Severity = severity,
            Title = title,
            Description = description,
            Category = Category,
            Source = Attribution(host),
            Entity = host.Id,
        };

    private static string Attribution(Entity host) =>
        string.IsNullOrWhiteSpace(host.SourceInstanceId) ? Platform : host.SourceInstanceId;

    private static string DescribeSinglePath(string datastoreName, string device, StoragePath path) =>
        $"This host reaches datastore {datastoreName} ({device}) over exactly one path, " +
        $"{path.Name} on adapter {(path.Adapter.Length > 0 ? path.Adapter : "not reported")}. " +
        "There is no second route for this LUN to fail over to at all: the loss of this one " +
        "HBA, cable, switch port or array target port takes every machine on this datastore " +
        "with it, and nothing will fail over because nothing else was ever configured. This is " +
        "a shared, VMFS-backed device, not a local disk — Broadcom's own multipathing guidance " +
        "is written for exactly this case. Add a second path from a different adapter, ideally " +
        "through a different switch and a different storage-array controller port.";

    private static string DescribeSingleHba(
        string datastoreName, string device, string adapter, List<StoragePath> paths) =>
        $"This host has {paths.Count} paths to datastore {datastoreName} ({device}), and every " +
        $"one currently working leaves through adapter {adapter}. The path count looks like " +
        "redundancy but is not: losing that one HBA, its cable or its switch port removes every " +
        "working route at once, exactly as if there had only ever been one path. Add a working " +
        "path that leaves through a different adapter so that an HBA failure has somewhere to " +
        "fail over to.";
}
