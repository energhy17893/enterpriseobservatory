using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>One alarm the transition resolved, and the finding that now carries its condition.</summary>
/// <param name="Alarm">The alarm as resolved.</param>
/// <param name="Finding">The matching continuity finding, or null when none could be named.</param>
public sealed record MovedAlarm(AlertInstance Alarm, ComplianceFinding? Finding);

/// <summary>
/// Resolves the alarms the four retired M8 rules left open, as "moved to
/// compliance finding" (ADR-0024 §5).
/// </summary>
/// <remarks>
/// <para>
/// Run after a continuity evaluation has succeeded, so the findings exist
/// before the alarms go. Every alarm of <see cref="MovedContinuityRules"/>
/// that is not already resolved — open, acknowledged or silenced — is
/// resolved with <see cref="AlertTransitionReason.MovedToFinding"/>. Already
/// resolved ones are left alone, so running it again changes nothing: it is
/// one-time in effect, and safe to call on every evaluation.
/// </para>
/// <para>
/// A silence is <b>not</b> carried over as an acceptance. It has no reason
/// and no end; an acceptance invented from it would be the indefinite
/// exception §6 forbids. The operator accepts the finding again, with a reason.
/// </para>
/// <para>
/// Until this runs, the inventory cycle keeps these alarms open rather than
/// resolving them for being absent: they still carry their retired rule's id,
/// no rule gives them a verdict, so they are "not reported" and marked stale
/// (ADR-0026). Otherwise the first cycle after the upgrade would close them as
/// "condition cleared", which is not what happened. One that is still not
/// moved on the next inventory cycle — the continuity catalogue did not load,
/// or its evaluation failed — is closed there as "rule retired" instead, so it
/// cannot stay "not reported" for ever.
/// </para>
/// </remarks>
public static class ContinuityAlarmTransition
{
    /// <summary>Whether an alarm is one this transition still has to move.</summary>
    public static bool AwaitsMove(AlertInstance alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        return alarm.State != AlertLifecycleState.Resolved &&
               MovedContinuityRules.RuleOf(alarm.Fingerprint.Value) is not null;
    }

    /// <summary>Resolves every alarm that awaits the move; returns what it resolved.</summary>
    /// <param name="alerts">The alarm store.</param>
    /// <param name="findings">The continuity findings of the evaluation just run.</param>
    /// <param name="nowUtc">When.</param>
    public static IReadOnlyList<MovedAlarm> Run(
        IAlertStateStore alerts, IReadOnlyList<ComplianceFinding> findings, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(findings);

        var waiting = alerts.All.Where(AwaitsMove).Select(a => a.Fingerprint).ToList();

        if (waiting.Count == 0)
        {
            return [];
        }

        var moved = alerts.MutateMany(waiting, a => AlertLifecycle.MoveToFinding(a, nowUtc));

        return
        [
            .. moved
                .Where(a => a.History.Count > 0 && a.History[^1].Reason == AlertTransitionReason.MovedToFinding)
                .Select(a => new MovedAlarm(a, Match(a, findings))),
        ];
    }

    /// <summary>The finding that now carries this alarm's condition, or null.</summary>
    public static ComplianceFinding? Match(AlertInstance alarm, IReadOnlyList<ComplianceFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(findings);

        if (alarm.Entity is not { } entity)
        {
            return null;
        }

        var segments = alarm.Fingerprint.Value.Split('|');

        if (segments.Length != 5)
        {
            return null;
        }

        var (title, objectName, checkId) = (segments[1], segments[3], segments[4]);
        var detail = objectName.Contains('/', StringComparison.Ordinal) ? objectName[(objectName.IndexOf('/') + 1)..] : "";

        IEnumerable<ComplianceFinding> Of(string control) =>
            findings.Where(f =>
                string.Equals(f.CatalogueRelease, ContinuityCatalogue.Release, StringComparison.Ordinal) &&
                string.Equals(f.ControlId, control, StringComparison.Ordinal) &&
                f.Entity == entity);

        bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        return MovedContinuityRules.RuleOf(alarm.Fingerprint.Value) switch
        {
            MovedContinuityRules.HighAvailability => HaControl(checkId) is { } control
                ? Of(control).FirstOrDefault(f => f.Subject.Length == 0)
                : null,
            MovedContinuityRules.Drs =>
                Of(DrsRule).FirstOrDefault(f => Same(f.SubjectLabel, detail) || Same(f.Subject, detail)),
            MovedContinuityRules.Multipath => title switch
            {
                "storage device has only one path" => Of(PathSingle).FirstOrDefault(f => Same(f.Subject, detail)),
                "all working paths share one hba" => Covering(Of(PathSingleHba), detail),
                "all working paths reach one target port" => Covering(Of(PathSingleTarget), detail),
                _ => null,
            },
            MovedContinuityRules.NPlusOne => detail switch
            {
                "cpu" => Of(NPlusOneCpu).FirstOrDefault(),
                "memory" => Of(NPlusOneMemory).FirstOrDefault(),
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>The HBA or port finding naming this device, else the host's failing one.</summary>
    private static ComplianceFinding? Covering(IEnumerable<ComplianceFinding> findings, string device)
    {
        var list = findings.ToList();

        return list.FirstOrDefault(f => f.Observed?.Contains(device, StringComparison.OrdinalIgnoreCase) == true) ??
               list.FirstOrDefault(f => f.Verdict == ComplianceVerdict.Failing) ??
               list.FirstOrDefault();
    }

    private static string? HaControl(string checkId) =>
        checkId.Length <= MovedContinuityRules.HighAvailability.Length + 1
            ? null
            : checkId[(MovedContinuityRules.HighAvailability.Length + 1)..] switch
        {
            "ha-disabled" => HaEnabled,
            "admission-control-disabled" => HaAdmissionControl,
            "host-monitoring-disabled" => HaHostMonitoring,
            "storage-protection-disabled" => HaStorageProtection,
            "too-few-heartbeat-datastores" => HaHeartbeatDatastores,
            "redundant-network-warning-silenced" => HaNetworkWarning,
            _ => null,
        };
}
