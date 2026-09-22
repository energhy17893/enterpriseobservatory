using System.Globalization;
using System.Text.RegularExpressions;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>Which object an event-raised alert is about.</summary>
public enum EventSubject
{
    /// <summary>The ESXi host that reported it.</summary>
    Host,

    /// <summary>The cluster (vCenter's compute resource).</summary>
    Cluster,

    /// <summary>The virtual machine the event names.</summary>
    VirtualMachine,
}

/// <summary>
/// One row of the table: a condition vCenter announces with an event.
/// </summary>
/// <remarks>
/// Data, not code. Adding the next event that deserves an alert is a row here
/// and a test, never a second mechanism — the lesson <see cref="RemoteLogging"/>
/// draws from vROps pinning its alarms to one vSphere release.
/// </remarks>
public sealed record EventCondition
{
    /// <summary>Stable across releases: it is part of the fingerprint.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required string Category { get; init; }

    public required EventSubject About { get; init; }

    /// <summary>The type ids that mean the condition has started.</summary>
    /// <remarks>
    /// More than one where vCenter reports the same thing twice — a standard
    /// class and the HA agent's own <c>com.vmware.vc.HA.*</c> twin. They share a
    /// row so that, where both name the same subject, one isolation is one
    /// alert, not two.
    /// </remarks>
    public required IReadOnlyList<string> RaisedBy { get; init; }

    /// <summary>
    /// The type ids that mean it has ended; empty when vCenter sends none.
    /// </summary>
    public IReadOnlyList<string> ClearedBy { get; init; } = [];

    /// <summary>
    /// How long after its last report the condition is held open when no clear
    /// arrives. <strong>This product's choice</strong>, not a cited threshold.
    /// </summary>
    /// <remarks>
    /// Needed even where a clear exists: a clear that fell into a gap in the
    /// event stream (<see cref="EventCursor.LastGapUtc"/>) would otherwise leave
    /// the alert open for ever, and an alarm nobody can close is one everybody
    /// learns to ignore. It must stay well above two inventory cycles, or a
    /// warning is forgotten before hysteresis confirms it.
    /// </remarks>
    public TimeSpan TimeToLive { get; init; } = EventAlertPolicy.DefaultTimeToLive;

    /// <summary>
    /// A pattern that picks, out of the message, which instance on the subject
    /// this is — the vmnic, the storage device, the NFS mount — or null when the
    /// subject alone is enough.
    /// </summary>
    /// <remarks>
    /// Read from the message because the product does not store an EventEx's
    /// arguments, and a host with four vmnics must not have vmnic2 coming back
    /// close the alert for vmnic3. Where the pattern does not match, the
    /// instance is simply absent, and an absent instance pairs only with
    /// another absent one: the failure mode is an alert held until its time to
    /// live, never one closed by somebody else's recovery.
    /// </remarks>
    public string? Instance { get; init; }

    /// <summary>One sentence on what it means, for the alert's text.</summary>
    public required string Meaning { get; init; }
}

/// <summary>Which vCenter events become alerts, and how they end.</summary>
public sealed record EventAlertPolicy
{
    /// <summary>
    /// A day. Long enough that an event at 02:00 is still open when somebody
    /// arrives at 09:00; short enough that an ended condition vCenter never
    /// announced does not sit in the inbox for a week.
    /// <strong>This product's choice.</strong>
    /// </summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromDays(1);

    public static EventAlertPolicy Default { get; } = new();

    /// <summary>The table. See <see cref="EventAlerts"/> for where each id is from.</summary>
    public IReadOnlyList<EventCondition> Conditions { get; init; } = EventAlerts.Catalogue;
}

/// <summary>
/// Turns specific vCenter events into alerts (roadmap M2.2 and M2.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where the ids come from.</strong> Every type id below is in the
/// vCenter event catalogue for both 7.0 U3 and 8.0 U3, as dumped from
/// <c>EventManager.description</c> in github.com/lamw/vcenter-event-mapping
/// (<c>vsphere-7.0u3.md</c>, <c>vsphere-8.0u3.md</c>). The message shapes the
/// instance patterns rely on are from Broadcom KB 424517 (vmnic), KB 328900
/// (storage device) and KB 1009568 (NFS). Nothing was taken from memory, and
/// nothing here has yet been seen arriving from a live vCenter.
/// </para>
/// <para>
/// <strong>How an alert opens and closes.</strong> An event is a moment, and
/// an alert is a state, so something has to say how long the moment lasts.
/// Here it is: for each condition, subject and instance, the newest report is
/// compared with the newest clear. The alert is open while the report is the
/// newer of the two and younger than the condition's time to live. A clear
/// closes it; so does the time to live running out, which is the only way a
/// condition vCenter never announces the end of — an isolated host, a failed
/// failover — can close at all.
/// </para>
/// <para>
/// <strong>Why it re-reads rather than remembering.</strong> The rule is
/// re-derived every inventory cycle from the stored events of the last time to
/// live, and the fingerprint (condition, subject, instance — never the event's
/// key or time) is what keeps that from raising the same alert every cycle:
/// reconciliation sees the same fingerprint, keeps the one instance, and
/// notifies once. The alternative, reading only events newer than the last
/// evaluation, cannot work here. Reconciliation treats what a cycle reports as
/// the whole truth, so a rule that reported the isolation once and was silent
/// the next cycle would have its alert resolved five minutes after raising it;
/// and the rule would need a watermark of its own to persist, beside the one
/// the event cursor already keeps.
/// </para>
/// <para>
/// Two costs of that, stated rather than hidden. A condition that starts and
/// clears between two inventory cycles is never shown — a link down for ninety
/// seconds is in the event list, not the inbox; the flapping event is what
/// catches a link that does it repeatedly. And an operator's clear holds while
/// the condition is still being reported, so a second isolation of the same
/// host within the time to live stays cleared; that is the lifecycle's
/// sticky-clear rule, and the event list still shows it.
/// </para>
/// </remarks>
public static class EventAlerts
{
    /// <summary>Stable across releases: it is part of the fingerprint.</summary>
    public const string RuleId = "vcenter-events";

    private const string Platform = "platform";

    private const string HighAvailability = "High availability";
    private const string StorageConnectivity = "Storage connectivity";
    private const string Network = "Network";

    /// <summary>A vmnic's name, as ESXi writes it: <c>vmnic0</c>.</summary>
    private const string Vmnic = @"\bvmnic\d+\b";

    /// <summary>
    /// A SCSI device name — <c>naa.</c>, <c>eui.</c>, <c>t10.</c> or <c>mpx.</c>
    /// — without the full stop that ends the lost message's sentence.
    /// </summary>
    private const string Device = @"\b(?:naa|eui|t10|mpx)\.[0-9A-Za-z_:\-]+(?:\.[0-9A-Za-z_:\-]+)*";

    /// <summary>
    /// The NFS server and export, which the lost and restored messages both
    /// write as <c>server X mount point Y</c> (KB 1009568).
    /// </summary>
    private const string NfsMount = @"server \S+ mount point \S+";

    /// <summary>The table the default policy uses.</summary>
    /// <remarks>
    /// Severities are this product's choice. Critical where virtual machines
    /// are down or about to be — a failed host, a lost device, an uplink set
    /// with no link left; warning where protection is reduced but nothing has
    /// yet stopped. Every id is cited in the class remarks.
    /// </remarks>
    public static IReadOnlyList<EventCondition> Catalogue { get; } =
    [
        // --- M2.2: vSphere HA ------------------------------------------------

        // The standard class names its host in isolatedHost, which the
        // collector does not read; where the base host field is empty too it
        // is filed against the cluster (see KeyFor), and then it and the HA
        // agent's twin are two alerts rather than one. Unverified against a
        // live vCenter which of the two fields it fills.
        new()
        {
            Id = "ha-host-isolated",
            Title = "HA host isolated",
            Severity = AlertSeverity.Critical,
            Category = HighAvailability,
            About = EventSubject.Host,
            RaisedBy = ["DasHostIsolatedEvent", "com.vmware.vc.HA.DasHostIsolatedEvent"],
            Meaning =
                "vSphere HA declared a host network-isolated: it can no longer reach its " +
                "isolation addresses, and its virtual machines are subject to the isolation response.",
        },
        new()
        {
            Id = "ha-host-failed",
            Title = "HA host failed",
            Severity = AlertSeverity.Critical,
            Category = HighAvailability,
            About = EventSubject.Host,
            RaisedBy = ["DasHostFailedEvent", "com.vmware.vc.HA.DasHostFailedEvent"],
            Meaning =
                "vSphere HA declared a host failed and will have tried to restart its " +
                "virtual machines elsewhere.",
        },
        new()
        {
            Id = "ha-failover-failed",
            Title = "HA failover failed",
            Severity = AlertSeverity.Critical,
            Category = HighAvailability,
            About = EventSubject.VirtualMachine,
            RaisedBy =
            [
                "VmFailoverFailed",
                "NotEnoughResourcesToStartVmEvent",
                "com.vmware.vc.HA.FailedRestartAfterIsolationEvent",
            ],
            Meaning =
                "vSphere HA could not restart a virtual machine after a failure, so it is " +
                "down until somebody starts it.",
        },

        // FailoverLevelRestored is the documented counterpart of the standard
        // class. That it also ends the HA agent's InsufficientFailoverLevelEvent
        // is this product's reading of the two names, not a cited pairing; the
        // time to live bounds the cost of being wrong.
        new()
        {
            Id = "ha-insufficient-failover-resources",
            Title = "HA failover resources insufficient",
            Severity = AlertSeverity.Warning,
            Category = HighAvailability,
            About = EventSubject.Cluster,
            RaisedBy =
            [
                "InsufficientFailoverResourcesEvent",
                "com.vmware.vc.HA.InsufficientFailoverLevelEvent",
            ],
            ClearedBy = ["FailoverLevelRestored"],
            Meaning =
                "The cluster no longer holds enough spare capacity for its configured " +
                "failover level; the next host failure may leave virtual machines unrestarted.",
        },
        new()
        {
            Id = "ha-master-lost",
            Title = "HA master unreachable",
            Severity = AlertSeverity.Warning,
            Category = HighAvailability,
            About = EventSubject.Cluster,
            RaisedBy =
            [
                "com.vmware.vc.HA.VcCannotFindMasterEvent",
                "com.vmware.vc.HA.VcDisconnectedFromMasterEvent",
                "com.vmware.vc.HA.VcCannotCommunicateWithMasterEvent",
            ],
            ClearedBy = ["com.vmware.vc.HA.VcConnectedToMasterEvent"],
            Meaning =
                "vCenter cannot reach the cluster's vSphere HA master agent, so it cannot " +
                "say whether HA is protecting anything.",
        },

        // --- M2.3: storage connectivity --------------------------------------

        new()
        {
            Id = "storage-connectivity-lost",
            Title = "Storage device connectivity lost",
            Severity = AlertSeverity.Critical,
            Category = StorageConnectivity,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.storage.connectivity.lost"],
            ClearedBy = ["esx.clear.storage.connectivity.restored"],
            Instance = Device,
            Meaning =
                "A host lost the last path to a storage device; anything on it is " +
                "unreachable from that host.",
        },
        new()
        {
            Id = "nfs-server-lost",
            Title = "NFS server connection lost",
            Severity = AlertSeverity.Critical,
            Category = StorageConnectivity,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.vmfs.nfs.server.disconnect"],
            ClearedBy = ["esx.clear.vmfs.nfs.server.restored"],
            Instance = NfsMount,
            Meaning =
                "A host lost its connection to an NFS server; the volume and the virtual " +
                "machines on it are unavailable to that host until it comes back.",
        },

        // --- M2.3: physical uplinks ------------------------------------------

        // Per KB 424517, a vmnic in use by a port group or DVPort reports the
        // redundancy events below instead of this one; this fires for a vmnic
        // nothing is using yet, which is still a cable somebody should know about.
        new()
        {
            Id = "vmnic-link-down",
            Title = "Physical NIC link down",
            Severity = AlertSeverity.Warning,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.vmnic.linkstate.down"],
            ClearedBy = ["esx.clear.net.vmnic.linkstate.up"],
            Instance = Vmnic,
            Meaning = "A physical NIC on a host lost its link.",
        },

        // ESXi raises this itself when a link keeps changing state, and sends
        // no event when it settles.
        new()
        {
            Id = "vmnic-link-flapping",
            Title = "Physical NIC link unstable",
            Severity = AlertSeverity.Warning,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.vmnic.linkstate.flapping"],
            Instance = Vmnic,
            Meaning =
                "A physical NIC's link is going up and down repeatedly — usually a cable, " +
                "an optic or a switch port.",
        },

        // Standard switch and distributed switch are separate rows because
        // their clears are separate events; nothing documents one ending the
        // other. No instance: the restored messages are not cited here, so
        // both pair on the host alone.
        new()
        {
            Id = "uplink-redundancy-lost",
            Title = "Uplink redundancy lost",
            Severity = AlertSeverity.Warning,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.redundancy.lost"],
            ClearedBy = ["esx.clear.net.redundancy.restored"],
            Meaning =
                "A standard switch on a host is down to one working uplink; the next " +
                "failure disconnects its port groups.",
        },
        new()
        {
            Id = "dvport-redundancy-lost",
            Title = "DVPort uplink redundancy lost",
            Severity = AlertSeverity.Warning,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.dvport.redundancy.lost"],
            ClearedBy = ["esx.clear.net.dvport.redundancy.restored"],
            Meaning =
                "Distributed switch ports on a host are down to one working uplink; the " +
                "next failure disconnects them.",
        },
        new()
        {
            Id = "network-connectivity-lost",
            Title = "Port group connectivity lost",
            Severity = AlertSeverity.Critical,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.connectivity.lost"],
            ClearedBy = ["esx.clear.net.connectivity.restored"],
            Meaning = "A standard switch on a host has no working uplink left.",
        },
        new()
        {
            Id = "dvport-connectivity-lost",
            Title = "DVPort connectivity lost",
            Severity = AlertSeverity.Critical,
            Category = Network,
            About = EventSubject.Host,
            RaisedBy = ["esx.problem.net.dvport.connectivity.lost"],
            ClearedBy = ["esx.clear.net.dvport.connectivity.restored"],
            Meaning = "Distributed switch ports on a host have no working uplink left.",
        },
    ];

    /// <summary>
    /// Reads, from the store, every event the policy could act on.
    /// </summary>
    /// <remarks>
    /// Bounded by the longest time to live: nothing older can hold an alert
    /// open, and a clear older than that has nothing left to close.
    /// </remarks>
    public static IReadOnlyList<SourceEvent> Read(
        IEventReader store, DateTimeOffset nowUtc, EventAlertPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var conditions = (policy ?? EventAlertPolicy.Default).Conditions;

        if (conditions.Count == 0)
        {
            return [];
        }

        var types = conditions
            .SelectMany(c => c.RaisedBy.Concat(c.ClearedBy))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return store.OfTypes(types, nowUtc - conditions.Max(c => c.TimeToLive));
    }

    /// <summary>
    /// Names every condition currently open, one alert per condition, subject
    /// and instance.
    /// </summary>
    /// <remarks>
    /// Pure: the events are handed in, and the same events and the same clock
    /// always give the same alerts. See the class remarks for the open and
    /// close rule.
    /// </remarks>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<SourceEvent> events,
        DateTimeOffset nowUtc,
        EventAlertPolicy? policy = null) =>
        [.. Judge(events, nowUtc, [], policy).OfType<ConditionPresent>().SelectMany(p => p.Alerts)];

    /// <summary>
    /// The same, in three values (ADR-0026): one verdict per condition,
    /// subject and instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A clear at least as new as the report is <see cref="AbsenceKind.ConditionCleared"/>,
    /// dated at the clear rather than at "now" — vCenter's own statement is
    /// the evidence. The time to live running out on a condition with no
    /// documented clear is <see cref="AbsenceKind.Expired"/>: an occurrence,
    /// which is why the type still declares it two-valued. The time to live
    /// running out on a condition that documents a clear and never saw one is
    /// <see cref="UnknownReason.InsufficientSeries"/> — the clear may simply
    /// not have arrived yet, and the difference between "it ended quietly"
    /// and "we stopped hearing about it" is exactly what this ADR exists to
    /// keep separate.
    /// </para>
    /// <para>
    /// A held alert with no event at all in this cycle's batch — no report, no
    /// clear, nothing that aged past its time to live — gets an ordinary
    /// <see cref="ConditionAbsent"/>, exactly as a rule with no memory should:
    /// nothing here says it is wrong. <see cref="EventReadFreshness"/> is what
    /// turns that into <see cref="UnknownReason.SourceSilent"/> for a source
    /// whose events are not known to have been read through — the rule itself
    /// does not read watermarks.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SubjectVerdict> Judge(
        IReadOnlyList<SourceEvent> events,
        DateTimeOffset nowUtc,
        IReadOnlyList<HeldAlert> held,
        EventAlertPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(held);

        var conditions = (policy ?? EventAlertPolicy.Default).Conditions;
        var (raises, clears) = Index(conditions);
        var patterns = conditions
            .Where(c => c.Instance is not null)
            .ToDictionary(
                c => c.Id,
                c => new Regex(c.Instance!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
                StringComparer.Ordinal);

        var tracks = new Dictionary<TrackKey, Track>();

        foreach (var e in events)
        {
            if (raises.TryGetValue(e.TypeId, out var raised))
            {
                var key = KeyFor(raised, e, patterns);
                var track = tracks.TryGetValue(key, out var existing) ? existing : new Track(raised);

                if (nowUtc - e.CreatedAtUtc < raised.TimeToLive)
                {
                    track.Reports++;
                }

                if (track.LastRaise is null || IsNewer(e, track.LastRaise))
                {
                    track.LastRaise = e;
                    track.SubjectName = SubjectOf(raised, e)?.Name;
                }

                tracks[key] = track;
            }

            if (clears.TryGetValue(e.TypeId, out var cleared))
            {
                foreach (var condition in cleared)
                {
                    var key = KeyFor(condition, e, patterns);
                    var track = tracks.TryGetValue(key, out var existing) ? existing : new Track(condition);

                    if (track.LastClear is null || IsNewer(e, track.LastClear))
                    {
                        track.LastClear = e;
                    }

                    tracks[key] = track;
                }
            }
        }

        var verdicts = new List<SubjectVerdict>();

        foreach (var (key, track) in tracks)
        {
            if (track.LastRaise is not { } raise)
            {
                continue;
            }

            var fingerprint = FingerprintFor(key, track.Condition);
            var entity = EntityFor(key);

            // A clear at least as new as the report ends it. "At least": vCenter
            // stamps to the second, and a clear in the same second as its
            // problem is a recovery, not a new failure.
            if (track.LastClear is { } clear && !IsNewer(raise, clear))
            {
                verdicts.Add(new ConditionAbsent
                {
                    Covers = [fingerprint],
                    Entity = entity,
                    EvidenceAtUtc = clear.CreatedAtUtc,
                });

                continue;
            }

            if (nowUtc - raise.CreatedAtUtc >= track.Condition.TimeToLive)
            {
                verdicts.Add(track.Condition.ClearedBy.Count == 0
                    ? new ConditionAbsent
                    {
                        Covers = [fingerprint],
                        Entity = entity,
                        EvidenceAtUtc = nowUtc,
                        Because = AbsenceKind.Expired,
                    }
                    : new Unknown
                    {
                        Covers = [fingerprint],
                        Entity = entity,
                        Reason = UnknownReason.InsufficientSeries,
                        Detail = $"'{string.Join(" or ", track.Condition.ClearedBy)}' never arrived for this " +
                                 $"subject, and the {Hours(track.Condition.TimeToLive)} time to live on the last " +
                                 $"report ({raise.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC) has run out",
                    });

                continue;
            }

            verdicts.Add(new ConditionPresent
            {
                Covers = [fingerprint],
                Alerts = [Alert(key, track, raise)],
                Entity = entity,
                EvidenceAtUtc = raise.CreatedAtUtc,
            });
        }

        // A held alert with nothing at all in this cycle's batch about it:
        // an ordinary absence, exactly as a rule with no memory should say.
        // EventReadFreshness is what turns this into "unknown" for a source
        // that is not known to be read through.
        var spoken = verdicts.SelectMany(v => v.Covers).ToHashSet();

        foreach (var alert in held)
        {
            if (!spoken.Contains(alert.Fingerprint))
            {
                verdicts.Add(new ConditionAbsent
                {
                    Covers = [alert.Fingerprint],
                    Entity = alert.Entity,
                    EvidenceAtUtc = nowUtc,
                });
            }
        }

        return verdicts;
    }

    /// <summary>
    /// Which condition each type id raises, and which it clears.
    /// </summary>
    /// <remarks>
    /// A type id that raised two conditions, or both raised and cleared, would
    /// make the table's meaning depend on the order of its rows. That is a
    /// broken policy rather than an estate fact, so it throws — and
    /// <see cref="GuardedRule"/> turns the throw into an alert saying so.
    /// </remarks>
    private static (Dictionary<string, EventCondition> Raises, Dictionary<string, List<EventCondition>> Clears)
        Index(IReadOnlyList<EventCondition> conditions)
    {
        var raises = new Dictionary<string, EventCondition>(StringComparer.OrdinalIgnoreCase);
        var clears = new Dictionary<string, List<EventCondition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var condition in conditions)
        {
            foreach (var type in condition.RaisedBy)
            {
                if (!raises.TryAdd(type, condition))
                {
                    throw new InvalidOperationException(
                        $"Event type '{type}' raises both '{raises[type].Id}' and '{condition.Id}'.");
                }
            }

            foreach (var type in condition.ClearedBy)
            {
                if (!clears.TryGetValue(type, out var list))
                {
                    clears[type] = list = [];
                }

                list.Add(condition);
            }
        }

        if (raises.Keys.FirstOrDefault(clears.ContainsKey) is { } both)
        {
            throw new InvalidOperationException(
                $"Event type '{both}' both raises and clears a condition.");
        }

        return (raises, clears);
    }

    /// <summary>What one alert is about.</summary>
    /// <remarks>
    /// The subject falls back to the cluster, then to the vCenter itself, when
    /// the event does not carry the object the row asks for. HA's standard
    /// classes name their host in a field the collector does not read, and an
    /// event dropped for want of a host would be an isolation nobody was told
    /// about. The fallback is coarser — two hosts isolated in one cluster
    /// become one alert — and the text still quotes vCenter's own message,
    /// which names the host.
    /// </remarks>
    private static TrackKey KeyFor(
        EventCondition condition, SourceEvent e, Dictionary<string, Regex> patterns)
    {
        var subject = SubjectOf(condition, e);

        var instance = patterns.TryGetValue(condition.Id, out var pattern) &&
            pattern.Match(e.Message) is { Success: true } match
                ? match.Value
                : null;

        return new TrackKey(condition.Id, e.SourceInstanceId, subject?.MoRef, instance);
    }

    private static EventObjectRef? SubjectOf(EventCondition condition, SourceEvent e)
    {
        var preferred = condition.About switch
        {
            EventSubject.Host => e.Host,
            EventSubject.VirtualMachine => e.VirtualMachine,
            _ => null,
        };

        return preferred ?? e.ComputeResource;
    }

    private static EntityId? EntityFor(TrackKey key) =>
        key.MoRef is null ? (EntityId?)null : EntityId.For(key.Source, key.MoRef);

    private static AlertFingerprint FingerprintFor(TrackKey key, EventCondition condition)
    {
        var entity = EntityFor(key);
        var about = entity?.Value ?? key.Source;
        var objectName = key.Instance is null ? about : $"{about}/{key.Instance}";

        return AlertFingerprint.Create(
            Platform, condition.Title, condition.Category, objectName, $"{RuleId}:{condition.Id}");
    }

    private static AlertDefinition Alert(TrackKey key, Track track, SourceEvent raise)
    {
        var condition = track.Condition;
        var entity = EntityFor(key);

        return new AlertDefinition
        {
            Fingerprint = FingerprintFor(key, condition),
            Severity = condition.Severity,
            Title = condition.Title,
            Description =
                $"{condition.Meaning} vCenter '{key.Source}' reported it" +
                $"{On(track.SubjectName, key.Instance)} at {raise.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC" +
                $"{Repeats(track)}: \"{raise.Message}\" {HowItCloses(condition)}",
            Category = condition.Category,
            Source = Platform,
            Entity = entity,
            IsDerived = true,
        };
    }

    private static string On(string? name, string? instance) =>
        (name, instance) switch
        {
            (null, null) => string.Empty,
            (null, { } i) => $" for {i}",
            ({ } n, null) => $" on '{n}'",
            ({ } n, { } i) => $" on '{n}' for {i}",
        };

    private static string Repeats(Track track) =>
        track.Reports > 1
            ? $" (the latest of {track.Reports.ToString(CultureInfo.InvariantCulture)} reports in the last {Hours(track.Condition.TimeToLive)})"
            : string.Empty;

    private static string HowItCloses(EventCondition condition) =>
        condition.ClearedBy.Count > 0
            ? $"This closes when vCenter reports {string.Join(" or ", condition.ClearedBy)} for the same " +
              $"object, or {Hours(condition.TimeToLive)} after the last report if that never arrives."
            : $"vCenter sends no event when this ends, so it stays open for {Hours(condition.TimeToLive)} " +
              "after the last report and then closes by itself; clear it sooner once it has been checked.";

    private static string Hours(TimeSpan span) =>
        $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours";

    /// <summary>Whether <paramref name="a"/> came after <paramref name="b"/>.</summary>
    /// <remarks>
    /// Time first, then key: vCenter numbers its events in order, and two in
    /// the same second are ordered by the number.
    /// </remarks>
    private static bool IsNewer(SourceEvent a, SourceEvent b) =>
        a.CreatedAtUtc != b.CreatedAtUtc ? a.CreatedAtUtc > b.CreatedAtUtc : a.Key > b.Key;

    /// <summary>
    /// Keyed on the reference, not the name: a host renamed between two events
    /// is the same host, and its restore must still close its loss.
    /// </summary>
    private readonly record struct TrackKey(
        string ConditionId, string Source, string? MoRef, string? Instance);

    private sealed class Track(EventCondition condition)
    {
        public EventCondition Condition { get; } = condition;

        /// <summary>The subject's name as the newest report gave it.</summary>
        public string? SubjectName { get; set; }

        public SourceEvent? LastRaise { get; set; }

        public SourceEvent? LastClear { get; set; }

        public int Reports { get; set; }
    }
}
