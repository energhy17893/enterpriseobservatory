using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The <c>Entity.Settings</c> keys collection PR 1 adds.
/// </summary>
/// <remarks>
/// <para>
/// "Export first, compute later": verdicts vCenter already computes and the
/// read-only role can read, plus the inputs of M8.4 (maintenance and vMotion
/// blockers) and M8.7 (expiry radar). Carried, never judged — the checks come
/// after K2 as findings.
/// </para>
/// <para>
/// camelCase with a dot, so no key can collide with a host advanced setting
/// (<c>Syslog.global.logHost</c>) or a cluster HA key (<c>dasConfig.*</c>)
/// sharing the same dictionary. A key that is absent means "not read"; a
/// count of zero means "read, and none".
/// </para>
/// <para>
/// Every source path was seen live before it was requested:
/// docs/measurements/collection-pr1-shapes.md.
/// </para>
/// </remarks>
public static class InventoryVerdicts
{
    /// <summary>
    /// Host or VM: when the configuration tier read what this entity's
    /// configuration keys say, ISO-8601 UTC. Absent when none is carried.
    /// </summary>
    public const string ConfigurationReadAtUtc = "configuration.readAtUtc";
    /// <summary>How many <c>configIssue</c> events vCenter holds; every type.</summary>
    public const string ConfigIssueCount = "configIssue.count";

    /// <summary>
    /// Their event types, distinct, comma-separated. Absent when there are
    /// none. <strong>Entry shape not validated live</strong> — every object
    /// on the measured estate had an empty list.
    /// </summary>
    public const string ConfigIssueTypes = "configIssue.types";

    /// <summary>Host: numeric hardware sensors reported.</summary>
    public const string HardwareSensorCount = "hardwareHealth.sensors";

    public const string HardwareSensorsGreen = "hardwareHealth.sensors.green";

    public const string HardwareSensorsYellow = "hardwareHealth.sensors.yellow";

    public const string HardwareSensorsRed = "hardwareHealth.sensors.red";

    public const string HardwareSensorsUnknown = "hardwareHealth.sensors.unknown";

    /// <summary>
    /// Host: sensors and CPU/memory/storage status items that are red or
    /// yellow, as <c>name (state)</c> joined by <c>; </c>. Absent when none.
    /// </summary>
    public const string HardwareAlerting = "hardwareHealth.alerting";

    /// <summary>Host or vCenter: the certificate's notAfter, ISO-8601 UTC (M8.7).</summary>
    public const string CertificateNotAfter = "certificate.notAfter";

    /// <summary>
    /// Host or vCenter: the certificate's SHA-256 fingerprint, upper-case hex
    /// (M8.7). With <see cref="CertificateNotAfter"/>, all that is kept of a
    /// certificate: the certificate itself is never stored.
    /// </summary>
    public const string CertificateSha256 = "certificate.sha256";

    /// <summary>VM: <c>runtime.connectionState</c> as vCenter words it.</summary>
    public const string ConnectionState = "connectionState";

    /// <summary>VM: <c>runtime.consolidationNeeded</c>, <c>true</c>/<c>false</c> (M8.4).</summary>
    public const string ConsolidationNeeded = "consolidationNeeded";

    /// <summary>VM: CD drives currently connected, any backing (M8.4).</summary>
    public const string ConnectedCdroms = "cdrom.connected";

    /// <summary>VM: of those, the ones backed by an ISO file (M8.4).</summary>
    public const string ConnectedIsoCdroms = "cdrom.connectedIso";

    /// <summary>
    /// VM: legacy E1000 network adapters (<c>config.hardware.device</c>), by
    /// count. Set to <c>0</c>, not left absent, when the device list was read
    /// and none were found (eo-bestpractice: legacy virtual adapters).
    /// </summary>
    public const string LegacyAdapterE1000 = "adapter.legacy.e1000";

    /// <summary>VM: legacy E1000e network adapters. See <see cref="LegacyAdapterE1000"/>.</summary>
    public const string LegacyAdapterE1000e = "adapter.legacy.e1000e";

    /// <summary>VM: legacy LSI Logic Parallel SCSI controllers. See <see cref="LegacyAdapterE1000"/>.</summary>
    public const string LegacyAdapterLsiLogic = "adapter.legacy.lsiLogic";

    /// <summary>
    /// VM: how many VMware snapshots its <c>snapshot</c> tree holds, <c>0</c> when
    /// it has none (eo-simplivity). vCenter leaves the property out on a VM
    /// with no snapshot, which is why a retrieved VM without it counts zero --
    /// the same reading the stale-snapshot alarm has always made.
    /// </summary>
    public const string SnapshotCount = "snapshot.count";

    /// <summary>
    /// Host: <c>config.powerSystemInfo.currentPolicy.shortName</c> as vCenter
    /// words it (<c>static</c> = High Performance, <c>dynamic</c> = Balanced,
    /// <c>low</c>, <c>custom</c>) (eo-bestpractice, P3b).
    /// </summary>
    public const string PowerPolicy = "power.policy";

    /// <summary>
    /// Host: physical cores per NUMA node, <c>hardware.cpuInfo.numCpuCores</c>
    /// divided by <c>hardware.numaInfo.numNodes</c>. Absent unless both were read.
    /// </summary>
    public const string NumaCoresPerNode = "numa.coresPerNode";

    /// <summary>VM: <c>config.cpuHotAddEnabled</c>, <c>true</c>/<c>false</c>.</summary>
    public const string CpuHotAddEnabled = "cpuHotAdd.enabled";

    /// <summary>VM: <c>config.version</c>, the virtual hardware version (<c>vmx-19</c>).</summary>
    public const string HardwareVersion = "hardware.version";

    /// <summary>Cluster: <c>true</c> when a current EVC mode is set (M8.4).</summary>
    public const string EvcEnabled = "evc.enabled";

    /// <summary>Cluster: the current EVC mode key, when EVC is enabled.</summary>
    public const string EvcModeKey = "evc.modeKey";

    /// <summary>
    /// Host: <c>summary.maxEVCModeKey</c>, the newest EVC mode its CPU can
    /// run, as vCenter words it (collection PR 2). Absent when not read — a
    /// disconnected host does not report one.
    /// </summary>
    public const string HostMaxEvcModeKey = "evc.maxModeKey";

    /// <summary>Datastore: <c>summary.maintenanceMode</c> as vCenter words it.</summary>
    public const string MaintenanceMode = "maintenanceMode";

    /// <summary>Datastore: hosts that have it mounted (M8.4: one pins its VMs).</summary>
    public const string MountedHostCount = "mountedHosts";

    /// <summary>
    /// VM: <c>true</c> when its <c>customValue</c> and the custom field
    /// definitions were both read (M8.8). Absent means "not read", which is
    /// not "no backup attribute".
    /// </summary>
    public const string BackupRead = "backup.read";

    /// <summary>VM: the name of the last-backup custom attribute the time came from.</summary>
    public const string BackupField = "backup.field";

    /// <summary>VM: that attribute's value as the backup product wrote it.</summary>
    public const string BackupValue = "backup.value";

    /// <summary>VM: the value read as a time, ISO-8601 UTC; absent when it did not read.</summary>
    public const string BackupLastUtc = "backup.lastUtc";

    /// <summary>VM: the clock the value was read in, since it carries no offset.</summary>
    public const string BackupTimeBasis = "backup.timeBasis";

    // S2b, eo-simplivity (docs/measurements/s2b-cross-env-shapes.md).

    /// <summary>VM: <c>config.memoryAllocation.reservation</c>, in MB as vCenter sends it.</summary>
    public const string MemoryReservationMb = "memory.reservationMb";

    /// <summary>
    /// VM: the resource pool it sits in; cluster: its root resource pool.
    /// A moRef. Absent on a template, which has none.
    /// </summary>
    public const string ResourcePool = "resourcePool";

    /// <summary>
    /// Host: each vmkernel adapter on a standard port group as
    /// <c>portgroup=mtu</c>, one per line; empty when it has none. An adapter
    /// on a distributed port has no port group name and is left out.
    /// </summary>
    public const string VmkernelMtu = "vmk.mtu";

    /// <summary>
    /// Host: each standard port group as <c>portgroup=mtu</c> of the standard
    /// switch that carries it, one per line; empty when it has no standard switch.
    /// </summary>
    public const string PortGroupSwitchMtu = "vswitch.portgroupMtu";

    /// <summary>
    /// Host in lockdown mode: its lockdown exception users, one per line;
    /// empty when there are none. Absent when the host is not in lockdown
    /// mode (not asked) or the call was refused.
    /// </summary>
    public const string LockdownExceptions = "lockdown.exceptions";
}

/// <summary>Reads <see cref="InventoryVerdicts"/> out of one retrieved object.</summary>
public static class InventoryVerdictParser
{
    public const string ConfigIssuePath = "configIssue";
    public const string HealthSystemRuntimePath = "runtime.healthSystemRuntime";
    public const string CertificatePath = "config.certificate";
    public const string ConnectionStatePath = "runtime.connectionState";
    public const string ConsolidationNeededPath = "runtime.consolidationNeeded";
    public const string DevicePath = "config.hardware.device";

    /// <summary>
    /// Requested whole: <c>summary.currentEVCModeKey</c> is refused as
    /// InvalidProperty (measured), because the property is declared as
    /// <c>ComputeResourceSummary</c> — the same reason as <c>configurationEx</c>.
    /// </summary>
    public const string ClusterSummaryPath = "summary";

    /// <summary>
    /// A sub-path of <c>HostSystem.summary</c>, which is declared as
    /// <c>HostListSummary</c> — a concrete type, unlike the cluster's — and
    /// accepted live on 10 of 10 hosts (docs/measurements/collection-pr2-shapes.md).
    /// </summary>
    public const string HostMaxEvcModePath = "summary.maxEVCModeKey";

    // P3b (docs/measurements/p3b-bestpractice-shapes.md): scalar sub-paths,
    // each read alone live without a fault. currentPolicy whole arrives
    // flattened without field names; numaInfo whole is ~9.5 KB a host, most
    // of it pciId lists nothing reads.
    public const string HostPowerPolicyPath = "config.powerSystemInfo.currentPolicy.shortName";
    public const string HostNumaNodesPath = "hardware.numaInfo.numNodes";
    public const string HostCpuCoresPath = "hardware.cpuInfo.numCpuCores";
    public const string VmCpuHotAddPath = "config.cpuHotAddEnabled";
    public const string VmHardwareVersionPath = "config.version";

    public const string MaintenanceModePath = "summary.maintenanceMode";
    public const string DatastoreHostPath = "host";

    /// <summary>
    /// Beside <c>config.memoryAllocation.limit</c>. Measured the same as
    /// <c>resourceConfig.memoryAllocation.reservation</c> on 1100 of 1100 VMs,
    /// and 8 bytes a VM smaller.
    /// </summary>
    public const string MemoryReservationPath = "config.memoryAllocation.reservation";

    public const string ResourcePoolPath = "resourcePool";
    public const string VnicPath = "config.network.vnic";
    public const string VswitchPath = "config.network.vswitch";
    public const string HostAccessManagerPath = "configManager.hostAccessManager";

    private const string PortGroupKeyPrefix = "key-vim.host.PortGroup-";

    private static readonly string[] SensorStates = ["green", "yellow", "red", "unknown"];

    public static IReadOnlyDictionary<string, string> Read(PropertyObject o)
    {
        ArgumentNullException.ThrowIfNull(o);

        var verdicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ReadConfigIssues(o, verdicts);

        switch (o.Type)
        {
            case "HostSystem":
                ReadHardwareHealth(o, verdicts);
                ReadCertificate(o, verdicts);
                CopyValue(o, HostMaxEvcModePath, InventoryVerdicts.HostMaxEvcModeKey, verdicts);
                CopyValue(o, HostPowerPolicyPath, InventoryVerdicts.PowerPolicy, verdicts);
                ReadNumaCoresPerNode(o, verdicts);
                ReadVmkernelMtu(o, verdicts);
                break;

            case "VirtualMachine":
                CopyValue(o, ConnectionStatePath, InventoryVerdicts.ConnectionState, verdicts);
                CopyValue(o, ConsolidationNeededPath, InventoryVerdicts.ConsolidationNeeded, verdicts);
                ReadCdroms(o, verdicts);
                ReadLegacyAdapters(o, verdicts);
                CopyValue(o, VmCpuHotAddPath, InventoryVerdicts.CpuHotAddEnabled, verdicts);
                CopyValue(o, VmHardwareVersionPath, InventoryVerdicts.HardwareVersion, verdicts);
                // Absent = 0 is correct here, not a guess: vSphere does not send an
                // unset property at all, and a VM with no snapshot has 'snapshot' unset.
                // The property is in the VM request, so a VM that arrived was asked.
                verdicts[InventoryVerdicts.SnapshotCount] = Count(VsphereClient.ReadSnapshots(o).Count);
                CopyValue(o, MemoryReservationPath, InventoryVerdicts.MemoryReservationMb, verdicts);
                CopyValue(o, ResourcePoolPath, InventoryVerdicts.ResourcePool, verdicts);
                break;

            case "ClusterComputeResource":
                ReadEvc(o, verdicts);
                CopyValue(o, ResourcePoolPath, InventoryVerdicts.ResourcePool, verdicts);
                break;

            case "Datastore":
                CopyValue(o, MaintenanceModePath, InventoryVerdicts.MaintenanceMode, verdicts);
                ReadMounts(o, verdicts);
                break;
        }

        return verdicts;
    }

    private static void CopyValue(
        PropertyObject o, string path, string key, Dictionary<string, string> verdicts)
    {
        if (PropertyCollectorParser.ReadString(o.Values, path) is { } value)
        {
            verdicts[key] = value;
        }
    }

    /// <summary>
    /// An empty array arrives as an empty value (measured); a non-empty one
    /// as a structure whose elements are typed events.
    /// </summary>
    private static void ReadConfigIssues(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (o.Structures.TryGetValue(ConfigIssuePath, out var issues))
        {
            verdicts[InventoryVerdicts.ConfigIssueCount] = Count(issues.Count);

            // Every Event array element declares its concrete subtype. An entry
            // without one is a shape nobody has seen live, and its element name
            // is not an event type — so the types are left unread rather than
            // reported from a tag name. The count still stands: an entry exists.
            var types = issues
                .Select(i => i.Type)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (types.Count > 0 && types.All(t => t.Length > 0))
            {
                verdicts[InventoryVerdicts.ConfigIssueTypes] = string.Join(", ", types);
            }
        }
        else if (o.Values.TryGetValue(ConfigIssuePath, out var flat))
        {
            // Events always have children, so a flat value is the empty list.
            // Anything else would be a shape nobody has seen; counting it as
            // zero would be a guess, so it is left unread.
            if (flat.Length == 0)
            {
                verdicts[InventoryVerdicts.ConfigIssueCount] = "0";
            }
        }
    }

    /// <summary>
    /// Measured: a <c>HealthSystemRuntime</c> with <c>systemHealthInfo</c>
    /// (749 <c>numericSensorInfo</c> on 10 hosts, <c>healthState.key</c>
    /// lower-case) and <c>hardwareStatusInfo</c> (<c>cpuStatusInfo</c>,
    /// <c>memoryStatusInfo</c>, <c>status.key</c>). <c>storageStatusInfo</c>
    /// was absent on the measured estate and is read by the same rule.
    /// </summary>
    private static void ReadHardwareHealth(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Structures.TryGetValue(HealthSystemRuntimePath, out var runtime))
        {
            return;
        }

        var sensors = runtime
            .Where(n => n.Name == "systemHealthInfo")
            .SelectMany(n => n.All("numericSensorInfo"))
            .ToList();

        var alerting = new List<string>();

        if (runtime.Any(n => n.Name == "systemHealthInfo"))
        {
            verdicts[InventoryVerdicts.HardwareSensorCount] = Count(sensors.Count);

            var byState = sensors
                .GroupBy(s => State(s.Child("healthState")), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (var state in SensorStates)
            {
                verdicts[$"{InventoryVerdicts.HardwareSensorCount}.{state}"] =
                    Count(byState.GetValueOrDefault(state));
            }

            alerting.AddRange(sensors
                .Select(s => (s.TextOf("name"), State(s.Child("healthState"))))
                .Where(s => s.Item2 is "red" or "yellow")
                .Select(s => $"{s.Item1} ({s.Item2})"));
        }

        alerting.AddRange(runtime
            .Where(n => n.Name == "hardwareStatusInfo")
            .SelectMany(n => n.Children)
            .Select(item => (item.TextOf("name"), State(item.Child("status"))))
            .Where(s => s.Item2 is "red" or "yellow")
            .Select(s => $"{s.Item1} ({s.Item2})"));

        if (alerting.Count > 0)
        {
            verdicts[InventoryVerdicts.HardwareAlerting] = string.Join("; ", alerting);
        }

        static string State(PropertyNode? description) =>
            description?.TextOf("key").ToLowerInvariant() switch
            {
                "green" => "green",
                "yellow" => "yellow",
                "red" => "red",
                _ => "unknown",
            };
    }

    /// <summary>
    /// <c>config.certificate</c> is <c>xsd:byte[]</c>: one signed element per
    /// byte, flattened by the parser into a separated list (measured: 10 of 10
    /// hosts parse as X.509). The expiry is read from the certificate itself.
    /// </summary>
    private static void ReadCertificate(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Values.TryGetValue(CertificatePath, out var raw) || raw.Length == 0)
        {
            return;
        }

        try
        {
            var bytes = PropertyCollectorParser.SplitValues(raw)
                .Select(b => unchecked((byte)sbyte.Parse(b, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)))
                .ToArray();

            using var certificate = X509CertificateLoader.LoadCertificate(bytes);

            foreach (var (key, value) in CertificateVerdicts(certificate))
            {
                verdicts[key] = value;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or CryptographicException)
        {
            // Not a certificate this reader understands. No expiry is better
            // than a guessed one: a radar that invents dates is worse than none.
        }
    }

    /// <summary>
    /// What is kept of a certificate: its expiry and its SHA-256 fingerprint,
    /// never the certificate itself (M8.7). Shared by the host's
    /// <c>config.certificate</c> and the vCenter endpoint's handshake.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CertificateVerdicts(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [InventoryVerdicts.CertificateNotAfter] =
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero)
                    .ToString("o", CultureInfo.InvariantCulture),
            [InventoryVerdicts.CertificateSha256] = Convert.ToHexString(SHA256.HashData(certificate.RawData)),
        };
    }

    private static void ReadCdroms(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Structures.TryGetValue(DevicePath, out var devices))
        {
            return;
        }

        var connected = devices
            .Where(d => d.Type == "VirtualCdrom")
            .Where(d => string.Equals(
                d.Child("connectable")?.TextOf("connected"), "true", StringComparison.OrdinalIgnoreCase))
            .ToList();

        verdicts[InventoryVerdicts.ConnectedCdroms] = Count(connected.Count);
        verdicts[InventoryVerdicts.ConnectedIsoCdroms] =
            Count(connected.Count(d => d.TypeOf("backing") == "VirtualCdromIsoBackingInfo"));
    }

    /// <summary>
    /// Legacy virtual adapters (eo-bestpractice: memory/legacy-adapters PR):
    /// E1000/E1000e network cards and LSI Logic Parallel SCSI controllers,
    /// each declared by its own xsi:type on the same device list
    /// <see cref="ReadCdroms"/> already reads. Counted, not just flagged, so
    /// the aggregated finding can say how many. Set to <c>0</c> rather than
    /// left absent when the device list was read and none were found -- the
    /// same "read means present, even as zero" rule as the CD/DVD count.
    /// </summary>
    private static void ReadLegacyAdapters(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Structures.TryGetValue(DevicePath, out var devices))
        {
            return;
        }

        verdicts[InventoryVerdicts.LegacyAdapterE1000] = Count(devices.Count(d => d.Type == "VirtualE1000"));
        verdicts[InventoryVerdicts.LegacyAdapterE1000e] = Count(devices.Count(d => d.Type == "VirtualE1000e"));
        verdicts[InventoryVerdicts.LegacyAdapterLsiLogic] =
            Count(devices.Count(d => d.Type == "VirtualLsiLogicController"));
    }

    /// <summary>
    /// Physical cores per NUMA node: the width a VM's vCPUs must fit in to stay
    /// on one node (vSphere 8.0 U3 Performance Best Practices, vNUMA). Nodes
    /// are taken as equal, as every measured host's were.
    /// </summary>
    private static void ReadNumaCoresPerNode(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (PropertyCollectorParser.ReadLong(o.Values, HostCpuCoresPath) is { } cores &&
            PropertyCollectorParser.ReadLong(o.Values, HostNumaNodesPath) is { } nodes && nodes > 0)
        {
            verdicts[InventoryVerdicts.NumaCoresPerNode] = (cores / nodes).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void ReadEvc(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Structures.TryGetValue(ClusterSummaryPath, out var summary))
        {
            return;
        }

        var key = summary.FirstOrDefault(n => n.Name == "currentEVCModeKey")?.Text;

        verdicts[InventoryVerdicts.EvcEnabled] = string.IsNullOrWhiteSpace(key) ? "false" : "true";

        if (!string.IsNullOrWhiteSpace(key))
        {
            verdicts[InventoryVerdicts.EvcModeKey] = key;
        }
    }

    /// <summary>
    /// Measured: <c>HostVirtualNic</c> with <c>portgroup</c> (empty on a
    /// distributed port) and <c>spec.mtu</c>; <c>HostVirtualSwitch</c> with
    /// <c>mtu</c> and its port groups as <c>key-vim.host.PortGroup-&lt;name&gt;</c>
    /// keys. An empty array arrives as an empty value, so a read with no
    /// adapter or no standard switch is an empty verdict, not an absent one.
    /// </summary>
    private static void ReadVmkernelMtu(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (o.Structures.TryGetValue(VnicPath, out var vnics))
        {
            verdicts[InventoryVerdicts.VmkernelMtu] = Lines(vnics
                .Where(n => n.TextOf("portgroup").Length > 0 && n.Child("spec")?.TextOf("mtu") is { Length: > 0 })
                .Select(n => $"{n.TextOf("portgroup")}={n.Child("spec")!.TextOf("mtu")}"));
        }
        else if (o.Values.TryGetValue(VnicPath, out var flat) && flat.Length == 0)
        {
            verdicts[InventoryVerdicts.VmkernelMtu] = string.Empty;
        }

        if (o.Structures.TryGetValue(VswitchPath, out var switches))
        {
            verdicts[InventoryVerdicts.PortGroupSwitchMtu] = Lines(switches
                .Where(s => s.TextOf("mtu").Length > 0)
                .SelectMany(s => s.All("portgroup")
                    .Where(p => p.Text.StartsWith(PortGroupKeyPrefix, StringComparison.Ordinal))
                    .Select(p => $"{p.Text[PortGroupKeyPrefix.Length..]}={s.TextOf("mtu")}")));
        }
        else if (o.Values.TryGetValue(VswitchPath, out var flat) && flat.Length == 0)
        {
            verdicts[InventoryVerdicts.PortGroupSwitchMtu] = string.Empty;
        }

        static string Lines(IEnumerable<string> lines) => string.Join('\n', lines);
    }

    private static void ReadMounts(PropertyObject o, Dictionary<string, string> verdicts)
    {
        if (!o.Structures.TryGetValue(DatastoreHostPath, out var mounts))
        {
            return;
        }

        // mounted is optional in the schema; it was present on 302 of 302
        // measured mounts. Missing reads as mounted, the way vCenter's own UI
        // lists a host under a datastore.
        verdicts[InventoryVerdicts.MountedHostCount] = Count(mounts.Count(m =>
            !string.Equals(m.Child("mountInfo")?.TextOf("mounted"), "false", StringComparison.OrdinalIgnoreCase)));
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
