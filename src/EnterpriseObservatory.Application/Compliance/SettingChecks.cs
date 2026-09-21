using System.Globalization;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>How a setting's value is judged against a control's baseline.</summary>
/// <remarks>
/// <para>
/// The first four judge a host advanced setting from
/// <see cref="Domain.Entity.Settings"/>. They are shapes rather than
/// per-control code on purpose: the catalogue says which setting and what
/// baseline, and the only thing this product adds is how to compare. A new
/// edition of the guide that moves a control to a new id binds to the same
/// check with nothing changed here.
/// </para>
/// <para>
/// The rest judge the typed host configuration — services, time, standard
/// switch security, lockdown — for controls whose guide row names no
/// parameter at all. See <see cref="SettingCheck.ControlIds"/>.
/// </para>
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

    /// <summary>
    /// Passes when the service named by <see cref="SettingCheck.Setting"/> is
    /// in the state and start policy the baseline words, e.g.
    /// <c>Stopped, Start and stop manually</c>.
    /// </summary>
    ServiceMatchesBaseline,

    /// <summary>
    /// Passes when the service that keeps the host's time — <c>ntpd</c>, or
    /// <c>ptpd</c> on a host that uses PTP — is in the state and start policy
    /// the baseline words.
    /// </summary>
    TimeServiceMatchesBaseline,

    /// <summary>
    /// Passes when the host has a time source: at least one NTP server, or a
    /// PTP configuration on a host that uses PTP.
    /// </summary>
    TimeSourcesConfigured,

    /// <summary>
    /// A time source and a running time service that starts with the host:
    /// the one control VCF 9 makes of what vSphere 8 split in two.
    /// </summary>
    TimeSynchronized,

    /// <summary>Passes when lockdown mode is at least as strict as the baseline.</summary>
    LockdownAtLeastBaseline,

    /// <summary>
    /// Passes when every standard vSwitch and port group rejects the layer-2
    /// behaviour named by <see cref="SettingCheck.Setting"/>, judged on the
    /// effective policy.
    /// </summary>
    NetworkRejects,
}

/// <summary>One setting this product reads, and how to judge it.</summary>
/// <param name="Setting">
/// For a host advanced setting, the vendor's name as it appears in the guide's
/// parameter column. For the typed host configuration, the name the guide's
/// own PowerCLI assessment reads — the service key (<c>snmpd</c>) or the
/// property (<c>LockdownMode</c>, <c>ForgedTransmits</c>).
/// </param>
/// <param name="Kind">How to compare it with the baseline.</param>
public sealed record SettingCheck(string Setting, SettingCheckKind Kind)
{
    /// <summary>
    /// The controls this check answers, for controls whose guide row names no parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding by the parameter column is preferred, because it survives the
    /// id changes between editions. But the guide gives services, time,
    /// switch security and lockdown the parameter <c>N/A</c>, so there is no
    /// name to bind by, and the id is the only key the vendor supplies. They
    /// are listed per edition; an edition that renames one leaves it honestly
    /// "not evaluated" until it is added here.
    /// </para>
    /// <para>Empty for a check that binds by <see cref="Setting"/>.</para>
    /// </remarks>
    public IReadOnlyList<string> ControlIds { get; init; } = [];

    /// <summary>Whether this check reads a value from <see cref="Domain.Entity.Settings"/>.</summary>
    public bool ReadsAdvancedSetting => Kind is
        SettingCheckKind.Present or
        SettingCheckKind.True or
        SettingCheckKind.TimeoutWithinBaseline or
        SettingCheckKind.NotInstallationDefault;
}

/// <summary>The result of judging one host against one control.</summary>
public sealed record SettingJudgement
{
    public required ComplianceVerdict Verdict { get; init; }

    public required string Expected { get; init; }

    /// <summary>What the host reported, as evidence; null when nothing usable was read.</summary>
    public string? Observed { get; init; }

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
public static partial class SettingChecks
{
    public static IReadOnlyList<SettingCheck> Default { get; } =
    [
        new("Syslog.global.logHost", SettingCheckKind.Present),
        new("Syslog.global.auditRecord.remoteEnable", SettingCheckKind.True),
        new("Syslog.global.auditRecord.storageEnable", SettingCheckKind.True),
        new("UserVars.ESXiShellTimeOut", SettingCheckKind.TimeoutWithinBaseline),
        new("UserVars.ESXiShellInteractiveTimeOut", SettingCheckKind.TimeoutWithinBaseline),
        new("Config.HostAgent.plugins.hostsvc.esxAdminsGroup", SettingCheckKind.NotInstallationDefault),

        // Parameter "N/A" in both editions, so bound by id. The names are the
        // ones the guide's own PowerCLI assessment reads, not made up here.
        new("snmpd", SettingCheckKind.ServiceMatchesBaseline)
        {
            ControlIds = ["esxi-8.deactivate-snmp", "esx-9.snmp"],
        },
        new("sfcbd-watchdog", SettingCheckKind.ServiceMatchesBaseline)
        {
            ControlIds = ["esxi-8.deactivate-cim"],
        },
        new("ntpd", SettingCheckKind.TimeServiceMatchesBaseline)
        {
            ControlIds = ["esxi-8.timekeeping-services"],
        },
        new("NtpServer", SettingCheckKind.TimeSourcesConfigured)
        {
            ControlIds = ["esxi-8.timekeeping-sources"],
        },
        new("ntpd", SettingCheckKind.TimeSynchronized)
        {
            ControlIds = ["esx-9.time"],
        },
        new("LockdownMode", SettingCheckKind.LockdownAtLeastBaseline)
        {
            ControlIds = ["esxi-8.lockdown-mode", "esx-9.lockdown-mode"],
        },
        new("ForgedTransmits", SettingCheckKind.NetworkRejects)
        {
            ControlIds =
            [
                "esxi-8.network-reject-forged-transmit-standardswitch",
                "esx-9.network-standard-reject-forged-transmit",
            ],
        },
        new("MacChanges", SettingCheckKind.NetworkRejects)
        {
            ControlIds =
            [
                "esxi-8.network-reject-mac-changes-standardswitch",
                "esx-9.network-standard-reject-mac-changes",
            ],
        },
    ];

    /// <summary>
    /// Judges one host against a control: reads what the check needs, then compares.
    /// </summary>
    /// <remarks>
    /// Whatever the host did not report comes back
    /// <see cref="ComplianceVerdict.NotEvaluated"/> with the reason. Absent is
    /// unread, and unread is not a verdict.
    /// </remarks>
    public static SettingJudgement Judge(SettingCheck check, ComplianceControl control, Domain.Entity host)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(host);

        if (!check.ReadsAdvancedSetting)
        {
            return JudgeConfiguration(check, control, host);
        }

        // The setting arrives as an empty string when nobody configured it;
        // it is missing only when the product never managed to read it — see
        // Entity.Settings.
        if (!host.Settings.TryGetValue(check.Setting, out var observed))
        {
            return NotEvaluated(
                control.BaselineValue,
                $"The host did not report '{check.Setting}', so it was not read. That is a " +
                "collection gap, not a pass or a failure; see Collectors for what could be read.");
        }

        return Judge(check, control, observed) with { Observed = observed };
    }

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
                return NotEvaluated(
                    control.BaselineValue,
                    $"'{check.Kind}' judges the host's configuration, not one setting's value.");
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
