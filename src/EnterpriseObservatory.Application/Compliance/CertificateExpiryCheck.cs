using System.Globalization;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Whether a certificate is about to expire, or has (M8.7, expiry radar).
/// </summary>
/// <remarks>
/// <para>
/// One check for two entities: an ESXi host's certificate (read from
/// <c>config.certificate</c>) and the vCenter's (read by a TLS handshake with
/// its endpoint). Entity = the host or the vCenter, subject <c>''</c>
/// (K1 §3.1): the fix is "renew this machine's certificate", once. The
/// fingerprint is evidence, in <see cref="CheckVerdict.Observed"/>, not
/// identity — renewal must read as the same finding going Failing → Passing,
/// not as one finding vanishing and another appearing (K1 §3.2).
/// </para>
/// <para>
/// Expired → Failing; expiring within <see cref="WarningWithin"/> (inclusive)
/// → Failing with warning wording; otherwise Passing. vCenter's own
/// certificate alarm threshold is not collected, so the threshold is a
/// tool's default and says so in the control's source (reference-approaches
/// §10.4). No expiry read → not evaluated.
/// </para>
/// </remarks>
public sealed class CertificateExpiryCheck(EntityKind appliesTo) : IComplianceCheck
{
    /// <summary>vCheck's default warning window for certificate expiry.</summary>
    public static readonly TimeSpan WarningWithin = TimeSpan.FromDays(60);

    public EntityKind AppliesTo { get; } = appliesTo;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var expected = $"certificate valid for more than {WarningWithin.TotalDays:0} days";
        var what = AppliesTo == EntityKind.VCenter ? "vCenter endpoint's certificate" : "host's certificate";

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.CertificateNotAfter, out var raw))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason: AppliesTo == EntityKind.VCenter
                    ? "The vCenter endpoint's certificate was not read (the TLS handshake did not complete, " +
                      "or this collector does not read it)."
                    : "The host's certificate (config.certificate) was not read, or did not parse as X.509."),
            ];
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var notAfter))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                    $"The {what} expiry '{raw}' is not a date this product can read."),
            ];
        }

        var fingerprint = entity.Settings.TryGetValue(InventoryVerdictKeys.CertificateSha256, out var sha)
            ? $"; SHA-256 {sha}"
            : string.Empty;

        var date = notAfter.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var left = notAfter - context.NowUtc;

        if (left <= TimeSpan.Zero)
        {
            var ago = (int)Math.Floor(-left.TotalDays);

            return
            [
                Verdict(ComplianceVerdict.Failing, expected,
                    $"expired on {date} ({ago} {Days(ago)} ago){fingerprint}"),
            ];
        }

        var days = (int)Math.Floor(left.TotalDays);

        return left <= WarningWithin
            ? [Verdict(ComplianceVerdict.Failing, expected,
                $"expires in {days} {Days(days)}, on {date}: renew it before then{fingerprint}")]
            : [Verdict(ComplianceVerdict.Passing, expected, $"valid until {date} ({days} days){fingerprint}")];
    }

    private static string Days(int n) => n == 1 ? "day" : "days";
}
