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

    [Fact]
    public void Malformed_xml_yields_nothing_rather_than_throwing()
    {
        var page = PropertyCollectorParser.ParsePage("<not-xml");

        Assert.Empty(page.Objects);
        Assert.False(page.HasMore);
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
            Fault("RuntimeFault", "Request processing is restricted by administrator."));

        Assert.Equal(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
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
            .TryRead(Fault("RuntimeFault", "Request processing is restricted by administrator."))!
            .IsWorthRetrying);
    }

    [Fact]
    public void A_fault_without_a_recognisable_type_is_still_reported()
    {
        var fault = VsphereSoapFaultReader.TryRead(Fault("SomeNewFault", "unexpected"));

        Assert.Equal(VsphereFaultKind.Other, fault!.Kind);
        Assert.Equal("unexpected", fault.Message);
    }
}
