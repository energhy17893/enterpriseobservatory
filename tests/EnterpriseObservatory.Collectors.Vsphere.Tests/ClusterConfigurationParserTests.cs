using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// A cluster's vSphere HA configuration, read the way a vim25
/// <c>RetrievePropertiesEx</c> reply carrying <c>configurationEx</c> whole
/// presents it: <c>dasConfig</c> is one child of that structure. The path
/// <c>configurationEx.dasConfig</c> cannot be requested — vCenter refuses it
/// as InvalidProperty and fails the whole retrieval.
/// </summary>
/// <remarks>
/// The fixtures follow the vSphere Web Services API reference for
/// <c>ClusterDasConfigInfo</c>, <c>ClusterFailoverResourceAdmissionControlPolicy</c>
/// and <c>ClusterVmComponentProtectionSettings</c> (developer.broadcom.com and
/// vdc-repo.vmware.com), which this file's remarks on
/// <see cref="ClusterConfigurationParser"/> cite in full. They have not been
/// compared with a live vCenter's reply; they prove the reader against the
/// documented shape.
/// </remarks>
public class ClusterConfigurationParserTests
{
    private static PropertyObject Cluster(string propSets) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="ClusterComputeResource">domain-c7</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">Prod-Cluster</val></propSet>
                  {propSets}
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    private static readonly PropertyObject Bare = Cluster(string.Empty);

    /// <summary>A realistic, fully-configured cluster.</summary>
    private const string WellConfigured = """
        <propSet>
          <name>configurationEx</name>
          <val xsi:type="ClusterConfigInfoEx"><dasConfig>
            <enabled>true</enabled>
            <admissionControlEnabled>true</admissionControlEnabled>
            <admissionControlPolicy xsi:type="ClusterFailoverResourceAdmissionControlPolicy">
              <autoComputePercentages>true</autoComputePercentages>
              <cpuFailoverResourcePercent>25</cpuFailoverResourcePercent>
              <memoryFailoverResourcePercent>25</memoryFailoverResourcePercent>
            </admissionControlPolicy>
            <hostMonitoring>enabled</hostMonitoring>
            <vmMonitoring>vmAndAppMonitoring</vmMonitoring>
            <vmComponentProtecting>enabled</vmComponentProtecting>
            <defaultVmSettings>
              <restartPriority>medium</restartPriority>
              <vmComponentProtectionSettings>
                <vmStorageProtectionForAPD>restartConservative</vmStorageProtectionForAPD>
                <enableAPDTimeoutForHosts>true</enableAPDTimeoutForHosts>
                <vmTerminateDelayForAPDSec>180</vmTerminateDelayForAPDSec>
                <vmStorageProtectionForPDL>restartAggressive</vmStorageProtectionForPDL>
              </vmComponentProtectionSettings>
            </defaultVmSettings>
            <heartbeatDatastore type="Datastore">datastore-101</heartbeatDatastore>
            <heartbeatDatastore type="Datastore">datastore-102</heartbeatDatastore>
            <hBDatastoreCandidatePolicy>allFeasibleDsWithUserPreference</hBDatastoreCandidatePolicy>
            <option>
              <key>das.ignoreRedundantNetWarning</key>
              <value>false</value>
            </option>
            <option>
              <key>das.respectVmVmAntiAffinityRules</key>
              <value>true</value>
            </option>
          </dasConfig></val>
        </propSet>
        """;

    [Fact]
    public void Not_reported_is_null_rather_than_empty()
    {
        Assert.Null(ClusterConfigurationParser.ReadHaSettings(Bare));
    }

    [Fact]
    public void ConfigurationEx_without_a_dasConfig_child_is_null()
    {
        Assert.Null(ClusterConfigurationParser.ReadHaSettings(Cluster("""
            <propSet><name>configurationEx</name><val xsi:type="ClusterConfigInfoEx">
              <drsConfig><enabled>true</enabled></drsConfig>
            </val></propSet>
            """)));
    }

    [Fact]
    public void The_live_shape_with_rules_and_groups_beside_dasConfig_is_read()
    {
        // Measured: dasConfig arrives untyped beside drsConfig, repeated
        // group and rule elements and a dozen other ClusterConfigInfoEx
        // fields; the deprecated failoverLevel sits at dasConfig's top level.
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster("""
            <propSet><name>configurationEx</name><val xsi:type="ClusterConfigInfoEx">
              <dasConfig>
                <enabled>true</enabled>
                <vmMonitoring>vmMonitoringDisabled</vmMonitoring>
                <hostMonitoring>enabled</hostMonitoring>
                <vmComponentProtecting>disabled</vmComponentProtecting>
                <failoverLevel>1</failoverLevel>
                <admissionControlPolicy xsi:type="ClusterFailoverResourceAdmissionControlPolicy">
                  <cpuFailoverResourcesPercent>50</cpuFailoverResourcesPercent>
                </admissionControlPolicy>
                <admissionControlEnabled>true</admissionControlEnabled>
                <hBDatastoreCandidatePolicy>allFeasibleDsWithUserPreference</hBDatastoreCandidatePolicy>
              </dasConfig>
              <drsConfig><enabled>true</enabled></drsConfig>
              <rule xsi:type="ClusterVmHostRuleInfo"><name>r</name><enabled>true</enabled></rule>
              <group xsi:type="ClusterVmGroup"><name>g</name></group>
            </val></propSet>
            """))!;

        Assert.Equal("true", settings[ClusterHaSettings.Enabled]);
        Assert.Equal("enabled", settings[ClusterHaSettings.HostMonitoring]);
        Assert.Equal(
            "ClusterFailoverResourceAdmissionControlPolicy",
            settings[ClusterHaSettings.AdmissionControlPolicyType]);
        Assert.Equal("0", settings[ClusterHaSettings.HeartbeatDatastoreCount]);
    }

    [Fact]
    public void Reported_empty_is_empty_rather_than_null()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster("""
            <propSet><name>configurationEx</name><val xsi:type="ClusterConfigInfoEx"><dasConfig></dasConfig><drsConfig><enabled>true</enabled></drsConfig></val></propSet>
            """));

        Assert.NotNull(settings);
        // Still written: dasConfig was read, and zero heartbeat datastores is
        // itself an answer, not an absence of one.
        Assert.Equal("0", settings[ClusterHaSettings.HeartbeatDatastoreCount]);
    }

    [Fact]
    public void Enabled_and_admission_control_are_read()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal("true", settings[ClusterHaSettings.Enabled]);
        Assert.Equal("true", settings[ClusterHaSettings.AdmissionControlEnabled]);
    }

    [Fact]
    public void The_admission_control_policy_type_is_read_from_its_xsi_type()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal(
            "ClusterFailoverResourceAdmissionControlPolicy",
            settings[ClusterHaSettings.AdmissionControlPolicyType]);
    }

    [Fact]
    public void Host_and_vm_monitoring_are_read()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal("enabled", settings[ClusterHaSettings.HostMonitoring]);
        Assert.Equal("vmAndAppMonitoring", settings[ClusterHaSettings.VmMonitoring]);
    }

    [Fact]
    public void The_apd_and_pdl_responses_are_read_from_default_vm_settings()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal("restartConservative", settings[ClusterHaSettings.ApdResponse]);
        Assert.Equal("restartAggressive", settings[ClusterHaSettings.PdlResponse]);
    }

    [Fact]
    public void Heartbeat_datastores_are_counted_rather_than_named()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal("2", settings[ClusterHaSettings.HeartbeatDatastoreCount]);
        Assert.Equal(
            "allFeasibleDsWithUserPreference",
            settings[ClusterHaSettings.HeartbeatDatastoreCandidatePolicy]);
    }

    [Fact]
    public void The_ignore_redundant_network_warning_option_is_picked_out_of_the_option_table()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster(WellConfigured))!;

        Assert.Equal("false", settings[ClusterHaSettings.IgnoreRedundantNetworkWarning]);
        // The other option in the same table is not this product's business.
        Assert.False(settings.ContainsKey("dasConfig.option.das.respectVmVmAntiAffinityRules"));
    }

    [Fact]
    public void The_ignore_redundant_network_warning_true_is_surfaced_as_a_hidden_risk_input()
    {
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster("""
            <propSet>
              <name>configurationEx</name>
              <val xsi:type="ClusterConfigInfoEx"><dasConfig>
                <enabled>true</enabled>
                <option><key>das.ignoreRedundantNetWarning</key><value>true</value></option>
              </dasConfig></val>
            </propSet>
            """))!;

        Assert.Equal("true", settings[ClusterHaSettings.IgnoreRedundantNetworkWarning]);
    }

    [Fact]
    public void A_cluster_with_a_single_heartbeat_datastore_is_counted_as_one()
    {
        // Only flat leaves under dasConfig. Requested on its own this would
        // have flattened (PropertyCollectorParser.IsStructureArray); inside
        // configurationEx, dasConfig is itself the nested structure, so it
        // stays readable.
        var settings = ClusterConfigurationParser.ReadHaSettings(Cluster("""
            <propSet>
              <name>configurationEx</name>
              <val xsi:type="ClusterConfigInfoEx"><dasConfig>
                <enabled>true</enabled>
                <heartbeatDatastore type="Datastore">datastore-101</heartbeatDatastore>
              </dasConfig></val>
            </propSet>
            """))!;

        Assert.Equal("1", settings[ClusterHaSettings.HeartbeatDatastoreCount]);
    }
}
