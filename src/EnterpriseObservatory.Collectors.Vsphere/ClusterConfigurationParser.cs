namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Reads a cluster's vSphere HA configuration —
/// <c>ClusterComputeResource.configurationEx.dasConfig</c> — into the keys
/// <see cref="ClusterHaSettings"/> declares.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="HostConfigurationParser"/>: reads a single
/// property path into a typed answer, tells "not reported" (null) apart from
/// "reported empty", and follows the published vim25 schema —
/// <c>ClusterDasConfigInfo</c>, <c>ClusterDasAdmissionControlPolicy</c>,
/// <c>ClusterDasVmSettings</c>, <c>ClusterVmComponentProtectionSettings</c>.
/// </para>
/// <para>
/// <strong>Measured against a live vCenter</strong> (probe <c>--shapes</c>, 3
/// of 3 clusters): <c>configurationEx.dasConfig</c> cannot be requested — the
/// property is declared as the base <c>ComputeResourceConfigInfo</c>, vCenter
/// refuses the sub-path as InvalidProperty, and that one fault fails the whole
/// retrieval. So <c>configurationEx</c> is requested whole and arrives as one
/// structure; <c>dasConfig</c> is a single untyped child of it, beside
/// <c>drsConfig</c> and the repeated <c>rule</c> and <c>group</c> elements.
/// The children observed under <c>dasConfig</c> were <c>enabled</c>,
/// <c>vmMonitoring</c>, <c>hostMonitoring</c>, <c>vmComponentProtecting</c>,
/// <c>failoverLevel</c>, <c>admissionControlPolicy</c>,
/// <c>admissionControlEnabled</c>, <c>defaultVmSettings</c>, <c>option</c> and
/// <c>hBDatastoreCandidatePolicy</c>. <c>heartbeatDatastore</c> did not occur
/// on that estate, and its shape follows the published schema.
/// </para>
/// <para>
/// The <c>IsStructureArray</c> fragility this parser used to carry — a
/// <c>dasConfig</c> of only flat leaves flattening into <c>Values</c> — is
/// gone with the whole-structure request: <c>dasConfig</c> is now itself the
/// nested child that makes <c>configurationEx</c> a structure. It would only
/// return if every field of <c>ClusterConfigInfoEx</c> arrived as a bare leaf,
/// which the schema does not allow for <c>dasConfig</c> or <c>drsConfig</c>.
/// </para>
/// </remarks>
public static class ClusterConfigurationParser
{
    /// <summary>The property requested: <c>configurationEx</c>, whole.</summary>
    public const string ConfigurationExPath = "configurationEx";

    /// <summary><c>dasConfig</c>'s element name inside it.</summary>
    public const string DasConfigElement = "dasConfig";

    /// <summary>
    /// The advanced HA option this product keeps out of <c>option</c>.
    /// </summary>
    private const string IgnoreRedundantNetWarningKey = "das.ignoreRedundantNetWarning";

    /// <summary>
    /// The cluster's HA configuration, filed under
    /// <see cref="ClusterHaSettings"/>'s keys, or null when
    /// <c>configurationEx</c> was not reported or held no <c>dasConfig</c>.
    /// </summary>
    /// <remarks>
    /// Every key is written only when the corresponding field was present in
    /// the reply: an absent field means vCenter did not report it, and it
    /// stays absent from the returned dictionary rather than arriving as a
    /// written default that looks like an answer.
    /// </remarks>
    public static IReadOnlyDictionary<string, string>? ReadHaSettings(PropertyObject cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        var nodes = DasConfigNodes(cluster);
        if (nodes is null)
        {
            return null;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Set(string key, string? value)
        {
            if (value is { Length: > 0 })
            {
                settings[key] = value;
            }
        }

        Set(ClusterHaSettings.Enabled, TextAmong(nodes, "enabled"));
        Set(ClusterHaSettings.AdmissionControlEnabled, TextAmong(nodes, "admissionControlEnabled"));
        Set(ClusterHaSettings.HostMonitoring, TextAmong(nodes, "hostMonitoring"));
        Set(ClusterHaSettings.VmMonitoring, TextAmong(nodes, "vmMonitoring"));
        Set(ClusterHaSettings.HeartbeatDatastoreCandidatePolicy, TextAmong(nodes, "hBDatastoreCandidatePolicy"));

        // DPM sits beside dasConfig in configurationEx, not inside it; same
        // reply, no extra path. Absent when vCenter did not report it.
        Set(ClusterHaSettings.DpmEnabled, cluster.Structures[ConfigurationExPath]
            .FirstOrDefault(n => Is(n, "dpmConfigInfo"))?.TextOf("enabled"));

        // Polymorphic: ClusterFailoverResourcesAdmissionControlPolicy,
        // ClusterFailoverHostAdmissionControlPolicy, or the deprecated
        // ClusterFailoverLevelAdmissionControlPolicy. The concrete type is
        // what a rule cares about, not any one policy's own fields.
        var admissionControlPolicy = NodeAmong(nodes, "admissionControlPolicy");
        Set(ClusterHaSettings.AdmissionControlPolicyType, admissionControlPolicy?.Type);

        var vmComponentProtection = NodeAmong(nodes, "defaultVmSettings")
            ?.Child("vmComponentProtectionSettings");
        Set(ClusterHaSettings.ApdResponse, vmComponentProtection?.TextOf("vmStorageProtectionForAPD"));
        Set(ClusterHaSettings.PdlResponse, vmComponentProtection?.TextOf("vmStorageProtectionForPDL"));

        // Repeated at the top level of dasConfig, one node per configured
        // heartbeat datastore -- a count is what the scorecard needs, not
        // which datastores were picked. Written unconditionally, including
        // zero, because nodes is only non-null when dasConfig was actually
        // read: "no heartbeat datastores configured" is exactly the finding
        // the scorecard exists to raise, and a missing key would read as
        // "we did not look" instead.
        var heartbeatDatastoreCount = nodes.Count(n => Is(n, "heartbeatDatastore"));
        settings[ClusterHaSettings.HeartbeatDatastoreCount] =
            heartbeatDatastoreCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Also repeated at the top level: one node per advanced option.
        // Only the one this product reads is kept, for the same reason
        // AdvancedSettings keeps a handful of a host's thousand-odd settings.
        foreach (var option in nodes.Where(n => Is(n, "option")))
        {
            if (string.Equals(option.TextOf("key"), IgnoreRedundantNetWarningKey, StringComparison.Ordinal))
            {
                Set(ClusterHaSettings.IgnoreRedundantNetworkWarning, option.TextOf("value"));
            }
        }

        return settings;
    }

    /// <summary>
    /// The top-level nodes of <c>dasConfig</c>, or null when
    /// <c>configurationEx</c> was not read or carried no <c>dasConfig</c>.
    /// </summary>
    private static IReadOnlyList<PropertyNode>? DasConfigNodes(PropertyObject cluster) =>
        cluster.Structures.TryGetValue(ConfigurationExPath, out var fields) &&
        fields.FirstOrDefault(n => Is(n, DasConfigElement)) is { } dasConfig
            ? dasConfig.Children
            : null;

    private static PropertyNode? NodeAmong(IReadOnlyList<PropertyNode> nodes, string name) =>
        nodes.FirstOrDefault(n => Is(n, name));

    private static string TextAmong(IReadOnlyList<PropertyNode> nodes, string name) =>
        NodeAmong(nodes, name)?.Text ?? string.Empty;

    private static bool Is(PropertyNode node, string name) =>
        string.Equals(node.Name, name, StringComparison.Ordinal);
}
