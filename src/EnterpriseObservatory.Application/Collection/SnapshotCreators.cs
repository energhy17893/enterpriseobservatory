using System.Globalization;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>One snapshot a stale-snapshot finding is about.</summary>
public sealed record SnapshotTaken
{
    public required string Name { get; init; }

    /// <summary>When the source says it was taken, or null when it would not say.</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }
}

/// <summary>
/// The facts behind a "snapshot left behind" alert, kept structured so that
/// who took each snapshot can be looked up after collection.
/// </summary>
/// <remarks>
/// A collector sees the snapshot tree but not the event history, and must not:
/// the history is stored state, and a collector that read the store would
/// stop being a stateless adapter. So it reports what it saw and the
/// application joins the two. See <see cref="SnapshotCreators"/>.
/// </remarks>
public sealed record SnapshotFinding
{
    /// <summary>The alert this finding belongs to.</summary>
    public required AlertFingerprint Fingerprint { get; init; }

    /// <summary>The machine's reference as the source names it, e.g. <c>vm-42</c>.</summary>
    public required string VmMoRef { get; init; }

    /// <summary>Every snapshot on the machine, oldest first.</summary>
    public IReadOnlyList<SnapshotTaken> Snapshots { get; init; } = [];
}

/// <summary>Read access to the collected event history.</summary>
/// <remarks>
/// Separate from <see cref="IEventStore"/> because the event collection
/// pipeline writes and this only reads, and a reader that cannot write is one
/// that cannot move a cursor by accident.
/// </remarks>
public interface IEventHistory
{
    /// <summary>
    /// Events of the given kinds from one source, created within the window
    /// (both ends inclusive), newest first, at most
    /// <see cref="EventCollectionPipeline.MaxMatching"/> of them.
    /// </summary>
    /// <remarks>
    /// Type ids are matched without regard to case, as
    /// <see cref="IEventReader.OfTypes"/> matches them.
    /// </remarks>
    IReadOnlyList<SourceEvent> Find(
        string sourceInstanceId,
        IReadOnlyCollection<string> typeIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc);

    /// <summary>The creation time of the oldest event still held for a source, or null when none is.</summary>
    DateTimeOffset? EarliestHeld(string sourceInstanceId);

    /// <summary>Where the source's stream stands, or null when it has never been asked.</summary>
    EventCursor? Cursor(string sourceInstanceId);
}

/// <summary>
/// Names who took each snapshot a "snapshot left behind" finding is about.
/// </summary>
/// <remarks>
/// <para>
/// The evidence is the vCenter task event. Taking a snapshot is a task,
/// <c>VirtualMachine.CreateSnapshot_Task</c>, and vCenter records a
/// <c>TaskEvent</c> for it whose <c>info.descriptionId</c> is
/// <c>VirtualMachine.createSnapshot</c> and whose <c>userName</c> is the
/// account that started it. There is no dedicated "snapshot created" event
/// class in vim25 to use instead; <c>com.vmware.vc.vm.VmStateRevertedToSnapshot</c>
/// exists for reverting, and nothing like it is documented for creating.
/// The parser files a task event under its description id — see
/// <c>VsphereEventParser</c>.
/// </para>
/// <para>
/// The match is by machine and time, because nothing else joins them: the
/// snapshot tree does not record a task key, and the task event does not
/// record the snapshot it made. See <see cref="EarliestEventBeforeSnapshot"/>.
/// </para>
/// <para>
/// When no event matches, the finding says the creator is unknown and why.
/// Events are kept thirty days, and the product may not have been running when
/// an old snapshot was taken, so a snapshot 216 days old is expected to be
/// unknown. That answer is correct; a guess would not be.
/// </para>
/// </remarks>
public static class SnapshotCreators
{
    /// <summary>The task description id vCenter gives a snapshot being taken.</summary>
    public const string CreateSnapshotTypeId = "VirtualMachine.createSnapshot";

    /// <summary>
    /// The class a task event arrives as. Only used to recognise task events
    /// recorded before their description id was kept.
    /// </summary>
    public const string TaskEventClass = "TaskEvent";

    /// <summary>
    /// How long before the snapshot's own timestamp its task event may be.
    /// <strong>This product's choice</strong>, not a cited figure.
    /// </summary>
    /// <remarks>
    /// The task event is written when the task starts; the snapshot's
    /// <c>createTime</c> is stamped as it is taken, after any quiescing, so the
    /// event is normally the earlier of the two by seconds. Five minutes
    /// leaves room for a slow quiesce without reaching back to an unrelated
    /// task. Not yet measured against a live estate.
    /// </remarks>
    public static readonly TimeSpan EarliestEventBeforeSnapshot = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after the snapshot's timestamp its task event may be.
    /// <strong>This product's choice</strong>, not a cited figure.
    /// </summary>
    /// <remarks>
    /// Should be never, but the two timestamps can come from different clocks —
    /// the host that took the snapshot and the vCenter that logged the task —
    /// and one minute covers the skew NTP normally leaves between them.
    /// </remarks>
    public static readonly TimeSpan LatestEventAfterSnapshot = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Adds who took each snapshot to the findings' alert descriptions.
    /// </summary>
    /// <remarks>
    /// Never throws. A history that cannot be read costs the attribution, not
    /// the alert: the finding is still raised, and says the history was
    /// unreadable.
    /// </remarks>
    public static InventorySnapshot Attribute(InventorySnapshot snapshot, IEventHistory? history)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SnapshotFindings.Count == 0)
        {
            return snapshot;
        }

        var sentences = Explain(snapshot.SourceInstanceId, snapshot.SnapshotFindings, history);

        return snapshot with
        {
            Alerts =
            [
                .. snapshot.Alerts.Select(alert =>
                    sentences.TryGetValue(alert.Fingerprint, out var sentence)
                        ? alert with { Description = $"{alert.Description} {sentence}" }
                        : alert),
            ],
        };
    }

    private static Dictionary<AlertFingerprint, string> Explain(
        string source,
        IReadOnlyList<SnapshotFinding> findings,
        IEventHistory? history)
    {
        var sentences = new Dictionary<AlertFingerprint, string>();

        if (history is null)
        {
            foreach (var finding in findings)
            {
                sentences[finding.Fingerprint] = Unknown(
                    finding, "this installation does not collect vCenter events");
            }

            return sentences;
        }

        Context context;
        try
        {
            context = Read(source, findings, history);
        }
#pragma warning disable CA1031 // Justified: an unreadable history costs the
        // attribution, never the stale-snapshot alert itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            foreach (var finding in findings)
            {
                sentences[finding.Fingerprint] = Unknown(
                    finding, "the event history could not be read (" + ex.Message + ")");
            }

            return sentences;
        }

        foreach (var finding in findings)
        {
            sentences[finding.Fingerprint] = Describe(finding, Match(finding, context), context);
        }

        return sentences;
    }

    /// <summary>What the history holds for one source, read once for all its findings.</summary>
    private sealed record Context(
        EventCursor? Cursor,
        DateTimeOffset? HistoryStart,
        IReadOnlyList<SourceEvent> Events);

    private static Context Read(string source, IReadOnlyList<SnapshotFinding> findings, IEventHistory history)
    {
        var cursor = history.Cursor(source);

        if (cursor?.LastSuccessUtc is null)
        {
            return new Context(cursor, null, []);
        }

        // The oldest event held is where the history begins: before it the
        // events were pruned or never read. A source read successfully that
        // holds nothing at all began, as far as can be told, at its first
        // successful read — and the last one is the only one recorded.
        var start = history.EarliestHeld(source) ?? cursor.LastSuccessUtc;

        var times = findings
            .SelectMany(f => f.Snapshots)
            .Select(s => s.CreatedAtUtc)
            .OfType<DateTimeOffset>()
            .ToList();

        if (times.Count == 0)
        {
            return new Context(cursor, start, []);
        }

        var events = history.Find(
            source,
            [CreateSnapshotTypeId, TaskEventClass],
            times.Min() - EarliestEventBeforeSnapshot,
            times.Max() + LatestEventAfterSnapshot);

        return new Context(cursor, start, events);
    }

    /// <summary>
    /// Pairs each snapshot with at most one event, and each event with at most
    /// one snapshot.
    /// </summary>
    /// <remarks>
    /// Closest pair first, over every snapshot and event on the machine. Two
    /// snapshots taken minutes apart can both have both events inside their
    /// window, and taking each snapshot's nearest event independently would
    /// let one event name the creator of both.
    /// </remarks>
    private static Dictionary<int, SourceEvent> Match(SnapshotFinding finding, Context context)
    {
        var candidates = context.Events
            .Where(e => string.Equals(e.TypeId, CreateSnapshotTypeId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.VirtualMachine?.MoRef, finding.VmMoRef, StringComparison.Ordinal))
            .ToList();

        var pairs = new List<(int Snapshot, SourceEvent Event, TimeSpan Distance)>();

        for (var i = 0; i < finding.Snapshots.Count; i++)
        {
            if (finding.Snapshots[i].CreatedAtUtc is not { } created)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (Within(candidate.CreatedAtUtc, created))
                {
                    pairs.Add((i, candidate, (created - candidate.CreatedAtUtc).Duration()));
                }
            }
        }

        var matched = new Dictionary<int, SourceEvent>();
        var used = new HashSet<SourceEvent>(ReferenceEqualityComparer.Instance);

        foreach (var (snapshot, e, _) in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Event.Key))
        {
            if (!matched.ContainsKey(snapshot) && used.Add(e))
            {
                matched[snapshot] = e;
            }
        }

        return matched;
    }

    private static bool Within(DateTimeOffset eventAt, DateTimeOffset snapshotAt) =>
        eventAt >= snapshotAt - EarliestEventBeforeSnapshot &&
        eventAt <= snapshotAt + LatestEventAfterSnapshot;

    private static string Describe(SnapshotFinding finding, Dictionary<int, SourceEvent> matched, Context context)
    {
        if (finding.Snapshots.Count == 1)
        {
            var only = finding.Snapshots[0];

            return (matched.GetValueOrDefault(0) is { } e
                ? "Taken by " + Who(e)
                : "Who took it is unknown: " + WhyUnknown(only, finding.VmMoRef, context)) + ".";
        }

        return "Who took them: " + string.Join(
            "; ",
            finding.Snapshots.Select((s, i) => matched.GetValueOrDefault(i) is { } e
                ? $"'{s.Name}' by {Who(e)}"
                : $"'{s.Name}' unknown, because {WhyUnknown(s, finding.VmMoRef, context)}")) + ".";
    }

    private static string Who(SourceEvent match)
    {
        var who = match.UserName is { Length: > 0 } user
            ? user
            : "a vCenter task that recorded no user";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{who}, per the vCenter task logged at {match.CreatedAtUtc:u}");
    }

    private static string WhyUnknown(SnapshotTaken snapshot, string vmMoRef, Context context)
    {
        if (context.Cursor is null)
        {
            return "vCenter events have not been collected from this vCenter";
        }

        if (context.HistoryStart is not { } start)
        {
            return "vCenter events have never been read successfully from this vCenter" +
                   (context.Cursor.LastFailure is { Length: > 0 } failure ? $" ({failure})" : string.Empty);
        }

        if (snapshot.CreatedAtUtc is not { } created)
        {
            return "vCenter did not report when it was taken, so no event can be matched to it";
        }

        if (created < start)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"it was taken before events were collected (the history held for this vCenter starts {start:yyyy-MM-dd})");
        }

        // A task event read before its description id was kept cannot be
        // told apart from any other task on the machine. Said rather than
        // claimed as the creation, and rather than reported as no event at all.
        if (context.Events.Any(e =>
                string.Equals(e.TypeId, TaskEventClass, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.VirtualMachine?.MoRef, vmMoRef, StringComparison.Ordinal) &&
                Within(e.CreatedAtUtc, created)))
        {
            return "a vCenter task was recorded on this machine at that time, but before task kinds were " +
                   "kept, so it is not claimed as the one that took it";
        }

        var gap = context.Cursor.LastGapUtc is { } at
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"; event reads have had to skip events, most recently at {at:u}, so it may have been among them")
            : string.Empty;

        return "no creation event was found for it in the collected history" + gap;
    }

    private static string Unknown(SnapshotFinding finding, string why) =>
        (finding.Snapshots.Count == 1 ? "Who took it is unknown: " : "Who took them is unknown: ") + why + ".";
}
