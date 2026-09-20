using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The graph, alert state, maintenance windows and connections, against a real
/// server.
/// </summary>
/// <remarks>
/// Alert state is the reason the persistence layer exists. Losing the graph on
/// restart costs one inventory cycle; losing this forgets every
/// acknowledgement and every record of what has already been notified, after
/// which the product comes back up and pages somebody for thirty problems it
/// has already reported.
/// </remarks>
public class GraphAndAlertTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    // --- entity graph -----------------------------------------------------

    private static EntityId Id(string name) => EntityId.For("vc-1", name);

    private static Entity Host(string name = "host-1") => new()
    {
        Id = Id(name),
        Kind = EntityKind.EsxiHost,
        DisplayName = name,
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
        Marks = [new IdentityMark(IdentityMarkKind.IpAddress, "10.0.0.1", "vc-1")],
    };

    [SkippableFact]
    public void A_graph_survives_a_restart_with_its_marks_and_edges()
    {
        RequireDatabase();

        var graph = new EntityGraph
        {
            Entities = new Dictionary<EntityId, Entity> { [Id("host-1")] = Host() },
            Relationships =
            [
                new Relationship
                {
                    From = Id("host-1"),
                    To = Id("cluster-1"),
                    Kind = RelationshipKind.PartOf,
                    ObservedAtUtc = T0,
                    Evidence = [new IdentityMark(IdentityMarkKind.IpAddress, "10.0.0.1", "vc-1")],
                },
            ],
        };

        new PostgresEntityGraphStore(_live.Database).Replace(graph);

        _live.Restart();

        var recovered = new PostgresEntityGraphStore(_live.Database).Current;

        var entity = Assert.Single(recovered.Entities.Values);
        Assert.Equal("10.0.0.1", Assert.Single(entity.Marks).Value);
        Assert.Equal(T0, entity.LastSeenUtc);

        var edge = Assert.Single(recovered.Relationships);
        Assert.Equal(RelationshipKind.PartOf, edge.Kind);
        Assert.Single(edge.Evidence);
    }

    [SkippableFact]
    public void Replacing_the_graph_removes_what_is_no_longer_there()
    {
        RequireDatabase();

        // The graph is a single decision about what exists. Applying
        // retirements by omission is how a decommissioned host stays on the
        // screen forever.
        var store = new PostgresEntityGraphStore(_live.Database);

        store.Replace(new EntityGraph
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Id("host-1")] = Host(),
                [Id("host-2")] = Host("host-2"),
            },
        });

        store.Replace(new EntityGraph
        {
            Entities = new Dictionary<EntityId, Entity> { [Id("host-1")] = Host() },
        });

        _live.Restart();

        Assert.Single(new PostgresEntityGraphStore(_live.Database).Current.Entities);
    }

    [SkippableFact]
    public void A_mark_reported_by_two_sources_is_stored_once_per_source()
    {
        RequireDatabase();

        // Two collectors reporting the same evidence is normal and neither is
        // wrong; the same collector reporting it twice is noise.
        var store = new PostgresEntityGraphStore(_live.Database);

        store.Replace(new EntityGraph
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [Id("host-1")] = Host() with
                {
                    Marks =
                    [
                        new IdentityMark(IdentityMarkKind.IpAddress, "10.0.0.1", "vc-1"),
                        new IdentityMark(IdentityMarkKind.IpAddress, "10.0.0.1", "vc-1"),
                        new IdentityMark(IdentityMarkKind.IpAddress, "10.0.0.1", "ilo-1"),
                    ],
                },
            },
        });

        _live.Restart();

        var entity = Assert.Single(new PostgresEntityGraphStore(_live.Database).Current.Entities.Values);

        Assert.Equal(2, entity.Marks.Count);
    }

    // --- alert state ------------------------------------------------------

    private static AlertInstance Alert(string name = "one", string scope = "Inventory") => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", name, "Inventory", "host-1", name),
        Scope = scope,
        Severity = AlertSeverity.Warning,
        State = AlertLifecycleState.Open,
        Title = name,
        Description = "something",
        Category = "Inventory",
        Source = "vc-1",
        Entity = Id("host-1"),
        ConsecutiveHits = 2,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
        History =
        [
            new AlertTransition
            {
                From = AlertLifecycleState.Open,
                To = AlertLifecycleState.Open,
                Reason = AlertTransitionReason.Confirmed,
                AtUtc = T0,
            },
        ],
    };

    [SkippableFact]
    public void An_alert_survives_a_restart_with_its_history()
    {
        RequireDatabase();

        // The history is the audit trail: why it fired and who closed it.
        var store = new PostgresAlertStateStore(_live.Database);

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            Instances = [Alert()],
        });

        _live.Restart();

        var recovered = Assert.Single(new PostgresAlertStateStore(_live.Database).All);

        Assert.Equal(AlertLifecycleState.Open, recovered.State);
        Assert.Equal(T0, recovered.FirstSeenUtc);
        Assert.Equal(AlertTransitionReason.Confirmed, Assert.Single(recovered.History).Reason);
    }

    [SkippableFact]
    public void An_acknowledgement_survives_the_next_cycle()
    {
        RequireDatabase();

        // The failure that makes a product untrustworthy: an operator
        // acknowledges, the next reconciliation runs, and the alert reopens
        // with nothing to show why.
        var store = new PostgresAlertStateStore(_live.Database);

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            Instances = [Alert()],
        });

        store.Mutate(Alert().Fingerprint, i => i with
        {
            State = AlertLifecycleState.Acknowledged,
        });

        var afterCycle = store.Reconcile("Inventory", (stored, _) => new AlertReconciliationResult
        {
            Instances = stored,
        });

        Assert.Equal(AlertLifecycleState.Acknowledged, Assert.Single(afterCycle.Instances).State);
    }

    [SkippableFact]
    public void One_scope_reconciling_does_not_clear_another()
    {
        RequireDatabase();

        // The metric cycle runs every thirty seconds and the inventory cycle
        // every five minutes. Without scopes the faster one would clear the
        // slower one's alerts on every pass. See ADR-0009.
        var store = new PostgresAlertStateStore(_live.Database);

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            Instances = [Alert("inventory-one", "Inventory")],
        });

        store.Reconcile("Observation", (_, _) => new AlertReconciliationResult
        {
            Instances = [Alert("metric-one", "Observation")],
        });

        store.Reconcile("Observation", (_, _) => new AlertReconciliationResult());

        Assert.Single(store.All);
        Assert.Single(store.InstancesIn("Inventory"));
        Assert.Empty(store.InstancesIn("Observation"));
    }

    [SkippableFact]
    public void A_notification_marked_as_sent_stays_sent()
    {
        RequireDatabase();

        // Otherwise the alert notifies on every cycle, forever.
        var store = new PostgresAlertStateStore(_live.Database);

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            Instances = [Alert() with { PendingNotification = AlertNotificationKind.Raised }],
        });

        store.MarkNotified("Inventory", [Alert().Fingerprint]);

        _live.Restart();

        Assert.Equal(
            AlertNotificationKind.None,
            Assert.Single(new PostgresAlertStateStore(_live.Database).All).PendingNotification);
    }

    [SkippableFact]
    public void A_bulk_acknowledgement_that_fails_to_commit_leaves_nothing_acknowledged()
    {
        RequireDatabase();

        // The cached copy must never be ahead of the record it copies. An
        // operator acknowledges twenty alerts, a statement fails part-way and
        // the transaction rolls back, so the database still has all twenty
        // open. If the cache was written as the batch walked, the request
        // returns a 500 while every screen shows those alerts acknowledged and
        // the unacknowledged count no longer includes them -- and the only
        // thing that corrects it is a restart, which puts them back with
        // nothing anywhere to explain where they went.
        //
        // The failure is injected through the change itself rather than by
        // breaking the connection, because it aborts the transaction at the
        // same place a dropped connection would: part-way through, after some
        // rows have been written and before the commit.
        var store = new PostgresAlertStateStore(_live.Database);
        var alerts = Enumerable.Range(0, 20).Select(i => Alert($"bulk-{i}")).ToArray();

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            Instances = alerts,
        });

        var reached = 0;

        Assert.Throws<InvalidOperationException>(() =>
        {
            store.MutateMany(
                [.. alerts.Select(a => a.Fingerprint)],
                instance => reached++ < 9
                    ? instance with { State = AlertLifecycleState.Acknowledged }
                    : throw new InvalidOperationException("the connection dropped"));
        });

        Assert.All(store.All, a => Assert.Equal(AlertLifecycleState.Open, a.State));

        // And the database agrees, which is the half of the claim the cache
        // cannot make for itself.
        _live.Restart();

        Assert.All(
            new PostgresAlertStateStore(_live.Database).All,
            a => Assert.Equal(AlertLifecycleState.Open, a.State));
    }

    [SkippableFact]
    public void Flap_history_survives_a_restart()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);

        store.Reconcile("Inventory", (_, _) => new AlertReconciliationResult
        {
            FlapHistories =
            [
                new FlapHistory
                {
                    Fingerprint = Alert().Fingerprint,
                    Scope = "Inventory",
                    ObjectName = "host-1",
                    CeasedAtUtc = [T0, T0.AddMinutes(5)],
                },
            ],
        });

        _live.Restart();

        var recovered = Assert.Single(
            new PostgresAlertStateStore(_live.Database).FlapHistoriesIn("Inventory"));

        Assert.Equal(2, recovered.CeasedAtUtc.Count);
        Assert.Equal(T0.AddMinutes(5), recovered.CeasedAtUtc[1]);
    }

    // --- maintenance windows ----------------------------------------------

    [SkippableFact]
    public void A_maintenance_window_survives_a_restart_with_what_it_covers()
    {
        RequireDatabase();

        new PostgresMaintenanceWindowStore(_live.Database).Add(new MaintenanceWindow
        {
            Id = "w-1",
            Title = "SAN firmware",
            Reason = "planned",
            StartUtc = T0,
            EndUtc = T0.AddHours(4),
            DeclaredBy = "ertugrul",
            DeclaredAtUtc = T0.AddMinutes(-10),
            Entities = [Id("host-1"), Id("host-2")],
        });

        _live.Restart();

        var recovered = Assert.Single(new PostgresMaintenanceWindowStore(_live.Database).All());

        Assert.Equal(2, recovered.Entities.Count);
        Assert.Equal(T0.AddHours(4), recovered.EndUtc);
        Assert.Equal("ertugrul", recovered.DeclaredBy);
    }

    [SkippableFact]
    public void A_window_covering_the_whole_estate_has_no_entities_rather_than_a_missing_list()
    {
        RequireDatabase();

        // Deliberately expressible and deliberately blunt: everything goes
        // quiet, including the failure the test was meant to reveal.
        new PostgresMaintenanceWindowStore(_live.Database).Add(new MaintenanceWindow
        {
            Id = "w-all",
            Title = "datacentre power test",
            Reason = "planned",
            StartUtc = T0,
            EndUtc = T0.AddHours(2),
            DeclaredBy = "ertugrul",
            DeclaredAtUtc = T0,
        });

        _live.Restart();

        Assert.Empty(Assert.Single(new PostgresMaintenanceWindowStore(_live.Database).All()).Entities);
    }

    // --- connections ------------------------------------------------------

    /// <summary>Reversible, and obviously not cryptography.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(Secret secret) =>
            secret.IsEmpty ? string.Empty : new string(secret.Reveal().Reverse().ToArray());

        public Secret Unprotect(string protectedValue) =>
            protectedValue.Length == 0
                ? Secret.Empty
                : Secret.From(new string(protectedValue.Reverse().ToArray()));
    }

    [SkippableFact]
    public void A_connection_survives_a_restart_and_its_password_is_not_stored_as_itself()
    {
        RequireDatabase();

        var protector = new ReversingProtector();

        new PostgresSourceConnectionStore(_live.Database, protector).Add(new SourceConnection
        {
            InstanceId = "vc-1",
            Kind = "vsphere",
            BaseAddress = new Uri("https://vc.example.local/"),
            Username = "observatory@vsphere.local",
            Password = Secret.From("hunter2"),
            AcceptUntrustedCertificate = true,
            PageSize = 100,
            CreatedUtc = T0,
            CreatedBy = "ertugrul",
            PasswordSetUtc = T0,
        });

        var stored = _live.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT password_protected FROM source_connection;";
            return (string)command.ExecuteScalar()!;
        });

        Assert.NotEqual("hunter2", stored);

        _live.Restart();

        var recovered = Assert.Single(
            new PostgresSourceConnectionStore(_live.Database, protector).All);

        Assert.Equal(Secret.From("hunter2"), recovered.Password);
        Assert.True(recovered.AcceptUntrustedCertificate);
        Assert.Equal(100, recovered.PageSize);
        Assert.Equal(T0, recovered.PasswordSetUtc);
    }

    [SkippableFact]
    public void An_empty_password_on_an_update_keeps_the_stored_one()
    {
        RequireDatabase();

        // The form cannot show a password it is not allowed to read back, so an
        // unchanged form submits an empty one. Taking that at face value would
        // break a working collector by opening a page and saving it.
        var protector = new ReversingProtector();
        var store = new PostgresSourceConnectionStore(_live.Database, protector);

        var connection = new SourceConnection
        {
            InstanceId = "vc-1",
            Kind = "vsphere",
            BaseAddress = new Uri("https://vc.example.local/"),
            Username = "observatory@vsphere.local",
            Password = Secret.From("hunter2"),
            CreatedUtc = T0,
        };

        store.Add(connection);
        store.Update(connection with { Password = Secret.Empty, PageSize = 250 });

        _live.Restart();

        var recovered = Assert.Single(
            new PostgresSourceConnectionStore(_live.Database, protector).All);

        Assert.Equal(Secret.From("hunter2"), recovered.Password);
        Assert.Equal(250, recovered.PageSize);
    }
}
