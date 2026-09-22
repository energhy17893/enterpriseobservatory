using System.Globalization;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Turns one vCenter's inventory into entities and relationships.
/// </summary>
/// <remarks>
/// <para>
/// This collector is the single source of truth for the ESXi host list. BMC and
/// management-appliance collectors contribute hardware telemetry to hosts that
/// already exist here; they never create one. That rule is what makes counting
/// the same physical box twice structurally impossible — see product principle
/// 2 and ADR-0005.
/// </para>
/// <para>
/// It reports identity <em>marks</em> and never decides identity. Whether an
/// iLO record and a host are the same machine is the resolver's call, made from
/// all collectors' marks at once, because no single collector has enough
/// information to make it — see ADR-0003.
/// </para>
/// </remarks>
public sealed class VsphereInventorySource(IVsphereInventoryApi api, IClock clock) : IInventorySource
{
    private readonly IVsphereInventoryApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The <c>Entity.Settings</c> key a virtual machine's power state is
    /// carried under. A plain constant rather than a policy property: unlike
    /// <c>ClusterHighAvailabilityPolicy</c>'s setting names, nothing about
    /// this key is a judgement call a deployment might want to override.
    /// <c>DrsRuleViolations</c> in the application layer reads the same
    /// literal from its own copy of this name, since it cannot reference a
    /// collector constant across the layer boundary.
    /// </summary>
    private const string PowerStateSetting = "powerState";

    public string InstanceId => _api.InstanceId;

    public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var payload = await _api.RetrieveInventoryAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var entities = new List<Entity>();
        var relationships = new List<Relationship>();
        var alerts = new List<AlertDefinition>();
        var snapshotFindings = new List<SnapshotFinding>();
        var observations = new List<Observation>();

        // Managed-object references are unique within a vCenter but not between
        // them, so they are qualified before becoming entity ids.
        EntityId Id(string moRef) => EntityId.For(InstanceId, moRef);

        var vCenter = new Entity
        {
            Id = Id("vcenter"),
            Kind = EntityKind.VCenter,
            DisplayName = payload.VCenterName,
            Health = HealthState.Healthy,
            LastSeenUtc = now,
            Marks = [IdentityMark.Create(IdentityMarkKind.Fqdn, payload.VCenterName, InstanceId)],
            // Its endpoint certificate's expiry and fingerprint (M8.7); empty
            // when the handshake did not read one.
            Settings = payload.VCenterVerdicts,
        };
        entities.Add(vCenter);

        AddClusters(payload, Id, now, entities, relationships, alerts);
        AddHosts(payload, Id, now, vCenter.Id, entities, relationships, alerts);
        AddDatastores(payload, Id, now, entities, relationships, alerts, observations);
        AddVirtualMachines(payload, Id, now, entities, relationships, alerts, snapshotFindings);
        AddTriggeredAlarms(payload, Id, entities, alerts);

        return new InventorySnapshot
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Entities = entities,
            Relationships = relationships,
            Alerts = alerts,
            Failures = [.. payload.Failures.Select(ToFailure)],
            Coverage = payload.Coverage,
            SnapshotFindings = snapshotFindings,
            Observations = observations,
        };
    }

    /// <summary>
    /// Adds collection PR 1's verdicts beside what an entity already carries.
    /// </summary>
    /// <remarks>
    /// The verdict keys are dotted camelCase (<see cref="InventoryVerdicts"/>)
    /// and cannot collide with an advanced setting or an HA key; if one ever
    /// did, the setting already there wins, because it is the older contract.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> WithVerdicts(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> verdicts)
    {
        if (verdicts.Count == 0)
        {
            return settings;
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in verdicts)
        {
            merged[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            merged[key] = value;
        }

        return merged;
    }

    private void AddClusters(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts)
    {
        foreach (var cluster in payload.Clusters)
        {
            entities.Add(new Entity
            {
                Id = id(cluster.MoRef),
                Kind = EntityKind.Cluster,
                DisplayName = cluster.Name,
                DrsRules = ResolveDrsRules(cluster, id),
                // Unknown, and today that is where it stays. This said the
                // health-propagation pass derives it from the members, which
                // was not true: ADR-0004 settles which edges propagate and in
                // which direction, RelationshipRules.PropagatesHealth encodes
                // it, and nothing calls that method. Every cluster in the
                // estate has been Unknown since the first cycle.
                //
                // Left Unknown rather than guessed at, because Unknown is the
                // honest answer to a question nobody has computed and the
                // product's first principle is not to claim what it has not
                // established. What ADR-0004 does not decide is the combining
                // function — whether one critical host in ten makes a cluster
                // critical, or degraded, or nothing at all while HA is doing
                // its job — and that is a product decision rather than a gap
                // to be filled in by whoever notices it. See
                // docs/live-verification.md.
                Health = HealthState.Unknown,
                LastSeenUtc = now,
                // The platform's own words for its HA configuration, carried
                // rather than judged -- the same contract host advanced
                // settings and datastore type follow. The HA scorecard rule
                // reads these; this collector only ever hands them over.
                Settings = WithVerdicts(cluster.HaSettings, cluster.Verdicts),
            });

            if (cluster.HighAvailabilityEnabled is null || cluster.DrsEnabled is null)
            {
                // The previous product got this right and it is worth keeping:
                // when the configuration cannot be read, HA and DRS are not
                // written as enabled. Saying "HA is off" when we simply could
                // not look would send someone to fix a cluster that is fine —
                // or leave one that is not.
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Cluster configuration unreadable", "Configuration",
                        cluster.Name, "cluster-config-unreadable"),
                    Severity = AlertSeverity.Warning,
                    Title = "Cluster configuration unreadable",
                    Description =
                        $"HA and DRS settings for '{cluster.Name}' could not be read, so they are " +
                        "reported as Unknown rather than assumed. Best-practice checks that depend " +
                        "on them are skipped for this cluster.",
                    Category = "Configuration",
                    Source = InstanceId,
                    Entity = id(cluster.MoRef),
                });
            }

            relationships.Add(Edge(id(cluster.MoRef), id("vcenter"), RelationshipKind.ManagedBy, now));
        }
    }

    // --- M8.3: DRS rule resolution -----------------------------------------
    //
    // A separate, self-contained method rather than inline in AddClusters, so
    // that another collector-side change to cluster configuration (HA's
    // dasConfig, read from the same configurationEx structure) touches
    // neither this method's body nor its call site's surrounding lines.
    //
    // What this turns a wire-shaped VsphereDrsRule into is the one thing the
    // wire shape deliberately does not carry: group names resolved to
    // members. See VsphereDrsRule's remarks for why that split exists.

    /// <summary>
    /// Resolves a cluster's DRS rules' group references against its groups.
    /// </summary>
    /// <remarks>
    /// A rule naming a group vCenter did not also report — a group deleted
    /// between the two reads of a snapshot that is not transactional, or a
    /// group this account could not see — resolves to no members rather than
    /// throwing: the rule is then carried with an empty host or VM list,
    /// which the analysis rule reads as "nothing to judge" rather than as
    /// "the placement failed."
    /// </remarks>
    private static IReadOnlyList<Domain.DrsRule> ResolveDrsRules(
        VsphereCluster cluster, Func<string, EntityId> id)
    {
        if (cluster.DrsRules.Count == 0)
        {
            return [];
        }

        var vmGroups = cluster.Groups
            .Where(g => g.Kind == VsphereClusterGroupKind.VirtualMachine)
            .ToDictionary(g => g.Name, g => g.MemberMoRefs, StringComparer.Ordinal);

        var hostGroups = cluster.Groups
            .Where(g => g.Kind == VsphereClusterGroupKind.Host)
            .ToDictionary(g => g.Name, g => g.MemberMoRefs, StringComparer.Ordinal);

        IReadOnlyList<string> MembersOf(
            IReadOnlyDictionary<string, IReadOnlyList<string>> groups, string? name) =>
            name is { Length: > 0 } key && groups.TryGetValue(key, out var members)
                ? [.. members.Select(m => id(m).Value)]
                : [];

        return [.. cluster.DrsRules.Select(rule => new Domain.DrsRule
        {
            Name = rule.Name,
            RuleUuid = rule.RuleUuid,
            Kind = rule.Kind,
            Enabled = rule.Enabled,
            Mandatory = rule.Mandatory,
            VCenterInCompliance = rule.InCompliance,
            VirtualMachineEntityIds = rule.Kind is Domain.DrsRuleKind.Affinity or Domain.DrsRuleKind.AntiAffinity
                ? [.. rule.VirtualMachineMoRefs.Select(m => id(m).Value)]
                : MembersOf(vmGroups, rule.VmGroupName),
            HostEntityIds = rule.Kind is Domain.DrsRuleKind.VmHostAffine or Domain.DrsRuleKind.VmHostAntiAffine
                ? MembersOf(hostGroups, rule.HostGroupName)
                : [],
        })];
    }

    private void AddHosts(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        EntityId vCenterId,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts)
    {
        foreach (var host in payload.Hosts)
        {
            var reachable = IsReachable(host.ConnectionState);

            entities.Add(new Entity
            {
                Id = id(host.MoRef),
                Kind = EntityKind.EsxiHost,
                DisplayName = host.Name,
                Marks = MarksFor(host),
                // A host vCenter cannot reach has unknown health regardless of
                // the colour it was last painted.
                Health = reachable ? MapStatus(host.OverallStatus) : HealthState.Unknown,
                ObservationState = host.InMaintenanceMode
                    ? ObservationState.InMaintenance
                    : ObservationState.Active,
                LastSeenUtc = now,
                // The path table, carried rather than judged. What a lost path
                // means is a rule's decision and it is being written
                // elsewhere; what this collector owes it is the table itself,
                // because nothing else in the product can produce it. It is
                // also the only place a storage path counter's runtime name —
                // vmhba0:C0:T0:L1, which carries no LUN identity — can be
                // turned into the NAA a datastore is marked with.
                StoragePaths = [.. host.StoragePaths.Select(ToStoragePath)],
                // Carried, not judged -- same contract as the path table
                // above. Which settings arrive is decided in
                // AdvancedSettings; what they mean is a rule's business.
                Settings = WithVerdicts(host.AdvancedSettings, host.Verdicts),
                // Same contract again, and null passes through as null: a
                // host whose services were not read must not reach a rule
                // looking like a host with none.
                Services = host.Services,
                TimeConfiguration = host.TimeConfiguration,
                VirtualSwitchSecurity = host.VirtualSwitchSecurity,
                PortGroupSecurity = host.PortGroupSecurity,
                LockdownMode = host.LockdownMode,
            });

            relationships.Add(Edge(id(host.MoRef), vCenterId, RelationshipKind.ManagedBy, now));

            if (!string.IsNullOrWhiteSpace(host.ClusterMoRef))
            {
                relationships.Add(Edge(id(host.MoRef), id(host.ClusterMoRef), RelationshipKind.PartOf, now));
            }

            if (!reachable)
            {
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Host not reachable from vCenter", "Inventory",
                        host.Name, "host-disconnected"),
                    Severity = AlertSeverity.Critical,
                    Title = "Host not reachable from vCenter",
                    Description =
                        $"Connection state is '{host.ConnectionState}'. Everything on this host is " +
                        "reported as Unknown; it is not being claimed healthy and it is not being " +
                        "claimed down.",
                    Category = "Inventory",
                    Source = InstanceId,
                    Entity = id(host.MoRef),
                });
            }
        }
    }

    private void AddDatastores(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts,
        List<Observation> observations)
    {
        foreach (var datastore in payload.Datastores)
        {
            entities.Add(new Entity
            {
                Id = id(datastore.MoRef),
                Kind = EntityKind.Datastore,
                DisplayName = datastore.Name,
                // Accessible is nullable on purpose: unreadable is not the same
                // as inaccessible, and neither is the same as fine.
                Health = datastore.Accessible switch
                {
                    true => HealthState.Healthy,
                    false => HealthState.Critical,
                    null => HealthState.Unknown,
                },
                LastSeenUtc = now,
                // Two vocabularies, both kept. The volume identifier is what
                // performance counters name this datastore by; the storage
                // device is what its paths and disk devices are named by. One
                // without the other leaves "this datastore is slow" and "this
                // path has errors" as two facts about the same LUN that cannot
                // be put together.
                Marks = Marks(datastore),
                // The capacity report's "type" column. vSphere's own word
                // (VMFS, NFS, vsan, ...), not translated — the same choice
                // Settings makes everywhere else. Absent from the dictionary,
                // not written empty, when vCenter did not report one.
                Settings = WithVerdicts(
                    datastore.Type is { Length: > 0 } type
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = type }
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    datastore.Verdicts),
            });

            relationships.Add(Edge(id(datastore.MoRef), id("vcenter"), RelationshipKind.ManagedBy, now));

            // Health said Critical and nothing reached the inbox. The same gap
            // the collector-unreachable alert exists to close, left open for
            // the one entity kind where it means an outage rather than a
            // degradation: virtual machines on an inaccessible datastore are
            // not slow, they are stopped.
            if (datastore.Accessible == false)
            {
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Datastore not accessible", "Inventory",
                        datastore.Name, "datastore-inaccessible"),
                    Severity = AlertSeverity.Critical,
                    Title = "Datastore not accessible",
                    Description =
                        $"vCenter reports '{datastore.Name}' as inaccessible. Virtual machines " +
                        "stored on it cannot read or write, and anything this datastore is the " +
                        "only copy of is unavailable.",
                    Category = "Inventory",
                    Source = InstanceId,
                    Entity = id(datastore.MoRef),
                });
            }

            if (Fullness(datastore) is { } fullness)
            {
                AddFullnessAlert(datastore, fullness, id(datastore.MoRef), InstanceId, alerts);
            }

            // The over-commit finding is not raised here any more. It needs a
            // date, the date needs history, and a collector may not read
            // history; the rule that estimates the date raises it from the
            // readings below (DatastoreTimeToFull, roadmap M4.4).
            observations.AddRange(CapacityReadings(datastore, id(datastore.MoRef), now, InstanceId));
        }
    }

    /// <summary>
    /// Keeps the capacity figures this read already has, as series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No extra call to vCenter. The three properties are the ones the
    /// fullness alert above is computed from; until now they
    /// were used for those and dropped, so the product could say a volume was
    /// 95% full and not how fast it got there. The <c>disk.*.latest</c>
    /// performance counters would carry the same numbers at the cost of a
    /// query per datastore and the risk of a counter that is defined and
    /// returns nothing; these are known to arrive.
    /// </para>
    /// <para>
    /// Each figure is written only when what it is made from was actually
    /// read. A gap is how the product says it was not looking, and a zero
    /// written for an unread capacity would read as a volume that emptied —
    /// the first thing a forecast would fit a line to. An inaccessible
    /// datastore is skipped entirely: vCenter reports its size as whatever it
    /// last knew, or as zero, and neither is a measurement.
    /// </para>
    /// </remarks>
    private static IEnumerable<Observation> CapacityReadings(
        VsphereDatastore datastore, EntityId entity, DateTimeOffset now, string source)
    {
        if (datastore.Accessible == false || datastore.CapacityBytes is not (> 0 and { } capacity))
        {
            yield break;
        }

        Observation Reading(string counter, long bytes) =>
            CapacityCounters.Reading(entity, counter, bytes, now, source);

        yield return Reading(CapacityCounters.DatastoreCapacity, capacity);

        // Free space larger than the volume is not a reading, whatever sent it.
        if (datastore.FreeSpaceBytes is not (>= 0 and { } free) || free > capacity)
        {
            yield break;
        }

        yield return Reading(CapacityCounters.DatastoreFree, free);
        yield return Reading(CapacityCounters.DatastoreUsed, capacity - free);

        // Absent both when unreadable and on a volume with no thin disks, and
        // the two cannot be told apart — so neither is written as zero.
        if (datastore.UncommittedBytes is not (>= 0 and { } uncommitted))
        {
            yield break;
        }

        yield return Reading(CapacityCounters.DatastoreUncommitted, uncommitted);
        yield return Reading(CapacityCounters.DatastoreProvisioned, capacity - free + uncommitted);
    }

    /// <summary>
    /// Turns the alarms vCenter has raised into alerts of our own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the product could show a host as Critical and have nothing
    /// in the inbox to say why — which is exactly what a live estate did: one
    /// red host, one screen reading "no alert is currently firing for this
    /// entity". The colour came from <c>summary.overallStatus</c>, and the
    /// reason for the colour lived in a triggered alarm nobody was reading.
    /// </para>
    /// <para>
    /// These are vCenter's judgements, not the product's, and they are labelled
    /// as such. An operator needs to know which tool to go and silence, and an
    /// alarm somebody disabled in vCenter should stop appearing here without
    /// anyone touching this product.
    /// </para>
    /// <para>
    /// Alarms about objects outside the collected inventory are dropped rather
    /// than attached to something nearby. vCenter raises alarms on datacentres,
    /// folders and the vCenter itself, none of which this collector reads yet;
    /// an alert pointing at an entity that does not exist is worse than a known
    /// gap, because it cannot be navigated to or understood.
    /// </para>
    /// </remarks>
    private void AddTriggeredAlarms(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        List<Entity> entities,
        List<AlertDefinition> alerts)
    {
        if (payload.TriggeredAlarms.Count == 0)
        {
            return;
        }

        var known = entities.Select(e => e.Id).ToHashSet();
        var unattached = 0;

        // De-duplicated here as well as in the client. The client collapses by
        // vCenter's key so it need not resolve one alarm's name twice; this
        // collapses by fingerprint so the mapping is right on its own terms.
        // A source that is only correct because its supplier was careful is a
        // source that breaks the first time a different supplier is honest.
        var seen = new HashSet<AlertFingerprint>();

        foreach (var alarm in payload.TriggeredAlarms)
        {
            // Green is a triggered alarm that has returned to normal and not
            // yet been cleared; gray is one that cannot currently be evaluated.
            // Neither is a reason to wake anybody, and gray in particular must
            // not read as healthy — it is absence of knowledge, and the entity
            // health model already carries that.
            var severity = alarm.OverallStatus?.ToUpperInvariant() switch
            {
                "RED" => AlertSeverity.Critical,
                "YELLOW" => AlertSeverity.Warning,
                _ => (AlertSeverity?)null,
            };

            if (severity is not { } level)
            {
                continue;
            }

            var entity = id(alarm.EntityMoRef);

            if (!known.Contains(entity))
            {
                unattached++;
                continue;
            }

            // vCenter's own key, which is alarmId.entityId and survives both a
            // restart and the alarm appearing on several objects at once.
            // Deriving one from the name instead would raise a second alert
            // the day somebody renames an alarm.
            var fingerprint = AlertFingerprint.Create(
                InstanceId, "vCenter alarm", "vCenter", alarm.Key, "vcenter-alarm");

            if (!seen.Add(fingerprint))
            {
                continue;
            }

            var name = alarm.AlarmName is { Length: > 0 } resolved
                ? resolved
                : $"vCenter alarm {alarm.AlarmMoRef}";

            alerts.Add(new AlertDefinition
            {
                Fingerprint = fingerprint,
                Severity = level,
                Title = name,
                Description = Describe(alarm, name),
                Category = "vCenter",
                Source = InstanceId,
                Entity = entity,
            });
        }

        if (unattached > 0)
        {
            alerts.Add(new AlertDefinition
            {
                Fingerprint = AlertFingerprint.Create(
                    InstanceId, "vCenter alarms on uncollected objects", "vCenter",
                    InstanceId, "vcenter-alarm-unattached"),
                Severity = AlertSeverity.Warning,
                Title = "vCenter alarms on uncollected objects",
                Description =
                    $"{unattached.ToString(CultureInfo.InvariantCulture)} alarm(s) vCenter has " +
                    "raised concern objects this collector does not read — datacentres, folders, " +
                    "resource pools or the vCenter itself. They are counted rather than shown, " +
                    "because an alert pointing at an entity that does not exist cannot be acted on.",
                Category = "vCenter",
                Source = InstanceId,
                Entity = id("vcenter"),
            });
        }
    }

    private static string Describe(VsphereTriggeredAlarm alarm, string name)
    {
        var description = alarm.AlarmDescription is { Length: > 0 } text
            ? text.Trim()
            : $"vCenter raised '{name}'.";

        var raised = alarm.TriggeredAtUtc is { } at
            ? string.Create(CultureInfo.InvariantCulture, $" Raised {at:u}.")
            : string.Empty;

        // Said, not obeyed. The product keeps its own acknowledgement with its
        // own audit trail, and adopting vCenter's would show an alert as
        // acknowledged by somebody this installation cannot name.
        var acknowledged = alarm.Acknowledged
            ? alarm.AcknowledgedByUser is { Length: > 0 } who
                ? $" Acknowledged in vCenter by {who}."
                : " Acknowledged in vCenter."
            : string.Empty;

        return $"{description}{raised}{acknowledged} Raised by vCenter, not by this product — " +
               "clear it there.";
    }

    /// <summary>What a datastore can be recognised by.</summary>
    /// <remarks>
    /// A spanned VMFS volume occupies several devices and each is reported, so
    /// a path error on any of them can be tied back here. Reporting only the
    /// first would lose exactly the extent that explains an outage.
    /// </remarks>
    private List<IdentityMark> Marks(VsphereDatastore datastore)
    {
        var marks = new List<IdentityMark>();

        if (VsphereVolume.IdentifierFrom(datastore.Url) is { } volume)
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.VolumeIdentifier, volume, InstanceId));
        }

        foreach (var device in datastore.StorageDevices)
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.StorageDeviceId, device, InstanceId));
        }

        return marks;
    }

    /// <summary>
    /// How full a datastore is, or null when the numbers cannot say.
    /// </summary>
    /// <remarks>
    /// Zero capacity is refused rather than divided by: it would read as
    /// completely full, which is the most alarming possible answer to arrive at
    /// by accident. Absent values mean the properties were not readable, and an
    /// unreadable datastore is not a full one.
    /// </remarks>
    private static double? Fullness(VsphereDatastore datastore) =>
        datastore is { CapacityBytes: > 0 and { } capacity, FreeSpaceBytes: >= 0 and { } free }
            ? (capacity - free) / (double)capacity * 100d
            : null;

    /// <summary>
    /// Raises the fullness alert, when there is one to raise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data for this was already being fetched. <c>summary.capacity</c> and
    /// <c>summary.freeSpace</c> have been requested from vCenter on every
    /// inventory cycle since the collector was written, parsed, and then
    /// dropped on the floor — no entity field, no series, no alert, nothing on
    /// screen. "This datastore is nearly full" is the most commonly configured
    /// alert in VMware monitoring and the product could not say it, for want of
    /// using what it already had.
    /// </para>
    /// <para>
    /// The free space is in the message beside the percentage, deliberately.
    /// Ninety percent of a hundred-terabyte volume is ten terabytes free and
    /// nobody's emergency; ninety percent of a five-hundred-gigabyte one is
    /// fifty gigabytes and somebody's weekend. A percentage alone cannot tell
    /// those apart and an operator should not have to go and look.
    /// </para>
    /// </remarks>
    private static void AddFullnessAlert(
        VsphereDatastore datastore,
        double fullness,
        EntityId entity,
        string instanceId,
        List<AlertDefinition> alerts)
    {
        var severity = fullness switch
        {
            >= CriticalFullnessPercent => AlertSeverity.Critical,
            >= WarningFullnessPercent => AlertSeverity.Warning,
            _ => (AlertSeverity?)null,
        };

        if (severity is not { } level)
        {
            return;
        }

        var free = datastore.FreeSpaceBytes ?? 0;

        alerts.Add(new AlertDefinition
        {
            // One fingerprint across both severities, so a datastore crossing
            // from warning to critical raises the same alert rather than a
            // second one beside it — the inbox should say the problem got
            // worse, not that a new problem appeared.
            Fingerprint = AlertFingerprint.Create(
                instanceId, "Datastore nearly full", "Capacity",
                datastore.Name, "datastore-full"),
            Severity = level,
            Title = "Datastore nearly full",
            Description = string.Create(
                CultureInfo.InvariantCulture,
                $"'{datastore.Name}' is {fullness:0.#}% full, with {Gigabytes(free):0.#} GB free " +
                $"of {Gigabytes(datastore.CapacityBytes ?? 0):0.#} GB."),
            Category = "Capacity",
            Source = instanceId,
            Entity = entity,
        });
    }

    /// <summary>Warn here. Not a crisis, but the point where somebody plans.</summary>
    private const double WarningFullnessPercent = 85d;

    /// <summary>
    /// And here it is a crisis.
    /// </summary>
    /// <remarks>
    /// A VMFS datastore that fills completely does not degrade: every virtual
    /// machine with a snapshot or a thin disk on it stops, at once.
    /// </remarks>
    private const double CriticalFullnessPercent = 95d;

    private static double Gigabytes(long bytes) => bytes / 1024d / 1024d / 1024d;

    private void AddVirtualMachines(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts,
        List<SnapshotFinding> snapshotFindings)
    {
        var knownHosts = payload.Hosts.Select(h => h.MoRef).ToHashSet(StringComparer.Ordinal);
        var knownDatastores = payload.Datastores.Select(d => d.MoRef).ToHashSet(StringComparer.Ordinal);
        var datastores = payload.Datastores.ToDictionary(d => d.MoRef, StringComparer.Ordinal);

        foreach (var vm in payload.VirtualMachines)
        {
            var poweredOn = string.Equals(vm.PowerState, "poweredOn", StringComparison.OrdinalIgnoreCase);

            entities.Add(new Entity
            {
                Id = id(vm.MoRef),
                Kind = EntityKind.VirtualMachine,
                DisplayName = vm.Name,
                Marks = string.IsNullOrWhiteSpace(vm.InstanceUuid)
                    ? []
                    : [IdentityMark.Create(IdentityMarkKind.HardwareUuid, vm.InstanceUuid, InstanceIdOf(payload))],
                // A powered-off VM is not unhealthy, but neither is it observed.
                // Reporting it healthy would be claiming something we did not
                // measure.
                Health = poweredOn ? MapStatus(vm.OverallStatus) : HealthState.Unknown,
                LastSeenUtc = now,
                // Deliberately a property and deliberately not a mark. See
                // EntitySizing: the vCPU count is what a ready-time percentage
                // has to be divided by, and it is evidence of nothing at all
                // about which machine this is.
                Sizing = SizingOf(vm),
                // Carried so a rule that judges live placement (DrsRuleViolations)
                // can tell a powered-off VM from one this cycle actually placed
                // somewhere, without leaning on Health -- which is also Unknown
                // for a powered-on VM whose overall status simply was not green,
                // yellow or red. Empty when the platform did not report a power
                // state at all, the same "absent is not a value" contract every
                // other Settings key follows.
                Settings = WithVerdicts(
                    string.IsNullOrWhiteSpace(vm.PowerState)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [PowerStateSetting] = vm.PowerState,
                        },
                    vm.Verdicts),
            });

            AddSnapshotAlert(vm, datastores, id(vm.MoRef), now, alerts, snapshotFindings);

            // Only edges to things we actually saw. A reference to a host in
            // another vCenter, or one we failed to read, would otherwise become
            // an edge to an entity that does not exist.
            if (!string.IsNullOrWhiteSpace(vm.HostMoRef) && knownHosts.Contains(vm.HostMoRef))
            {
                relationships.Add(Edge(id(vm.MoRef), id(vm.HostMoRef), RelationshipKind.RunsOn, now));
            }

            foreach (var datastore in vm.DatastoreMoRefs.Where(knownDatastores.Contains))
            {
                relationships.Add(Edge(id(vm.MoRef), id(datastore), RelationshipKind.BackedBy, now));
            }
        }
    }

    /// <summary>
    /// Carries a machine's configured sizes, or nothing when none were read.
    /// </summary>
    /// <remarks>
    /// Null rather than an <see cref="EntitySizing"/> full of nulls, so that
    /// "this collector read no sizing at all" is one check rather than four —
    /// and so a rule cannot mistake an empty record for a machine with no
    /// processors.
    /// </remarks>
    private static EntitySizing? SizingOf(VsphereVirtualMachine vm) =>
        vm is { VirtualCpuCount: null, ConfiguredMemoryMb: null, CpuLimitMhz: null, MemoryLimitMb: null }
            ? null
            : new EntitySizing
            {
                VirtualCpuCount = vm.VirtualCpuCount,
                ConfiguredMemoryMb = vm.ConfiguredMemoryMb,
                CpuLimitMhz = vm.CpuLimitMhz,
                MemoryLimitMb = vm.MemoryLimitMb,
            };

    private static StoragePath ToStoragePath(VsphereStoragePath path) => new()
    {
        Name = path.Name,
        StorageDeviceId = path.StorageDeviceId,
        DeviceKey = path.DeviceKey,
        State = path.State,
        Adapter = path.Adapter,
        Transport = path.TransportType,
        Target = path.Target,
    };

    /// <summary>
    /// Reports a snapshot that has been left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vROps has no snapshot alert. It reads the snapshot tree only as a guard
    /// — "does this machine have one at all" — and never says a word about how
    /// old it is or how large it has grown. That is not parity to be matched;
    /// it is the gap, and a forgotten snapshot is the most common self-inflicted
    /// outage in a VMware estate. Nobody deletes one on purpose and forgets;
    /// somebody takes one before a change, the change goes fine, and a delta
    /// disk grows for eight months until the volume fills and every machine on
    /// it stops at once. The same failure this file's
    /// <see cref="CriticalFullnessPercent"/> remark describes, from the other
    /// end.
    /// </para>
    /// <para>
    /// The severity is not a threshold on size, because there is no size that
    /// is wrong in itself. It is critical when the chain is already larger
    /// than the free space on a datastore it lives on — at that point the
    /// outage is arithmetic, not a risk — and otherwise it is a question of
    /// age, because age is what "forgotten" means.
    /// </para>
    /// <para>
    /// Size travels in the message beside the age for the same reason free
    /// space travels beside fullness: a three-week-old snapshot holding two
    /// hundred megabytes is somebody's tidying task, and a three-week-old one
    /// holding four hundred gigabytes is somebody's weekend.
    /// </para>
    /// </remarks>
    private void AddSnapshotAlert(
        VsphereVirtualMachine vm,
        Dictionary<string, VsphereDatastore> datastores,
        EntityId entity,
        DateTimeOffset now,
        List<AlertDefinition> alerts,
        List<SnapshotFinding> snapshotFindings)
    {
        if (vm.Snapshots.Count == 0)
        {
            return;
        }

        // Oldest first, and a snapshot whose creation time vCenter would not
        // give up sorts last — so an unreadable timestamp cannot become the
        // oldest snapshot in the estate.
        var oldest = vm.Snapshots[0];
        var age = oldest.CreatedAtUtc is { } created ? now - created : (TimeSpan?)null;

        var willFill = vm.SnapshotBytes is { } bytes && bytes > 0 && vm.DatastoreMoRefs
            .Select(moRef => datastores.TryGetValue(moRef, out var d) ? d.FreeSpaceBytes : null)
            .Any(free => free is { } remaining && bytes > remaining);

        var severity = willFill ? AlertSeverity.Critical
            : age >= StaleSnapshotCritical ? AlertSeverity.Critical
            : age >= StaleSnapshotWarning ? AlertSeverity.Warning
            : (AlertSeverity?)null;

        if (severity is not { } level)
        {
            return;
        }

        // One fingerprint per machine rather than per snapshot. A chain that
        // grows a second link is the same problem getting worse, and an inbox
        // that gained a row every time somebody took another snapshot would be
        // teaching people to ignore it.
        var fingerprint = AlertFingerprint.Create(
            InstanceId, "Snapshot left behind", "Capacity", vm.Name, "vm-stale-snapshot");

        // Who took them is not known here and is not looked up here: the
        // answer is in the event history the product has stored, and a
        // collector reads its source, never the product's store. The facts
        // travel structured so the application can join them — see
        // SnapshotCreators.
        snapshotFindings.Add(new SnapshotFinding
        {
            Fingerprint = fingerprint,
            VmMoRef = vm.MoRef,
            Snapshots =
            [
                .. vm.Snapshots.Select(s => new SnapshotTaken { Name = s.Name, CreatedAtUtc = s.CreatedAtUtc }),
            ],
        });

        alerts.Add(new AlertDefinition
        {
            Fingerprint = fingerprint,
            Severity = level,
            Title = "Snapshot left behind",
            Description = DescribeSnapshots(vm, oldest, age, willFill),
            Category = "Capacity",
            Source = InstanceId,
            Entity = entity,
        });
    }

    private static string DescribeSnapshots(
        VsphereVirtualMachine vm, VsphereSnapshot oldest, TimeSpan? age, bool willFill)
    {
        var how = age is { } old
            ? string.Create(CultureInfo.InvariantCulture, $"{old.TotalDays:0} days old")
            : "of unknown age";

        var size = vm.SnapshotBytes is { } bytes
            ? string.Create(CultureInfo.InvariantCulture, $"{Gigabytes(bytes):0.#} GB")
            // Not "0 GB". The layout was unreadable, and a zero here would read
            // as a measurement rather than as the absence of one.
            : "an unmeasured amount";

        // Depth is named separately from the count because they answer
        // different questions. Four snapshots side by side off one parent is
        // somebody being careful; a chain four deep is four delta disks that
        // every read has to walk, and it is slow as well as large.
        var deepest = vm.Snapshots.Max(s => s.Depth);

        var count = vm.Snapshots.Count == 1
            ? "one snapshot"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{vm.Snapshots.Count} snapshots {deepest} deep, the oldest '{oldest.Name}',");

        var consequence = willFill
            ? " It is already larger than the free space on a datastore it lives on, so " +
              "deleting it needs planning rather than a click: consolidation itself needs room."
            : " Nothing is wrong yet. A snapshot nobody deletes grows until the datastore " +
              "fills, and then every machine on that volume stops at once.";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{vm.Name}' has {count} {how}, occupying {size}.{consequence}");
    }

    /// <summary>
    /// Old enough that somebody has forgotten it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot taken before a change is meant to live for hours. Three days
    /// is generous enough that a change window spanning a weekend does not
    /// raise it, and short enough to catch the thing while it is still small.
    /// </para>
    /// <para>
    /// This number is ours. No vendor document states a snapshot age: the
    /// Security Configuration Guide has no snapshot control at all, and
    /// Performance Best Practices mentions snapshots twenty-two times without
    /// ever giving a duration. The "72 hours" sometimes attributed to VMware
    /// is that document's physical-memory burn-in test, which is about
    /// hardware, not snapshots. Said plainly here so nobody later cites a
    /// source that does not exist.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StaleSnapshotWarning = TimeSpan.FromDays(3);

    /// <summary>Two weeks. By now nobody remembers taking it.</summary>
    /// <remarks>
    /// Borrowed rather than invented: fourteen days is the default of vCheck's
    /// <c>60 VM/02 Snapshot Information</c> plugin (<c>$SnapshotAge = 14</c>),
    /// which is the closest thing this field has to an agreed number. It is a
    /// community project, not a vendor, and the product should say so wherever
    /// it shows the threshold.
    /// </remarks>
    private static readonly TimeSpan StaleSnapshotCritical = TimeSpan.FromDays(14);

    private List<IdentityMark> MarksFor(VsphereHost host)
    {
        var marks = new List<IdentityMark>();

        if (!string.IsNullOrWhiteSpace(host.HardwareUuid))
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.HardwareUuid, host.HardwareUuid, InstanceId));
        }

        foreach (var ip in host.IpAddresses.Where(ip => !string.IsNullOrWhiteSpace(ip)))
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.IpAddress, ip, InstanceId));
        }

        // vCenter names a host by whatever it was added as: an FQDN, a short
        // name, or a bare IP address. Which of those it is has to be decided
        // here, because the resolver weighs the kinds differently.
        var name = string.IsNullOrWhiteSpace(host.Fqdn) ? host.Name : host.Fqdn;
        marks.AddRange(MarksForName(name));

        return marks;
    }

    /// <summary>
    /// Classifies whatever vCenter calls a host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An address is checked for first. It contains dots, so a naive FQDN test
    /// accepts it — and then derives a "short hostname" from the text before
    /// the first dot, which for <c>10.5.1.76</c> is <c>10</c>. Every host in
    /// the network would carry that same mark, which is exactly the kind of
    /// worthless evidence that turns into a wrong match.
    /// </para>
    /// <para>
    /// Found by running against a live vCenter whose hosts are registered by
    /// address.
    /// </para>
    /// </remarks>
    private IEnumerable<IdentityMark> MarksForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        var trimmed = name.Trim();

        if (System.Net.IPAddress.TryParse(trimmed, out _))
        {
            yield return IdentityMark.Create(IdentityMarkKind.IpAddress, trimmed, InstanceId);
            yield break;
        }

        var firstDot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (firstDot > 0)
        {
            yield return IdentityMark.Create(IdentityMarkKind.Fqdn, trimmed, InstanceId);
            yield return IdentityMark.Create(
                IdentityMarkKind.ShortHostname, trimmed[..firstDot], InstanceId);
        }
        else
        {
            yield return IdentityMark.Create(IdentityMarkKind.ShortHostname, trimmed, InstanceId);
        }
    }

    private static string InstanceIdOf(VsphereInventoryPayload payload) => payload.VCenterName;

    private static Relationship Edge(EntityId from, EntityId to, RelationshipKind kind, DateTimeOffset now) =>
        new() { From = from, To = to, Kind = kind, ObservedAtUtc = now };

    private static bool IsReachable(string? connectionState) =>
        string.Equals(connectionState, "connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps vSphere's health colour.
    /// </summary>
    /// <remarks>
    /// Gray means vSphere itself does not know, and an unrecognised value means
    /// we do not — both are Unknown. Defaulting an unfamiliar colour to healthy
    /// is how a monitoring product ends up green during an outage.
    /// </remarks>
    private static HealthState MapStatus(string? overallStatus) =>
        overallStatus?.Trim().ToLowerInvariant() switch
        {
            "green" => HealthState.Healthy,
            "yellow" => HealthState.Warning,
            "red" => HealthState.Critical,
            _ => HealthState.Unknown,
        };

    private static CollectionFailure ToFailure(VsphereReadFailure failure) => new()
    {
        Kind = failure.IsPermissionDenied
            ? CollectionFailureKind.AuthorizationDenied
            : CollectionFailureKind.ProtocolError,
        Target = failure.Target,
        Detail = failure.Detail,
    };
}
