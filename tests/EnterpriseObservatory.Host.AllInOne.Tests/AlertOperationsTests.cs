using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Persistence.Sqlite;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// What an operator can do to an alert, against the real store.
/// </summary>
/// <remarks>
/// Run through the store rather than a fake, because the thing most likely to
/// go wrong here is not the transition — the domain has those covered — but
/// what happens when an operator acts while a collection cycle is running.
/// </remarks>
public class AlertOperationsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(T0);

    private readonly ObservatoryDatabase _database = new(new SqliteStoreOptions
    {
        Path = string.Empty,
        InMemory = true,
    });

    private readonly SqliteAlertStateStore _alerts;
    private readonly AlertOperations _operations;

    public AlertOperationsTests()
    {
        _alerts = new SqliteAlertStateStore(_database);
        _operations = new AlertOperations(_alerts, _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static OperatorIdentity Ertugrul { get; } = OperatorIdentity.Verified("ertugrul");

    // --- acknowledging ----------------------------------------------------

    [Fact]
    public void Acknowledging_takes_ownership_without_hiding_the_alert()
    {
        Given(Alert("psu"));

        var result = _operations.Acknowledge(Fingerprint("psu"), Ertugrul);

        Assert.True(result.Applied);
        Assert.Equal(AlertLifecycleState.Acknowledged, result.Instance!.State);

        // Still in the inbox: somebody owning a problem is not the same as the
        // problem being over.
        Assert.True(result.Instance.IsVisible);
    }

    [Fact]
    public void Acknowledging_twice_is_not_an_argument()
    {
        // A screen refreshed twice, or two operators reaching for the same
        // alert, must not produce an error.
        Given(Alert("psu"));

        _operations.Acknowledge(Fingerprint("psu"), Ertugrul);
        var second = _operations.Acknowledge(Fingerprint("psu"), Ertugrul);

        Assert.True(second.Applied);
        Assert.Equal(AlertLifecycleState.Acknowledged, second.Instance!.State);
    }

    [Fact]
    public void Acting_on_an_alert_that_has_gone_is_reported_not_thrown()
    {
        // An operator acting from a screen thirty seconds old has not made a
        // mistake.
        var result = _operations.Acknowledge(Fingerprint("nothing"), Ertugrul);

        Assert.False(result.Applied);
        Assert.Equal(AlertActionRefusal.NotFound, result.Refusal);
    }

    // --- attribution ------------------------------------------------------

    [Fact]
    public void Who_acted_is_recorded()
    {
        Given(Alert("psu"));

        var result = _operations.Acknowledge(Fingerprint("psu"), Ertugrul);

        var step = result.Instance!.History[^1];

        Assert.Equal(AlertTransitionReason.OperatorAcknowledged, step.Reason);
        Assert.Equal("ertugrul", step.Actor);
        Assert.Equal(T0, step.AtUtc);
    }

    [Fact]
    public void An_actor_the_product_cannot_verify_is_recorded_as_unverified()
    {
        // A name we cannot verify, recorded as though we could, is worse than
        // no name: it reads as authoritative and is not. Nobody reading this
        // history later should have to know how the installation was
        // configured at the time.
        Given(Alert("psu"));

        var result = _operations.Clear(
            Fingerprint("psu"), OperatorIdentity.Unverified("192.168.10.7"));

        Assert.Equal("unverified:192.168.10.7", result.Instance!.History[^1].Actor);
    }

    // --- clearing ---------------------------------------------------------

    [Fact]
    public void A_clear_outlives_the_condition()
    {
        // Re-observing the same fault must not reopen it, or clearing a known
        // and accepted condition would be useless.
        Given(Alert("psu"));

        _operations.Clear(Fingerprint("psu"), Ertugrul);

        var observed = AlertLifecycle.OnObserved(
            _alerts.All.Single(), Definition("psu"), HysteresisPolicy.Default, T0.AddMinutes(1));

        Assert.Equal(AlertLifecycleState.Resolved, observed.State);
        Assert.True(observed.ClearedByOperator);
        Assert.Equal(AlertNotificationKind.None, observed.PendingNotification);
    }

    // --- silencing --------------------------------------------------------

    [Fact]
    public void A_silence_needs_a_deadline_in_the_future()
    {
        // Something switched off for ever is something nobody remembers to
        // switch back on, and a monitoring system full of those has quietly
        // stopped monitoring.
        Given(Alert("psu"));

        var result = _operations.Silence(Fingerprint("psu"), Ertugrul, T0.AddMinutes(-1));

        Assert.False(result.Applied);
        Assert.Equal(AlertActionRefusal.DeadlineInThePast, result.Refusal);
    }

    [Fact]
    public void A_silenced_alert_returns_on_its_own()
    {
        Given(Alert("psu"));

        _operations.Silence(Fingerprint("psu"), Ertugrul, T0.AddHours(1));

        var stillSilenced = AlertLifecycle.ExpireSilenceIfDue(_alerts.All.Single(), T0.AddMinutes(30));
        Assert.Equal(AlertLifecycleState.Silenced, stillSilenced.State);

        var returned = AlertLifecycle.ExpireSilenceIfDue(_alerts.All.Single(), T0.AddHours(2));
        Assert.Equal(AlertLifecycleState.Open, returned.State);
    }

    // --- the race the port shape exists to prevent ------------------------

    [Fact]
    public void An_acknowledgement_survives_the_next_cycle()
    {
        // The thing an operator actually relies on. Acknowledging stops the
        // notifications; if the very next collection cycle reopened the alert
        // because the condition is still there, the button would be a lie.
        Given(Alert("psu"));

        _operations.Acknowledge(Fingerprint("psu"), Ertugrul);

        var result = Cycle(T0.AddMinutes(1));

        Assert.Equal(AlertLifecycleState.Acknowledged, result.Instances.Single().State);
    }

    [Fact]
    public async Task Operator_changes_and_collection_cycles_never_interleave()
    {
        // Why the store reconciles through a callback instead of offering a
        // read and a write. With those separate, an operator acting in the gap
        // between them is silently overwritten: the button appears to work and
        // the alert reopens with nothing to show why. Holding alert state
        // across the whole decision makes the two serialise, so whichever goes
        // second wins and neither is lost.
        Given(Alert("psu"));

        var until = T0.AddHours(1);

        var work = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
            {
                Cycle(T0.AddMinutes(i));
            }
            else
            {
                _operations.Silence(Fingerprint("psu"), Ertugrul, until);
            }
        }));

        // No exception, and no half-written state: the alert is either where a
        // cycle left it or where an operator did, never a mixture.
        await Task.WhenAll(work);

        var final = Assert.Single(_alerts.All);

        Assert.True(
            final.State is AlertLifecycleState.Open or AlertLifecycleState.Silenced,
            $"Left in {final.State}, which is neither where a cycle nor an operator puts it.");

        Assert.All(final.History, step => Assert.NotEqual(default, step.AtUtc));
    }

    [Fact]
    public void A_change_is_applied_to_the_alert_as_stored_not_as_the_caller_saw_it()
    {
        // A client acting on what it last saw would otherwise undo whatever
        // happened since — including another operator's acknowledgement.
        Given(Alert("psu"));

        var stale = _alerts.All.Single();

        _operations.Acknowledge(Fingerprint("psu"), OperatorIdentity.Verified("first"));
        _operations.Silence(Fingerprint("psu"), OperatorIdentity.Verified("second"), T0.AddHours(1));

        var current = _alerts.All.Single();

        Assert.Equal(AlertLifecycleState.Open, stale.State);
        Assert.Equal(AlertLifecycleState.Silenced, current.State);

        // Both transitions are in the history, in order, with both names.
        Assert.Equal(
            ["first", "second"],
            current.History
                .Where(h => h.Actor is not null)
                .Select(h => h.Actor));
    }

    // --- fixtures ---------------------------------------------------------

    private AlertReconciliationResult Cycle(DateTimeOffset now) =>
        _alerts.Reconcile(AlertScopes.Inventory, (stored, flaps) =>
            AlertReconciler.Reconcile(new AlertReconciliationRequest
            {
                Observed = [Definition("psu")],
                Stored = stored,
                FlapHistories = flaps,
                NowUtc = now,
            }));

    private void Given(params AlertInstance[] instances) =>
        _alerts.Reconcile(AlertScopes.Inventory, (_, _) => new AlertReconciliationResult
        {
            Instances = instances,
        });

    private static AlertFingerprint Fingerprint(string id) =>
        AlertFingerprint.Create("vc-1", id, "Hardware", id, id);

    private static AlertDefinition Definition(string id) => new()
    {
        Fingerprint = Fingerprint(id),
        Severity = AlertSeverity.Critical,
        Title = id,
        Description = $"{id} is unhappy.",
        Category = "Hardware",
        Source = "vc-1",
        Scope = AlertScopes.Inventory,
    };

    private static AlertInstance Alert(string id) => new()
    {
        Fingerprint = Fingerprint(id),
        Severity = AlertSeverity.Critical,
        State = AlertLifecycleState.Open,
        Title = id,
        Description = $"{id} is unhappy.",
        Category = "Hardware",
        Source = "vc-1",
        Scope = AlertScopes.Inventory,
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0,
        LastSeenUtc = T0,
    };
}
