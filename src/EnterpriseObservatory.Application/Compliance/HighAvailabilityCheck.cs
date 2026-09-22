using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// One aspect of a cluster's HA scorecard (M8.1), judged as a finding.
/// </summary>
/// <remarks>
/// <para>
/// Moved from the <c>cluster-ha-scorecard</c> alarm (ADR-0024): none of these
/// settings changes without somebody changing it, so a hole in HA protection
/// is fixed or accepted, never "cleared". The setting names are read from
/// <see cref="ClusterHighAvailabilityPolicy"/>, whose defaults are pinned
/// against the collector's constants by a drift test.
/// </para>
/// <para>
/// A cluster whose configuration was never read is <c>NotEvaluated</c> — "HA
/// settings not read", never "HA disabled". The five aspects other than
/// <see cref="Aspect.Enabled"/> mean nothing on a cluster HA is not confirmed
/// on, so they are not evaluated there either, and say why: the one finding
/// that matters on such a cluster is <see cref="ContinuityControls.HaEnabled"/>.
/// </para>
/// </remarks>
public sealed class HighAvailabilityCheck(HighAvailabilityCheck.Aspect aspect) : IComplianceCheck
{
    /// <summary>Which setting this check judges.</summary>
    public enum Aspect
    {
        Enabled,
        AdmissionControl,
        HostMonitoring,
        StorageProtection,
        HeartbeatDatastores,
        NetworkWarning,
    }

    public const string NotReadReason =
        "HA settings not read: this cluster's configuration (configurationEx) was not read, so " +
        "nothing is known about its HA protection.";

    private static readonly ClusterHighAvailabilityPolicy Policy = ClusterHighAvailabilityPolicy.Default;

    public Aspect Judges { get; } = aspect;

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var expected = Expected(Judges);

        if (!ConfigurationRead(entity))
        {
            return [Verdict(ComplianceVerdict.NotEvaluated, expected, reason: NotReadReason)];
        }

        var settings = entity.Settings;

        if (Judges == Aspect.Enabled)
        {
            return [Enabled(settings, expected)];
        }

        if (!settings.TryGetValue(Policy.EnabledSetting, out var enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    "HA is not confirmed enabled on this cluster, so how it is configured does not " +
                    $"apply; see {HaEnabled}."),
            ];
        }

        return
        [
            Judges switch
            {
                Aspect.AdmissionControl => AdmissionControl(settings, expected),
                Aspect.HostMonitoring => HostMonitoring(settings, expected),
                Aspect.StorageProtection => StorageProtection(settings, expected),
                Aspect.HeartbeatDatastores => HeartbeatDatastores(settings, expected),
                Aspect.NetworkWarning => NetworkWarning(settings, expected),
                _ => throw new InvalidOperationException($"Unknown aspect {Judges}."),
            },
        ];
    }

    private static string Expected(Aspect aspect) => aspect switch
    {
        Aspect.Enabled => "vSphere HA enabled",
        Aspect.AdmissionControl => "admission control enabled",
        Aspect.HostMonitoring => "host monitoring enabled",
        Aspect.StorageProtection => "APD and PDL responses not disabled",
        Aspect.HeartbeatDatastores =>
            $"at least {Policy.MinimumHeartbeatDatastores} heartbeat datastores when chosen by hand",
        Aspect.NetworkWarning => "das.ignoreRedundantNetWarning not set to true",
        _ => string.Empty,
    };

    private static CheckVerdict Enabled(IReadOnlyDictionary<string, string> settings, string expected)
    {
        if (!settings.TryGetValue(Policy.EnabledSetting, out var value) || !bool.TryParse(value, out var on))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: "The HA enabled flag was not read.");
        }

        return on
            ? Verdict(ComplianceVerdict.Passing, expected, "enabled")
            : Verdict(ComplianceVerdict.Failing, expected,
                "disabled: a host failing does not restart its virtual machines anywhere");
    }

    private static CheckVerdict AdmissionControl(IReadOnlyDictionary<string, string> settings, string expected)
    {
        if (!settings.TryGetValue(Policy.AdmissionControlEnabledSetting, out var value) ||
            !bool.TryParse(value, out var on))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: "Admission control was not read.");
        }

        return on
            ? Verdict(ComplianceVerdict.Passing, expected, "enabled")
            : Verdict(ComplianceVerdict.Failing, expected,
                "disabled: no failover capacity is reserved on the surviving hosts");
    }

    private static CheckVerdict HostMonitoring(IReadOnlyDictionary<string, string> settings, string expected)
    {
        if (!settings.TryGetValue(Policy.HostMonitoringSetting, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: "Host monitoring was not read.");
        }

        return string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase)
            ? Verdict(ComplianceVerdict.Failing, expected,
                "disabled: HA cannot detect that a host has failed")
            : Verdict(ComplianceVerdict.Passing, expected, value);
    }

    private static CheckVerdict StorageProtection(IReadOnlyDictionary<string, string> settings, string expected)
    {
        var apd = settings.GetValueOrDefault(Policy.ApdResponseSetting);
        var pdl = settings.GetValueOrDefault(Policy.PdlResponseSetting);

        if (apd is null && pdl is null)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected,
                reason: "Neither the APD nor the PDL response was read.");
        }

        var observed = $"APD {apd ?? "not read"}, PDL {pdl ?? "not read"}";

        var disabled =
            string.Equals(apd, "disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pdl, "disabled", StringComparison.OrdinalIgnoreCase);

        return Verdict(disabled ? ComplianceVerdict.Failing : ComplianceVerdict.Passing, expected, observed);
    }

    /// <remarks>
    /// Judged only when the operator chose the datastores by hand
    /// (<c>userSelectedDs</c>). Under the other policies HA picks its own, and
    /// the set in use is not read yet — so that is not evaluated, not passing.
    /// </remarks>
    private static CheckVerdict HeartbeatDatastores(IReadOnlyDictionary<string, string> settings, string expected)
    {
        if (!settings.TryGetValue(Policy.HeartbeatDatastoreCandidatePolicySetting, out var policy) ||
            string.IsNullOrWhiteSpace(policy))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected,
                reason: "The heartbeat datastore selection policy was not read.");
        }

        if (!string.Equals(policy, "userSelectedDs", StringComparison.Ordinal))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, observed: policy, reason:
                $"HA chooses the heartbeat datastores itself under '{policy}'; the set it actually " +
                "uses is not read yet, so how many there are cannot be said.");
        }

        if (!settings.TryGetValue(Policy.HeartbeatDatastoreCountSetting, out var raw) ||
            !int.TryParse(raw, out var count))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected,
                reason: "The heartbeat datastores were not read.");
        }

        return Verdict(
            count >= Policy.MinimumHeartbeatDatastores ? ComplianceVerdict.Passing : ComplianceVerdict.Failing,
            expected,
            $"{count} chosen by hand");
    }

    /// <remarks>
    /// The option is absent unless somebody set it; with the configuration
    /// read, absent means not set.
    /// </remarks>
    private static CheckVerdict NetworkWarning(IReadOnlyDictionary<string, string> settings, string expected)
    {
        var value = settings.GetValueOrDefault(Policy.IgnoreRedundantNetworkWarningSetting);

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            ? Verdict(ComplianceVerdict.Failing, expected,
                "true: vCenter's warning about a non-redundant HA management network is hidden, not fixed")
            : Verdict(ComplianceVerdict.Passing, expected, value ?? "not set");
    }
}
