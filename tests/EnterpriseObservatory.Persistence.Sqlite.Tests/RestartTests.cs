using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Persistence.Sqlite.Tests;

/// <summary>
/// Everything here is one question asked in different ways: after the process
/// dies, is what we knew still there?
/// </summary>
/// <remarks>
/// A real file, not an in-memory database, because the whole point is what
/// happens when every connection closes. An in-memory database is destroyed at
/// exactly that moment, so it can prove a round trip but never a restart.
/// </remarks>
public class RestartTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"eo-restart-{Guid.NewGuid():n}.db");

    private ObservatoryDatabase _database;

    public RestartTests() => _database = Open();

    private ObservatoryDatabase Open() => new(new SqliteStoreOptions { Path = _path });

    /// <summary>Closes everything and opens it again, as a service restart does.</summary>
    private void Restart()
    {
        _database.Dispose();
        _database = Open();
    }

    public void Dispose()
    {
        _database.Dispose();

        foreach (var file in Directory.GetFiles(
            Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A test's leftovers in the temp directory are not worth
                // failing a test run over.
            }
        }

        GC.SuppressFinalize(this);
    }

    // --- alert state: the reason this layer exists ------------------------

    [Fact]
    public void An_acknowledgement_survives_a_restart()
    {
        // Forgetting this is how a product teaches people to ignore it: every
        // problem an operator already took ownership of comes back as new.
        var store = new SqliteAlertStateStore(_database);
        var acknowledged = AlertLifecycle.Acknowledge(Alert("psu"), "ertugrul", T0);

        Store(store, AlertScopes.Inventory, Reconciled(acknowledged));

        Restart();

        var recovered = Assert.Single(new SqliteAlertStateStore(_database).All);

        Assert.Equal(AlertLifecycleState.Acknowledged, recovered.State);
    }

    [Fact]
    public void A_problem_already_notified_does_not_notify_again_after_a_restart()
    {
        // The pending kind survives every observation until it is cleared, so
        // losing the fact that it was cleared re-pages everyone on startup.
        var store = new SqliteAlertStateStore(_database);
        var raised = Alert("psu") with { PendingNotification = AlertNotificationKind.Raised };

        Store(store, AlertScopes.Inventory, Reconciled(raised));
        store.MarkNotified(AlertScopes.Inventory, [raised.Fingerprint]);

        Restart();

        var recovered = Assert.Single(new SqliteAlertStateStore(_database).All);

        Assert.Equal(AlertNotificationKind.None, recovered.PendingNotification);
        Assert.False(recovered.ShouldNotify);
    }

    [Fact]
    public void An_operator_clear_survives_a_restart()
    {
        // A clear outlives the condition on purpose: re-observing must not
        // reopen it. That promise is only kept if the clear is durable.
        var store = new SqliteAlertStateStore(_database);

        Store(store, AlertScopes.Inventory, Reconciled(
            AlertLifecycle.Clear(Alert("psu"), "ertugrul", T0)));

        Restart();

        var recovered = Assert.Single(new SqliteAlertStateStore(_database).All);

        Assert.True(recovered.ClearedByOperator);
        Assert.Equal(AlertLifecycleState.Resolved, recovered.State);
    }

    [Fact]
    public void The_audit_trail_survives_a_restart()
    {
        // "Why did this fire and who closed it" has to stay answerable, or the
        // history is decorative.
        var store = new SqliteAlertStateStore(_database);
        var acknowledged = AlertLifecycle.Acknowledge(Alert("psu"), "ertugrul", T0.AddMinutes(1));

        Store(store, AlertScopes.Inventory, Reconciled(acknowledged));

        Restart();

        var recovered = Assert.Single(new SqliteAlertStateStore(_database).All);
        var step = recovered.History[^1];

        Assert.Equal(AlertTransitionReason.OperatorAcknowledged, step.Reason);
        Assert.Equal("ertugrul", step.Actor);
        Assert.Equal(T0.AddMinutes(1), step.AtUtc);
    }

    [Fact]
    public void Flap_counters_survive_a_restart()
    {
        // These outlive the instances they describe — that is their whole
        // purpose. A signal that starts and stops fifty times a day leaves no
        // instance behind, so losing the counter makes it perfectly invisible.
        var store = new SqliteAlertStateStore(_database);

        var history = new FlapHistory
        {
            Fingerprint = Alert("psu").Fingerprint,
            ObjectName = "psu-1",
            Scope = AlertScopes.Inventory,
            CeasedAtUtc = [T0, T0.AddMinutes(5), T0.AddMinutes(9)],
        };

        Store(store, AlertScopes.Inventory, new AlertReconciliationResult { FlapHistories = [history] });

        Restart();

        var recovered = Assert.Single(
            new SqliteAlertStateStore(_database).FlapHistoriesIn(AlertScopes.Inventory));

        Assert.Equal(3, recovered.CeasedAtUtc.Count);
        Assert.Equal(T0.AddMinutes(9), recovered.CeasedAtUtc[^1]);
    }

    [Fact]
    public void Writing_one_scope_leaves_the_other_alone_on_disk()
    {
        // The in-memory version of this rule is already tested; this one proves
        // the SQL agrees with it, which is where a DELETE without a WHERE would
        // show up.
        var store = new SqliteAlertStateStore(_database);

        Store(store, AlertScopes.Inventory, Reconciled(Alert("psu")));
        Store(store, AlertScopes.Observation, Reconciled(Alert("latency")));

        Restart();

        var recovered = new SqliteAlertStateStore(_database);

        Assert.Single(recovered.InstancesIn(AlertScopes.Inventory));
        Assert.Single(recovered.InstancesIn(AlertScopes.Observation));
    }

    [Fact]
    public void Retiring_everything_in_a_scope_actually_empties_it()
    {
        var store = new SqliteAlertStateStore(_database);

        Store(store, AlertScopes.Inventory, Reconciled(Alert("psu")));
        Store(store, AlertScopes.Inventory, new AlertReconciliationResult());

        Restart();

        Assert.Empty(new SqliteAlertStateStore(_database).All);
    }

    // --- the entity graph -------------------------------------------------

    [Fact]
    public void A_vanished_entitys_clock_is_not_reset_by_a_restart()
    {
        // ADR-0004 retains a vanished entity for thirty days measured from when
        // it was last seen. Held only in memory, every restart set that clock
        // back to zero and the tombstone never expired.
        var store = new SqliteEntityGraphStore(_database);
        var seen = T0.AddDays(-20);

        store.Replace(EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [new EntityId("h1")] = Host("h1", seen) with
                {
                    ObservationState = ObservationState.Vanished,
                },
            },
        });

        Restart();

        var recovered = Assert.Single(new SqliteEntityGraphStore(_database).Current.Entities.Values);

        Assert.Equal(seen, recovered.LastSeenUtc);
        Assert.Equal(ObservationState.Vanished, recovered.ObservationState);
        Assert.Equal(HealthState.Unknown, recovered.EffectiveHealth);
    }

    [Fact]
    public void Identity_marks_survive_a_restart()
    {
        // Without them the resolver has to rediscover every match, and until it
        // does, one physical server is two entities.
        var store = new SqliteEntityGraphStore(_database);

        store.Replace(EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [new EntityId("h1")] = Host("h1", T0) with
                {
                    Marks =
                    [
                        IdentityMark.Create(IdentityMarkKind.HardwareUuid, "ABC-123", "vc-1"),
                        IdentityMark.Create(IdentityMarkKind.Fqdn, "h1.corp.local", "vc-1"),
                    ],
                },
            },
        });

        Restart();

        var recovered = Assert.Single(new SqliteEntityGraphStore(_database).Current.Entities.Values);

        Assert.Equal(2, recovered.Marks.Count);
        Assert.Contains(recovered.Marks, m => m.Value == "abc-123");
    }

    [Fact]
    public void Relationships_and_their_evidence_survive_a_restart()
    {
        // ADR-0004 models an identity match as a retractable claim rather than
        // a merge. A claim whose evidence is gone cannot be audited, and an
        // unauditable claim cannot honestly be withdrawn.
        var store = new SqliteEntityGraphStore(_database);

        store.Replace(EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [new EntityId("h1")] = Host("h1", T0),
                [new EntityId("i1")] = Host("i1", T0),
            },
            Relationships =
            [
                new Relationship
                {
                    From = new EntityId("h1"),
                    To = new EntityId("i1"),
                    Kind = RelationshipKind.SameAs,
                    ObservedAtUtc = T0,
                    Evidence = [IdentityMark.Create(IdentityMarkKind.SerialNumber, "SN-9", "ilo-1")],
                },
            ],
        });

        Restart();

        var recovered = Assert.Single(new SqliteEntityGraphStore(_database).Current.Relationships);

        Assert.Equal(RelationshipKind.SameAs, recovered.Kind);
        Assert.Equal("sn-9", Assert.Single(recovered.Evidence).Value);
    }

    [Fact]
    public void Replacing_the_graph_removes_what_is_no_longer_in_it()
    {
        var store = new SqliteEntityGraphStore(_database);

        store.Replace(EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [new EntityId("h1")] = Host("h1", T0),
                [new EntityId("h2")] = Host("h2", T0),
            },
        });

        store.Replace(EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity> { [new EntityId("h1")] = Host("h1", T0) },
        });

        Restart();

        Assert.Single(new SqliteEntityGraphStore(_database).Current.Entities);
    }

    // --- collector health -------------------------------------------------

    [Fact]
    public void A_failure_count_survives_a_restart()
    {
        // The circuit breaker counts consecutive failures. A process that
        // forgets them comes back and hammers a collector that has been
        // refusing it all day — and an account that was merely rate-limited
        // gets locked out by the monitoring tool, which principle 5 forbids.
        var store = new SqliteCollectorHealthStore(_database);

        store.Merge([
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 7,
                LastSuccessUtc = T0.AddHours(-3),
                LastFailureDetail = "authentication rejected",
            },
        ]);

        Restart();

        var recovered = Assert.Single(new SqliteCollectorHealthStore(_database).Current);

        Assert.Equal(7, recovered.ConsecutiveFailures);
        Assert.Equal(T0.AddHours(-3), recovered.LastSuccessUtc);
        Assert.Equal("authentication rejected", recovered.LastFailureDetail);
    }

    [Fact]
    public void The_two_roles_of_one_source_are_stored_separately()
    {
        var store = new SqliteCollectorHealthStore(_database);

        store.Merge([
            Health("vc-1", CollectorRole.Inventory, failures: 4),
            Health("vc-1", CollectorRole.Observation, failures: 0),
        ]);

        Restart();

        var recovered = new SqliteCollectorHealthStore(_database).Current;

        Assert.Equal(2, recovered.Count);
        Assert.Equal(4, recovered.Single(c => c.Role == CollectorRole.Inventory).ConsecutiveFailures);
        Assert.Equal(0, recovered.Single(c => c.Role == CollectorRole.Observation).ConsecutiveFailures);
    }

    [Fact]
    public void A_collector_that_never_succeeded_is_stored_as_never()
    {
        // Null, not the epoch. "It has never worked" and "it last worked in
        // 1970" read the same to a careless query and mean different things.
        var store = new SqliteCollectorHealthStore(_database);

        store.Merge([Health("vc-1", CollectorRole.Inventory, failures: 1) with { LastSuccessUtc = null }]);

        Restart();

        Assert.Null(Assert.Single(new SqliteCollectorHealthStore(_database).Current).LastSuccessUtc);
    }

    [Fact]
    public void What_the_breaker_decides_with_survives_a_restart()
    {
        // Without these two, a restart re-opens the gates: the cooldown has
        // nothing to count from and the one-strike rule forgets that the
        // password was rejected, so the service comes back up and starts
        // guessing again. That is the lockout this whole record exists to
        // prevent, reintroduced by the act of restarting.
        var store = new SqliteCollectorHealthStore(_database);

        store.Merge([
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 1,
                LastSuccessUtc = null,
                LastAttemptUtc = T0.AddMinutes(-2),
                LastFailureKind = CollectionFailureKind.AuthenticationRejected,
                LastFailureDetail = "vCenter rejected the credentials.",
            },
        ]);

        Restart();

        var recovered = Assert.Single(new SqliteCollectorHealthStore(_database).Current);

        Assert.Equal(T0.AddMinutes(-2), recovered.LastAttemptUtc);
        Assert.Equal(CollectionFailureKind.AuthenticationRejected, recovered.LastFailureKind);
    }

    [Fact]
    public void A_failure_nobody_classified_is_stored_as_unclassified()
    {
        // Null, not a guess. Every row written before the column existed is
        // null, and reading those as "authentication rejected" would hold a
        // perfectly healthy source off after an upgrade.
        var store = new SqliteCollectorHealthStore(_database);

        store.Merge([Health("vc-1", CollectorRole.Inventory, failures: 1)]);

        Restart();

        Assert.Null(Assert.Single(new SqliteCollectorHealthStore(_database).Current).LastFailureKind);
    }

    // --- fixtures ---------------------------------------------------------

    private static AlertReconciliationResult Reconciled(params AlertInstance[] instances) =>
        new() { Instances = instances };

    /// <summary>
    /// Stores a made-up reconciliation result.
    /// </summary>
    /// <remarks>
    /// The store reconciles through a callback so that reading, deciding and
    /// writing happen without releasing alert state in between — which is what
    /// stops an operator's acknowledgement being lost to a cycle that happened
    /// to be running. Here the "decision" is simply the fixture.
    /// </remarks>
    private static void Store(
        SqliteAlertStateStore store, string scope, AlertReconciliationResult result) =>
        store.Reconcile(scope, (_, _) => result);

    private static AlertInstance Alert(string id) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Hardware", id, id),
        Severity = AlertSeverity.Critical,
        State = AlertLifecycleState.Open,
        Title = id,
        Description = $"{id} is unhappy.",
        Category = "Hardware",
        Source = "vc-1",
        Scope = AlertScopes.Inventory,
        Entity = new EntityId("h1"),
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
    };

    private static Entity Host(string id, DateTimeOffset seen) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.EsxiHost,
        DisplayName = $"{id}.corp.local",
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = seen,
    };

    private static CollectorHealth Health(string id, CollectorRole role, int failures) => new()
    {
        InstanceId = id,
        Role = role,
        Health = failures == 0 ? HealthState.Healthy : HealthState.Unknown,
        ConsecutiveFailures = failures,
        LastSuccessUtc = T0,
    };
}
