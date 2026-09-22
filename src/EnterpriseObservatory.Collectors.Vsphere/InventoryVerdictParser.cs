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

    public const string MaintenanceModePath = "summary.maintenanceMode";
    public const string DatastoreHostPath = "host";

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
                break;

            case "VirtualMachine":
                CopyValue(o, ConnectionStatePath, InventoryVerdicts.ConnectionState, verdicts);
                CopyValue(o, ConsolidationNeededPath, InventoryVerdicts.ConsolidationNeeded, verdicts);
                ReadCdroms(o, verdicts);
                break;

            case "ClusterComputeResource":
                ReadEvc(o, verdicts);
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
