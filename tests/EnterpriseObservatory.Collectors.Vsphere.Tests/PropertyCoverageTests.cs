using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Counting what came back, per object type and property.
/// </summary>
/// <remarks>
/// This is where a false claim about the product's own sight would be born, so
/// the tests care more about what is <em>not</em> counted than about what is.
/// A row that says "nothing answered" when the estate simply had nothing to
/// say is worse than no row at all: it teaches an operator that the coverage
/// surface fires for healthy estates, and after that it is furniture.
/// </remarks>
public class PropertyCoverageTests
{
    private static string Page(string objects) =>
        $"""
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            {objects}
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    private static string Host(string moRef, string props) =>
        $"""
        <objects>
          <obj type="HostSystem">{moRef}</obj>
          {props}
        </objects>
        """;

    private static string Scalar(string name, string value) =>
        $"""
        <propSet>
          <name>{name}</name>
          <val xsi:type="xsd:string" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">{value}</val>
        </propSet>
        """;

    private static IReadOnlyList<Application.Collection.PropertyCoverage> Measure(string objects) =>
        VsphereClient.MeasureCoverage(PropertyCollectorParser.ParsePage(Page(objects)).Objects);

    private static Application.Collection.PropertyCoverage Find(
        IReadOnlyList<Application.Collection.PropertyCoverage> rows, string property) =>
        rows.Single(r => r.Property == property && r.ObjectType == "HostSystem");

    [Fact]
    public void A_property_every_object_carried_is_complete()
    {
        var rows = Measure(
            Host("host-1", Scalar("name", "esx01")) +
            Host("host-2", Scalar("name", "esx02")));

        var name = Find(rows, "name");

        Assert.Equal(2, name.Asked);
        Assert.Equal(2, name.Answered);
        Assert.True(name.IsComplete);
        Assert.False(name.IsBlind);
    }

    [Fact]
    public void A_property_nobody_carried_is_blind()
    {
        var rows = Measure(
            Host("host-1", Scalar("name", "esx01")) +
            Host("host-2", Scalar("name", "esx02")));

        var option = Find(rows, "config.option");

        Assert.Equal(2, option.Asked);
        Assert.Equal(0, option.Answered);
        Assert.True(option.IsBlind);
    }

    [Fact]
    public void A_property_some_objects_carried_is_neither()
    {
        // The middle case, and the one the rule deliberately does not alert
        // on. It has to be countable even so, because a partial that decays
        // to zero is the thing anyone reading the numbers is watching for.
        var rows = Measure(
            Host("host-1", Scalar("name", "esx01") + Scalar("hardware.systemInfo.uuid", "u1")) +
            Host("host-2", Scalar("name", "esx02")));

        var uuid = Find(rows, "hardware.systemInfo.uuid");

        Assert.Equal(2, uuid.Asked);
        Assert.Equal(1, uuid.Answered);
        Assert.False(uuid.IsComplete);
        Assert.False(uuid.IsBlind);
    }

    [Fact]
    public void A_type_with_no_objects_gets_no_row_at_all()
    {
        // Not a zero row. "No clusters in this estate" and "clusters we could
        // not read" must not arrive looking the same, and a row of zeroes
        // reads as the second to anyone scanning the list.
        var rows = Measure(Host("host-1", Scalar("name", "esx01")));

        Assert.DoesNotContain(rows, r => r.ObjectType == "ClusterComputeResource");
        Assert.DoesNotContain(rows, r => r.ObjectType == "Datastore");
    }

    [Fact]
    public void A_property_carried_as_a_structure_counts_as_answered()
    {
        // Structures and scalars land in different dictionaries, so counting
        // only one of them would report every array-valued property in the
        // expected list as permanently blind.
        var rows = Measure(Host("host-1",
            Scalar("name", "esx01") +
            """
            <propSet>
              <name>config.option</name>
              <val xsi:type="ArrayOfOptionValue" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <OptionValue xsi:type="OptionValue">
                  <key>Syslog.global.logHost</key>
                  <value xsi:type="xsd:string"></value>
                </OptionValue>
              </val>
            </propSet>
            """));

        Assert.Equal(1, Find(rows, "config.option").Answered);
    }

    [Fact]
    public void Only_the_always_expected_properties_are_counted()
    {
        // Most requested properties are legitimately absent on a healthy
        // object: a machine with no snapshots reports no snapshot, and an
        // object with nothing wrong reports no triggeredAlarmState. Counting
        // those would report an estate as unreadable for being healthy.
        var rows = Measure(Host("host-1", Scalar("name", "esx01")));

        Assert.DoesNotContain(rows, r => r.Property == "triggeredAlarmState");
        Assert.DoesNotContain(rows, r => r.Property == "config.storageDevice.multipathInfo");
        Assert.DoesNotContain(rows, r => r.Property == "config.fileSystemVolume.mountInfo");
    }

    [Fact]
    public void Every_expected_property_is_one_the_collector_actually_requests()
    {
        // The list that would rot silently. An expected property nobody asks
        // for is blind in every estate forever, and the finding would name a
        // path the product never sent -- which is the product being
        // confidently wrong about its own sight.
        var rows = Measure(
            Host("host-1", Scalar("name", "esx01")) +
            """
            <objects>
              <obj type="Datastore">datastore-1</obj>
              <propSet>
                <name>name</name>
                <val xsi:type="xsd:string" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">ds-1</val>
              </propSet>
            </objects>
            """);

        Assert.All(rows, r =>
            Assert.Contains(r.Property, VsphereClient.RequestedProperties(r.ObjectType)));
    }

    [Fact]
    public void Host_services_and_time_are_expected_but_switches_and_lockdown_are_not()
    {
        // Every host runs services and keeps a clock, so their absence can
        // only be a failed read. A host moved wholly onto a distributed switch
        // has no standard switch, and lockdownMode is optional in the schema,
        // so counting those would call a correctly built host unreadable.
        var rows = Measure(Host("host-1", Scalar("name", "esx01")));

        Assert.True(Find(rows, "config.service").IsBlind);
        Assert.True(Find(rows, "config.dateTimeInfo").IsBlind);
        Assert.DoesNotContain(rows, r => r.Property == "config.network.vswitch");
        Assert.DoesNotContain(rows, r => r.Property == "config.network.portgroup");
        Assert.DoesNotContain(rows, r => r.Property == "config.lockdownMode");
    }

    [Fact]
    public void A_cluster_configurationEx_is_expected_but_its_rules_and_groups_are_not()
    {
        // configurationEx is requested whole -- its sub-paths cannot be asked
        // for -- and it always carries dasConfig, so a cluster without it was
        // not read. Rules and groups are repeated children that a cluster
        // with none legitimately lacks, and have no row of their own.
        var rows = VsphereClient.MeasureCoverage(PropertyCollectorParser.ParsePage(Page("""
            <objects>
              <obj type="ClusterComputeResource">domain-c1</obj>
              <propSet>
                <name>name</name>
                <val xsi:type="xsd:string" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">c1</val>
              </propSet>
            </objects>
            """)).Objects);

        Assert.True(rows.Single(r =>
            r.ObjectType == "ClusterComputeResource" && r.Property == "configurationEx").IsBlind);
        Assert.DoesNotContain(rows, r => r.Property.Contains("rule", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Property.Contains("group", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_reply_produces_no_rows_rather_than_total_blindness()
    {
        // A cycle that read nothing is a collection failure with its own
        // channel. Reporting every expected property as blind would bury the
        // one alert that matters under a dozen that repeat it.
        Assert.Empty(VsphereClient.MeasureCoverage([]));
    }
}
