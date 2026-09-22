using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Pins the one fact <c>ClusterHighAvailabilityPolicy</c>'s own remarks
/// promise but nothing enforced: that its setting-key defaults, which
/// <c>ReadModel</c>'s HA scorecard and continuity report read from, agree
/// with the keys <see cref="ClusterHaSettings"/> and
/// <c>ClusterConfigurationParser</c> actually file <c>Entity.Settings</c>
/// under.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClusterHighAvailabilityPolicy"/> deliberately does not
/// reference <see cref="ClusterHaSettings"/> directly -- the application
/// layer must not depend on a specific collector, per its own remarks and
/// <c>RemoteLoggingPolicy</c>'s before it. That leaves two independently
/// maintained copies of the same vSphere key names with no compiler check
/// that they still agree. This test is that check, run where both projects
/// are in reach.
/// </para>
/// <para>
/// Two keys -- <see cref="ClusterHaSettings.AdmissionControlPolicyType"/> and
/// <see cref="ClusterHaSettings.VmMonitoring"/> -- are excluded on purpose:
/// no HA rule check reads either, only the scorecard displays them, so
/// <see cref="ClusterHighAvailabilityPolicy"/> has no corresponding property
/// for this test to pin them against. <c>ReadModel.HaScorecard</c> carries
/// both as documented literals instead.
/// </para>
/// </remarks>
public class ClusterHaSettingKeyDriftTests
{
    private static readonly ClusterHighAvailabilityPolicy Policy = ClusterHighAvailabilityPolicy.Default;

    [Fact]
    public void Enabled_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.Enabled, Policy.EnabledSetting);

    [Fact]
    public void Admission_control_enabled_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.AdmissionControlEnabled, Policy.AdmissionControlEnabledSetting);

    [Fact]
    public void Host_monitoring_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.HostMonitoring, Policy.HostMonitoringSetting);

    [Fact]
    public void Apd_response_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.ApdResponse, Policy.ApdResponseSetting);

    [Fact]
    public void Pdl_response_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.PdlResponse, Policy.PdlResponseSetting);

    [Fact]
    public void Heartbeat_datastore_count_key_matches_the_collectors_constant() =>
        Assert.Equal(ClusterHaSettings.HeartbeatDatastoreCount, Policy.HeartbeatDatastoreCountSetting);

    [Fact]
    public void Heartbeat_datastore_candidate_policy_key_matches_the_collectors_constant() =>
        Assert.Equal(
            ClusterHaSettings.HeartbeatDatastoreCandidatePolicy,
            Policy.HeartbeatDatastoreCandidatePolicySetting);

    [Fact]
    public void Ignore_redundant_network_warning_key_matches_the_collectors_constant() =>
        Assert.Equal(
            ClusterHaSettings.IgnoreRedundantNetworkWarning,
            Policy.IgnoreRedundantNetworkWarningSetting);

    /// <summary>
    /// Every HA setting key shares one prefix -- <c>ReadModel</c> uses that to
    /// decide whether a cluster's HA configuration was ever read at all,
    /// rather than repeating the literal <c>"dasConfig."</c>. Pinned here
    /// against the same source the rest of this file pins: the policy's own
    /// <see cref="ClusterHighAvailabilityPolicy.EnabledSetting"/>.
    /// </summary>
    [Fact]
    public void Every_ha_setting_key_shares_the_enabled_settings_prefix()
    {
        var prefix = Policy.EnabledSetting[..(Policy.EnabledSetting.IndexOf('.', StringComparison.Ordinal) + 1)];

        Assert.Equal("dasConfig.", prefix);

        Assert.All(
            [
                ClusterHaSettings.Enabled,
                ClusterHaSettings.AdmissionControlEnabled,
                ClusterHaSettings.AdmissionControlPolicyType,
                ClusterHaSettings.HostMonitoring,
                ClusterHaSettings.VmMonitoring,
                ClusterHaSettings.ApdResponse,
                ClusterHaSettings.PdlResponse,
                ClusterHaSettings.HeartbeatDatastoreCount,
                ClusterHaSettings.HeartbeatDatastoreCandidatePolicy,
                ClusterHaSettings.IgnoreRedundantNetworkWarning,
            ],
            key => Assert.StartsWith(prefix, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// The datastore-type setting key: a plain <c>"type"</c> literal in three
    /// places this batch is not free to edit into a shared constant --
    /// <c>VsphereInventorySource</c> (which writes it),
    /// <c>MultipathCheck</c> (which reads it to scope itself
    /// to VMFS) and <c>ReadModel</c> (which reads it for the entity page).
    /// Pinned to the same literal here so a change to any one of the three
    /// without the others fails a test instead of silently drifting.
    /// </summary>
    [Fact]
    public void Datastore_type_setting_key_is_the_pinned_literal()
    {
        const string datastoreTypeKey = "type";
        const string naa = "naa.1";

        var datastore = new Entity
        {
            Id = new EntityId("vc-1:ds-1"),
            Kind = EntityKind.Datastore,
            DisplayName = "ds-1",
            SourceInstanceId = "vc-1",
            Health = HealthState.Healthy,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [datastoreTypeKey] = "VMFS",
            },
            Marks = [IdentityMark.Create(IdentityMarkKind.StorageDeviceId, naa, "vc-1")],
        };

        // MultipathCheck only ever produces a failing finding for a
        // device it considers VMFS -- reached exclusively through
        // Settings["type"]. Evaluating it over two hosts sharing this one
        // datastore's device, each with a single path, proves the rule is
        // still keyed on the literal this test pins: without a "type" of
        // "VMFS" being read, the device would never be treated as shared
        // VMFS storage and neither host's single path would be reported.
        var host1 = HostWithOnePathTo(naa);
        var host2 = HostWithOnePathTo(naa);

        var findings = Application.Compliance.ComplianceEvaluation.Evaluate(
            Application.Compliance.ContinuityCatalogue.Build(Application.Compliance.ContinuityCatalogue.Production),
            [datastore, host1, host2],
            [],
            DateTimeOffset.UtcNow,
            checksById: Application.Compliance.ContinuityCatalogue.ChecksById(
                Application.Compliance.ContinuityCatalogue.Production));

        Assert.Contains(findings, f =>
            f.ControlId == Application.Compliance.ContinuityControls.PathSingle &&
            f.Verdict == Domain.Compliance.ComplianceVerdict.Failing);
    }

    private static Entity HostWithOnePathTo(string naa) =>
        new()
        {
            Id = new EntityId($"vc-1:host-{Guid.NewGuid():N}"),
            Kind = EntityKind.EsxiHost,
            DisplayName = "host",
            SourceInstanceId = "vc-1",
            Health = HealthState.Healthy,
            LastSeenUtc = DateTimeOffset.UtcNow,
            StoragePaths =
            [
                new StoragePath
                {
                    Name = "vmhba0:C0:T0:L1",
                    StorageDeviceId = naa,
                    State = "active",
                    Adapter = "vmhba0",
                },
            ],
        };
}
