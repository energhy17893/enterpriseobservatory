using System.Globalization;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Whether a VM's last backup is within the RPO (M8.8, backup freshness).
/// </summary>
/// <remarks>
/// <para>
/// Vendor-independent: the input is the "last backup" custom attribute the
/// backup product itself writes on the VM (Commvault's <c>Last Backup</c> on
/// the measured estate — docs/measurements/backup-freshness-shapes.md), read
/// by the collector into <see cref="InventoryVerdictKeys.BackupLastUtc"/>.
/// Entity = the VM, subject <c>''</c>: the fix is "back this machine up".
/// </para>
/// <para>
/// Older than the RPO → Failing with its age; otherwise Passing (exactly the
/// RPO passes). A VM with no such attribute is not evaluated — the product
/// cannot tell an unprotected machine from one protected by a tool that
/// writes nothing, so it never says "not backed up". An attribute whose value
/// is not a date it can read, attributes that were not read, and a time in
/// the future (a clock or time zone disagreement) are not evaluated either,
/// each with its own reason.
/// </para>
/// <para>
/// The RPO is one estate-wide number: VM tags (CIS REST) and folders are not
/// collected, so a per-tag or per-folder RPO has nothing to hang on.
/// </para>
/// </remarks>
public sealed class BackupFreshnessCheck(TimeSpan? rpo = null) : IComplianceCheck
{
    /// <summary>Product policy: a VM should have a backup no older than a day.</summary>
    public static readonly TimeSpan DefaultRpo = TimeSpan.FromHours(24);

    /// <summary>How far in the future a backup time may be before it is not believed.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(15);

    public TimeSpan Rpo { get; } = rpo ?? DefaultRpo;

    public EntityKind AppliesTo => EntityKind.VirtualMachine;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var expected = $"a backup within the last {Describe(Rpo)}";

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.BackupRead, out var read) ||
            !string.Equals(read, "true", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    "The VM's custom attributes (customValue) or their definitions were not read, " +
                    "so its last backup time is not known."),
            ];
        }

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.BackupField, out var field))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    "no backup attribute: no custom attribute naming a last backup time carries a value " +
                    "on this VM, so whether and when it was backed up is not known."),
            ];
        }

        var raw = entity.Settings.TryGetValue(InventoryVerdictKeys.BackupValue, out var value) ? value : string.Empty;

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.BackupLastUtc, out var iso) ||
            !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var last))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    $"The backup attribute '{field}' holds '{raw}', which is not a date this product can " +
                    "read without guessing (the day and month order may be ambiguous)."),
            ];
        }

        var basis = entity.Settings.TryGetValue(InventoryVerdictKeys.BackupTimeBasis, out var b) && b.Length > 0
            ? $", read as {b}"
            : string.Empty;
        var when = last.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var age = context.NowUtc - last;

        if (age < -FutureTolerance)
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    $"The backup attribute '{field}' ('{raw}'{basis}) is {Describe(-age)} in the future; " +
                    "the backup server's clock or time zone disagrees with this one."),
            ];
        }

        var observed = $"last backup {when} UTC, {Describe(age < TimeSpan.Zero ? TimeSpan.Zero : age)} ago " +
                       $"(attribute '{field}'{basis})";

        return age > Rpo
            ? [Verdict(ComplianceVerdict.Failing, expected, observed + $": older than the {Describe(Rpo)} RPO")]
            : [Verdict(ComplianceVerdict.Passing, expected, observed)];
    }

    /// <summary>Hours below two days, days from there.</summary>
    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromHours(48))
        {
            var hours = (int)Math.Floor(span.TotalHours);
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        return $"{(int)Math.Floor(span.TotalDays)} days";
    }
}
