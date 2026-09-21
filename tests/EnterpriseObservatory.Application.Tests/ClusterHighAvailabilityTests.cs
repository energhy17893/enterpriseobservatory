using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The per-cluster HA scorecard: settings that leave a cluster looking green
/// while its HA protection has a hole in it.
/// </summary>
/// <remarks>
/// Every check is silent on a key that was never read, for the reason
/// <see cref="RemoteLoggingTests"/> gives at length: an absent key is a
/// collection failure with its own channel, and reporting it here would name
/// a cluster nobody managed to look at.
/// </remarks>
public class ClusterHighAvailabilityTests
{
    // Setting names live on the policy, not on the collector's own constants
    // -- the application layer must not depend on what vSphere calls
    // something. See ClusterHighAvailabilityPolicy's remarks.
    private static readonly ClusterHighAvailabilityPolicy Rules = ClusterHighAvailabilityPolicy.Default;

    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static Entity Cluster(
        string id = "vc-1:domain-c1",
        ObservationState state = ObservationState.Active,
        params (string Key, string Value)[] settings) => new()
        {
            Id = new EntityId(id),
            Kind = EntityKind.Cluster,
            DisplayName = id,
            LastSeenUtc = T0,
            ObservationState = state,
            Settings = settings.ToDictionary(
                s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        };

    // --- HA disabled ---------------------------------------------------------

    [Fact]
    public void HA_disabled_is_named()
    {
        var alerts = ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.EnabledSetting, "false"))]);

        Assert.Contains(alerts, a => a.Title == "Cluster has no vSphere HA protection");
        Assert.Equal(AlertSeverity.Critical, alerts.Single(a => a.Title.Contains("no vSphere HA")).Severity);
    }

    [Fact]
    public void HA_enabled_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.EnabledSetting, "true"))]));
    }

    [Fact]
    public void HA_never_read_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate([Cluster()]));
    }

    // --- admission control ---------------------------------------------------

    [Fact]
    public void Admission_control_disabled_is_named()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.AdmissionControlEnabledSetting, "false"))]));

        Assert.Equal("Cluster admission control is disabled", alert.Title);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void Admission_control_enabled_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.AdmissionControlEnabledSetting, "true"))]));
    }

    // --- host monitoring -------------------------------------------------------

    [Fact]
    public void Host_monitoring_disabled_is_named()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HostMonitoringSetting, "disabled"))]));

        Assert.Equal("Cluster host monitoring is disabled", alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void Host_monitoring_enabled_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HostMonitoringSetting, "enabled"))]));
    }

    // --- APD / PDL storage protection ------------------------------------------

    [Fact]
    public void Apd_response_disabled_is_named()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.ApdResponseSetting, "disabled"))]));

        Assert.Equal("Cluster storage failure protection is disabled", alert.Title);
        Assert.Contains("All-Paths-Down", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdl_response_disabled_is_named()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.PdlResponseSetting, "disabled"))]));

        Assert.Contains("Permanent-Device-Loss", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_apd_and_pdl_disabled_is_one_finding_not_two()
    {
        var alerts = ClusterHighAvailability.Evaluate(
        [
            Cluster(settings:
            [
                (Rules.ApdResponseSetting, "disabled"),
                (Rules.PdlResponseSetting, "disabled"),
            ]),
        ]);

        Assert.Single(alerts);
        Assert.Contains("All-Paths-Down and a Permanent-Device-Loss", alerts[0].Description);
    }

    [Fact]
    public void A_non_disabled_storage_protection_value_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
        [
            Cluster(settings:
            [
                (Rules.ApdResponseSetting, "restartConservative"),
                (Rules.PdlResponseSetting, "restartAggressive"),
            ]),
        ]));
    }

    // --- heartbeat datastores ---------------------------------------------------

    [Fact]
    public void One_heartbeat_datastore_is_named()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HeartbeatDatastoreCountSetting, "1"))]));

        Assert.Equal("Cluster has too few HA heartbeat datastores", alert.Title);
        Assert.Contains("1 heartbeat datastore(s)", alert.Description);
    }

    [Fact]
    public void Zero_heartbeat_datastores_is_named()
    {
        Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HeartbeatDatastoreCountSetting, "0"))]));
    }

    [Fact]
    public void Two_heartbeat_datastores_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HeartbeatDatastoreCountSetting, "2"))]));
    }

    [Fact]
    public void The_minimum_is_policy_rather_than_compiled_in()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.HeartbeatDatastoreCountSetting, "2"))],
            new ClusterHighAvailabilityPolicy { MinimumHeartbeatDatastores = 3 }));

        Assert.Equal("Cluster has too few HA heartbeat datastores", alert.Title);
    }

    // --- das.ignoreRedundantNetWarning -------------------------------------------

    [Fact]
    public void The_redundant_network_warning_being_silenced_is_named_as_a_hidden_risk()
    {
        var alert = Assert.Single(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.IgnoreRedundantNetworkWarningSetting, "true"))]));

        Assert.Equal("Cluster hides its HA network redundancy warning", alert.Title);
        Assert.Contains("hidden risk", alert.Description, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void The_redundant_network_warning_left_alone_is_silent()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(settings: (Rules.IgnoreRedundantNetworkWarningSetting, "false"))]));
    }

    // --- entity scoping ----------------------------------------------------------

    [Fact]
    public void Only_clusters_are_judged()
    {
        var host = new Entity
        {
            Id = new EntityId("vc-1:host-1"),
            Kind = EntityKind.EsxiHost,
            DisplayName = "host-1",
            LastSeenUtc = T0,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Rules.EnabledSetting] = "false",
            },
        };

        Assert.Empty(ClusterHighAvailability.Evaluate([host]));
    }

    [Fact]
    public void A_vanished_cluster_is_not_named()
    {
        Assert.Empty(ClusterHighAvailability.Evaluate(
            [Cluster(state: ObservationState.Vanished, settings: (Rules.EnabledSetting, "false"))]));
    }

    [Fact]
    public void Each_cluster_is_its_own_finding()
    {
        var alerts = ClusterHighAvailability.Evaluate(
        [
            Cluster("vc-1:domain-c1", settings: (Rules.EnabledSetting, "false")),
            Cluster("vc-1:domain-c2", settings: (Rules.EnabledSetting, "false")),
            Cluster("vc-1:domain-c3", settings: (Rules.EnabledSetting, "true")),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void A_cluster_can_carry_several_findings_at_once()
    {
        var alerts = ClusterHighAvailability.Evaluate(
        [
            Cluster(settings:
            [
                (Rules.AdmissionControlEnabledSetting, "false"),
                (Rules.HeartbeatDatastoreCountSetting, "0"),
                (Rules.IgnoreRedundantNetworkWarningSetting, "true"),
            ]),
        ]);

        Assert.Equal(3, alerts.Count);
        Assert.Equal(3, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void A_missing_entity_list_is_a_programming_error_rather_than_an_empty_estate()
    {
        Assert.Throws<ArgumentNullException>(() => ClusterHighAvailability.Evaluate(null!));
    }
}
