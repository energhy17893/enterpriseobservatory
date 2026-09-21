using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The first rule that reads configuration rather than measurement.
/// </summary>
/// <remarks>
/// There is no threshold here and none to invent: a log target is either
/// pointed somewhere or it is not. So these tests are almost entirely about
/// the one distinction that can go wrong — a setting nobody configured against
/// a setting nobody read — and about the silences that follow from it.
/// </remarks>
public class RemoteLoggingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Target = "Syslog.global.logHost";
    private const string Directory = "Syslog.global.logDir";

    /// <summary>
    /// A host carrying the given settings. Passing no settings at all is the
    /// "never read" case, which is not the same as an empty target.
    /// </summary>
    private static Entity Host(
        string id = "vc-1:host-1",
        EntityKind kind = EntityKind.EsxiHost,
        ObservationState state = ObservationState.Active,
        params (string Key, string Value)[] settings) => new()
        {
            Id = new EntityId(id),
            Kind = kind,
            DisplayName = id,
            LastSeenUtc = T0,
            ObservationState = state,
            Settings = settings.ToDictionary(
                s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void A_host_with_no_log_target_is_named()
    {
        var alert = Assert.Single(RemoteLogging.Evaluate([Host(settings: (Target, ""))]));

        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
        Assert.Equal("Host forwards no logs", alert.Title);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void A_host_that_forwards_its_logs_is_silent()
    {
        Assert.Empty(RemoteLogging.Evaluate(
            [Host(settings: (Target, "udp://10.0.0.5:514"))]));
    }

    [Fact]
    public void A_host_that_never_reported_the_setting_is_silent()
    {
        // The distinction the whole rule rests on. An absent key means the
        // product never read config.option from that host, and a property
        // that could not be read is a collection failure with its own
        // channel. Reporting it here would say "this host forwards no logs"
        // about a host nobody looked at.
        Assert.Empty(RemoteLogging.Evaluate([Host()]));
    }

    [Fact]
    public void Whitespace_is_not_a_log_target()
    {
        // A target of spaces is a host that answered "nothing", not a host
        // that answered. Treating it as configured would hide the finding
        // behind a value nobody can send a log to.
        Assert.Empty(RemoteLogging.Evaluate(
            [Host(settings: (Target, "udp://10.0.0.5:514"))]));

        Assert.Single(RemoteLogging.Evaluate([Host(settings: (Target, "   "))]));
    }

    [Fact]
    public void Only_hosts_are_judged()
    {
        // A virtual machine carrying the same key -- which nothing produces
        // today, and which a second vendor might -- is not this rule's
        // business, and naming it would be confidently wrong.
        Assert.Empty(RemoteLogging.Evaluate(
            [Host(kind: EntityKind.VirtualMachine, settings: (Target, ""))]));
    }

    [Fact]
    public void A_vanished_host_is_not_named()
    {
        // Its settings are the last ones seen, and a machine that is gone
        // cannot be configured. The finding would be unclearable.
        Assert.Empty(RemoteLogging.Evaluate(
            [Host(state: ObservationState.Vanished, settings: (Target, ""))]));
    }

    [Fact]
    public void A_host_in_maintenance_is_still_named()
    {
        // Deliberately not suppressed. Maintenance silences things that are
        // temporarily broken; a log target that was never set is neither
        // temporary nor caused by the maintenance.
        Assert.Single(RemoteLogging.Evaluate(
            [Host(state: ObservationState.InMaintenance, settings: (Target, ""))]));
    }

    [Fact]
    public void The_finding_says_where_the_logs_are_going_instead()
    {
        var alert = Assert.Single(RemoteLogging.Evaluate(
            [Host(settings: [(Target, ""), (Directory, "/scratch/log")])]));

        Assert.Contains("/scratch/log", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreported_directory_is_left_unsaid_rather_than_guessed()
    {
        // Naming the documented default would send somebody to look in a
        // place nothing was written.
        var alert = Assert.Single(RemoteLogging.Evaluate([Host(settings: (Target, ""))]));

        Assert.DoesNotContain("under '", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_host_is_its_own_finding()
    {
        var alerts = RemoteLogging.Evaluate(
        [
            Host("vc-1:host-1", settings: (Target, "")),
            Host("vc-1:host-2", settings: (Target, "")),
            Host("vc-1:host-3", settings: (Target, "udp://10.0.0.5:514")),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_setting_name_is_policy_rather_than_compiled_in()
    {
        // The application layer should not depend on what vSphere calls
        // something. A second vendor with a different name for the same fact
        // changes configuration, not code.
        var alert = Assert.Single(RemoteLogging.Evaluate(
            [Host(settings: ("logging.remote.target", ""))],
            new RemoteLoggingPolicy { RemoteHostSetting = "logging.remote.target" }));

        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
    }

    [Fact]
    public void The_finding_cites_the_control_it_came_from()
    {
        // The threshold discipline applied to a rule that has no threshold:
        // this one is borrowed wholesale from a vendor control, and the text
        // says so rather than letting a reader assume it is our opinion.
        var alert = Assert.Single(RemoteLogging.Evaluate([Host(settings: (Target, ""))]));

        Assert.Contains("esx-9.log-forwarding", alert.Description, StringComparison.Ordinal);
    }
}
