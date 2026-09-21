using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which HA-protected clusters are not actually protected.
/// </summary>
/// <remarks>
/// <para>
/// A cluster is added to vCenter with HA turned on once, at build time, and
/// then nobody looks at it again until a host dies and the workloads it held
/// do not come back. Every finding here is a setting that silently defeats
/// the reason HA exists while leaving the cluster looking green: HA itself
/// can be enabled while admission control is off, while host monitoring is
/// off, while the cluster has one heartbeat datastore instead of two, or
/// while vCenter's own warning about a non-redundant management network has
/// been switched off rather than fixed. None of these read as a health
/// problem on the cluster tile, because none of them are — until the day a
/// host fails and the promise the cluster was configured to keep is not the
/// one it can actually keep.
/// </para>
/// <para>
/// Reads <c>Entity.Settings</c> rather than a typed configuration object, for
/// the same reason <see cref="RemoteLogging"/> does. The setting names are
/// held as policy rather than compiled in, also for the reason
/// <see cref="RemoteLoggingPolicy"/> gives: the application layer must not
/// depend on what vSphere calls something, so this file does not reference
/// the collector's <c>ClusterHaSettings</c> constants directly -- it carries
/// its own defaults, which the collector's constants are required to match.
/// </para>
/// <para>
/// Every check is silent on a cluster whose configuration could not be read.
/// An absent key is a collection failure with its own channel, not a finding
/// about a cluster nobody looked at -- see <see cref="RemoteLogging"/>'s
/// remarks for why that distinction is load-bearing rather than a nicety.
/// </para>
/// </remarks>
public sealed record ClusterHighAvailabilityPolicy
{
    public static ClusterHighAvailabilityPolicy Default { get; } = new();

    /// <summary>Whether vSphere HA is enabled. <c>ClusterDasConfigInfo.enabled</c>.</summary>
    public string EnabledSetting { get; init; } = "dasConfig.enabled";

    /// <summary>
    /// Whether strict admission control is enabled.
    /// <c>ClusterDasConfigInfo.admissionControlEnabled</c>.
    /// </summary>
    public string AdmissionControlEnabledSetting { get; init; } = "dasConfig.admissionControlEnabled";

    /// <summary>
    /// <c>enabled</c> or <c>disabled</c>. <c>ClusterDasConfigInfo.hostMonitoring</c>.
    /// </summary>
    public string HostMonitoringSetting { get; init; } = "dasConfig.hostMonitoring";

    /// <summary>
    /// The cluster-wide default response to an All-Paths-Down storage
    /// failure. <c>ClusterDasConfigInfo.defaultVmSettings
    /// .vmComponentProtectionSettings.vmStorageProtectionForAPD</c>.
    /// </summary>
    public string ApdResponseSetting { get; init; } =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForAPD";

    /// <summary>
    /// The cluster-wide default response to a Permanent-Device-Loss storage
    /// failure. <c>ClusterDasConfigInfo.defaultVmSettings
    /// .vmComponentProtectionSettings.vmStorageProtectionForPDL</c>.
    /// </summary>
    public string PdlResponseSetting { get; init; } =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForPDL";

    /// <summary>
    /// How many datastores are configured for storage heartbeating, as a
    /// count rather than as the datastores themselves.
    /// </summary>
    public string HeartbeatDatastoreCountSetting { get; init; } = "dasConfig.heartbeatDatastore.count";

    /// <summary>
    /// The advanced HA option that silences vCenter's own warning about a
    /// non-redundant management network.
    /// </summary>
    public string IgnoreRedundantNetworkWarningSetting { get; init; } =
        "dasConfig.option.das.ignoreRedundantNetWarning";

    /// <summary>
    /// The fewest heartbeat datastores a cluster should have configured.
    /// </summary>
    /// <remarks>
    /// vSphere's own number, not invented here: the vSphere Availability guide
    /// and VMware KB 2004739 both recommend at least two heartbeat datastores,
    /// because with only one, losing that single datastore's connectivity
    /// removes HA's only way to distinguish a network-partitioned host from
    /// one that has actually failed on the storage channel too.
    /// </remarks>
    public int MinimumHeartbeatDatastores { get; init; } = 2;
}

/// <summary>Reports HA-protected clusters whose protection has a hole in it.</summary>
public static class ClusterHighAvailability
{
    private const string Category = "Configuration";
    private const string Platform = "platform";

    public const string RuleId = "cluster-ha-scorecard";

    private static readonly HashSet<string> Disabled =
        new(StringComparer.OrdinalIgnoreCase) { "disabled" };

    /// <summary>Runs every check below over every live cluster.</summary>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Entity> entities,
        ClusterHighAvailabilityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var rules = policy ?? ClusterHighAvailabilityPolicy.Default;
        var alerts = new List<AlertDefinition>();

        foreach (var cluster in entities)
        {
            if (cluster.Kind != EntityKind.Cluster ||
                cluster.ObservationState == ObservationState.Vanished)
            {
                continue;
            }

            HaDisabled(cluster, rules, alerts);
            AdmissionControlDisabled(cluster, rules, alerts);
            HostMonitoringDisabled(cluster, rules, alerts);
            StorageProtectionDisabled(cluster, rules, alerts);
            TooFewHeartbeatDatastores(cluster, rules, alerts);
            RedundantNetworkWarningSilenced(cluster, rules, alerts);
        }

        return alerts;
    }

    /// <summary>
    /// HA turned off. The one finding here that is not subtle: nothing about
    /// this cluster will restart on a host failure at all.
    /// </summary>
    private static void HaDisabled(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        if (!cluster.Settings.TryGetValue(rules.EnabledSetting, out var value) ||
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Add(alerts, cluster, "ha-disabled", AlertSeverity.Critical, "Cluster has no vSphere HA protection",
            $"'{cluster.DisplayName}' has vSphere HA turned off. A host failing on this cluster " +
            "does not cause its virtual machines to restart anywhere -- they stay down until " +
            "somebody notices and powers them on by hand.");
    }

    /// <summary>
    /// HA on, admission control off: the cluster will attempt to fail
    /// everything over, whether or not the survivors have room for it.
    /// </summary>
    private static void AdmissionControlDisabled(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        if (!cluster.Settings.TryGetValue(rules.AdmissionControlEnabledSetting, out var value) ||
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Add(alerts, cluster, "admission-control-disabled", AlertSeverity.Warning,
            "Cluster admission control is disabled",
            $"'{cluster.DisplayName}' has HA admission control turned off, so vCenter is not " +
            "reserving failover capacity on the surviving hosts. A host failure can still trigger " +
            "restarts that the remaining hosts do not have the CPU or memory for, which turns one " +
            "host's failure into contention across the whole cluster.");
    }

    /// <summary>Host monitoring off: HA cannot see a host has failed at all.</summary>
    private static void HostMonitoringDisabled(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        if (!cluster.Settings.TryGetValue(rules.HostMonitoringSetting, out var value) ||
            !Disabled.Contains(value))
        {
            return;
        }

        Add(alerts, cluster, "host-monitoring-disabled", AlertSeverity.Critical,
            "Cluster host monitoring is disabled",
            $"'{cluster.DisplayName}' has HA host monitoring turned off. HA is enabled but will not " +
            "restart virtual machines after a host failure, because the mechanism that detects the " +
            "failure is the part that is switched off. This is the setting VMware asks an " +
            "administrator to disable only for the duration of a planned network change, and it is " +
            "meant to be turned back on immediately afterwards.");
    }

    /// <summary>
    /// The cluster-wide default response to a storage path going away is
    /// switched off, on a cluster otherwise configured for HA.
    /// </summary>
    private static void StorageProtectionDisabled(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        var apd = cluster.Settings.GetValueOrDefault(rules.ApdResponseSetting);
        var pdl = cluster.Settings.GetValueOrDefault(rules.PdlResponseSetting);

        var apdDisabled = apd is not null && Disabled.Contains(apd);
        var pdlDisabled = pdl is not null && Disabled.Contains(pdl);

        if (!apdDisabled && !pdlDisabled)
        {
            return;
        }

        var which = (apdDisabled, pdlDisabled) switch
        {
            (true, true) => "an All-Paths-Down and a Permanent-Device-Loss",
            (true, false) => "an All-Paths-Down",
            _ => "a Permanent-Device-Loss",
        };

        Add(alerts, cluster, "storage-protection-disabled", AlertSeverity.Warning,
            "Cluster storage failure protection is disabled",
            $"'{cluster.DisplayName}' has VM Component Protection's response to {which} storage " +
            "failure set to disabled. A datastore this cluster's virtual machines depend on going " +
            "away does not trigger a restart anywhere; the machines that lost storage simply hang " +
            "until somebody intervenes.");
    }

    /// <summary>
    /// Fewer heartbeat datastores than vCenter itself recommends -- one
    /// datastore's connectivity problem removes the whole storage channel HA
    /// uses to tell a partitioned host from a dead one.
    /// </summary>
    private static void TooFewHeartbeatDatastores(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        if (!cluster.Settings.TryGetValue(rules.HeartbeatDatastoreCountSetting, out var raw) ||
            !int.TryParse(raw, out var count) ||
            count >= rules.MinimumHeartbeatDatastores)
        {
            return;
        }

        Add(alerts, cluster, "too-few-heartbeat-datastores", AlertSeverity.Warning,
            "Cluster has too few HA heartbeat datastores",
            $"'{cluster.DisplayName}' has {count} heartbeat datastore(s) configured for HA, " +
            $"fewer than the {rules.MinimumHeartbeatDatastores} VMware recommends (KB 2004739). " +
            "With only one, losing that datastore's connectivity removes HA's only way to " +
            "distinguish a network-partitioned host from one that has actually failed.");
    }

    /// <summary>
    /// The advanced option that silences vCenter's own warning about a
    /// non-redundant management network. Set to true, it does not fix the
    /// condition -- it only stops vCenter from saying so.
    /// </summary>
    private static void RedundantNetworkWarningSilenced(
        Entity cluster, ClusterHighAvailabilityPolicy rules, List<AlertDefinition> alerts)
    {
        if (!cluster.Settings.TryGetValue(rules.IgnoreRedundantNetworkWarningSetting, out var value) ||
            !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Add(alerts, cluster, "redundant-network-warning-silenced", AlertSeverity.Warning,
            "Cluster hides its HA network redundancy warning",
            $"'{cluster.DisplayName}' has the advanced option " +
            "das.ignoreRedundantNetWarning set to true, which turns off vCenter's own warning " +
            "that this cluster's HA management network has no redundant path. The underlying risk " +
            "is unchanged: HA's isolation response depends on that redundancy to tell a partitioned " +
            "host from a dead one, and this setting only makes the condition invisible rather than " +
            "fixing it. This is a hidden risk, not a resolved one.");
    }

    private static void Add(
        List<AlertDefinition> alerts,
        Entity cluster,
        string findingKey,
        AlertSeverity severity,
        string title,
        string description)
    {
        alerts.Add(new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create(
                Platform, title, Category, cluster.Id.Value, $"{RuleId}-{findingKey}"),
            Severity = severity,
            Title = title,
            Description = description,
            Category = Category,
            Source = Platform,
            Entity = cluster.Id,
            IsDerived = true,
        });
    }
}
