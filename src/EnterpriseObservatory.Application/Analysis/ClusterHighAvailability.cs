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
    /// How HA chooses heartbeat datastores (<c>hBDatastoreCandidatePolicy</c>).
    /// </summary>
    /// <remarks>
    /// <c>heartbeatDatastore</c> is the <b>user-preferred</b> list, not the set
    /// HA actually uses. Under <c>allFeasibleDs</c> or
    /// <c>allFeasibleDsWithUserPreference</c> (the default) HA picks two by
    /// itself, so an empty or short preferred list is normal. Only under
    /// <c>userSelectedDs</c> is the preferred list the whole story.
    /// </remarks>
    public string HeartbeatDatastoreCandidatePolicySetting { get; init; } = "dasConfig.hBDatastoreCandidatePolicy";

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

    private const string HaDisabledTitle = "Cluster has no vSphere HA protection";
    private const string AdmissionControlDisabledTitle = "Cluster admission control is disabled";
    private const string HostMonitoringDisabledTitle = "Cluster host monitoring is disabled";
    private const string StorageProtectionDisabledTitle = "Cluster storage failure protection is disabled";
    private const string TooFewHeartbeatDatastoresTitle = "Cluster has too few HA heartbeat datastores";
    private const string RedundantNetworkWarningSilencedTitle = "Cluster hides its HA network redundancy warning";

    /// <summary>The finding key and title of every alert this rule can raise for one cluster.</summary>
    private static readonly (string Key, string Title)[] Findings =
    [
        ("ha-disabled", HaDisabledTitle),
        ("admission-control-disabled", AdmissionControlDisabledTitle),
        ("host-monitoring-disabled", HostMonitoringDisabledTitle),
        ("storage-protection-disabled", StorageProtectionDisabledTitle),
        ("too-few-heartbeat-datastores", TooFewHeartbeatDatastoresTitle),
        ("redundant-network-warning-silenced", RedundantNetworkWarningSilencedTitle),
    ];

    /// <summary>Every fingerprint this rule can raise for one cluster.</summary>
    /// <remarks>
    /// For <see cref="RuleContext.Unevaluated"/>, the same purpose
    /// <see cref="ClusterNPlusOne.Fingerprints"/> serves there: a cluster
    /// whose <c>dasConfig.*</c> settings could not be read this cycle must
    /// keep whatever findings it already had rather than have them silently
    /// resolved by a transient read failure. See <see cref="Evaluate"/>.
    /// </remarks>
    public static IReadOnlyList<AlertFingerprint> Fingerprints(EntityId cluster) =>
        [.. Findings.Select(f => FingerprintFor(cluster, f.Key, f.Title))];

    private static AlertFingerprint FingerprintFor(EntityId cluster, string findingKey, string title) =>
        AlertFingerprint.Create(Platform, title, Category, cluster.Value, $"{RuleId}-{findingKey}");

    /// <summary>Runs every check below over every live cluster.</summary>
    /// <param name="unevaluated">
    /// Receives this cluster's fingerprints when its HA configuration could
    /// not be read at all this cycle, so reconciliation keeps whatever
    /// alerts it already had instead of resolving them on a read failure it
    /// cannot tell apart from a genuinely clean cluster. Optional because
    /// several callers (tests, the entity page) only want the verdict.
    /// </param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Entity> entities,
        ClusterHighAvailabilityPolicy? policy = null,
        ICollection<AlertFingerprint>? unevaluated = null)
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

            // No dasConfig.* key at all means this cycle never read the
            // cluster's HA configuration -- a collection failure, not a
            // cluster nobody configured. Every check below is already silent
            // on an absent key, so this changes no alert; it only keeps this
            // cluster's existing ones open rather than letting a transient
            // read failure resolve them.
            if (!HasDasConfig(cluster))
            {
                if (unevaluated is not null)
                {
                    foreach (var fingerprint in Fingerprints(cluster.Id))
                    {
                        unevaluated.Add(fingerprint);
                    }
                }

                continue;
            }

            HaDisabled(cluster, rules, alerts);

            // The other checks are about how HA is configured; none of them
            // mean anything on a cluster HA itself is off on, or one whose
            // enabled flag was never confirmed true. Running them anyway
            // produced noise alongside HaDisabled's own critical finding —
            // "admission control is off" on a cluster that has no HA
            // protection to admit failovers into in the first place.
            if (HaConfirmedEnabled(cluster, rules))
            {
                AdmissionControlDisabled(cluster, rules, alerts);
                HostMonitoringDisabled(cluster, rules, alerts);
                StorageProtectionDisabled(cluster, rules, alerts);
                TooFewHeartbeatDatastores(cluster, rules, alerts);
                RedundantNetworkWarningSilenced(cluster, rules, alerts);
            }
        }

        return alerts;
    }

    private static bool HasDasConfig(Entity cluster) =>
        cluster.Settings.Keys.Any(k => k.StartsWith("dasConfig", StringComparison.OrdinalIgnoreCase));

    private static bool HaConfirmedEnabled(Entity cluster, ClusterHighAvailabilityPolicy rules) =>
        cluster.Settings.TryGetValue(rules.EnabledSetting, out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

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

        Add(alerts, cluster, "ha-disabled", AlertSeverity.Critical, HaDisabledTitle,
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
            AdmissionControlDisabledTitle,
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
            HostMonitoringDisabledTitle,
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
            StorageProtectionDisabledTitle,
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
        // Judged only when the operator chose the datastores by hand; otherwise
        // HA picks its own and the preferred list says nothing about coverage.
        // The in-use set (RetrieveDasAdvancedRuntimeInfo) is not read yet.
        if (!cluster.Settings.TryGetValue(rules.HeartbeatDatastoreCandidatePolicySetting, out var policy) ||
            !string.Equals(policy, "userSelectedDs", StringComparison.Ordinal) ||
            !cluster.Settings.TryGetValue(rules.HeartbeatDatastoreCountSetting, out var raw) ||
            !int.TryParse(raw, out var count) ||
            count >= rules.MinimumHeartbeatDatastores)
        {
            return;
        }

        Add(alerts, cluster, "too-few-heartbeat-datastores", AlertSeverity.Warning,
            TooFewHeartbeatDatastoresTitle,
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
            RedundantNetworkWarningSilencedTitle,
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
            Fingerprint = FingerprintFor(cluster.Id, findingKey, title),
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
