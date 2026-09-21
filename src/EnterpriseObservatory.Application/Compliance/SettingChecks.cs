using System.Globalization;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>How a setting's value is judged against a control's baseline.</summary>
/// <remarks>
/// Four shapes cover every setting this product reads today, and they are
/// shapes rather than per-control code on purpose: the catalogue says which
/// setting and what baseline, and the only thing this product adds is how to
/// compare. A new edition of the guide that moves a control to a new id binds
/// to the same check with nothing changed here.
/// </remarks>
public enum SettingCheckKind
{
    /// <summary>Passes when the setting names something; the baseline is site-specific.</summary>
    Present,

    /// <summary>Passes when the setting is true.</summary>
    True,

    /// <summary>
    /// Passes when the timeout is on and no longer than the baseline's number
    /// of seconds. Zero is the shipped value and means "never times out".
    /// </summary>
    TimeoutWithinBaseline,

    /// <summary>Passes when the setting is anything but the shipped value.</summary>
    NotInstallationDefault,
}

/// <summary>One setting this product reads, and how to judge it.</summary>
/// <param name="Setting">The vendor's name, as it appears in the guide's parameter column.</param>
/// <param name="Kind">How to compare it with the baseline.</param>
public sealed record SettingCheck(string Setting, SettingCheckKind Kind);

/// <summary>The result of judging one value.</summary>
public sealed record SettingJudgement
{
    public required ComplianceVerdict Verdict { get; init; }

    public required string Expected { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The settings the compliance engine can judge, and how.
/// </summary>
/// <remarks>
/// <para>
/// Exactly the host advanced settings the collector already carries in
/// <see cref="Domain.Entity.Settings"/>. A check for a setting nobody collects
/// would bind a control and then report every host as unread, which is true
/// and useless; the control stays honestly "not evaluated — no data
/// collected" until the collector reads it.
/// </para>
/// <para>
/// Held as data with the vendor's names in it, for the reason
/// <c>RemoteLoggingPolicy</c> gives: the application layer judges values and
/// should not have opinions about what vSphere calls them beyond what the
/// guide itself says.
/// </para>
/// </remarks>
public static class SettingChecks
{
    public static IReadOnlyList<SettingCheck> Default { get; } =
    [
        new("Syslog.global.logHost", SettingCheckKind.Present),
        new("Syslog.global.auditRecord.remoteEnable", SettingCheckKind.True),
        new("Syslog.global.auditRecord.storageEnable", SettingCheckKind.True),
        new("UserVars.ESXiShellTimeOut", SettingCheckKind.TimeoutWithinBaseline),
        new("UserVars.ESXiShellInteractiveTimeOut", SettingCheckKind.TimeoutWithinBaseline),
        new("Config.HostAgent.plugins.hostsvc.esxAdminsGroup", SettingCheckKind.NotInstallationDefault),
    ];

    /// <summary>
    /// Judges what a host reported against a control.
    /// </summary>
    /// <param name="check">How to judge.</param>
    /// <param name="control">The control, for its baseline and shipped value.</param>
    /// <param name="observed">The value as the host reported it.</param>
    public static SettingJudgement Judge(SettingCheck check, ComplianceControl control, string observed)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(observed);

        var value = observed.Trim();

        switch (check.Kind)
        {
            case SettingCheckKind.Present:
                return Verdict(
                    value.Length > 0,
                    $"set to a log target (baseline: {control.BaselineValue})");

            case SettingCheckKind.True:
                return Verdict(IsTrue(value), "true");

            case SettingCheckKind.TimeoutWithinBaseline:
                if (!int.TryParse(control.BaselineValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) ||
                    most <= 0)
                {
                    return NotEvaluated(
                        control.BaselineValue,
                        $"The guide's baseline '{control.BaselineValue}' is not a number of seconds, " +
                        "so there is nothing to compare the host's value with.");
                }

                var expected = $"between 1 and {most} seconds (0 never times out)";

                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    ? Verdict(seconds > 0 && seconds <= most, expected)
                    : NotEvaluated(
                        expected,
                        $"The host reported '{observed}', which is not a number of seconds.");

            case SettingCheckKind.NotInstallationDefault:
                var shipped = control.InstallationDefault.Trim().Trim('"').Trim();

                if (shipped.Length == 0 ||
                    shipped.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ||
                    shipped.StartsWith("Site-Specific", StringComparison.OrdinalIgnoreCase))
                {
                    return NotEvaluated(
                        control.BaselineValue,
                        $"The guide gives no concrete shipped value ('{control.InstallationDefault}') " +
                        "to compare against.");
                }

                return Verdict(
                    !value.Equals(shipped, StringComparison.OrdinalIgnoreCase),
                    $"anything but the shipped '{shipped}' (baseline: {control.BaselineValue})");

            default:
                return NotEvaluated(control.BaselineValue, $"No judgement is defined for '{check.Kind}'.");
        }
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.Ordinal);

    private static SettingJudgement Verdict(bool passes, string expected) => new()
    {
        Verdict = passes ? ComplianceVerdict.Passing : ComplianceVerdict.Failing,
        Expected = expected,
    };

    private static SettingJudgement NotEvaluated(string expected, string reason) => new()
    {
        Verdict = ComplianceVerdict.NotEvaluated,
        Expected = expected,
        Reason = reason,
    };
}
