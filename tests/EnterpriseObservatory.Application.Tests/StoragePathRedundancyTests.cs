using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rule for the failure that changes nothing until it changes everything.
/// </summary>
/// <remarks>
/// <para>
/// A dead path is the one fault in this product that produces no symptom at
/// all. No counter moves, no latency rises, no machine complains — the
/// surviving path carries the load and the estate runs on one leg until that
/// leg goes too, at which point every machine on the volume stops at once.
/// It is the archetypal finding only a monitoring tool can produce, and the
/// firing tests below are the ones that matter.
/// </para>
/// <para>
/// The silences matter nearly as much, and the most important of them is the
/// single-path device. A LUN with one path has not lost redundancy; it never
/// had any. Conflating the two would put a permanent, unfixable alert on every
/// local disk and boot device in the estate, which is the noise principle 4
/// forbids. Each negative test below names what would reach an operator if the
/// gate it covers were removed.
/// </para>
/// </remarks>
public class StoragePathRedundancyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Host = "vc-1:host-1";

    /// <summary>The NAA the live estate's datastores are marked with.</summary>
    private const string Naa = "naa.600508b1001cb736";

    private const string LostTitle = "Storage path redundancy lost";
    private const string DownTitle = "No working path to storage device";

    private static StoragePath Path(
        string state,
        string name = "vmhba0:C0:T0:L1",
        string device = Naa,
        string key = "key-vim.host.ScsiDisk-0200",
        string adapter = "vmhba0") => new()
        {
            Name = name,
            State = state,
            StorageDeviceId = device,
            DeviceKey = key,
            Adapter = adapter,
        };

    private static Entity HostWith(params StoragePath[] paths) => new()
    {
        Id = new EntityId(Host),
        Kind = EntityKind.EsxiHost,
        DisplayName = "esx01",
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        StoragePaths = paths,
    };

    private static IReadOnlyList<AlertDefinition> Evaluate(params Entity[] entities) =>
        StoragePathRedundancy.Evaluate(entities);

    // --- the finding this rule exists for ---------------------------------

    [Fact]
    public void One_dead_path_beside_a_working_one_is_redundancy_lost()
    {
        // The whole point. Two paths, one gone, the device still served: no
        // counter anywhere in this product moves, and without this sentence
        // nobody learns anything until the second path dies.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("active"),
            Path("dead", name: "vmhba1:C0:T0:L1", adapter: "vmhba1"))));

        Assert.Equal(LostTitle, alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(new EntityId(Host), alert.Entity);
        Assert.Equal("vc-1", alert.Source);
    }

    [Fact]
    public void A_standby_path_counts_as_redundancy_rather_than_as_a_loss()
    {
        // ALUA. The standby path is working and unused, which is what a
        // correctly configured array looks like. Counting standby as lost
        // would raise this alert on most of the estate at once, and counting
        // it as absent would raise the severe verdict on a device that is
        // fully protected.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("standby"),
            Path("dead", name: "vmhba1:C0:T0:L1"))));

        Assert.Equal(LostTitle, alert.Title);
    }

    [Fact]
    public void The_alert_names_the_adapters_the_dead_paths_left_by()
    {
        // Four dead paths on one adapter is a cable or an SFP; four spread
        // across four adapters is the array. The adapter is the thing somebody
        // physically walks up to, so a description without it names no fix.
        //
        // The adapter deliberately does not appear in the runtime name here.
        // A path is normally called vmhba1:C0:T0:L1 and its adapter is
        // vmhba1, so an assertion using a realistic pair passes on the path
        // name alone and proves nothing about the adapter at all — which is
        // exactly what this test did until a mutation survived it.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("active"),
            Path("dead", name: "vmhba1:C0:T0:L1", adapter: "vmhba7"))));

        Assert.Contains("vmhba7", alert.Description, StringComparison.Ordinal);
        Assert.Contains("vmhba1:C0:T0:L1", alert.Description, StringComparison.Ordinal);
        Assert.Contains(Naa, alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_adapter_the_platform_did_not_name_is_said_to_be_unreported()
    {
        // Rather than an empty gap in the sentence, which reads as though the
        // product knows the adapter and chose not to say.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("active"),
            Path("dead", name: "vmhba1:C0:T0:L1", adapter: ""))));

        Assert.Contains("not reported", alert.Description, StringComparison.Ordinal);
    }

    // --- the more severe fact ---------------------------------------------

    [Fact]
    public void A_device_with_no_working_path_left_is_a_different_and_worse_alert()
    {
        // Not a loss of redundancy: a loss of the device. Every machine whose
        // disks live on this LUN is stopped or about to be, which is the same
        // kind of fact as a host vCenter cannot reach — and that is already
        // Critical in this product.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("dead"),
            Path("dead", name: "vmhba1:C0:T0:L1"))));

        Assert.Equal(DownTitle, alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void The_two_verdicts_are_different_alerts_rather_than_one_that_changes()
    {
        // A device that goes from "one path left" to "no paths left" is a new
        // fault, not a relabelling of the old one. The title is in the
        // fingerprint so that the history says what actually happened.
        var lost = Assert.Single(Evaluate(HostWith(
            Path("active"), Path("dead", name: "vmhba1:C0:T0:L1"))));

        var down = Assert.Single(Evaluate(HostWith(
            Path("dead"), Path("dead", name: "vmhba1:C0:T0:L1"))));

        Assert.NotEqual(lost.Fingerprint, down.Fingerprint);
    }

    [Fact]
    public void A_single_path_device_whose_only_path_is_dead_is_still_reported()
    {
        // The single-path exclusion is about redundancy, not about the device.
        // A local disk with its one path gone is genuinely gone, and staying
        // quiet here would be silence about an outage rather than about a
        // configuration.
        var alert = Assert.Single(Evaluate(HostWith(Path("dead"))));

        Assert.Equal(DownTitle, alert.Title);
    }

    // --- the trap: devices that never had redundancy ----------------------

    [Fact]
    public void A_healthy_single_path_device_is_not_a_finding()
    {
        // The trap. A boot device, a local disk or a USB stick has exactly one
        // path by design and always will. Reporting it as redundancy lost puts
        // a permanent alert on every host in the estate that no operator can
        // ever clear, and teaches them to stop reading the box.
        Assert.Empty(Evaluate(HostWith(Path("active"))));
    }

    [Fact]
    public void Redundancy_lost_needs_two_paths_and_gets_that_from_arithmetic()
    {
        // Stated as its own test because it is the reason no threshold was
        // invented here. "At least one dead and at least one working" cannot
        // be satisfied by one path, so the single-path case falls out of the
        // arithmetic rather than out of a minimum-path-count gate somebody
        // would later have to defend.
        foreach (var state in new[] { "active", "standby", "disabled", "unknown", "" })
        {
            Assert.DoesNotContain(
                Evaluate(HostWith(Path(state))), a => a.Title == LostTitle);
        }
    }

    // --- silences ---------------------------------------------------------

    [Fact]
    public void A_device_with_every_path_working_says_nothing()
    {
        Assert.Empty(Evaluate(HostWith(
            Path("active"),
            Path("active", name: "vmhba1:C0:T0:L1"))));
    }

    [Fact]
    public void A_path_the_platform_declined_to_state_is_neither_working_nor_dead()
    {
        // 'unknown' and an unreported state are the platform saying it does not
        // know. Treating them as dead invents an outage from a gap in the
        // vendor's answer; treating them as working invents protection that may
        // not be there. Neither is claimed, so a device whose paths are all
        // unknown produces nothing at all.
        Assert.Empty(Evaluate(HostWith(
            Path("unknown"),
            Path("", name: "vmhba1:C0:T0:L1"))));
    }

    [Fact]
    public void An_administratively_disabled_path_is_not_a_failure()
    {
        // Somebody typed a command to make this happen, and a monitoring tool
        // that alerts on a deliberate act is a tool that argues with its
        // operator. It is not counted as working either — it is carrying
        // nothing — so this device is simply not judged.
        Assert.Empty(Evaluate(HostWith(
            Path("active"),
            Path("disabled", name: "vmhba1:C0:T0:L1"))));
    }

    [Fact]
    public void A_host_whose_path_table_could_not_be_read_is_not_a_host_with_no_paths()
    {
        // An empty table means the property was unreadable. Reporting it as
        // total path loss would turn one permissions problem into an estate-wide
        // storage outage on the screen.
        Assert.Empty(Evaluate(HostWith()));
    }

    [Fact]
    public void Nothing_but_a_host_is_judged()
    {
        // StoragePaths is empty on every other kind by construction, but the
        // kind is checked rather than relied upon: a future collector that
        // hangs a path table off an array or a switch would otherwise be
        // silently folded into a rule written about hosts.
        var datastore = new Entity
        {
            Id = new EntityId("vc-1:ds-prod"),
            Kind = EntityKind.Datastore,
            DisplayName = "PRODVOL10",
            SourceInstanceId = "vc-1",
            LastSeenUtc = T0,
            StoragePaths = [Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")],
        };

        Assert.Empty(Evaluate(datastore));
    }

    [Fact]
    public void A_host_that_has_vanished_is_not_reported_as_having_lost_paths()
    {
        // Its path table is the last one we saw, not the current one. A host
        // that went away takes its storage with it, and re-reporting a stale
        // table would have the product making claims about an estate it can no
        // longer see.
        var gone = HostWith(Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")) with
        {
            ObservationState = ObservationState.Vanished,
        };

        Assert.Empty(Evaluate(gone));
    }

    [Fact]
    public void A_host_in_maintenance_is_not_reported_as_having_lost_paths()
    {
        // Maintenance is where cables get moved. The paths are genuinely down
        // and it is genuinely expected, and this product already treats
        // maintenance as a reason not to speak.
        var maintenance = HostWith(Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")) with
        {
            ObservationState = ObservationState.InMaintenance,
        };

        Assert.Empty(Evaluate(maintenance));
    }

    [Fact]
    public void Paths_naming_no_device_at_all_are_not_piled_into_one_heap()
    {
        // The collector reports a path even when neither the NAA nor the
        // platform's key could be read, because "this path is dead" is worth
        // saying. Grouping those by an empty name would invent a single device
        // with every unnamed path in the host attached to it, and then report
        // its redundancy — a verdict about an object that does not exist.
        Assert.Empty(Evaluate(HostWith(
            Path("active", device: "", key: ""),
            Path("dead", name: "vmhba1:C0:T0:L1", device: "", key: ""))));
    }

    // --- identity ---------------------------------------------------------

    [Fact]
    public void Two_devices_on_one_host_are_two_findings()
    {
        // Per device, because the fix is per device. One alert saying "this
        // host has lost paths" cannot be acknowledged for the LUN that is
        // being decommissioned without silencing the one that matters.
        var alerts = Evaluate(HostWith(
            Path("active"),
            Path("dead", name: "vmhba1:C0:T0:L1"),
            Path("active", name: "vmhba0:C0:T0:L2", device: "naa.600508b1001cb999",
                key: "key-vim.host.ScsiDisk-0201"),
            Path("dead", name: "vmhba1:C0:T0:L2", device: "naa.600508b1001cb999",
                key: "key-vim.host.ScsiDisk-0201")));

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_same_device_losing_a_path_on_two_hosts_is_two_findings()
    {
        // A path is a property of one host's route to a LUN. Two hosts losing
        // a path to the same array volume are two cables, two HBAs and two
        // visits — and on the live estate one LUN is reached from ten hosts.
        var second = HostWith(Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")) with
        {
            Id = new EntityId("vc-1:host-2"),
            DisplayName = "esx02",
        };

        var alerts = Evaluate(
            HostWith(Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")),
            second);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void A_device_with_no_naa_is_still_counted_by_the_platforms_own_key()
    {
        // The case where the NAA lookup failed is not the case in which to give
        // up on counting redundancy: it is disproportionately the case where
        // something is already wrong.
        var alert = Assert.Single(Evaluate(HostWith(
            Path("active", device: ""),
            Path("dead", name: "vmhba1:C0:T0:L1", device: ""))));

        Assert.Equal(LostTitle, alert.Title);
        Assert.Contains("key-vim.host.ScsiDisk-0200", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_carrying_no_source_is_attributed_to_the_platform()
    {
        // The fingerprint's first part must never be blank, or two collectors'
        // findings about identically named objects would collide into one
        // alert.
        var orphan = HostWith(Path("active"), Path("dead", name: "vmhba1:C0:T0:L1")) with
        {
            SourceInstanceId = string.Empty,
        };

        Assert.Equal("platform", Assert.Single(Evaluate(orphan)).Source);
    }

    // --- policy -----------------------------------------------------------

    [Fact]
    public void The_default_failed_state_is_the_one_the_domain_already_calls_dead()
    {
        // The vocabulary lives in two places — Domain.StoragePath.IsDead and
        // this policy — and this test is what stops them drifting. They are
        // not merged because "working" cannot be written as "not dead":
        // disabled and unknown are neither, and splitting the three buckets
        // across two layers would make them unreadable.
        foreach (var state in new[] { "active", "standby", "disabled", "dead", "unknown", "" })
        {
            var path = Path(state);
            var policy = StoragePathRedundancyPolicy.Default;

            Assert.Equal(
                path.IsDead,
                policy.FailedStates.Contains(state, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void A_platform_that_words_its_states_differently_is_described_in_policy()
    {
        // No vim25 string is compiled into the rule's own logic. A collector
        // whose paths come back 'down' and 'online' is configured in rather
        // than requiring this file to change.
        var policy = StoragePathRedundancyPolicy.Default with
        {
            FailedStates = ["down"],
            WorkingStates = ["online"],
        };

        var alert = Assert.Single(StoragePathRedundancy.Evaluate(
            [HostWith(Path("online"), Path("down", name: "vmhba1:C0:T0:L1"))], policy));

        Assert.Equal(LostTitle, alert.Title);
    }

    [Fact]
    public void States_are_matched_whatever_case_the_vendor_spells_them_in()
    {
        Assert.Equal(
            LostTitle,
            Assert.Single(Evaluate(HostWith(
                Path("Active"), Path("DEAD", name: "vmhba1:C0:T0:L1")))).Title);
    }

    [Fact]
    public void Evaluate_rejects_a_null_estate_rather_than_reporting_an_empty_one()
    {
        Assert.Throws<ArgumentNullException>(() => StoragePathRedundancy.Evaluate(null!));
    }
}
