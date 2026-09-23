using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Redfish.Tests;

/// <summary>The iLO folded onto its ESXi host (ADR-0027), against Kibar's recorded iLO 5.</summary>
public class RedfishInventorySourceTests
{
    private static readonly EntityId Host = EntityId.For("vc-kibar", "host-4");

    private static (FakeIlo Ilo, FakeDirectory Directory, FixedClock Clock) Kibar()
    {
        var ilo = new FakeIlo();
        var directory = new FakeDirectory();
        directory.Uuids[ilo.Uuid.ToLowerInvariant()] = Host; // vSphere reports it lower case
        return (ilo, directory, new FixedClock());
    }

    private static IReadOnlyDictionary<string, string> Settings(InventorySnapshot snapshot) =>
        Assert.Single(snapshot.Annotations).Settings;

    [Fact]
    public async Task The_recorded_ilo5_folds_onto_its_host_and_annotates_what_it_measured()
    {
        var (ilo, directory, clock) = Kibar();

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Failures);
        Assert.Empty(snapshot.Alerts);

        var annotation = Assert.Single(snapshot.Annotations);
        Assert.Equal(Host, annotation.Entity);
        Assert.Equal("redfish", annotation.Namespace);

        var s = annotation.Settings;
        Assert.Equal("uuid", s["redfish.fold_rule"]);
        Assert.Equal("ProLiant DL380 Gen10", s["redfish.model"]);
        Assert.Equal("OK", s["redfish.aggregate_health"]);
        Assert.Equal("Ready", s["redfish.ams"]);
        Assert.Equal("2", s["redfish.psu.count"]);
        Assert.Equal("OK", s["redfish.psu.redundancy"]);
        Assert.Equal("6", s["redfish.fan.count"]);
        Assert.Equal("Redundant", s["redfish.fan.redundancy"]);
        Assert.Equal("77", s["redfish.temperature.sensors"]);
        Assert.Equal("0", s["redfish.temperature.at_critical"]);
        Assert.Equal("2", s["redfish.storage.controllers"]);
        Assert.Equal("14", s["redfish.storage.drives"]);
        Assert.Equal("0", s["redfish.storage.failure_predicted"]);
        Assert.Equal("24", s["redfish.memory.dimms"]);
        Assert.Equal("36", s["redfish.firmware.components"]);
        Assert.Equal("196", s["redfish.iml.entries"]); // the recorded $filter reply; live reads all 207
        Assert.Equal("2026-06-09T18:04:49.0000000Z", s["redfish.iml.newest_utc"]);
    }

    [Fact]
    public async Task A_cycle_is_three_gets_the_deep_walk_runs_daily()
    {
        var (ilo, directory, clock) = Kibar();
        var source = ilo.Source(directory, clock);

        await source.ReadAsync(CancellationToken.None);
        var firstCycle = ilo.Requests.Count;
        ilo.Requests.Clear();

        clock.UtcNow += TimeSpan.FromMinutes(5);
        var second = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(23, firstCycle); // 3 + storage + 2 controllers + 14 drives + memory + firmware + IML
        Assert.Equal(
            ["GET /redfish/v1/Chassis/1/Power", "GET /redfish/v1/Chassis/1/Thermal", "GET /redfish/v1/Systems/1"],
            ilo.Requests.Order(StringComparer.Ordinal));

        // The deep walk's result stands between walks.
        Assert.Equal("14", Settings(second)["redfish.storage.drives"]);

        ilo.Requests.Clear();
        clock.UtcNow += TimeSpan.FromDays(1);
        await source.ReadAsync(CancellationToken.None);

        // Incremental: only entries after the newest one seen.
        Assert.Contains("GET /redfish/v1/Systems/1/LogServices/IML/Entries?$filter=Created gt '2026-06-09T18:04:49Z'", ilo.Requests);
        Assert.Contains("GET /redfish/v1/Systems/1/Storage", ilo.Requests);
    }

    [Fact]
    public async Task Aggregate_health_leaving_ok_walks_at_once()
    {
        var (ilo, directory, clock) = Kibar();
        var source = ilo.Source(directory, clock);
        await source.ReadAsync(CancellationToken.None);

        ilo.System["Oem"]!["Hpe"]!["AggregateHealthStatus"]!["AggregateServerHealth"] = "Warning";
        ilo.Requests.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(5);
        await source.ReadAsync(CancellationToken.None);

        Assert.Contains("GET /redfish/v1/Systems/1/Storage", ilo.Requests);

        // Still not OK: no walk every cycle.
        ilo.Requests.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(5);
        await source.ReadAsync(CancellationToken.None);
        Assert.Equal(3, ilo.Requests.Count);
    }

    [Fact]
    public async Task A_byte_swapped_smbios_uuid_folds_and_says_so()
    {
        var (ilo, _, clock) = Kibar();
        var directory = new FakeDirectory();
        directory.Uuids[RedfishInventorySource.ByteSwapped(ilo.Uuid)!] = Host;

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Failures);
        Assert.Equal(Host, Assert.Single(snapshot.Annotations).Entity);
        Assert.Equal("uuid-byte-swapped", Settings(snapshot)["redfish.fold_rule"]);
    }

    [Fact]
    public void Byte_swap_reverses_the_first_three_fields_only() =>
        Assert.Equal(
            "33221100-5544-7766-8899-aabbccddeeff",
            RedfishInventorySource.ByteSwapped("00112233-4455-6677-8899-aabbccddeeff"));

    [Fact]
    public async Task The_serial_folds_only_when_no_uuid_form_matches()
    {
        var (ilo, _, clock) = Kibar();
        var directory = new FakeDirectory();
        directory.Serials[ilo.Serial] = Host;

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        Assert.Equal("serial", Settings(snapshot)["redfish.fold_rule"]);
    }

    [Fact]
    public async Task No_match_is_could_not_fold_never_a_guess_and_hardware_alerts_still_raise()
    {
        var (ilo, _, clock) = Kibar();
        ilo.Power["Redundancy"]![0]!["Status"]!["Health"] = "Critical";

        var snapshot = await ilo.Source(new FakeDirectory(), clock).ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Annotations);
        var failure = Assert.Single(snapshot.Failures);
        Assert.Equal(CollectionFailureKind.NotConfigured, failure.Kind);
        Assert.Contains("could not fold", failure.Detail, StringComparison.Ordinal);
        Assert.Contains(ilo.Uuid, failure.Detail, StringComparison.Ordinal);

        var alert = Assert.Single(snapshot.Alerts);
        Assert.Null(alert.Entity);
    }

    [Fact]
    public async Task Lost_psu_redundancy_warns_and_clears_when_it_returns()
    {
        var (ilo, directory, clock) = Kibar();
        var source = ilo.Source(directory, clock);
        ilo.Power["Redundancy"]![0]!["Status"]!["Health"] = "Warning";

        var lost = await source.ReadAsync(CancellationToken.None);

        var alert = Assert.Single(lost.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("Power supply redundancy lost", alert.Title);
        Assert.Equal(Host, alert.Entity);

        ilo.Power["Redundancy"]![0]!["Status"]!["Health"] = "OK";
        var back = await source.ReadAsync(CancellationToken.None);

        Assert.Empty(back.Alerts);
    }

    [Fact]
    public async Task Lost_fan_redundancy_warns()
    {
        var (ilo, directory, clock) = Kibar();
        ilo.System["Oem"]!["Hpe"]!["AggregateHealthStatus"]!["FanRedundancy"] = "FailedRedundant";

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        Assert.Equal("Fan redundancy lost", Assert.Single(snapshot.Alerts).Title);
    }

    [Fact]
    public async Task A_temperature_at_its_critical_threshold_is_critical()
    {
        var (ilo, directory, clock) = Kibar();
        var inlet = ilo.Thermal["Temperatures"]![0]!;
        inlet["ReadingCelsius"] = inlet["UpperThresholdCritical"]!.GetValue<int>();

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        var alert = Assert.Single(snapshot.Alerts);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("01-Inlet Ambient", alert.Description, StringComparison.Ordinal);
        Assert.Equal("1", Settings(snapshot)["redfish.temperature.at_critical"]);
    }

    [Fact]
    public async Task A_drive_predicting_failure_warns_and_stays_raised_between_walks()
    {
        var (ilo, directory, clock) = Kibar();
        ilo.Docs["/redfish/v1/Systems/1/Storage/DE07C000/Drives/3"]["FailurePredicted"] = true;
        var source = ilo.Source(directory, clock);

        var walked = await source.ReadAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromMinutes(5);
        var cached = await source.ReadAsync(CancellationToken.None);

        foreach (var snapshot in new[] { walked, cached })
        {
            var alert = Assert.Single(snapshot.Alerts);
            Assert.Equal(AlertSeverity.Warning, alert.Severity);
            Assert.Equal("Drive failure predicted", alert.Title);
            Assert.Equal("1", Settings(snapshot)["redfish.storage.failure_predicted"]);
        }

        Assert.Equal(walked.Alerts[0].Fingerprint, cached.Alerts[0].Fingerprint);
    }

    [Fact]
    public async Task A_missing_field_is_unknown_never_ok_and_never_an_alert()
    {
        var (ilo, directory, clock) = Kibar();
        ilo.System["Oem"]!["Hpe"]!.AsObject().Remove("AggregateHealthStatus");
        ilo.Power.AsObject().Remove("Redundancy");
        ilo.Docs["/redfish/v1/Systems/1/Storage/DE07C000/Drives/0"].AsObject().Remove("FailurePredicted");

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Alerts);
        var s = Settings(snapshot);
        Assert.False(s.ContainsKey("redfish.aggregate_health"));
        Assert.False(s.ContainsKey("redfish.psu.redundancy"));
        Assert.False(s.ContainsKey("redfish.fan.redundancy"));

        Assert.Contains(snapshot.Coverage, c => c is { ObjectType: "System", Property: "AggregateServerHealth", Asked: 1, Answered: 0 });
        Assert.Contains(snapshot.Coverage, c => c is { ObjectType: "Power", Property: "Redundancy.Status.Health", Answered: 0 });
        Assert.Contains(snapshot.Coverage, c => c is { ObjectType: "Drive", Property: "FailurePredicted", Asked: 14, Answered: 13 });
    }

    [Fact]
    public async Task A_drive_that_cannot_be_read_is_a_named_partial_failure()
    {
        var (ilo, directory, clock) = Kibar();
        ilo.Docs.Remove("/redfish/v1/Systems/1/Storage/DE082000/Drives/2");

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);

        var failure = Assert.Single(snapshot.Failures);
        Assert.Contains("DE082000/Drives/2", failure.Target, StringComparison.Ordinal);
        Assert.Equal("13", Settings(snapshot)["redfish.storage.drives"]);
    }

    [Fact]
    public async Task A_rejected_password_fails_the_whole_read_as_authentication()
    {
        var (ilo, directory, clock) = Kibar();
        var source = new RedfishInventorySource("ilo-test", new RedfishChannel(ilo, new RedfishConnectionOptions
        {
            InstanceId = "ilo-test",
            BaseAddress = new Uri("https://ilo.test.local"),
            Username = "observatory",
            Password = Application.Security.Secret.From("wrong"),
        }), directory, clock);

        var ex = await Assert.ThrowsAsync<RedfishApiException>(() => source.ReadAsync(CancellationToken.None));

        Assert.Equal(CollectionFailureKind.AuthenticationRejected, ex.Kind);
    }

    [Fact]
    public async Task Folding_adds_no_entity_and_leaves_vsphere_s_settings_alone()
    {
        var (ilo, directory, clock) = Kibar();
        var host = new Entity
        {
            Id = Host,
            Kind = EntityKind.EsxiHost,
            DisplayName = "alhcesx04",
            SourceInstanceId = "vc-kibar",
            LastSeenUtc = clock.UtcNow,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Syslog.global.logHost"] = "x" },
        };
        var graph = EntityGraph.Empty.Merge([host], [], ["vc-kibar"], clock.UtcNow, EntityRetentionPolicy.Default);

        var snapshot = await ilo.Source(directory, clock).ReadAsync(CancellationToken.None);
        var merged = graph.Merge(
            [], [], ["ilo-test"], clock.UtcNow, EntityRetentionPolicy.Default,
            [.. snapshot.Annotations.Select(a => a with { SourceInstanceId = "ilo-test", ReadAtUtc = clock.UtcNow })]);

        Assert.Equal(graph.Entities.Count, merged.Entities.Count);
        var after = merged.Entities[Host];
        Assert.Equal("2", after.Settings["redfish.psu.count"]);
        Assert.Equal("x", after.Settings["Syslog.global.logHost"]);
        Assert.Equal("vc-kibar", after.SourceInstanceId);
    }
}
