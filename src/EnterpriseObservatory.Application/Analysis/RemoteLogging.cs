using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which hosts would take their evidence with them.
/// </summary>
/// <remarks>
/// <para>
/// A host that forwards no logs is not slow, not full and not failing. It is
/// a host whose account of its own death will die with it — and that only
/// becomes visible on the day somebody needs it, which is the day it cannot
/// be fixed. That shape is the reason a monitoring product should say it
/// unprompted rather than waiting to be asked.
/// </para>
/// <para>
/// The control is Broadcom's own: <c>esx-9.log-forwarding</c>, priority P0 in
/// the Security Configuration Guide, and marked non-default — the shipped
/// value is empty, so an untouched host fails it. **No threshold was invented
/// here and there is none to invent:** the setting is either pointed somewhere
/// or it is not.
/// </para>
/// <para>
/// This is the first rule in the product that reads configuration rather than
/// measurement, and it is deliberately the smallest possible one. The
/// catalogue behind it is 260 controls, and its home is a versioned table read
/// as data rather than rules compiled in one at a time — §10 step 4. One rule
/// proves the collection path; ninety would be the mistake that left vROps
/// shipping alarms pinned to vSphere 5.5.
/// </para>
/// </remarks>
public sealed record RemoteLoggingPolicy
{
    /// <summary>The default policy.</summary>
    public static RemoteLoggingPolicy Default { get; } = new();

    /// <summary>The setting that names the log target.</summary>
    /// <remarks>
    /// Held as policy rather than compiled in, for the reason
    /// <see cref="CpuContentionPolicy"/> gives about counter names: the
    /// application layer should not depend on what vSphere calls something.
    /// </remarks>
    public string RemoteHostSetting { get; init; } = "Syslog.global.logHost";

    /// <summary>The setting that names the local log directory.</summary>
    /// <remarks>
    /// Read only to make the finding's text useful. Where the logs are going
    /// instead is the first thing an operator wants to know, and it saves a
    /// round trip to the host.
    /// </remarks>
    public string LocalDirectorySetting { get; init; } = "Syslog.global.logDir";
}

/// <summary>
/// Reports a host that sends its logs nowhere.
/// </summary>
public static class RemoteLogging
{
    /// <summary>Stable across releases: it is part of the fingerprint.</summary>
    public const string RuleId = "remote-logging";

    private const string Title = "Host forwards no logs";
    private const string Category = "Configuration";
    private const string Platform = "platform";

    /// <summary>
    /// Names every live host whose log target is set to nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host that did not report the setting at all is **silent here, on
    /// purpose**, and the reason is architectural rather than a shrug. An
    /// absent key means the product never read <c>config.option</c> from that
    /// host, and a property that could not be read is a collection failure
    /// with its own channel — <c>InventorySnapshot.Failures</c> — which
    /// already reports per-source problems to the operator. Raising it a
    /// second time as an analysis finding would say "this host forwards no
    /// logs" about a host nobody looked at, which is exactly the confident
    /// wrong answer the first principle forbids.
    /// </para>
    /// <para>
    /// The empty string is a different fact and is reported: the host answered,
    /// and its answer was that nothing is configured.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Entity> entities,
        RemoteLoggingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var rules = policy ?? RemoteLoggingPolicy.Default;
        var alerts = new List<AlertDefinition>();

        foreach (var host in entities)
        {
            if (host.Kind != EntityKind.EsxiHost ||
                host.ObservationState == ObservationState.Vanished)
            {
                continue;
            }

            // Absent means unread, and unread is not a finding. Only a host
            // that answered gets judged on its answer.
            if (!host.Settings.TryGetValue(rules.RemoteHostSetting, out var target))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            alerts.Add(new AlertDefinition
            {
                Fingerprint = AlertFingerprint.Create(
                    Platform, Title, Category, host.Id.Value, RuleId),
                Severity = AlertSeverity.Warning,
                Title = Title,
                Description =
                    $"Host '{host.DisplayName}' has no remote syslog target set, so its " +
                    $"logs exist only on the host itself{Locally(host, rules)}. Nothing is " +
                    "wrong with the host right now; the cost lands later, because the one " +
                    "account of why a host failed is the one that fails with it. " +
                    "Broadcom's Security Configuration Guide carries this as a P0 control " +
                    "(esx-9.log-forwarding) and ships the setting empty, so a host nobody " +
                    "has configured will always report it.",
                Category = Category,
                Source = Platform,
                Entity = host.Id,
                IsDerived = true,
            });
        }

        return alerts;
    }

    /// <summary>Where the logs are going instead, when the host said.</summary>
    /// <remarks>
    /// Silent when the directory was not reported rather than guessing at the
    /// default, for the same reason the rule itself is: a sentence naming the
    /// wrong directory sends somebody to look in a place nothing was written.
    /// </remarks>
    private static string Locally(Entity host, RemoteLoggingPolicy rules) =>
        host.Settings.TryGetValue(rules.LocalDirectorySetting, out var directory) &&
        !string.IsNullOrWhiteSpace(directory)
            ? $", under '{directory}'"
            : string.Empty;
}
