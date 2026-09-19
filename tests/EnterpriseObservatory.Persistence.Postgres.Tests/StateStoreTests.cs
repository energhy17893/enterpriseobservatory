using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The state stores, against a real server.
/// </summary>
/// <remarks>
/// <para>
/// Weighted towards what changes when the engine does. SQLite had no date type
/// and no boolean, so every store spelled one in text and integers; PostgreSQL
/// has both, and the round trip is the thing most likely to be quietly wrong —
/// a timestamp that comes back an hour out, or a null that comes back as an
/// epoch, reads as data rather than as a fault.
/// </para>
/// <para>
/// A schema per test, so no test depends on another having run.
/// </para>
/// </remarks>
public class StateStoreTests : IDisposable
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

    // --- accounts ---------------------------------------------------------

    private static UserAccount Account(string name = "ertugrul") => new()
    {
        Username = name,
        Password = PasswordHash.Create(Secret.From("a long enough passphrase")),
        Role = Role.Administrator,
        CreatedUtc = T0,
    };

    [SkippableFact]
    public void An_account_survives_a_restart_with_its_verifier()
    {
        RequireDatabase();

        new PostgresUserAccountStore(_live.Database).TryAdd(Account());

        _live.Restart();

        var recovered = new PostgresUserAccountStore(_live.Database).Find("ertugrul");

        Assert.NotNull(recovered);
        Assert.Equal(Role.Administrator, recovered.Role);
        Assert.True(recovered.Password.Verify(Secret.From("a long enough passphrase")));
        Assert.Equal(T0, recovered.CreatedUtc);
    }

    [SkippableFact]
    public void A_name_already_taken_is_refused_by_the_database()
    {
        RequireDatabase();

        // Decided by the database rather than by a check followed by an
        // insert, which two callers can both pass at the same moment — and now
        // genuinely can, since two processes may serve sign-ups at once.
        var store = new PostgresUserAccountStore(_live.Database);

        Assert.True(store.TryAdd(Account()));
        Assert.False(store.TryAdd(Account()));
    }

    [SkippableFact]
    public void An_account_that_has_never_signed_in_says_never_rather_than_the_epoch()
    {
        RequireDatabase();

        // Null, not 1970. "It has never happened" and "it happened at the
        // beginning of time" read the same to a careless query and mean
        // different things.
        var store = new PostgresUserAccountStore(_live.Database);
        store.TryAdd(Account());

        var recovered = store.Find("ertugrul");

        Assert.Null(recovered!.LastSignedInUtc);
        Assert.Null(recovered.LockedUntilUtc);
    }

    [SkippableFact]
    public void A_lockout_time_round_trips_exactly()
    {
        RequireDatabase();

        // The value a sign-in attempt is compared against. An hour out either
        // way is either a lockout that never ends or one that never starts.
        var store = new PostgresUserAccountStore(_live.Database);
        var until = T0.AddMinutes(15);

        store.TryAdd(Account());
        store.Update(Account() with { FailedAttempts = 5, LockedUntilUtc = until });

        Assert.Equal(until, store.Find("ertugrul")!.LockedUntilUtc);
    }

    [SkippableFact]
    public void A_removed_account_is_gone()
    {
        RequireDatabase();

        var store = new PostgresUserAccountStore(_live.Database);
        store.TryAdd(Account());

        Assert.True(store.Remove("ertugrul"));
        Assert.Null(store.Find("ertugrul"));
        Assert.False(store.Any);
    }

    [SkippableFact]
    public void A_lockout_survives_a_restart()
    {
        RequireDatabase();

        // Held only in memory, restarting the service would be the way past it.
        // Moved here from the host suite when SQLite went: the claim is about
        // what the database keeps, and a fake proving it would be proving it
        // about itself.
        var clock = new TestClock(T0);
        var accounts = new PostgresUserAccountStore(_live.Database);
        var authentication = new AuthenticationService(accounts, clock);

        accounts.TryAdd(Account() with { Role = Role.Viewer });

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        _live.Restart();

        var afterRestart = new AuthenticationService(
            new PostgresUserAccountStore(_live.Database), clock);

        Assert.Equal(
            SignInFailure.LockedOut,
            afterRestart.SignIn("ertugrul", Secret.From("a long enough passphrase")).Failure);
    }

    /// <summary>A clock the test owns, so a lockout window is not wall-clock.</summary>
    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    // --- collector health -------------------------------------------------

    private static CollectorHealth Health(
        CollectorRole role = CollectorRole.Inventory, int failures = 0) => new()
    {
        InstanceId = "vc-1",
        Role = role,
        Health = HealthState.Unknown,
        ConsecutiveFailures = failures,
    };

    [SkippableFact]
    public void What_the_breaker_decides_with_survives_a_restart()
    {
        RequireDatabase();

        // Without these, a restart re-opens the gates: the cooldown has nothing
        // to count from and the one-strike rule forgets that the password was
        // rejected, so the service comes back up and starts guessing again.
        new PostgresCollectorHealthStore(_live.Database).Merge([
            Health(failures: 1) with
            {
                LastAttemptUtc = T0.AddMinutes(-2),
                LastFailureKind = CollectionFailureKind.AuthenticationRejected,
                LastFailureDetail = "vCenter rejected the credentials.",
                IsBackingOff = true,
            },
        ]);

        _live.Restart();

        var recovered = Assert.Single(new PostgresCollectorHealthStore(_live.Database).Current);

        Assert.Equal(T0.AddMinutes(-2), recovered.LastAttemptUtc);
        Assert.Equal(CollectionFailureKind.AuthenticationRejected, recovered.LastFailureKind);
        Assert.True(recovered.IsBackingOff);
    }

    [SkippableFact]
    public void Everything_a_collector_could_not_read_survives_a_restart()
    {
        RequireDatabase();

        new PostgresCollectorHealthStore(_live.Database).Merge([
            Health(CollectorRole.Observation) with
            {
                Health = HealthState.Warning,
                PartialFailures =
                [
                    new PartialFailure
                    {
                        Kind = CollectionFailureKind.ProtocolError,
                        Target = "datastore.totalLatency.average",
                        Detail = "No such counter on this vCenter.",
                    },
                    new PartialFailure
                    {
                        Kind = CollectionFailureKind.InsufficientDetailLevel,
                        Target = "storagePath.totalReadLatency.average",
                        Detail = "Requires statistics level 3.",
                    },
                ],
            },
        ]);

        _live.Restart();

        var recovered = Assert.Single(new PostgresCollectorHealthStore(_live.Database).Current);

        Assert.Equal(2, recovered.PartialFailures.Count);
        Assert.Contains(recovered.PartialFailures, f => f.Target == "datastore.totalLatency.average");
    }

    [SkippableFact]
    public void A_problem_that_stopped_happening_is_removed_rather_than_accumulated()
    {
        RequireDatabase();

        // Written afresh each cycle. A list that only ever grows stops being
        // read, and a statistics level somebody fixed would be reported as
        // broken forever.
        var store = new PostgresCollectorHealthStore(_live.Database);

        store.Merge([
            Health(CollectorRole.Observation) with
            {
                PartialFailures =
                [
                    new PartialFailure
                    {
                        Kind = CollectionFailureKind.ProtocolError,
                        Target = "a.counter",
                        Detail = "No such counter.",
                    },
                ],
            },
        ]);

        store.Merge([Health(CollectorRole.Observation)]);

        _live.Restart();

        Assert.Empty(Assert.Single(new PostgresCollectorHealthStore(_live.Database).Current)
            .PartialFailures);
    }

    [SkippableFact]
    public void The_two_roles_of_one_source_are_stored_separately()
    {
        RequireDatabase();

        // They fail independently (ADR-0009), and a key that ignored the role
        // would quietly attribute a metrics problem to the inventory reader.
        var store = new PostgresCollectorHealthStore(_live.Database);

        store.Merge([
            Health(CollectorRole.Inventory, failures: 4),
            Health(CollectorRole.Observation, failures: 0),
        ]);

        _live.Restart();

        var recovered = new PostgresCollectorHealthStore(_live.Database).Current;

        Assert.Equal(2, recovered.Count);
        Assert.Equal(4, recovered.Single(c => c.Role == CollectorRole.Inventory).ConsecutiveFailures);
    }

    [SkippableFact]
    public void A_collector_that_never_succeeded_is_stored_as_never()
    {
        RequireDatabase();

        // Null, not the epoch. "It has never worked" and "it last worked in
        // 1970" read the same to a careless query and mean different things.
        var store = new PostgresCollectorHealthStore(_live.Database);

        store.Merge([Health(failures: 1)]);
        _live.Restart();

        Assert.Null(Assert.Single(new PostgresCollectorHealthStore(_live.Database).Current)
            .LastSuccessUtc);
    }
}
