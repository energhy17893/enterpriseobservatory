using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Reading vim25 events.
/// </summary>
/// <remarks>
/// The fixtures are shaped from the published vim25 schema — an
/// <c>ArrayOfEvent</c> under <c>latestPage</c>, one <c>returnval</c> per event
/// from <c>ReadPreviousEvents</c>, each typed by <c>xsi:type</c>. None of them
/// was captured from a live vCenter, and that is the first thing to check when
/// one is available.
/// </remarks>
public class VsphereEventParserTests
{
    private const string LatestPage = """
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <soapenv:Body>
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="EventHistoryCollector">session[52b1a7c3]52c0e1f4</obj>
                  <propSet>
                    <name>latestPage</name>
                    <val xsi:type="ArrayOfEvent">
                      <Event xsi:type="EventEx">
                        <key>9001</key>
                        <chainId>9001</chainId>
                        <createdTime>2026-09-21T10:15:02.123456Z</createdTime>
                        <userName></userName>
                        <datacenter><name>DC1</name><datacenter type="Datacenter">datacenter-3</datacenter></datacenter>
                        <computeResource><name>Prod</name><computeResource type="ClusterComputeResource">domain-c7</computeResource></computeResource>
                        <host><name>esx01.corp.local</name><host type="HostSystem">host-42</host></host>
                        <fullFormattedMessage>Lost connectivity to storage device naa.6005. Path vmhba2:C0:T0:L3 is down.</fullFormattedMessage>
                        <eventTypeId>esx.problem.storage.connectivity.lost</eventTypeId>
                        <severity>error</severity>
                        <message></message>
                        <objectId>host-42</objectId>
                        <objectType>HostSystem</objectType>
                      </Event>
                      <Event xsi:type="VmPoweredOffEvent">
                        <key>9002</key>
                        <chainId>9000</chainId>
                        <createdTime>2026-09-21T10:16:00Z</createdTime>
                        <userName>VSPHERE.LOCAL\ertugrul</userName>
                        <datacenter><name>DC1</name><datacenter type="Datacenter">datacenter-3</datacenter></datacenter>
                        <computeResource><name>Prod</name><computeResource type="ClusterComputeResource">domain-c7</computeResource></computeResource>
                        <host><name>esx01.corp.local</name><host type="HostSystem">host-42</host></host>
                        <vm><name>sql-01</name><vm type="VirtualMachine">vm-101</vm></vm>
                        <ds><name>ds-gold-01</name><datastore type="Datastore">datastore-11</datastore></ds>
                        <fullFormattedMessage>sql-01 on esx01.corp.local in DC1 is powered off</fullFormattedMessage>
                        <template>false</template>
                      </Event>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    [Fact]
    public void The_latest_page_is_read_event_by_event()
    {
        var events = VsphereEventParser.ParseLatestPage(LatestPage);

        Assert.NotNull(events);
        Assert.Equal([9001L, 9002L], events.Select(e => e.Key));
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 10, 16, 0, TimeSpan.Zero), events[1].CreatedAtUtc);
        Assert.Equal(9000L, events[1].ChainId);
    }

    [Fact]
    public void An_EventEx_is_filed_under_its_type_id_rather_than_its_class()
    {
        // Every esx.problem arrives as the one class EventEx. Categorising on
        // the class would put a lost storage path and an isolated host under
        // the same heading and be confidently wrong about both.
        var storage = VsphereEventParser.ParseLatestPage(LatestPage)![0];

        Assert.Equal("EventEx", storage.EventClass);
        Assert.Equal("esx.problem.storage.connectivity.lost", storage.TypeId);
        Assert.Equal("error", storage.Severity);
    }

    [Fact]
    public void An_ordinary_event_is_its_own_type()
    {
        var powerOff = VsphereEventParser.ParseLatestPage(LatestPage)![1];

        Assert.Equal("VmPoweredOffEvent", powerOff.EventClass);
        Assert.Equal("VmPoweredOffEvent", powerOff.TypeId);
        Assert.Null(powerOff.Severity);
    }

    [Fact]
    public void The_objects_an_event_names_keep_their_reference_and_their_name()
    {
        var powerOff = VsphereEventParser.ParseLatestPage(LatestPage)![1];

        Assert.Equal("host-42", powerOff.Host?.MoRef);
        Assert.Equal("esx01.corp.local", powerOff.Host?.Name);
        Assert.Equal("vm-101", powerOff.VirtualMachine?.MoRef);
        Assert.Equal("domain-c7", powerOff.ComputeResource?.MoRef);
        Assert.Equal("DC1", powerOff.DatacenterName);

        // The one wrapper whose reference is not named after itself: ds/datastore.
        Assert.Equal("datastore-11", powerOff.Datastore?.MoRef);
        Assert.Equal("ds-gold-01", powerOff.Datastore?.Name);
    }

    [Fact]
    public void Who_did_it_is_kept_and_nobody_is_not_an_empty_name()
    {
        var events = VsphereEventParser.ParseLatestPage(LatestPage)!;

        // M2.4 reads this to say who took a snapshot. An empty string would
        // read as a user with no name rather than as "the system did it".
        Assert.Null(events[0].UserName);
        Assert.Equal(@"VSPHERE.LOCAL\ertugrul", events[1].UserName);
    }

    [Fact]
    public void The_type_id_is_recovered_from_fullFormat_when_eventTypeId_is_absent()
    {
        const string xml = """
            <ReadPreviousEventsResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval xsi:type="EventEx">
                <key>77</key>
                <createdTime>2026-09-21T09:00:00Z</createdTime>
                <fullFormattedMessage>Host is isolated</fullFormattedMessage>
                <fullFormat>com.vmware.vc.HA.HostIsolatedEvent|Host {host.name} is isolated</fullFormat>
              </returnval>
            </ReadPreviousEventsResponse>
            """;

        var isolated = Assert.Single(VsphereEventParser.ParseEvents(xml)!);

        Assert.Equal("com.vmware.vc.HA.HostIsolatedEvent", isolated.TypeId);
        Assert.Equal("EventEx", isolated.EventClass);
    }

    [Fact]
    public void A_task_event_is_filed_under_what_the_task_was()
    {
        // Every task arrives as the one class TaskEvent. What it was is
        // info.descriptionId, and M2.4 needs exactly that to find the task that
        // took a snapshot — the rendered message is localised and not safe to
        // match on.
        const string xml = """
            <ReadPreviousEventsResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval xsi:type="TaskEvent">
                <key>501</key>
                <chainId>501</chainId>
                <createdTime>2026-09-01T10:00:00Z</createdTime>
                <userName>CORP\alice</userName>
                <vm><name>fileserver</name><vm type="VirtualMachine">vm-1</vm></vm>
                <fullFormattedMessage>Task: Create virtual machine snapshot</fullFormattedMessage>
                <info>
                  <key>task-9001</key>
                  <task type="Task">task-9001</task>
                  <name>CreateSnapshot_Task</name>
                  <descriptionId>VirtualMachine.createSnapshot</descriptionId>
                  <state>queued</state>
                </info>
              </returnval>
            </ReadPreviousEventsResponse>
            """;

        var task = Assert.Single(VsphereEventParser.ParseEvents(xml)!);

        Assert.Equal("TaskEvent", task.EventClass);
        Assert.Equal("VirtualMachine.createSnapshot", task.TypeId);
        Assert.Equal(@"CORP\alice", task.UserName);
        Assert.Equal("vm-1", task.VirtualMachine?.MoRef);
    }

    [Fact]
    public void A_ReadPreviousEvents_reply_is_one_event_per_returnval()
    {
        const string xml = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <soapenv:Body>
                <ReadPreviousEventsResponse xmlns="urn:vim25">
                  <returnval xsi:type="UserLoginSessionEvent">
                    <key>10</key><createdTime>2026-09-21T08:00:00Z</createdTime>
                    <userName>svc-observatory</userName>
                    <fullFormattedMessage>User svc-observatory logged in</fullFormattedMessage>
                  </returnval>
                  <returnval xsi:type="vim25:AlarmStatusChangedEvent">
                    <key>11</key><createdTime>2026-09-21T08:00:01Z</createdTime>
                    <fullFormattedMessage>Alarm changed</fullFormattedMessage>
                  </returnval>
                </ReadPreviousEventsResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        var events = VsphereEventParser.ParseEvents(xml);

        Assert.NotNull(events);
        Assert.Equal(["UserLoginSessionEvent", "AlarmStatusChangedEvent"], events.Select(e => e.EventClass));
    }

    [Fact]
    public void An_event_without_a_key_or_a_time_is_dropped_rather_than_guessed()
    {
        // Without the key it cannot be placed against the mark; without the
        // time it cannot be kept for a bounded period.
        const string xml = """
            <ReadPreviousEventsResponse xmlns="urn:vim25">
              <returnval><createdTime>2026-09-21T08:00:00Z</createdTime><fullFormattedMessage>no key</fullFormattedMessage></returnval>
              <returnval><key>12</key><fullFormattedMessage>no time</fullFormattedMessage></returnval>
              <returnval><key>13</key><createdTime>2026-09-21T08:00:00Z</createdTime><fullFormattedMessage>fine</fullFormattedMessage></returnval>
            </ReadPreviousEventsResponse>
            """;

        Assert.Equal(13L, Assert.Single(VsphereEventParser.ParseEvents(xml)!).Key);
    }

    [Fact]
    public void Nothing_to_read_and_could_not_read_are_different_answers()
    {
        // Empty says "we looked and nothing happened"; null says "we did not
        // see". Conflating them is how a blind collector reads as a quiet estate.
        const string empty = """
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="EventHistoryCollector">session[1]2</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """;

        Assert.Empty(VsphereEventParser.ParseLatestPage(empty)!);
        Assert.Empty(VsphereEventParser.ParseEvents("<ReadPreviousEventsResponse xmlns=\"urn:vim25\" />")!);

        Assert.Null(VsphereEventParser.ParseLatestPage("<not xml"));
        Assert.Null(VsphereEventParser.ParseEvents("<not xml"));
    }

    [Fact]
    public void The_collector_reference_is_read_from_the_create_reply()
    {
        const string xml = """
            <CreateCollectorForEventsResponse xmlns="urn:vim25">
              <returnval type="EventHistoryCollector">session[52b1a7c3]52c0e1f4</returnval>
            </CreateCollectorForEventsResponse>
            """;

        Assert.Equal("session[52b1a7c3]52c0e1f4", VsphereEventParser.ParseCollector(xml));
        Assert.Null(VsphereEventParser.ParseCollector("<CreateCollectorForEventsResponse xmlns=\"urn:vim25\" />"));
    }
}
