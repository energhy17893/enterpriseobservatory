using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using K = EnterpriseObservatory.Application.Compliance.InventoryVerdictKeys;

namespace EnterpriseObservatory.Collectors.Simplivity.Tests;

/// <summary>
/// S4: GET /api/hosts/{id}/hardware onto the vSphere host. Every reply is the
/// one real Kibar reply (masked); any other colour is an edited copy of it.
/// </summary>
public class SimplivityHardwareTests
{
    private const string First = "00a1b2c3-0000-4000-8000-000000000001"; // folds onto host-21

    private static EntityId Vc(string moRef) => EntityId.For(FakeOvc.Vcenter, moRef);

    private static FakeDirectory Estate() =>
        FakeDirectory.For("host-21", "host-22", "host-23", "domain-c7", "vm-101", "vm-102", "vm-103", "vm-104");

    private static async Task<InventorySnapshot> Read(Action<JsonNode>? edit = null)
    {
        var ovc = FakeOvc.FromFixtures();

        if (edit is not null)
        {
            var tree = FakeOvc.LiveHardware();
            edit(tree);
            ovc.Hardware[First] = tree;
        }

        return await ovc.Source(Estate()).ReadAsync(CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, string> Host21(InventorySnapshot snapshot) =>
        Assert.Single(snapshot.Annotations, a => a.Entity == Vc("host-21")).Settings;

    private static IEnumerable<AlertDefinition> HardwareAlerts(InventorySnapshot snapshot) =>
        snapshot.Alerts.Where(a => a.Title.Contains("drive", StringComparison.Ordinal) ||
                                   a.Title.Contains("RAID", StringComparison.Ordinal) ||
                                   a.Title.Contains("SSD", StringComparison.Ordinal));

    private static IEnumerable<JsonNode> Drives(JsonNode tree) =>
        tree["logical_drives"]!.AsArray()
            .SelectMany(l => l!["drive_sets"]!.AsArray())
            .SelectMany(d => d!["physical_drives"]!.AsArray())
            .Select(p => p!);

    [Fact]
    public async Task The_real_all_green_reply_is_annotated_and_raises_nothing()
    {
        var snapshot = await Read();

        var hw = Host21(snapshot);
        Assert.Equal("GREEN", hw[K.SimplivityHwStatus]);
        Assert.Equal("GREEN", hw[K.SimplivityHwRaidStatus]);
        Assert.Equal("GREEN", hw[K.SimplivityHwBatteryStatus]);
        Assert.Equal("HEALTHY", hw[K.SimplivityHwBatteryHealth]);
        Assert.Equal("24", hw[K.SimplivityHwDrives]);
        Assert.Equal("GREEN=24", hw[K.SimplivityHwDriveStatus]);
        Assert.Equal("HEALTHY=24", hw[K.SimplivityHwDriveHealth]);
        Assert.Equal("99", hw[K.SimplivityHwLifeRemainingMin]);
        Assert.Equal("0", hw[K.SimplivityHwDrivesRebuilding]);

        // HPE answers -1 for "no reading": Unknown, not 0% and not full.
        Assert.False(hw.ContainsKey(K.SimplivityHwBatteryCharge));

        Assert.Empty(HardwareAlerts(snapshot));
        Assert.Contains(snapshot.Coverage, c => c is { ObjectType: "hosts", Property: "hardware", Asked: 3, Answered: 3 });
    }

    [Fact]
    public async Task An_empty_accelerator_status_is_unknown_never_green_and_never_an_alert()
    {
        // The real reply is one of Kibar's 8 hosts without the card: status "".
        var snapshot = await Read();

        Assert.Equal("", FakeOvc.LiveHardware()["accelerator_card"]!["status"]!.GetValue<string>());
        Assert.False(Host21(snapshot).ContainsKey(K.SimplivityHwAcceleratorStatus));
        Assert.Empty(HardwareAlerts(snapshot));

        var withCard = await Read(t => t["accelerator_card"]!["status"] = "GREEN");
        Assert.Equal("GREEN", Host21(withCard)[K.SimplivityHwAcceleratorStatus]);
    }

    [Fact]
    public async Task Red_drives_are_one_critical_alert_per_host_with_the_count()
    {
        var snapshot = await Read(t =>
        {
            foreach (var drive in Drives(t).Take(2))
            {
                drive["status"] = "RED";
            }
        });

        Assert.Equal("GREEN=22;RED=2", Host21(snapshot)[K.SimplivityHwDriveStatus]);
        var alert = Assert.Single(HardwareAlerts(snapshot));
        Assert.Equal((Vc("host-21"), AlertSeverity.Critical), (alert.Entity, alert.Severity));
        Assert.Contains("2 RED", alert.Description, StringComparison.Ordinal);
        Assert.Contains("of 24", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_yellow_drive_is_a_warning()
    {
        var snapshot = await Read(t => Drives(t).First()["status"] = "YELLOW");

        Assert.Equal(AlertSeverity.Warning, Assert.Single(HardwareAlerts(snapshot)).Severity);
    }

    [Theory]
    [InlineData(4, AlertSeverity.Critical)]
    [InlineData(5, AlertSeverity.Critical)]
    [InlineData(8, AlertSeverity.Warning)]
    [InlineData(10, AlertSeverity.Warning)]
    public async Task Ssd_life_at_or_under_ten_percent_warns_and_at_or_under_five_is_critical(int life, AlertSeverity expected)
    {
        var snapshot = await Read(t => Drives(t).Last()["life_remaining"] = life);

        Assert.Equal(life.ToString(System.Globalization.CultureInfo.InvariantCulture), Host21(snapshot)[K.SimplivityHwLifeRemainingMin]);
        var alert = Assert.Single(HardwareAlerts(snapshot));
        Assert.Equal(expected, alert.Severity);
        Assert.Contains("SSD", alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eleven_percent_life_is_data_only()
    {
        var snapshot = await Read(t => Drives(t).Last()["life_remaining"] = 11);

        Assert.Empty(HardwareAlerts(snapshot));
    }

    [Fact]
    public async Task A_red_raid_card_is_critical_and_an_unhealthy_battery_warns()
    {
        var raid = await Read(t => t["raid_card"]!["status"] = "RED");
        Assert.Equal(AlertSeverity.Critical, Assert.Single(HardwareAlerts(raid)).Severity);

        var battery = await Read(t => t["battery"]!["health"] = "DEGRADED");
        Assert.Equal(AlertSeverity.Warning, Assert.Single(HardwareAlerts(battery)).Severity);
        Assert.Equal("DEGRADED", Host21(battery)[K.SimplivityHwBatteryHealth]);
    }

    [Fact]
    public async Task A_rebuilding_drive_is_counted()
    {
        var snapshot = await Read(t => Drives(t).First()["percent_rebuilt"] = 40);

        Assert.Equal("1", Host21(snapshot)[K.SimplivityHwDrivesRebuilding]);
    }

    [Fact]
    public async Task A_host_whose_tree_does_not_answer_keeps_its_annotation_with_hardware_unknown()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Hardware[First] = JsonValue.Create("maintenance")!;

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        var hw = Host21(snapshot);
        Assert.Equal("ALIVE", hw[K.SimplivityState]);
        Assert.DoesNotContain(hw.Keys, k => k.StartsWith("simplivity.hw.", StringComparison.Ordinal));
        Assert.Empty(HardwareAlerts(snapshot));

        var failure = Assert.Single(snapshot.Failures);
        Assert.Equal(CollectionFailureKind.ProtocolError, failure.Kind);
        Assert.Contains("esx01.lab.example", failure.Target, StringComparison.Ordinal);
        Assert.Contains(snapshot.Coverage, c => c is { ObjectType: "hosts", Property: "hardware", Asked: 3, Answered: 2 });
    }

    [Fact]
    public async Task Kibar_26_hosts_are_read_in_parallel_at_most_four_at_once()
    {
        var ovc = new FakeOvc { HardwareDelay = TimeSpan.FromMilliseconds(40) };
        ovc.Collections["hosts"] = FakeOvc.Hosts(26);
        var directory = FakeDirectory.For([.. Enumerable.Range(1, 26).Select(i => $"host-{i}")]);

        var snapshot = await ovc.Source(directory).ReadAsync(CancellationToken.None);

        Assert.Equal(26, snapshot.Annotations.Count(a => a.Settings.ContainsKey(K.SimplivityHwStatus)));
        Assert.Equal(26, ovc.Requests.Count(r => r.EndsWith("/hardware?show_optional_fields=true", StringComparison.Ordinal)));
        Assert.InRange(ovc.MaxHardwareInFlight, 2, SimplivityInventorySource.HardwareParallelism);
        Assert.Empty(snapshot.Alerts);
    }
}
