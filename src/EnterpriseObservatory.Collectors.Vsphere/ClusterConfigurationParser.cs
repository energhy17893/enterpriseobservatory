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
/// <strong>Verified against the published vSphere Web Services API reference</strong>
/// (developer.broadcom.com/xapis/vsphere-web-services-api and
/// vdc-repo.vmware.com), not against a live vCenter: <c>ClusterConfigInfoEx</c>
/// carries a <c>dasConfig</c> field of type <c>ClusterDasConfigInfo</c>, whose
/// own fields are <c>enabled</c>, <c>vmMonitoring</c>, <c>hostMonitoring</c>,
/// <c>vmComponentProtecting</c>, <c>admissionControlPolicy</c>,
/// <c>admissionControlEnabled</c>, <c>defaultVmSettings</c>, <c>option</c>,
/// <c>heartbeatDatastore</c> and <c>hBDatastoreCandidatePolicy</c>, exactly as
/// read below; and <c>ClusterVmComponentProtectionSettings</c> carries
/// <c>vmStorageProtectionForAPD</c> and <c>vmStorageProtectionForPDL</c> with
/// the enum values this parser passes through untranslated. These tests prove
/// the reader against XML in that documented shape, not the shape itself
/// against a server.
/// </para>
/// <para>
/// <strong>Known fragility, inherited from
/// <see cref="PropertyCollectorParser"/>:</strong> whether <c>dasConfig</c>
/// lands in <c>Values</c> (flattened, unreadable by this parser) or
/// <c>Structures</c> depends on <c>IsStructureArray</c> finding at least one
/// nested structure among its direct children. Every field this parser reads
/// off a real cluster satisfies that -- <c>admissionControlPolicy</c> alone
/// guarantees it, since it is itself always a structure -- but a
/// <c>dasConfig</c> reply built to carry only flat leaves would flatten and
/// come back unreadable rather than partially read. This has not been seen
/// from a live vCenter and none of the vim25 documentation suggests
/// <c>admissionControlPolicy</c> can be entirely absent.
/// </para>
/// </remarks>
public static class ClusterConfigurationParser
{
    public const string DasConfigPath = "configurationEx.dasConfig";

    /// <summary>
    /// The advanced HA option this product keeps out of <c>option</c>.
    /// </summary>
    private const string IgnoreRedundantNetWarningKey = "das.ignoreRedundantNetWarning";

    /// <summary>
    /// The cluster's HA configuration, filed under
    /// <see cref="ClusterHaSettings"/>'s keys, or null when
    /// <c>configurationEx.dasConfig</c> was not reported at all.
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

        var nodes = Nodes(cluster, DasConfigPath);
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

        // Polymorphic: ClusterFailoverResourceAdmissionControlPolicy,
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
    /// The top-level nodes of <c>dasConfig</c>, or null when it was not
    /// reported in a readable shape. See <c>HostConfigurationParser.Nodes</c>,
    /// whose contract this mirrors.
    /// </summary>
    private static IReadOnlyList<PropertyNode>? Nodes(PropertyObject cluster, string path)
    {
        if (cluster.Structures.TryGetValue(path, out var nodes))
        {
            return nodes;
        }

        return cluster.Values.TryGetValue(path, out var flat) && flat.Length == 0 ? [] : null;
    }

    private static PropertyNode? NodeAmong(IReadOnlyList<PropertyNode> nodes, string name) =>
        nodes.FirstOrDefault(n => Is(n, name));

    private static string TextAmong(IReadOnlyList<PropertyNode> nodes, string name) =>
        NodeAmong(nodes, name)?.Text ?? string.Empty;

    private static bool Is(PropertyNode node, string name) =>
        string.Equals(node.Name, name, StringComparison.Ordinal);
}
