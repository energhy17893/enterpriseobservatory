using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class PropertyCollectorParserTests
{
    [Fact]
    public void Objects_and_their_properties_are_read()
    {
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-17</obj>
                  <propSet><name>name</name><val>esx01.corp.local</val></propSet>
                  <propSet><name>summary.overallStatus</name><val>green</val></propSet>
                  <propSet><name>runtime.inMaintenanceMode</name><val>false</val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var page = PropertyCollectorParser.ParsePage(xml);

        var host = Assert.Single(page.Objects);
        Assert.Equal("host-17", host.MoRef);
        Assert.Equal("HostSystem", host.Type);
        Assert.Equal("esx01.corp.local", PropertyCollectorParser.ReadString(host.Values, "name"));
        Assert.Equal("green", PropertyCollectorParser.ReadString(host.Values, "summary.overallStatus"));
        Assert.False(PropertyCollectorParser.ReadBoolean(host.Values, "runtime.inMaintenanceMode"));
    }

    [Fact]
    public void A_continuation_token_is_surfaced_so_paging_is_not_forgotten()
    {
        // Ignoring this truncates the inventory at whatever the server chose to
        // return, and the result looks like a small healthy estate rather than
        // a bug. This is the single easiest way to under-report an environment.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <token>session[abc]0123</token>
                <objects><obj type="HostSystem">host-1</obj></objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var page = PropertyCollectorParser.ParsePage(xml);

        Assert.True(page.HasMore);
        Assert.Equal("session[abc]0123", page.ContinuationToken);
    }

    [Fact]
    public void The_last_page_carries_no_token()
    {
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="HostSystem">host-1</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """;

        Assert.False(PropertyCollectorParser.ParsePage(xml).HasMore);
    }

    [Fact]
    public void An_array_property_is_readable_as_several_values()
    {
        // A host's IP addresses and a VM's datastores both arrive this way, so
        // the caller should not have to know which properties are arrays.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-9</obj>
                  <propSet>
                    <name>datastore</name>
                    <val>
                      <ManagedObjectReference type="Datastore">datastore-11</ManagedObjectReference>
                      <ManagedObjectReference type="Datastore">datastore-12</ManagedObjectReference>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var vm = Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);
        var datastores = PropertyCollectorParser.SplitValues(
            PropertyCollectorParser.ReadString(vm.Values, "datastore"));

        Assert.Equal(["datastore-11", "datastore-12"], datastores);
    }

    [Fact]
    public void A_property_the_account_may_not_read_is_reported_as_missing()
    {
        // Not permitted and empty are different facts, and conflating them is
        // how "HA is disabled" gets reported about a cluster nobody was allowed
        // to look at.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="ClusterComputeResource">domain-c7</obj>
                  <propSet><name>name</name><val>prod</val></propSet>
                  <missingSet>
                    <path>configuration.dasConfig.enabled</path>
                    <fault><fault xsi:type="NoPermission"/><localizedMessage>Permission to perform this operation was denied.</localizedMessage></fault>
                  </missingSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);

        var missing = Assert.Single(cluster.Missing);
        Assert.Equal("configuration.dasConfig.enabled", missing.Path);
        Assert.True(missing.IsPermissionDenied);

        // And it must not read as false.
        Assert.Null(PropertyCollectorParser.ReadBoolean(cluster.Values, "configuration.dasConfig.enabled"));
    }

    [Fact]
    public void An_unreadable_value_is_null_rather_than_a_default()
    {
        var page = PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="Datastore">ds-1</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """);

        var ds = Assert.Single(page.Objects);

        Assert.Null(PropertyCollectorParser.ReadBoolean(ds.Values, "summary.accessible"));
        Assert.Null(PropertyCollectorParser.ReadLong(ds.Values, "summary.capacity"));
        Assert.Null(PropertyCollectorParser.ReadString(ds.Values, "name"));
    }

    [Fact]
    public void A_non_numeric_value_does_not_become_zero()
    {
        // Zero capacity would read as a full datastore.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="Datastore">ds-1</obj>
                  <propSet><name>summary.capacity</name><val>unavailable</val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var ds = Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);

        Assert.Null(PropertyCollectorParser.ReadLong(ds.Values, "summary.capacity"));
    }

    [Fact]
    public void An_empty_inventory_is_a_legitimate_answer_not_a_failure()
    {
        var page = PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25"/>
            """);

        Assert.Empty(page.Objects);
        Assert.False(page.HasMore);
    }

    // An empty page is a claim: "this vCenter has nothing in it". The graph
    // believes it and marks every entity the source used to report as vanished
    // on the first miss. So a reply that could not be read must never become
    // one — it has to fail the read, which leaves the estate exactly as it was.

    [Fact]
    public void Malformed_xml_fails_the_read_rather_than_reporting_an_empty_estate()
    {
        var fault = Assert.Throws<VsphereApiException>(
            () => PropertyCollectorParser.ParsePage("<not-xml"));

        Assert.Equal(
            CollectionFailureKind.ProtocolError,
            ((ICollectionFault)fault).Kind);
    }

    [Fact]
    public void A_reply_cut_off_mid_page_fails_the_read()
    {
        Assert.Throws<VsphereApiException>(() => PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="HostSystem">host-1</obj>
            """));
    }

    [Fact]
    public void A_well_formed_reply_that_is_not_from_the_property_collector_fails_the_read()
    {
        // What a proxy or a captive portal answers with: 200, parses cleanly,
        // and says nothing about the inventory.
        Assert.Throws<VsphereApiException>(() => PropertyCollectorParser.ParsePage("""
            <html><body><h1>Service temporarily unavailable</h1></body></html>
            """));
    }

    [Fact]
    public void A_continuation_page_is_recognised_as_a_property_collector_reply()
    {
        var page = PropertyCollectorParser.ParsePage("""
            <ContinueRetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="Datastore">ds-1</obj></objects></returnval>
            </ContinueRetrievePropertiesExResponse>
            """);

        Assert.Single(page.Objects);
    }

    // --- structures -------------------------------------------------------

    /// <summary>
    /// Copied from a live vCenter, whitespace and all. See docs/collectors.
    /// </summary>
    private const string TriggeredAlarm = """
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="HostSystem">host-3615</obj>
              <propSet>
                <name>name</name>
                <val xsi:type="xsd:string" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">10.34.75.77</val>
              </propSet>
              <propSet>
                <name>triggeredAlarmState</name>
                <val xsi:type="ArrayOfAlarmState" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <AlarmState xsi:type="AlarmState">
                    <key>115.3615</key>
                    <entity type="HostSystem">host-3615</entity>
                    <alarm type="Alarm">alarm-115</alarm>
                    <overallStatus>red</overallStatus>
                    <time>2026-09-19T01:20:16.604498Z</time>
                    <acknowledged>false</acknowledged>
                    <eventKey>32074097</eventKey>
                  </AlarmState>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void An_array_of_structures_is_read_field_by_field()
    {
        var host = Assert.Single(PropertyCollectorParser.ParsePage(TriggeredAlarm).Objects);

        var alarm = Assert.Single(host.Structures["triggeredAlarmState"]);

        Assert.Equal("115.3615", alarm.TextOf("key"));
        Assert.Equal("host-3615", alarm.TextOf("entity"));
        Assert.Equal("alarm-115", alarm.TextOf("alarm"));
        Assert.Equal("red", alarm.TextOf("overallStatus"));
        Assert.Equal("false", alarm.TextOf("acknowledged"));
    }

    [Fact]
    public void A_reference_inside_a_structure_keeps_its_type()
    {
        // "host-3615" alone cannot say what it is, and the alarm's subject is
        // the one thing that decides which entity the alert belongs to.
        var host = Assert.Single(PropertyCollectorParser.ParsePage(TriggeredAlarm).Objects);

        Assert.Equal("HostSystem", host.Structures["triggeredAlarmState"][0].TypeOf("entity"));
        Assert.Equal("Alarm", host.Structures["triggeredAlarmState"][0].TypeOf("alarm"));
    }

    [Fact]
    public void A_structure_is_not_also_offered_as_a_string()
    {
        // It used to be, and the string was a lie: the flattener took each
        // element's first grandchild, so an alarm arrived as "115.3615" — its
        // key, wearing the name of the whole structure. A caller reading
        // Values would have got that and never known.
        var host = Assert.Single(PropertyCollectorParser.ParsePage(TriggeredAlarm).Objects);

        Assert.False(host.Values.ContainsKey("triggeredAlarmState"));
        Assert.Null(PropertyCollectorParser.ReadString(host.Values, "triggeredAlarmState"));
    }

    [Fact]
    public void An_empty_array_of_structures_is_not_a_structure()
    {
        // What vCenter returns for almost every object: the property is
        // present and empty. Treating presence as content matches everything
        // and finds nothing.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="Datastore">datastore-35429</obj>
                  <propSet><name>name</name><val>PRODVOL10</val></propSet>
                  <propSet><name>triggeredAlarmState</name><val xsi:type="ArrayOfAlarmState" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"></val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var datastore = Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);

        Assert.False(datastore.Structures.ContainsKey("triggeredAlarmState"));
        Assert.Equal("PRODVOL10", PropertyCollectorParser.ReadString(datastore.Values, "name"));
    }

    [Fact]
    public void A_flat_array_is_still_a_flat_array()
    {
        // A VM's datastore list has no structure and must keep arriving as
        // values, or every relationship built from one disappears.
        const string xml = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-1</obj>
                  <propSet>
                    <name>datastore</name>
                    <val><ManagedObjectReference type="Datastore">ds-1</ManagedObjectReference><ManagedObjectReference type="Datastore">ds-2</ManagedObjectReference></val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        var vm = Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);

        Assert.Empty(vm.Structures);
        Assert.Equal(
            ["ds-1", "ds-2"],
            PropertyCollectorParser.SplitValues(vm.Values["datastore"]));
    }
}

public class VsphereSoapFaultReaderTests
{
    private static string Fault(string faultType, string message = "something went wrong") => $"""
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body>
            <soapenv:Fault>
              <faultcode>ServerFaultCode</faultcode>
              <faultstring>{message}</faultstring>
              <detail><{faultType} xmlns="urn:vim25"/></detail>
            </soapenv:Fault>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    [Fact]
    public void A_successful_response_carries_no_fault()
    {
        Assert.Null(VsphereSoapFaultReader.TryRead("""
            <RetrievePropertiesExResponse xmlns="urn:vim25"/>
            """));
    }

    [Theory]
    [InlineData("InvalidLoginFault", VsphereFaultKind.InvalidLogin)]
    [InlineData("NoPermissionFault", VsphereFaultKind.NoPermission)]
    [InlineData("NotAuthenticatedFault", VsphereFaultKind.NotAuthenticated)]
    [InlineData("ManagedObjectNotFoundFault", VsphereFaultKind.ManagedObjectNotFound)]
    public void The_fault_type_is_authoritative(string faultType, VsphereFaultKind expected)
    {
        Assert.Equal(expected, VsphereSoapFaultReader.TryRead(Fault(faultType))!.Kind);
    }

    [Fact]
    public void A_query_size_refusal_is_recognised_from_its_message()
    {
        // It has no dedicated fault type; vCenter reports it as a generic
        // RuntimeFault whose message is the only clue.
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("RuntimeFault", "Request processing is restricted by administrator."),
            VsphereCallContext.PerformanceQuery);

        Assert.Equal(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
    }

    [Fact]
    public void A_size_refusal_is_only_read_into_a_performance_query()
    {
        // Outside one it is a guess, and a wrong guess sends the collector
        // shrinking batches against a problem that has nothing to do with size.
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("RuntimeFault", "Request processing is restricted by administrator."));

        Assert.NotEqual(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
    }

    [Fact]
    public void An_unset_advanced_option_is_not_mistaken_for_a_size_refusal()
    {
        // A live vCenter 8 returned exactly this for an option that was never
        // set. An earlier version matched "exceeds the maximum" and read it as
        // a performance-query size refusal.
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("InvalidNameFault",
                "'config.vpxd.stats.maxQueryMetrics' is invalid or exceeds the maximum " +
                "number of characters permitted."),
            VsphereCallContext.PerformanceQuery);

        Assert.Equal(VsphereFaultKind.InvalidName, fault!.Kind);
        Assert.True(fault.IsSurvivable);
    }

    [Fact]
    public void An_unset_option_is_survivable_so_the_cycle_continues()
    {
        // It costs the exact limit and nothing else; the batch sizer falls back
        // to the documented default.
        var fault = VsphereSoapFaultReader.TryRead(Fault("InvalidNameFault", "no such option"));

        Assert.True(fault!.IsSurvivable);
    }

    [Fact]
    public void An_invalid_message_is_never_read_as_a_size_problem()
    {
        // Belt and braces on the text matcher itself: "is invalid" rules it out
        // regardless of what else the message says.
        Assert.False(AdaptiveBatchSizer.IsQuerySizeRefusal(
            "'something' is invalid or exceeds the maximum number of characters permitted."));
    }

    [Fact]
    public void Credential_and_permission_faults_are_not_retried()
    {
        // Retrying an authentication failure every cycle is how a monitoring
        // account ends up locked out by its own loop.
        Assert.False(VsphereSoapFaultReader.TryRead(Fault("InvalidLoginFault"))!.IsWorthRetrying);
        Assert.False(VsphereSoapFaultReader.TryRead(Fault("NoPermissionFault"))!.IsWorthRetrying);
    }

    [Fact]
    public void An_expired_session_is_worth_retrying_after_logging_in_again()
    {
        Assert.True(VsphereSoapFaultReader.TryRead(Fault("NotAuthenticatedFault"))!.IsWorthRetrying);
    }

    [Fact]
    public void A_size_refusal_is_not_retried_unchanged()
    {
        // It is fixed by asking for less, not by asking again.
        Assert.False(VsphereSoapFaultReader
            .TryRead(
                Fault("RuntimeFault", "Request processing is restricted by administrator."),
                VsphereCallContext.PerformanceQuery)!
            .IsWorthRetrying);
    }

    [Fact]
    public void A_fault_without_a_recognisable_type_is_still_reported()
    {
        var fault = VsphereSoapFaultReader.TryRead(Fault("SomeNewFault", "unexpected"));

        Assert.Equal(VsphereFaultKind.Other, fault!.Kind);
        Assert.Equal("unexpected", fault.Message);
    }

    // --- nested structures ------------------------------------------------

    /// <summary>
    /// Copied from a live vCenter. The nesting is the content: mountInfo is the
    /// inner element and volume is its sibling, which is not what the property
    /// name suggests and cost a wrong guess to find out.
    /// </summary>
    private const string MountedVolumes = """
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="HostSystem">host-1</obj>
              <propSet>
                <name>config.fileSystemVolume.mountInfo</name>
                <val xsi:type="ArrayOfHostFileSystemMountInfo" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <HostFileSystemMountInfo xsi:type="HostFileSystemMountInfo">
                    <mountInfo>
                      <path>/vmfs/volumes/608bd301-3f719074-6962-f40343e85d10</path>
                      <accessMode>readWrite</accessMode>
                      <mounted>true</mounted>
                      <accessible>true</accessible>
                    </mountInfo>
                    <volume xsi:type="HostVmfsVolume">
                      <type>VMFS</type>
                      <name>HOST6_Local_DS1</name>
                      <uuid>608bd301-3f719074-6962-f40343e85d10</uuid>
                      <extent>
                        <diskName>naa.600508b1001cb7368fc569b9146949ad</diskName>
                        <partition>8</partition>
                      </extent>
                      <ssd>true</ssd>
                      <local>true</local>
                    </volume>
                  </HostFileSystemMountInfo>
                  <HostFileSystemMountInfo xsi:type="HostFileSystemMountInfo">
                    <mountInfo>
                      <path>/vmfs/volumes/aaaa</path>
                    </mountInfo>
                    <volume xsi:type="HostVmfsVolume">
                      <type>VMFS</type>
                      <name>SPANNED</name>
                      <uuid>aaaa</uuid>
                      <extent><diskName>naa.111</diskName><partition>1</partition></extent>
                      <extent><diskName>naa.222</diskName><partition>1</partition></extent>
                    </volume>
                  </HostFileSystemMountInfo>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    private static IReadOnlyList<PropertyNode> Mounts() =>
        Assert.Single(PropertyCollectorParser.ParsePage(MountedVolumes).Objects)
            .Structures["config.fileSystemVolume.mountInfo"];

    [Fact]
    public void A_structure_inside_a_structure_is_read_rather_than_flattened()
    {
        // The link that joins a datastore to the LUN it sits on. A one-level
        // reader gave back the concatenated text of the whole volume subtree,
        // which is a string that looks like data and is not.
        var volume = Assert.Single(Mounts().Take(1)).Child("volume");

        Assert.NotNull(volume);
        Assert.Equal("VMFS", volume.TextOf("type"));
        Assert.Equal("608bd301-3f719074-6962-f40343e85d10", volume.TextOf("uuid"));
        Assert.Equal(
            "naa.600508b1001cb7368fc569b9146949ad",
            volume.Child("extent")!.TextOf("diskName"));
    }

    [Fact]
    public void A_node_with_children_has_no_text_of_its_own()
    {
        // Rather than the concatenation of everything beneath it. That
        // concatenation is how a structure comes back looking like a value.
        var volume = Mounts()[0].Child("volume")!;

        Assert.Equal(string.Empty, volume.Text);
        Assert.NotEmpty(volume.Children);
    }

    [Fact]
    public void A_spanned_volume_keeps_every_extent()
    {
        // A VMFS volume may span several devices. Keeping only the first would
        // report the datastore as living on one LUN and quietly lose the
        // others — and for a spanned volume the lost half is the half that
        // explains the outage.
        var volume = Mounts()[1].Child("volume")!;

        Assert.Equal(
            ["naa.111", "naa.222"],
            volume.All("extent").Select(e => e.TextOf("diskName")));
    }

    [Fact]
    public void The_concrete_subtype_survives()
    {
        // HostVmfsVolume and HostNasVolume are not interchangeable: only one
        // of them has a storage device, and treating an NFS mount as a LUN
        // would put a block identifier on something that is not a block
        // device.
        Assert.Equal("HostVmfsVolume", Mounts()[0].TypeOf("volume"));
    }

    [Fact]
    public void A_field_that_is_not_there_reads_as_empty_rather_than_throwing()
    {
        // A probe or a differently configured host will be missing fields, and
        // a reader that threw would turn a partial answer into no answer.
        var volume = Mounts()[1].Child("volume")!;

        Assert.Equal(string.Empty, volume.TextOf("ssd"));
        Assert.Null(volume.Child("nowhere"));
        Assert.Empty(volume.All("nowhere"));
    }

    // --- M8.3: DRS affinity / anti-affinity / VM-host rules ----------------

    /// <summary>
    /// Built from the vSphere Web Services API reference
    /// (developer.broadcom.com, <c>vim.cluster.ConfigInfoEx</c>,
    /// <c>vim.cluster.RuleInfo</c> and its three rule subtypes,
    /// <c>vim.cluster.VmGroup</c>, <c>vim.cluster.HostGroup</c>), not dumped
    /// from a live vCenter — the same "schema-shaped, not yet observed"
    /// status <see cref="VsphereStoragePath"/> already carries for its wire
    /// shape. <c>configurationEx</c> was not previously requested by this
    /// collector, so there is nothing yet in docs/collectors to copy from.
    /// </summary>
    private const string ClusterConfigurationEx = """
        <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <returnval>
            <objects>
              <obj type="ClusterComputeResource">domain-c7</obj>
              <propSet>
                <name>configurationEx.group</name>
                <val xsi:type="ArrayOfClusterGroupInfo">
                  <group xsi:type="ClusterVmGroup">
                    <name>db-vms</name>
                    <vm type="VirtualMachine">vm-101</vm>
                    <vm type="VirtualMachine">vm-102</vm>
                  </group>
                  <group xsi:type="ClusterHostGroup">
                    <name>licensed-hosts</name>
                    <host type="HostSystem">host-11</host>
                    <host type="HostSystem">host-12</host>
                  </group>
                </val>
              </propSet>
              <propSet>
                <name>configurationEx.rule</name>
                <val xsi:type="ArrayOfClusterRuleInfo">
                  <rule xsi:type="ClusterAffinityRuleSpec">
                    <name>keep-app-tier-together</name>
                    <enabled>true</enabled>
                    <mandatory>false</mandatory>
                    <inCompliance>true</inCompliance>
                    <vm type="VirtualMachine">vm-201</vm>
                    <vm type="VirtualMachine">vm-202</vm>
                  </rule>
                  <rule xsi:type="ClusterAntiAffinityRuleSpec">
                    <name>separate-db-nodes</name>
                    <enabled>true</enabled>
                    <mandatory>true</mandatory>
                    <inCompliance>false</inCompliance>
                    <vm type="VirtualMachine">vm-101</vm>
                    <vm type="VirtualMachine">vm-102</vm>
                  </rule>
                  <rule xsi:type="ClusterVmHostRuleInfo">
                    <name>db-on-licensed-hosts</name>
                    <enabled>true</enabled>
                    <mandatory>false</mandatory>
                    <inCompliance>true</inCompliance>
                    <vmGroupName>db-vms</vmGroupName>
                    <affineHostGroupName>licensed-hosts</affineHostGroupName>
                  </rule>
                  <rule xsi:type="ClusterVmHostRuleInfo">
                    <name>keep-test-off-licensed-hosts</name>
                    <enabled>false</enabled>
                    <mandatory>false</mandatory>
                    <vmGroupName>db-vms</vmGroupName>
                    <antiAffineHostGroupName>licensed-hosts</antiAffineHostGroupName>
                  </rule>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void Cluster_groups_are_read_by_their_xsi_type()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(ClusterConfigurationEx).Objects);

        var groups = PropertyCollectorParser.ReadClusterGroups(cluster.Structures);

        Assert.Equal(2, groups.Count);

        var vmGroup = Assert.Single(groups, g => g.Kind == VsphereClusterGroupKind.VirtualMachine);
        Assert.Equal("db-vms", vmGroup.Name);
        Assert.Equal(["vm-101", "vm-102"], vmGroup.MemberMoRefs);

        var hostGroup = Assert.Single(groups, g => g.Kind == VsphereClusterGroupKind.Host);
        Assert.Equal("licensed-hosts", hostGroup.Name);
        Assert.Equal(["host-11", "host-12"], hostGroup.MemberMoRefs);
    }

    [Fact]
    public void An_affinity_rule_carries_its_vm_list()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(ClusterConfigurationEx).Objects);

        var rule = Assert.Single(
            PropertyCollectorParser.ReadDrsRules(cluster.Structures), r => r.Name == "keep-app-tier-together");

        Assert.Equal(EnterpriseObservatory.Domain.DrsRuleKind.Affinity, rule.Kind);
        Assert.True(rule.Enabled);
        Assert.False(rule.Mandatory);
        Assert.True(rule.InCompliance);
        Assert.Equal(["vm-201", "vm-202"], rule.VirtualMachineMoRefs);
    }

    [Fact]
    public void An_anti_affinity_rule_carries_its_vm_list_and_inCompliance()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(ClusterConfigurationEx).Objects);

        var rule = Assert.Single(
            PropertyCollectorParser.ReadDrsRules(cluster.Structures), r => r.Name == "separate-db-nodes");

        Assert.Equal(EnterpriseObservatory.Domain.DrsRuleKind.AntiAffinity, rule.Kind);
        Assert.True(rule.Mandatory);
        Assert.False(rule.InCompliance);
        Assert.Equal(["vm-101", "vm-102"], rule.VirtualMachineMoRefs);
    }

    [Fact]
    public void A_vm_host_rule_with_an_affine_group_is_read_as_must_should_run_on()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(ClusterConfigurationEx).Objects);

        var rule = Assert.Single(
            PropertyCollectorParser.ReadDrsRules(cluster.Structures), r => r.Name == "db-on-licensed-hosts");

        Assert.Equal(EnterpriseObservatory.Domain.DrsRuleKind.VmHostAffine, rule.Kind);
        Assert.Equal("db-vms", rule.VmGroupName);
        Assert.Equal("licensed-hosts", rule.HostGroupName);
    }

    [Fact]
    public void A_disabled_vm_host_rule_with_an_anti_affine_group_is_still_read()
    {
        // Disabled rules are not enforced by DRS, but they are still read:
        // the analysis rule, not the parser, decides what a disabled rule
        // means.
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage(ClusterConfigurationEx).Objects);

        var rule = Assert.Single(
            PropertyCollectorParser.ReadDrsRules(cluster.Structures),
            r => r.Name == "keep-test-off-licensed-hosts");

        Assert.Equal(EnterpriseObservatory.Domain.DrsRuleKind.VmHostAntiAffine, rule.Kind);
        Assert.False(rule.Enabled);
        Assert.Null(rule.InCompliance);
        Assert.Equal("licensed-hosts", rule.HostGroupName);
    }

    [Fact]
    public void No_configurationEx_properties_read_as_empty_lists_rather_than_throwing()
    {
        var cluster = Assert.Single(PropertyCollectorParser.ParsePage("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="ClusterComputeResource">domain-c1</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """).Objects);

        Assert.Empty(PropertyCollectorParser.ReadClusterGroups(cluster.Structures));
        Assert.Empty(PropertyCollectorParser.ReadDrsRules(cluster.Structures));
    }
}
