using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Parsers written from vim25's published schema, never seen live.
/// </summary>
/// <remarks>
/// <para>
/// This estate never returned the data shape these readers parse (an
/// affinity rule, an iSCSI transport, a non-empty <c>configIssue</c>). The
/// reader was written to the WSDL rather than to a probe dump, which is the
/// one place in this collector where "the schema said so" is doing the work
/// "measured live" does everywhere else. <see cref="SchemaOnlyParsers.All"/>
/// is the one list of every such reader and why it is on the list, kept here
/// rather than scattered across doc comments so a reviewer can see the whole
/// exposure in one place.
/// </para>
/// <para>
/// The contract for each: fed the schema-correct shape, it parses normally.
/// Fed a shape vim25 does not document -- a renamed element, an extra
/// wrapping layer, a child the schema says is required but this instance
/// omits -- it must stay silent (an absent key, a null field, an empty
/// list) rather than invent a value from whatever text happened to be
/// nearest. A rule reading a fabricated value is worse than a rule that
/// declines to run.
/// </para>
/// </remarks>
public static class SchemaOnlyParsers
{
    public sealed record Entry(string Parser, string Reason);

    public static readonly IReadOnlyList<Entry> All =
    [
        new(
            "InventoryVerdictParser.Read -- non-empty configIssue entries",
            "Every object on the measured estate (docs/measurements/collection-pr1-shapes.md) " +
            "carried configIssue as an empty ArrayOfEvent. A populated list -- and the per-entry " +
            "xsi:type every Event subtype declares -- follows the published schema, not a live dump."),
        new(
            "VsphereClient.ReadStoragePaths -- HostInternetScsiTargetTransport",
            "The measured estate had no iSCSI (1240 Fibre Channel paths were seen, no iSCSI ones). " +
            "iScsiName/iScsiAlias/address[] follow the published vim25 schema."),
        new(
            "PropertyCollectorParser.ReadDrsRules -- ClusterAffinityRuleSpec / ClusterAntiAffinityRuleSpec",
            "Measured on 3 clusters: every configurationEx.rule was a ClusterVmHostRuleInfo. The " +
            "affinity and anti-affinity rule spec elements follow the published vim.cluster.RuleInfo " +
            "schema and were not observed live."),
        new(
            "ProfileComplianceManager.QueryComplianceStatus",
            "Not collected yet -- no reader exists in this codebase to point a schema-derived test " +
            "at. Listed so the gap is not lost between now and whenever a reader is added."),
    ];
}

/// <summary>configIssue populated with real Event entries (Collection PR 1, InventoryVerdictParser).</summary>
public class ConfigIssueSchemaOnlyTests
{
    private static PropertyObject SingleHost(string propSets) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">x</val></propSet>
                  {propSets}
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    [Fact]
    public void Schema_correct_shape_is_counted_and_typed()
    {
        // vim25 ArrayOfEvent: each element is a concrete Event subtype
        // declared by xsi:type. Two distinct types, as the published schema
        // shapes them.
        var host = SingleHost("""
            <propSet>
              <name>configIssue</name>
              <val xsi:type="ArrayOfEvent">
                <Event xsi:type="HostNoRedundantManagementNetworkEvent"><key>1</key></Event>
                <Event xsi:type="HostShortNameToIpFailedEvent"><key>2</key></Event>
              </val>
            </propSet>
            """);

        var verdicts = InventoryVerdictParser.Read(host);

        Assert.Equal("2", verdicts[InventoryVerdicts.ConfigIssueCount]);
        Assert.Equal(
            "HostNoRedundantManagementNetworkEvent, HostShortNameToIpFailedEvent",
            verdicts[InventoryVerdicts.ConfigIssueTypes]);
    }

    [Fact]
    public void Unexpected_entry_shape_does_not_fabricate_an_event_type()
    {
        // No xsi:type on the entry: the schema requires every Event array
        // element to declare its concrete subtype, so an entry without one
        // is a shape this reader has never actually seen.
        var host = SingleHost("""
            <propSet>
              <name>configIssue</name>
              <val xsi:type="ArrayOfEvent">
                <Event><key>1</key></Event>
              </val>
            </propSet>
            """);

        var verdicts = InventoryVerdictParser.Read(host);

        // configIssue.count may legitimately state "1" -- an entry exists,
        // whatever it is. configIssue.types must not turn the element's own
        // tag name into a claimed vim25 event type.
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.ConfigIssueTypes));
    }

    [Fact]
    public void Extra_nesting_around_an_entry_does_not_fabricate_an_event_type()
    {
        var host = SingleHost("""
            <propSet>
              <name>configIssue</name>
              <val xsi:type="ArrayOfEvent">
                <issueWrapper>
                  <Event xsi:type="HostNoRedundantManagementNetworkEvent"><key>1</key></Event>
                </issueWrapper>
              </val>
            </propSet>
            """);

        var verdicts = InventoryVerdictParser.Read(host);

        Assert.False(verdicts.ContainsKey(InventoryVerdicts.ConfigIssueTypes));
    }
}

/// <summary>HostInternetScsiTargetTransport (VsphereClient.ReadStoragePaths / TransportTarget).</summary>
public class IscsiTransportSchemaOnlyTests
{
    private static PropertyObject SingleHost(string multipathVal) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet>
                    <name>config.storageDevice.multipathInfo</name>
                    <val xsi:type="HostMultipathInfo">{multipathVal}</val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    [Fact]
    public void Schema_correct_shape_yields_the_target_iqn()
    {
        // vim.host.InternetScsiTargetTransport: iScsiName, iScsiAlias,
        // address[], exactly as the published schema declares them.
        var host = SingleHost("""
            <lun>
              <lun>key-vim.host.ScsiLun-0001</lun>
              <path>
                <name>vmhba64:C0:T0:L0</name>
                <pathState>active</pathState>
                <adapter>vmhba64</adapter>
                <transport xsi:type="HostInternetScsiTargetTransport">
                  <iScsiName>iqn.2001-05.com.equallogic:0-8a0906-vol1</iScsiName>
                  <iScsiAlias>vol1</iScsiAlias>
                  <address>10.0.0.10</address>
                </transport>
              </path>
            </lun>
            """);

        var path = Assert.Single(VsphereClient.ReadStoragePaths(host));

        Assert.Equal("HostInternetScsiTargetTransport", path.TransportType);
        Assert.Equal("iqn.2001-05.com.equallogic:0-8a0906-vol1", path.Target);
    }

    [Fact]
    public void Unexpected_shape_with_no_iScsiName_leaves_the_target_null()
    {
        // iScsiName omitted, an extra child present instead -- a shape the
        // schema does not document for this transport.
        var host = SingleHost("""
            <lun>
              <lun>key-vim.host.ScsiLun-0002</lun>
              <path>
                <name>vmhba64:C0:T0:L1</name>
                <pathState>active</pathState>
                <adapter>vmhba64</adapter>
                <transport xsi:type="HostInternetScsiTargetTransport">
                  <iScsiAlias>vol2</iScsiAlias>
                  <unexpectedChild>surprise</unexpectedChild>
                </transport>
              </path>
            </lun>
            """);

        var path = Assert.Single(VsphereClient.ReadStoragePaths(host));

        Assert.Equal("HostInternetScsiTargetTransport", path.TransportType);
        Assert.Null(path.Target);
    }

    [Fact]
    public void Extra_nesting_around_iScsiName_leaves_the_target_null_not_fabricated()
    {
        // iScsiName wrapped in an extra layer instead of being a direct
        // child: PropertyNode.Text is empty for a node with children, so
        // TextOf("iScsiName") must come back empty, not the nested text.
        var host = SingleHost("""
            <lun>
              <lun>key-vim.host.ScsiLun-0003</lun>
              <path>
                <name>vmhba64:C0:T0:L2</name>
                <pathState>active</pathState>
                <adapter>vmhba64</adapter>
                <transport xsi:type="HostInternetScsiTargetTransport">
                  <iScsiName><value>iqn.2001-05.com.equallogic:0-8a0906-vol3</value></iScsiName>
                </transport>
              </path>
            </lun>
            """);

        var path = Assert.Single(VsphereClient.ReadStoragePaths(host));

        Assert.Null(path.Target);
    }

    [Fact]
    public void Missing_transport_element_entirely_reads_as_no_target_no_exception()
    {
        var host = SingleHost("""
            <lun>
              <lun>key-vim.host.ScsiLun-0004</lun>
              <path>
                <name>vmhba64:C0:T0:L3</name>
                <pathState>active</pathState>
                <adapter>vmhba64</adapter>
              </path>
            </lun>
            """);

        var path = Assert.Single(VsphereClient.ReadStoragePaths(host));

        Assert.Equal(string.Empty, path.TransportType);
        Assert.Null(path.Target);
    }
}

/// <summary>
/// ClusterAffinityRuleSpec / ClusterAntiAffinityRuleSpec
/// (PropertyCollectorParser.ReadDrsRules).
/// </summary>
public class DrsAffinityRuleSchemaOnlyTests
{
    private static PropertyObject SingleCluster(string ruleVal) =>
        Assert.Single(PropertyCollectorParser.ParsePage($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="ClusterComputeResource">domain-c7</obj>
                  <propSet>
                    <name>configurationEx</name>
                    <val xsi:type="ClusterConfigInfoEx">{ruleVal}</val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

    [Fact]
    public void Schema_correct_affinity_rule_is_parsed()
    {
        // vim.cluster.AffinityRuleSpec: name, enabled, mandatory,
        // inCompliance, vm[], exactly as the published schema declares it.
        var cluster = SingleCluster("""
            <rule xsi:type="ClusterAffinityRuleSpec">
              <name>keep-app-tier-together</name>
              <enabled>true</enabled>
              <mandatory>false</mandatory>
              <inCompliance>true</inCompliance>
              <vm type="VirtualMachine">vm-201</vm>
              <vm type="VirtualMachine">vm-202</vm>
            </rule>
            """);

        var rule = Assert.Single(PropertyCollectorParser.ReadDrsRules(cluster.Structures));

        Assert.Equal(DrsRuleKind.Affinity, rule.Kind);
        Assert.True(rule.Enabled);
        Assert.True(rule.InCompliance);
        Assert.Equal(["vm-201", "vm-202"], rule.VirtualMachineMoRefs);
    }

    [Fact]
    public void Schema_correct_anti_affinity_rule_is_parsed()
    {
        var cluster = SingleCluster("""
            <rule xsi:type="ClusterAntiAffinityRuleSpec">
              <name>separate-db-nodes</name>
              <enabled>true</enabled>
              <mandatory>true</mandatory>
              <inCompliance>false</inCompliance>
              <vm type="VirtualMachine">vm-101</vm>
              <vm type="VirtualMachine">vm-102</vm>
            </rule>
            """);

        var rule = Assert.Single(PropertyCollectorParser.ReadDrsRules(cluster.Structures));

        Assert.Equal(DrsRuleKind.AntiAffinity, rule.Kind);
        Assert.True(rule.Mandatory);
        Assert.False(rule.InCompliance);
        Assert.Equal(["vm-101", "vm-102"], rule.VirtualMachineMoRefs);
    }

    [Fact]
    public void Rule_with_unrecognized_xsi_type_is_dropped_not_guessed_at()
    {
        // ClusterDependencyRuleInfo (start-order) is a real vim25 rule kind
        // this reader does not judge yet; a wrong element name here stands
        // in for any rule kind the switch does not know.
        var cluster = SingleCluster("""
            <rule xsi:type="ClusterDependencyRuleInfo">
              <name>db-before-app</name>
              <enabled>true</enabled>
              <mandatory>true</mandatory>
              <vmGroup>db-vms</vmGroup>
              <dependsOnVmGroup>infra-vms</dependsOnVmGroup>
            </rule>
            """);

        Assert.Empty(PropertyCollectorParser.ReadDrsRules(cluster.Structures));
    }

    [Fact]
    public void Missing_name_on_an_otherwise_schema_correct_rule_is_dropped_not_guessed_at()
    {
        var cluster = SingleCluster("""
            <rule xsi:type="ClusterAffinityRuleSpec">
              <enabled>true</enabled>
              <mandatory>false</mandatory>
              <vm type="VirtualMachine">vm-201</vm>
            </rule>
            """);

        Assert.Empty(PropertyCollectorParser.ReadDrsRules(cluster.Structures));
    }

    [Fact]
    public void Extra_nesting_inside_a_vm_reference_drops_that_member_not_a_guessed_moref()
    {
        // vim25's vm[] is a flat array of ManagedObjectReference; a vm
        // element that itself has children is a shape the schema does not
        // produce. PropertyNode.Text is empty for a node with children, so
        // the reference text must not leak the nested value up as a moref.
        var cluster = SingleCluster("""
            <rule xsi:type="ClusterAffinityRuleSpec">
              <name>nested-vm-ref</name>
              <enabled>true</enabled>
              <mandatory>false</mandatory>
              <vm type="VirtualMachine"><extra>vm-201</extra></vm>
              <vm type="VirtualMachine">vm-202</vm>
            </rule>
            """);

        var rule = Assert.Single(PropertyCollectorParser.ReadDrsRules(cluster.Structures));

        Assert.Equal(["vm-202"], rule.VirtualMachineMoRefs);
    }

    [Fact]
    public void No_configurationEx_at_all_reads_as_an_empty_list_not_an_exception()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="ClusterComputeResource">domain-c1</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

        Assert.Empty(PropertyCollectorParser.ReadDrsRules(cluster.Structures));
    }
}

/// <summary>ProfileComplianceManager.QueryComplianceStatus -- not collected yet.</summary>
/// <remarks>
/// No reader exists in this codebase to point a schema-vs-unexpected-shape
/// test at (see <see cref="SchemaOnlyParsers.All"/>). Left as a documented,
/// skipped placeholder for the same reason <see cref="StoreErrorBalanceTests"/>
/// is: so the case is not lost between now and whenever a reader is added.
/// </remarks>
public sealed class ProfileComplianceManagerTests
{
    [Fact(Skip =
        "ProfileComplianceManager.QueryComplianceStatus is not collected yet -- no parser exists to " +
        "test. Listed in SchemaOnlyParsers.All so the gap is tracked; write the schema-correct / " +
        "unexpected-shape pair here once a reader lands.")]
    public void QueryComplianceStatus_is_not_yet_collected()
    {
    }
}
