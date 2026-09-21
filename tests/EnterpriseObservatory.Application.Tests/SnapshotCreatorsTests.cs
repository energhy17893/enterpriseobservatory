using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Roadmap M2.4: the stale-snapshot finding names who took the snapshot, from
/// the vCenter task that took it, or says why it cannot.
/// </summary>
public class SnapshotCreatorsTests
{
    private const string Source = "vc-1";

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly AlertFingerprint Fingerprint = AlertFingerprint.Create(
        Source, "Snapshot left behind", "Capacity", "fileserver", "vm-stale-snapshot");

    private sealed class History : IEventHistory
    {
        public List<SourceEvent> Events { get; } = [];

        public EventCursor? CursorValue { get; set; } = new()
        {
            SourceInstanceId = Source,
            LastAttemptUtc = Now,
            LastSuccessUtc = Now,
        };

        public DateTimeOffset? Earliest { get; set; } = Now.AddDays(-30);

        public bool Throws { get; init; }

        public IReadOnlyList<SourceEvent> Find(
            string sourceInstanceId,
            IReadOnlyCollection<string> typeIds,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc) =>
            Throws
                ? throw new InvalidOperationException("database is down")
                : [.. Events
                    .Where(e =>
                        e.SourceInstanceId == sourceInstanceId &&
                        typeIds.Contains(e.TypeId, StringComparer.OrdinalIgnoreCase) &&
                        e.CreatedAtUtc >= fromUtc && e.CreatedAtUtc <= toUtc)
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .ThenByDescending(e => e.Key)
                    .Take(EventCollectionPipeline.MaxMatching)];

        public DateTimeOffset? EarliestHeld(string sourceInstanceId) => Earliest;

        public EventCursor? Cursor(string sourceInstanceId) => CursorValue;
    }

    private static SourceEvent CreateTask(
        DateTimeOffset at, string? user, string vm = "vm-1", long key = 1, string typeId = SnapshotCreators.CreateSnapshotTypeId) => new()
    {
        SourceInstanceId = Source,
        Key = key,
        CreatedAtUtc = at,
        EventClass = "TaskEvent",
        TypeId = typeId,
        Message = "Task: Create virtual machine snapshot",
        UserName = user,
        VirtualMachine = new EventObjectRef { MoRef = vm, Name = "fileserver" },
    };

    private static InventorySnapshot Inventory(params SnapshotTaken[] snapshots) => new()
    {
        SourceInstanceId = Source,
        ReadAtUtc = Now,
        Alerts =
        [
            new AlertDefinition
            {
                Fingerprint = Fingerprint,
                Severity = AlertSeverity.Warning,
                Title = "Snapshot left behind",
                Description = "'fileserver' has one snapshot 5 days old.",
            },
        ],
        SnapshotFindings =
        [
            new SnapshotFinding { Fingerprint = Fingerprint, VmMoRef = "vm-1", Snapshots = snapshots },
        ],
    };

    private static string Describe(InventorySnapshot inventory, IEventHistory? history) =>
        Assert.Single(SnapshotCreators.Attribute(inventory, history).Alerts).Description;

    [Fact]
    public void The_account_that_ran_the_snapshot_task_is_named()
    {
        var taken = Now.AddDays(-5);
        var history = new History();
        history.Events.Add(CreateTask(taken.AddSeconds(-8), @"CORP\alice"));

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }), history);

        Assert.StartsWith("'fileserver' has one snapshot 5 days old.", description, StringComparison.Ordinal);
        Assert.Contains(@"Taken by CORP\alice", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creation_event_in_another_case_still_names_the_creator()
    {
        // The history matches type ids without regard to case, as vCenter's
        // own catalogue does not keep to one; the pairing must agree with it.
        var taken = Now.AddDays(-5);
        var history = new History();
        history.Events.Add(CreateTask(taken.AddSeconds(-8), @"CORP\alice", typeId: "virtualmachine.CREATESNAPSHOT"));

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }), history);

        Assert.Contains(@"Taken by CORP\alice", description, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_creation_event_the_creator_is_unknown_and_the_reason_is_given()
    {
        // Held history covers the snapshot's time, and nothing in it matches.
        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = Now.AddDays(-5) }),
            new History());

        Assert.Contains(
            "Who took it is unknown: no creation event was found for it in the collected history",
            description,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Taken by", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_older_than_the_history_is_unknown_because_it_predates_collection()
    {
        // A 216-day-old snapshot will usually be here, and that is the
        // correct answer: events are kept 30 days and the product may not
        // have been running when it was taken.
        var history = new History { Earliest = Now.AddDays(-3) };

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "golden", CreatedAtUtc = Now.AddDays(-216) }), history);

        Assert.Contains(
            "Who took it is unknown: it was taken before events were collected (the history held for this vCenter starts 2026-09-18)",
            description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_outside_the_tolerance_is_not_claimed_as_the_creation()
    {
        var taken = Now.AddDays(-5);
        var history = new History();

        // Ten minutes before (the window reaches back five) and two minutes
        // after (the window allows one): both are some other task.
        history.Events.Add(CreateTask(taken.AddMinutes(-10), @"CORP\mallory", key: 1));
        history.Events.Add(CreateTask(taken.AddMinutes(2), @"CORP\trent", key: 2));

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }), history);

        Assert.DoesNotContain("mallory", description, StringComparison.Ordinal);
        Assert.DoesNotContain("trent", description, StringComparison.Ordinal);
        Assert.Contains("no creation event was found", description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_for_another_machine_is_not_claimed()
    {
        var taken = Now.AddDays(-5);
        var history = new History();
        history.Events.Add(CreateTask(taken.AddSeconds(-5), @"CORP\bob", vm: "vm-2"));

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }), history);

        Assert.DoesNotContain("bob", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_snapshots_on_one_machine_are_each_given_their_own_creator()
    {
        // Three minutes apart, so alice's task is inside the second snapshot's
        // window as well as the first's. Pairing closest-first keeps one event
        // from naming the creator of both.
        var first = Now.AddDays(-6);
        var second = first.AddMinutes(3);
        var history = new History();
        history.Events.Add(CreateTask(first.AddSeconds(-20), @"CORP\alice", key: 1));
        history.Events.Add(CreateTask(second.AddSeconds(-20), @"CORP\bob", key: 2));

        var description = Describe(
            Inventory(
                new SnapshotTaken { Name = "pre-patch", CreatedAtUtc = first },
                new SnapshotTaken { Name = "pre-reboot", CreatedAtUtc = second }),
            history);

        Assert.Contains(@"'pre-patch' by CORP\alice", description, StringComparison.Ordinal);
        Assert.Contains(@"'pre-reboot' by CORP\bob", description, StringComparison.Ordinal);
    }

    [Fact]
    public void One_event_never_names_the_creator_of_two_snapshots()
    {
        var first = Now.AddDays(-6);
        var second = first.AddMinutes(2);
        var history = new History();
        history.Events.Add(CreateTask(first.AddSeconds(-20), @"CORP\alice"));

        var description = Describe(
            Inventory(
                new SnapshotTaken { Name = "pre-patch", CreatedAtUtc = first },
                new SnapshotTaken { Name = "pre-reboot", CreatedAtUtc = second }),
            history);

        Assert.Contains(@"'pre-patch' by CORP\alice", description, StringComparison.Ordinal);
        Assert.Contains("'pre-reboot' unknown, because no creation event was found", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_recorded_before_task_kinds_were_kept_is_not_claimed_and_is_not_called_absent()
    {
        var taken = Now.AddDays(-4);
        var history = new History();
        history.Events.Add(CreateTask(taken.AddSeconds(-5), @"CORP\alice", typeId: SnapshotCreators.TaskEventClass));

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }), history);

        Assert.DoesNotContain("alice", description, StringComparison.Ordinal);
        Assert.Contains("before task kinds were kept", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_without_a_creation_time_says_it_cannot_be_matched()
    {
        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = null }), new History());

        Assert.Contains("vCenter did not report when it was taken", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vcenter_whose_events_were_never_read_says_so()
    {
        var history = new History { CursorValue = null, Earliest = null };

        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = Now.AddDays(-5) }), history);

        Assert.Contains("vCenter events have not been collected from this vCenter", description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_history_costs_the_attribution_and_not_the_alert()
    {
        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = Now.AddDays(-5) }),
            new History { Throws = true });

        Assert.StartsWith("'fileserver' has one snapshot", description, StringComparison.Ordinal);
        Assert.Contains("the event history could not be read (database is down)", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_event_history_the_creator_is_unknown_rather_than_omitted()
    {
        var description = Describe(
            Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = Now.AddDays(-5) }), null);

        Assert.Contains("this installation does not collect vCenter events", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inventory_pipeline_names_the_creator()
    {
        var taken = Now.AddDays(-5);
        var history = new History();
        history.Events.Add(CreateTask(taken.AddSeconds(-8), @"CORP\alice"));

        var result = await new InventoryCollectionPipeline(new FixedClock(Now), history).RunAsync(
            [new FixedSource(Inventory(new SnapshotTaken { Name = "before upgrade", CreatedAtUtc = taken }))],
            [],
            CollectionPolicy.Default with { MaxRetries = 0 },
            CancellationToken.None);

        var alert = Assert.Single(Assert.Single(result.Snapshots).Alerts);
        Assert.Contains(@"Taken by CORP\alice", alert.Description, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FixedSource(InventorySnapshot snapshot) : IInventorySource
    {
        public string InstanceId => snapshot.SourceInstanceId;

        public Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
