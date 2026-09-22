using System.Net;
using System.Text;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// M8.8: the backup product's "last backup" custom attribute on a VM, read
/// into a UTC time — only the measured formats
/// (docs/measurements/backup-freshness-shapes.md), never a guess.
/// </summary>
public class BackupAttributeTests
{
    private static readonly TimeZoneInfo Plus3 =
        TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    // --- which attribute -------------------------------------------------------

    [Theory]
    [InlineData("Last Backup", true)]
    [InlineData("last backup time", true)]
    [InlineData("LastBackupDate", true)]
    [InlineData("Backup Time", true)]
    [InlineData("Backup Status", false)]
    [InlineData("Last Patched", false)]
    [InlineData("Owner", false)]
    public void A_last_backup_attribute_is_named_for_backup_and_a_time(string name, bool expected) =>
        Assert.Equal(expected, BackupAttributeParser.IsLastBackupField(name));

    [Fact]
    public void Only_vm_or_untyped_definitions_are_kept()
    {
        var definitions = new[]
        {
            Definition("48", "Last Backup", "VirtualMachine"),
            Definition("49", "Backup Status", "VirtualMachine"),
            Definition("50", "Last Backup", "HostSystem"),
            Definition("51", "Last Backup Time", string.Empty),
        };

        var fields = BackupAttributeParser.LastBackupFields(definitions);

        Assert.Equal(["48", "51"], fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Last Backup", fields["48"]);
    }

    // --- the value, as measured ----------------------------------------------------

    [Theory]
    [InlineData("21.09.2026 22:00:15", "2026-09-21T19:00:15Z")] // 84 of 86 live: dd.MM.yyyy
    [InlineData("1.09.2026 22:00:15", "2026-09-01T19:00:15Z")]  // 1 of 86: single-digit day
    [InlineData("08/15/2026 22:00:15", "2026-08-15T19:00:15Z")] // 1 of 86: month first, unambiguous
    [InlineData("15/08/2026 22:00:15", "2026-08-15T19:00:15Z")] // day first, unambiguous
    [InlineData("04/04/2026 10:00", "2026-04-04T07:00:00Z")]    // both parts equal: one reading
    [InlineData("2026-09-21 22:00:15", "2026-09-21T19:00:15Z")]  // ISO without an offset
    public void A_measured_format_reads_in_the_collector_zone(string value, string expectedUtc)
    {
        var (utc, basis) = BackupAttributeParser.ParseTimestamp(value, Plus3);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), utc);
        Assert.Equal("collector local time, UTC+03:00", basis);
    }

    [Fact]
    public void An_iso_value_with_an_offset_keeps_its_own()
    {
        var (utc, basis) = BackupAttributeParser.ParseTimestamp("2026-09-21T22:00:15+01:00", Plus3);

        Assert.Equal(new DateTimeOffset(2026, 9, 21, 21, 0, 15, TimeSpan.Zero), utc);
        Assert.Equal("the offset in the value", basis);
    }

    [Theory]
    [InlineData("03/04/2026 10:00:00")] // March 4th or April 3rd: not guessed
    [InlineData("Backup Job ID [12345678] Client: [x], Backup Set: [y], Subclient: [z]")]
    [InlineData("31.02.2026 10:00:00")]
    [InlineData("")]
    public void An_unreadable_value_is_not_a_time(string value) =>
        Assert.Null(BackupAttributeParser.ParseTimestamp(value, Plus3).Utc);

    // --- one VM --------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Fields =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["48"] = "Last Backup", ["52"] = "Backup Time" };

    [Fact]
    public void A_vm_with_the_attribute_carries_its_time_name_and_value()
    {
        var vm = VmObject(("48", "21.09.2026 22:00:15"), ("49", "Backup Job ID [1]"));

        var verdicts = BackupAttributeParser.Read(vm, Fields, Plus3);

        Assert.Equal("true", verdicts[InventoryVerdicts.BackupRead]);
        Assert.Equal("Last Backup", verdicts[InventoryVerdicts.BackupField]);
        Assert.Equal("21.09.2026 22:00:15", verdicts[InventoryVerdicts.BackupValue]);
        Assert.Equal("2026-09-21T19:00:15.0000000+00:00", verdicts[InventoryVerdicts.BackupLastUtc]);
        Assert.Equal("collector local time, UTC+03:00", verdicts[InventoryVerdicts.BackupTimeBasis]);
    }

    [Fact]
    public void An_unparseable_value_is_carried_without_a_time()
    {
        var verdicts = BackupAttributeParser.Read(VmObject(("48", "03/04/2026 10:00:00")), Fields, Plus3);

        Assert.Equal("Last Backup", verdicts[InventoryVerdicts.BackupField]);
        Assert.Equal("03/04/2026 10:00:00", verdicts[InventoryVerdicts.BackupValue]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.BackupLastUtc));
    }

    [Fact]
    public void A_vm_with_no_custom_values_was_read_and_has_no_attribute()
    {
        var verdicts = BackupAttributeParser.Read(VmObject(), Fields, Plus3);

        Assert.Equal("true", verdicts[InventoryVerdicts.BackupRead]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.BackupField));
    }

    [Fact]
    public void An_empty_attribute_value_is_no_attribute()
    {
        var verdicts = BackupAttributeParser.Read(VmObject(("48", string.Empty)), Fields, Plus3);

        Assert.Equal("true", verdicts[InventoryVerdicts.BackupRead]);
        Assert.False(verdicts.ContainsKey(InventoryVerdicts.BackupField));
    }

    [Fact]
    public void Of_two_last_backup_attributes_the_newest_readable_one_wins()
    {
        var verdicts = BackupAttributeParser.Read(
            VmObject(("48", "20.09.2026 22:00:15"), ("52", "2026-09-21 01:00:00")), Fields, Plus3);

        Assert.Equal("Backup Time", verdicts[InventoryVerdicts.BackupField]);
    }

    [Fact]
    public void Unread_definitions_or_an_unread_custom_value_leave_nothing()
    {
        Assert.Empty(BackupAttributeParser.Read(VmObject(("48", "21.09.2026 22:00:15")), null, Plus3));

        var unread = new PropertyObject { MoRef = "vm-1", Type = "VirtualMachine" };
        Assert.Empty(BackupAttributeParser.Read(unread, Fields, Plus3));
    }

    // --- the client ----------------------------------------------------------------

    [Fact]
    public void The_inventory_asks_every_vm_for_its_custom_values_whole()
    {
        Assert.Contains("customValue", VsphereClient.InventoryPropertiesFor("VirtualMachine"));
        Assert.DoesNotContain(VsphereClient.InventoryPropertiesFor("VirtualMachine"),
            p => p.StartsWith("customValue.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_definitions_are_read_in_a_call_of_their_own_and_the_vm_carries_its_backup_time()
    {
        var server = new CustomFieldServer();
        using var client = Client(server);

        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        var vm = Assert.Single(payload.VirtualMachines);
        Assert.Equal("Last Backup", vm.Verdicts[InventoryVerdicts.BackupField]);
        Assert.Equal("2026-09-21T19:00:15.0000000+00:00", vm.Verdicts[InventoryVerdicts.BackupLastUtc]);
        Assert.Empty(payload.Failures);

        var body = Assert.Single(server.FieldBodies);
        var paths = XDocument.Parse(body).Descendants()
            .Where(e => e.Name.LocalName == "pathSet").Select(e => e.Value).ToList();
        Assert.Equal(["field"], paths);
        Assert.Contains(">CustomFieldsManager<", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_definition_read_is_a_failure_and_the_vm_is_not_read_for_backup()
    {
        using var client = Client(new CustomFieldServer { RefuseFields = true });

        var payload = await client.RetrieveInventoryAsync(CancellationToken.None);

        var vm = Assert.Single(payload.VirtualMachines);
        Assert.False(vm.Verdicts.ContainsKey(InventoryVerdicts.BackupRead));
        var failure = Assert.Single(payload.Failures);
        Assert.Contains("custom field", failure.Target, StringComparison.Ordinal);
        Assert.True(failure.IsPermissionDenied);
    }

    private static VsphereClient Client(CustomFieldServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("https://vc.invalid") },
            new VsphereConnectionOptions
            {
                BaseAddress = new Uri("https://vc.invalid"),
                Username = "svc-readonly@vsphere.local",
                Password = Secret.From("not-a-real-password"),
                InstanceId = "vc-test",
            })
        {
            BackupTimeZone = Plus3,
        };

    private static PropertyNode Definition(string key, string name, string type) => new()
    {
        Name = "CustomFieldDef",
        Type = "CustomFieldDef",
        Children =
        [
            new PropertyNode { Name = "key", Text = key },
            new PropertyNode { Name = "name", Text = name },
            new PropertyNode { Name = "managedObjectType", Text = type },
            new PropertyNode { Name = "type", Text = "string" },
        ],
    };

    private static PropertyObject VmObject(params (string Key, string Value)[] values) => values.Length == 0
        ? new PropertyObject
        {
            MoRef = "vm-1",
            Type = "VirtualMachine",
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["customValue"] = string.Empty },
        }
        : new PropertyObject
        {
            MoRef = "vm-1",
            Type = "VirtualMachine",
            Structures = new Dictionary<string, IReadOnlyList<PropertyNode>>(StringComparer.Ordinal)
            {
                ["customValue"] =
                [
                    .. values.Select(v => new PropertyNode
                    {
                        Name = "CustomFieldValue",
                        Type = "CustomFieldStringValue",
                        Children =
                        [
                            new PropertyNode { Name = "key", Text = v.Key },
                            new PropertyNode { Name = "value", Text = v.Value },
                        ],
                    }),
                ],
            },
        };

    private sealed class CustomFieldServer : HttpMessageHandler
    {
        public bool RefuseFields { get; init; }

        public List<string> FieldBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var method = XDocument.Parse(body).Descendants()
                .First(e => e.Name.LocalName == "Body").Elements().First().Name.LocalName;

            switch (method)
            {
                case "RetrieveServiceContent":
                    return Ok(ServiceContent);
                case "Login":
                    return Ok("<LoginResponse xmlns=\"urn:vim25\"><returnval><key>s</key></returnval></LoginResponse>");
                case "CreateContainerView":
                    return Ok("<CreateContainerViewResponse xmlns=\"urn:vim25\"><returnval type=\"ContainerView\">view-1</returnval></CreateContainerViewResponse>");
                case "DestroyView":
                    return Ok("<DestroyViewResponse xmlns=\"urn:vim25\" />");
                case "RetrievePropertiesEx" when body.Contains("type=\"CustomFieldsManager\"", StringComparison.Ordinal):
                    FieldBodies.Add(body);
                    return RefuseFields ? NoPermission() : Ok(FieldPage);
                case "RetrievePropertiesEx" when body.Contains("type=\"Folder\"", StringComparison.Ordinal):
                    return Ok(EmptyPage);
                case "RetrievePropertiesEx":
                    return Ok(InventoryPage);
                default:
                    throw new InvalidOperationException($"Unscripted call {method}.");
            }
        }

        private const string EmptyPage = """
            <RetrievePropertiesExResponse xmlns="urn:vim25" />
            """;

        private const string InventoryPage = """
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-1</obj>
                  <propSet><name>name</name><val xsi:type="xsd:string">app01</val></propSet>
                  <propSet>
                    <name>customValue</name>
                    <val xsi:type="ArrayOfCustomFieldValue">
                      <CustomFieldValue xsi:type="CustomFieldStringValue"><key>48</key><value>21.09.2026 22:00:15</value></CustomFieldValue>
                      <CustomFieldValue xsi:type="CustomFieldStringValue"><key>49</key><value>Backup Job ID [1]</value></CustomFieldValue>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        private const string FieldPage = """
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="CustomFieldsManager">CustomFieldsManager</obj>
                  <propSet>
                    <name>field</name>
                    <val xsi:type="ArrayOfCustomFieldDef">
                      <CustomFieldDef xsi:type="CustomFieldDef"><key>48</key><name>Last Backup</name><type>string</type><managedObjectType>VirtualMachine</managedObjectType></CustomFieldDef>
                      <CustomFieldDef xsi:type="CustomFieldDef"><key>49</key><name>Backup Status</name><type>string</type><managedObjectType>VirtualMachine</managedObjectType></CustomFieldDef>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """;

        private const string ServiceContent = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
              <soapenv:Body>
                <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
                  <rootFolder type="Folder">group-d1</rootFolder>
                  <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
                  <viewManager type="ViewManager">ViewManager</viewManager>
                  <about><name>vc-test</name><apiVersion>8.0.3.0</apiVersion></about>
                  <sessionManager type="SessionManager">SessionManager</sessionManager>
                  <perfManager type="PerformanceManager">PerfMgr</perfManager>
                  <customFieldsManager type="CustomFieldsManager">CustomFieldsManager</customFieldsManager>
                </returnval></RetrieveServiceContentResponse>
              </soapenv:Body>
            </soapenv:Envelope>
            """;

        private static HttpResponseMessage Ok(string xml) =>
            new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

        private static HttpResponseMessage NoPermission() => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""
                <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <soapenv:Body>
                    <soapenv:Fault>
                      <faultcode>ServerFaultCode</faultcode>
                      <faultstring>Permission to perform this operation was denied.</faultstring>
                      <detail><NoPermissionFault xmlns="urn:vim25" xsi:type="NoPermission" /></detail>
                    </soapenv:Fault>
                  </soapenv:Body>
                </soapenv:Envelope>
                """, Encoding.UTF8, "text/xml"),
        };
    }
}
