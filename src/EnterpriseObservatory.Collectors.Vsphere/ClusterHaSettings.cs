namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The cluster HA configuration this collector carries, and the keys it files
/// each fact under in <c>Entity.Settings</c>.
/// </summary>
/// <remarks>
/// <para>
/// Same contract as <see cref="AdvancedSettings"/>, applied to
/// <c>ClusterComputeResource.configurationEx.dasConfig</c>
/// (<c>ClusterConfigInfoEx.dasConfig</c>, a <c>ClusterDasConfigInfo</c>)
/// instead of a host's advanced settings table. Carried, not judged: a rule
/// decides whether "admission control disabled" is a finding, this only makes
/// sure the rule is handed what vCenter actually said, filed under vSphere's
/// own words rather than a translated vocabulary.
/// </para>
/// <para>
/// **A missing key is not an empty value**, exactly as it is not for
/// <see cref="AdvancedSettings"/>: a cluster whose configuration could not be
/// read carries none of these keys at all, and a cluster that answered with
/// nothing configured carries the key with an empty or default value. A rule
/// that conflates the two would report a finding against a cluster nobody
/// managed to look at.
/// </para>
/// </remarks>
public static class ClusterHaSettings
{
    /// <summary>Whether vSphere HA is enabled. <c>ClusterDasConfigInfo.enabled</c>.</summary>
    public const string Enabled = "dasConfig.enabled";

    /// <summary>
    /// Whether strict admission control is enabled.
    /// <c>ClusterDasConfigInfo.admissionControlEnabled</c>.
    /// </summary>
    public const string AdmissionControlEnabled = "dasConfig.admissionControlEnabled";

    /// <summary>
    /// The concrete admission control policy in force, by its vim25 type name
    /// — e.g. <c>ClusterFailoverResourceAdmissionControlPolicy</c>,
    /// <c>ClusterFailoverHostAdmissionControlPolicy</c>, or the deprecated
    /// <c>ClusterFailoverLevelAdmissionControlPolicy</c>.
    /// <c>ClusterDasConfigInfo.admissionControlPolicy</c>, read from its
    /// <c>xsi:type</c> rather than from a field, because the policy is
    /// polymorphic and the type is the fact a rule cares about.
    /// </summary>
    public const string AdmissionControlPolicyType = "dasConfig.admissionControlPolicy.type";

    /// <summary>
    /// <c>enabled</c> or <c>disabled</c>. Determines whether HA restarts
    /// virtual machines after a host failure.
    /// <c>ClusterDasConfigInfo.hostMonitoring</c>.
    /// </summary>
    public const string HostMonitoring = "dasConfig.hostMonitoring";

    /// <summary>
    /// <c>vmMonitoringDisabled</c>, <c>vmMonitoringOnly</c> or
    /// <c>vmAndAppMonitoring</c>. <c>ClusterDasConfigInfo.vmMonitoring</c>.
    /// </summary>
    public const string VmMonitoring = "dasConfig.vmMonitoring";

    /// <summary>
    /// The cluster-wide default reaction to an All-Paths-Down storage failure:
    /// <c>disabled</c>, <c>warning</c>, <c>restartConservative</c>,
    /// <c>restartAggressive</c> or <c>clusterDefault</c>.
    /// <c>ClusterDasConfigInfo.defaultVmSettings.vmComponentProtectionSettings
    /// .vmStorageProtectionForAPD</c>.
    /// </summary>
    public const string ApdResponse =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForAPD";

    /// <summary>
    /// The cluster-wide default reaction to a Permanent-Device-Loss storage
    /// failure: <c>disabled</c>, <c>warning</c>, <c>restartAggressive</c> or
    /// <c>clusterDefault</c>.
    /// <c>ClusterDasConfigInfo.defaultVmSettings.vmComponentProtectionSettings
    /// .vmStorageProtectionForPDL</c>.
    /// </summary>
    public const string PdlResponse =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForPDL";

    /// <summary>
    /// How many datastores are configured for storage heartbeating, as a
    /// string. <c>ClusterDasConfigInfo.heartbeatDatastore</c>.Count.
    /// </summary>
    /// <remarks>
    /// A count rather than the datastore references themselves: which
    /// datastores were picked is not a fact this product's HA rule reasons
    /// about, only how many — vSphere itself recommends at least two.
    /// </remarks>
    public const string HeartbeatDatastoreCount = "dasConfig.heartbeatDatastore.count";

    /// <summary>
    /// <c>userSelectedDs</c>, <c>allFeasibleDs</c> or
    /// <c>allFeasibleDsWithUserPreference</c>.
    /// <c>ClusterDasConfigInfo.hBDatastoreCandidatePolicy</c>.
    /// </summary>
    public const string HeartbeatDatastoreCandidatePolicy = "dasConfig.hBDatastoreCandidatePolicy";

    /// <summary>
    /// The advanced HA option that silences vCenter's own warning about a
    /// cluster with no redundant management network, when set to <c>true</c>.
    /// One entry of <c>ClusterDasConfigInfo.option</c> — an
    /// <c>OptionValue[]</c> — carried under its own vSphere key because it is
    /// exactly the shape <see cref="AdvancedSettings"/> uses for a host's
    /// advanced settings: the property collector cannot address inside an
    /// array, so the whole table arrives and this is the one entry kept.
    /// </summary>
    /// <remarks>
    /// This is the hidden-risk setting the scorecard exists to surface. A
    /// management network with only one path is the exact condition HA's own
    /// isolation detection depends on being redundant to tell a partitioned
    /// host from a dead one; setting this to <c>true</c> does not fix that
    /// condition, it only stops vCenter from saying so. See VMware KB
    /// <c>1002080</c> ("HA cluster errors: 'The number of vmknic-based dasNetwork
    /// isolation addresses configured is less than the number of subnets'")
    /// and the vSphere Availability guide's chapter on HA network requirements.
    /// </remarks>
    public const string IgnoreRedundantNetworkWarning = "dasConfig.option.das.ignoreRedundantNetWarning";

    /// <summary>What a cluster with no readable HA configuration carries.</summary>
    public static IReadOnlyDictionary<string, string> None { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
