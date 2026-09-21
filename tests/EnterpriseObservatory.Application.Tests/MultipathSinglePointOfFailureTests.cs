using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rule for the protection that looks real on paper and is not.
/// </summary>
/// <remarks>
/// <see cref="StoragePathRedundancyTests"/> covers a route that was lost.
/// This file covers a route that was never really there: a LUN with one path,
/// or several paths that all leave through the same card. Both are silent in
/// exactly the way a dead path is silent — nothing degrades until the single
/// HBA or the single path actually fails — which is why this is a finding a
/// human has to be told rather than something any counter will ever show.
/// </remarks>
public class MultipathSinglePointOfFailureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Host = "vc-1:host-1";
    private const string Datastore = "vc-1:ds-prod";
    private const string DatastoreName = "PRODVOL10";
    private const string Naa = "naa.600508b1001cb736";

    private const string SinglePathTitle = "Storage device has only one path";
    private const string SingleHbaTitle = "All working paths share one HBA";

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

    private static Entity VmfsDatastore(string naa = Naa) => new()
    {
        Id = new EntityId(Datastore),
        Kind = EntityKind.Datastore,
        DisplayName = DatastoreName,
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = "VMFS" },
        Marks = [IdentityMark.Create(IdentityMarkKind.StorageDeviceId, naa, "vc-1")],
    };

    private static Entity NfsDatastore(string naa = Naa) => VmfsDatastore(naa) with
    {
        Id = new EntityId("vc-1:ds-nfs"),
        DisplayName = "NFSVOL1",
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = "NFS" },
    };

    /// <summary>
    /// Evaluates with a healthy second host that reaches every device the
    /// case's hosts do, so each device is shared (seen by two hosts) and the
    /// case judges only the host under test.
    /// </summary>
    private static IReadOnlyList<AlertDefinition> Evaluate(params Entity[] entities) =>
        MultipathSinglePointOfFailure.Evaluate([.. entities, SharingPeer(entities)]);

    private static Entity SharingPeer(IEnumerable<Entity> entities)
    {
        var devices = entities
            .Where(e => e.Kind == EntityKind.EsxiHost)
            .SelectMany(e => e.StoragePaths)
            .Select(p => (p.StorageDeviceId, p.DeviceKey))
            .Distinct();

        return HostWith(
        [
            .. devices.SelectMany(d => new[]
            {
                Path("active", name: "vmhba1:C0:T0:L1", device: d.StorageDeviceId, key: d.DeviceKey, adapter: "vmhba1"),
                Path("active", name: "vmhba2:C0:T0:L1", device: d.StorageDeviceId, key: d.DeviceKey, adapter: "vmhba2"),
            }),
        ]) with { Id = new EntityId("vc-1:host-peer"), DisplayName = "esx-peer" };
    }

    // --- local VMFS: seen by one host only ----------------------------------

    [Fact]
    public void A_vmfs_device_only_one_host_can_reach_is_local_and_not_judged()
    {
        Assert.Empty(MultipathSinglePointOfFailure.Evaluate([HostWith(Path("active")), VmfsDatastore()]));
    }

    [Fact]
    public void The_same_single_path_is_judged_once_a_second_host_shares_the_device()
    {
        Assert.Single(Evaluate(HostWith(Path("active")), VmfsDatastore()));
    }

    // --- the finding this rule exists for: single path --------------------

    [Fact]
    public void A_shared_lun_with_exactly_one_path_is_a_single_point_of_failure()
    {
        var alert = Assert.Single(Evaluate(HostWith(Path("active")), VmfsDatastore()));

        Assert.Equal(SinglePathTitle, alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(new EntityId(Host), alert.Entity);
        Assert.Contains(DatastoreName, alert.Description, StringComparison.Ordinal);
        Assert.Contains("vmhba0", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_single_path_finding_fires_whatever_state_the_one_path_is_in()
    {
        // Structural, not a judgement about health: StoragePathRedundancy
        // already says whether a dead single path took the device down. This
        // rule is about the configuration having no second route to lose.
        Assert.Single(Evaluate(HostWith(Path("dead")), VmfsDatastore()));
    }

    // --- the finding this rule exists for: single HBA ----------------------

    [Fact]
    public void Two_paths_that_both_leave_through_the_same_adapter_are_flagged()
    {
        var alert = Assert.Single(Evaluate(
            HostWith(
                Path("active", name: "vmhba0:C0:T0:L1"),
                Path("standby", name: "vmhba0:C1:T0:L1")),
            VmfsDatastore()));

        Assert.Equal(SingleHbaTitle, alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("vmhba0", alert.Description, StringComparison.Ordinal);
        Assert.Contains(DatastoreName, alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dead_second_path_on_a_different_adapter_still_counts_as_single_hba()
    {
        // Only the paths still carrying I/O decide the verdict. A dead path on
        // vmhba1 is not protection right now, whatever adapter it names.
        var alert = Assert.Single(Evaluate(
            HostWith(
                Path("active", name: "vmhba0:C0:T0:L1"),
                Path("dead", name: "vmhba1:C0:T0:L1", adapter: "vmhba1")),
            VmfsDatastore()));

        Assert.Equal(SingleHbaTitle, alert.Title);
    }

    // --- the healthy case: dual fabric --------------------------------------

    [Fact]
    public void Two_working_paths_on_two_adapters_is_not_a_finding()
    {
        // The reference shape: two HBAs, two fabrics, two array controller
        // ports. This is what the finding exists to distinguish from.
        Assert.Empty(Evaluate(
            HostWith(
                Path("active", name: "vmhba0:C0:T0:L1", adapter: "vmhba0"),
                Path("active", name: "vmhba1:C0:T0:L1", adapter: "vmhba1")),
            VmfsDatastore()));
    }

    // --- the exclusion: local and non-shared storage ------------------------

    [Fact]
    public void A_device_no_vmfs_datastore_points_at_is_not_judged_at_all()
    {
        // Local disks and boot devices have exactly one path by design and no
        // datastore mark ever names them, so they never enter this rule's
        // population in the first place — the same trap
        // StoragePathRedundancy's own tests guard against, closed here by
        // scope instead of by a path-count exception.
        Assert.Empty(Evaluate(HostWith(Path("active"))));
    }

    [Fact]
    public void An_nfs_datastore_on_the_same_naa_does_not_make_the_device_shared()
    {
        // Defensive: NFS carries no SCSI path table at all in practice, so this
        // can only happen if a datastore's type were ever misreported. Even
        // then, only a VMFS-typed datastore's mark is trusted.
        Assert.Empty(Evaluate(HostWith(Path("active")), NfsDatastore()));
    }

    [Fact]
    public void A_datastore_of_an_unrecognised_type_does_not_widen_the_rule()
    {
        var vsan = VmfsDatastore() with
        {
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = "vsan" },
        };

        Assert.Empty(Evaluate(HostWith(Path("active")), vsan));
    }

    // --- silences ------------------------------------------------------------

    [Fact]
    public void A_healthy_dual_fabric_datastore_with_extra_dead_paths_says_nothing_new()
    {
        Assert.Empty(Evaluate(
            HostWith(
                Path("active", name: "vmhba0:C0:T0:L1", adapter: "vmhba0"),
                Path("active", name: "vmhba1:C0:T0:L1", adapter: "vmhba1"),
                Path("dead", name: "vmhba2:C0:T0:L1", adapter: "vmhba2")),
            VmfsDatastore()));
    }

    [Fact]
    public void A_device_with_no_working_path_left_is_not_this_rules_business()
    {
        // Nothing is working, so there is no "the working paths share an
        // adapter" to say — StoragePathRedundancy already names the more
        // urgent fact that the device is down.
        Assert.Empty(Evaluate(
            HostWith(
                Path("dead", name: "vmhba0:C0:T0:L1", adapter: "vmhba0"),
                Path("dead", name: "vmhba1:C0:T0:L1", adapter: "vmhba1")),
            VmfsDatastore()));
    }

    [Fact]
    public void An_unreported_adapter_on_every_working_path_is_not_claimed_as_single()
    {
        // A "single HBA" verdict needs a named HBA to be worth anything; an
        // empty adapter is the platform declining to say, not evidence of one.
        Assert.Empty(Evaluate(
            HostWith(
                Path("active", name: "vmhba0:C0:T0:L1", adapter: ""),
                Path("standby", name: "vmhba1:C0:T0:L1", adapter: "")),
            VmfsDatastore()));
    }

    [Fact]
    public void A_host_that_has_vanished_is_not_reported()
    {
        var gone = HostWith(Path("active")) with { ObservationState = ObservationState.Vanished };

        Assert.Empty(Evaluate(gone, VmfsDatastore()));
    }

    [Fact]
    public void A_host_in_maintenance_is_not_reported()
    {
        var maintenance = HostWith(Path("active")) with
        {
            ObservationState = ObservationState.InMaintenance,
        };

        Assert.Empty(Evaluate(maintenance, VmfsDatastore()));
    }

    [Fact]
    public void Nothing_but_a_host_is_judged()
    {
        var datastore = VmfsDatastore() with { StoragePaths = [Path("active")] };

        Assert.Empty(Evaluate(datastore));
    }

    // --- identity --------------------------------------------------------

    [Fact]
    public void The_same_datastore_on_two_hosts_is_two_findings()
    {
        var second = HostWith(Path("active")) with
        {
            Id = new EntityId("vc-1:host-2"),
            DisplayName = "esx02",
        };

        var alerts = Evaluate(HostWith(Path("active")), second, VmfsDatastore());

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void An_entity_carrying_no_source_is_attributed_to_the_platform()
    {
        var orphan = HostWith(Path("active")) with { SourceInstanceId = string.Empty };

        Assert.Equal("platform", Assert.Single(Evaluate(orphan, VmfsDatastore())).Source);
    }

    [Fact]
    public void Evaluate_rejects_a_null_estate_rather_than_reporting_an_empty_one()
    {
        Assert.Throws<ArgumentNullException>(() => MultipathSinglePointOfFailure.Evaluate(null!));
    }

    // --- policy ------------------------------------------------------------

    [Fact]
    public void A_platform_that_words_its_working_states_differently_is_configured_in()
    {
        var policy = MultipathSinglePointOfFailurePolicy.Default with { WorkingStates = ["online"] };

        Entity[] estate =
        [
            HostWith(
                Path("online", name: "vmhba0:C0:T0:L1"),
                Path("online", name: "vmhba0:C1:T0:L1")),
            VmfsDatastore(),
        ];

        // The peer's paths read "active", which this policy does not count as
        // working, so the peer only makes the device shared and adds nothing.
        var alert = Assert.Single(MultipathSinglePointOfFailure.Evaluate(
            [.. estate, SharingPeer(estate)], policy));

        Assert.Equal(SingleHbaTitle, alert.Title);
    }
}
