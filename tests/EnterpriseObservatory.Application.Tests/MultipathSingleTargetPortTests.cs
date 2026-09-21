using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The multipath rule's third case: every working path lands on one
/// storage-array target port.
/// </summary>
/// <remarks>
/// Kept in its own file, apart from <see cref="MultipathSinglePointOfFailureTests"/>,
/// so the target-port case can merge independently of other work on that rule.
/// </remarks>
public class MultipathSingleTargetPortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Naa = "naa.600508b1001cb736";
    private const string SingleTargetTitle = "All working paths reach one target port";
    private const string PortA = "50:06:01:60:3b:20:1f:3a";
    private const string PortB = "50:06:01:68:3b:20:1f:3a";

    private static StoragePath Path(string name, string adapter, string? target, string state = "active") => new()
    {
        Name = name,
        State = state,
        StorageDeviceId = Naa,
        Adapter = adapter,
        Transport = "HostFibreChannelTargetTransport",
        Target = target,
    };

    private static Entity Host(string id, params StoragePath[] paths) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.EsxiHost,
        DisplayName = id,
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        StoragePaths = paths,
    };

    private static readonly Entity Datastore = new()
    {
        Id = new EntityId("vc-1:ds-prod"),
        Kind = EntityKind.Datastore,
        DisplayName = "PRODVOL10",
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = "VMFS" },
        Marks = [IdentityMark.Create(IdentityMarkKind.StorageDeviceId, Naa, "vc-1")],
    };

    /// <summary>A second host with two HBAs and two target ports, so the device is shared.</summary>
    private static readonly Entity HealthyPeer = Host(
        "vc-1:host-peer",
        Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
        Path("vmhba2:C0:T1:L1", "vmhba2", PortB));

    private static IReadOnlyList<AlertDefinition> Evaluate(Entity host) =>
        MultipathSinglePointOfFailure.Evaluate([host, HealthyPeer, Datastore]);

    [Fact]
    public void Two_hbas_that_both_land_on_one_array_port_are_flagged()
    {
        var alert = Assert.Single(Evaluate(Host(
            "vc-1:host-1",
            Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
            Path("vmhba2:C0:T0:L1", "vmhba2", PortA))));

        Assert.Equal(SingleTargetTitle, alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
        Assert.Contains(PortA, alert.Description, StringComparison.Ordinal);
        Assert.Contains("PRODVOL10", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_target_ports_are_not_a_finding()
    {
        Assert.Empty(Evaluate(Host(
            "vc-1:host-1",
            Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
            Path("vmhba2:C0:T1:L1", "vmhba2", PortB))));
    }

    [Fact]
    public void An_unknown_target_on_any_working_path_is_not_judged()
    {
        // Unknown is not "the same port": a path whose transport names no
        // port could land anywhere.
        Assert.Empty(Evaluate(Host(
            "vc-1:host-1",
            Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
            Path("vmhba2:C0:T0:L1", "vmhba2", target: null))));
    }

    [Fact]
    public void Only_working_paths_decide_the_target_verdict()
    {
        // A dead path to the second port is no protection right now.
        var alert = Assert.Single(Evaluate(Host(
            "vc-1:host-1",
            Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
            Path("vmhba2:C0:T0:L1", "vmhba2", PortA),
            Path("vmhba2:C0:T1:L1", "vmhba2", PortB, state: "dead"))));

        Assert.Equal(SingleTargetTitle, alert.Title);
    }

    [Fact]
    public void A_single_hba_finding_is_not_repeated_as_a_single_target_one()
    {
        // One HBA and one port together: the HBA finding already names the
        // single point of failure nearer the host.
        var alert = Assert.Single(Evaluate(Host(
            "vc-1:host-1",
            Path("vmhba1:C0:T0:L1", "vmhba1", PortA),
            Path("vmhba1:C1:T0:L1", "vmhba1", PortA))));

        Assert.Equal("All working paths share one HBA", alert.Title);
    }
}
