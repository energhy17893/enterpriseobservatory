using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// In-memory stores, for tests about composition rather than storage.
/// </summary>
/// <remarks>
/// <para>
/// These tests used a real SQLite store opened in memory, which was free while
/// the engine was a file. PostgreSQL has no in-process mode, and making this
/// suite require a database server would be the wrong trade: these are tests
/// about what the product decides, not about how it writes it down.
/// </para>
/// <para>
/// The split is deliberate and the other half matters. Every claim about
/// durability — that an acknowledgement survives a cycle, that a lockout
/// survives a restart, that a timestamp returns as it went in — lives in
/// EnterpriseObservatory.Persistence.Postgres.Tests and runs against a real
/// server. A fake proving durability would be proving it about itself.
/// </para>
/// </remarks>
internal sealed class InMemoryUserAccountStore : IUserAccountStore
{
    /// <summary>
    /// The same guarantee the real store makes, for the same reason.
    /// </summary>
    /// <remarks>
    /// PostgresUserAccountStore holds the row with <c>FOR UPDATE</c> from the
    /// read to the commit, so that a burst of sign-in attempts increments the
    /// failure counter once each rather than once between them. A fake without
    /// an equivalent would let the lockout tests pass while the lockout was
    /// defeatable by posting the guesses at the same moment, which is the one
    /// thing the lockout is for.
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, UserAccount> _accounts = new(StringComparer.Ordinal);

    public bool Any
    {
        get
        {
            lock (_gate)
            {
                return _accounts.Count > 0;
            }
        }
    }

    public UserAccount? Find(string username)
    {
        lock (_gate)
        {
            return _accounts.GetValueOrDefault(UserAccount.Normalize(username));
        }
    }

    public IReadOnlyList<UserAccount> All()
    {
        lock (_gate)
        {
            return [.. _accounts.Values.OrderBy(a => a.Username, StringComparer.Ordinal)];
        }
    }

    public bool TryAdd(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        lock (_gate)
        {
            return _accounts.TryAdd(account.Username, account);
        }
    }

    public UserAccount? Mutate(string username, Func<UserAccount, UserAccount> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            // Applied to the account as stored, never to a copy the caller
            // brought with it.
            if (!_accounts.TryGetValue(UserAccount.Normalize(username), out var stored))
            {
                return null;
            }

            var next = change(stored) with
            {
                Username = stored.Username,
                CreatedUtc = stored.CreatedUtc,
            };

            _accounts[stored.Username] = next;

            return next;
        }
    }

    public bool Remove(string username)
    {
        lock (_gate)
        {
            return _accounts.Remove(UserAccount.Normalize(username));
        }
    }
}

internal sealed class InMemoryEntityGraphStore : IEntityGraphStore
{
    public EntityGraph Current { get; private set; } = new();

    public void Replace(EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        Current = graph;
    }
}

/// <remarks>
/// Reconciliation replaces one scope and leaves the others alone, because
/// getting that wrong is what these tests are about: the metric cycle runs
/// every thirty seconds and the inventory cycle every five minutes, and a fake
/// that replaced everything would make a scope bug invisible here.
/// </remarks>
internal sealed class InMemoryAlertStateStore : IAlertStateStore
{
    /// <summary>
    /// The same guarantee the real store makes, for the same reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgresAlertStateStore holds a lock across each operation so that a
    /// reconciliation's read-decide-store cannot be interleaved with an
    /// operator's bulk acknowledgement. This fake did not, and the effect was
    /// a test that failed roughly once in thirty: twenty alerts left in mixed
    /// states because half an acknowledgement was overwritten by a cycle that
    /// had already read the old list.
    /// </para>
    /// <para>
    /// That is worth fixing here rather than relaxing the assertion. A fake
    /// weaker than the contract turns a genuine race into background noise,
    /// and the next real one gets dismissed as "that flaky test again".
    /// </para>
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<AlertInstance>> _instances =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<FlapHistory>> _flaps = new(StringComparer.Ordinal);

    public IReadOnlyList<AlertInstance> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances.Values.SelectMany(s => s)];
            }
        }
    }

    public IReadOnlyList<AlertInstance> InstancesIn(string scope)
    {
        lock (_gate)
        {
            return Read(scope);
        }
    }

    public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope)
    {
        lock (_gate)
        {
            return ReadFlaps(scope);
        }
    }

    public AlertReconciliationResult Reconcile(
        string scope,
        Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile)
    {
        ArgumentNullException.ThrowIfNull(reconcile);

        lock (_gate)
        {
            // Read, decide and store under one lock. Splitting them is the
            // whole defect: a cycle that reads before an acknowledgement and
            // writes after it silently undoes the acknowledgement.
            var result = reconcile(Read(scope), ReadFlaps(scope));

            // Filed exactly as it was decided, which is what
            // PostgresAlertStateStore.Store now does too. Both stores used to
            // stamp Scope here from the argument, and this fake once stamped
            // the returned result as well -- a fake STRONGER than the contract,
            // which made a cycle that handed in unscoped alerts look correct
            // here while the real store kept them unscoped until a restart. A
            // fake that is stronger hides the product's bug; a fake that is
            // weaker invents one. Neither may differ from the real store at
            // all -- and the surest way to keep two stores in step about a
            // field is for neither of them to have anything to say about it.
            _instances[scope] = [.. result.Instances];
            _flaps[scope] = [.. result.FlapHistories];

            return result;
        }
    }

    public AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            return MutateLocked(fingerprint, change);
        }
    }

    public IReadOnlyList<AlertInstance> MutateMany(
        IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(change);

        // One lock for the whole batch, which is why MutateMany exists at all
        // rather than a loop over Mutate: a cycle landing between two of them
        // leaves the operator with twenty alerts of which eleven are
        // acknowledged and nothing on screen to say which, or why.
        lock (_gate)
        {
            // Decided in full before any of it is applied, because the real
            // store does the whole batch in one transaction and so cannot
            // leave part of it behind. This fake used to apply each alert as
            // it reached it, so a change that threw on the twelfth left eleven
            // acknowledged -- weaker than the contract in exactly the way the
            // missing lock and the missing Scope stamp above were, and in
            // exactly the place the tests exist to watch.
            var pending = new List<(List<AlertInstance> Slice, int Index, AlertInstance Next)>(
                fingerprints.Count);

            foreach (var fingerprint in fingerprints)
            {
                foreach (var slice in _instances.Values)
                {
                    var index = slice.FindIndex(i => i.Fingerprint == fingerprint);

                    if (index < 0)
                    {
                        continue;
                    }

                    pending.Add((slice, index, change(slice[index])));

                    break;
                }
            }

            foreach (var (slice, index, next) in pending)
            {
                slice[index] = next;
            }

            return [.. pending.Select(p => p.Next)];
        }
    }

    public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        lock (_gate)
        {
            if (!_instances.TryGetValue(scope, out var slice))
            {
                return;
            }

            var wanted = fingerprints.ToHashSet();

            _instances[scope] =
                [.. slice.Select(i => wanted.Contains(i.Fingerprint) ? AlertLifecycle.MarkNotified(i) : i)];
        }
    }

    private List<AlertInstance> Read(string scope) =>
        _instances.TryGetValue(scope, out var slice) ? [.. slice] : [];

    private List<FlapHistory> ReadFlaps(string scope) =>
        _flaps.TryGetValue(scope, out var slice) ? [.. slice] : [];

    private AlertInstance? MutateLocked(
        AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change)
    {
        foreach (var slice in _instances.Values)
        {
            var index = slice.FindIndex(i => i.Fingerprint == fingerprint);

            if (index < 0)
            {
                continue;
            }

            var next = change(slice[index]);
            slice[index] = next;

            return next;
        }

        return null;
    }
}

internal sealed class InMemoryMaintenanceWindowStore : IMaintenanceWindowStore
{
    private readonly List<MaintenanceWindow> _windows = [];

    public IReadOnlyList<MaintenanceWindow> All() => [.. _windows];

    public IReadOnlyList<MaintenanceWindow> ActiveAt(DateTimeOffset atUtc) =>
        [.. _windows.Where(w => w.IsActiveAt(atUtc))];

    public void Add(MaintenanceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _windows.RemoveAll(w => w.Id == window.Id);
        _windows.Add(window);
    }

    public bool Remove(string id) => _windows.RemoveAll(w => w.Id == id) > 0;

    public int Forget(DateTimeOffset olderThanUtc) =>
        _windows.RemoveAll(w => w.EndUtc < olderThanUtc);
}

internal sealed class InMemoryCollectorHealthStore : ICollectorHealthStore
{
    /// <summary>
    /// The same guarantee the real store makes, for the same reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgresCollectorHealthStore guards both members with a lock because it
    /// is written by the same cycle that reads it — and there are two such
    /// cycles, running as independent tasks on different schedules. Each reads
    /// <see cref="Current"/> and then calls <see cref="Merge"/>, so the metric
    /// cycle inserting a key can resize the dictionary while the inventory
    /// cycle is enumerating it.
    /// </para>
    /// <para>
    /// A throw would be the good outcome. The bad one is a short list: a source
    /// missing from it makes SourceRunner treat it as never seen and hand the
    /// circuit breaker a fresh CollectorHealth with no failures, so a vCenter
    /// that has been refusing us all day is hammered again — and an account
    /// that was only being rate-limited gets locked out by the monitoring tool,
    /// which product principle 5 exists to forbid.
    /// </para>
    /// <para>
    /// Latent in the suite rather than absent from the product: no test has yet
    /// run the two cycles at the same moment, which is precisely how the
    /// missing lock and the missing stamp above both got in.
    /// </para>
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly Dictionary<(string, CollectorRole), CollectorHealth> _health = [];

    public IReadOnlyList<CollectorHealth> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _health.Values];
            }
        }
    }

    /// <remarks>
    /// Merged rather than replaced, like the real one: a cycle only reports on
    /// the sources it ran, and the two cycles run on different schedules.
    /// Replacing would erase the other one's findings — and a fake that did so
    /// would hide exactly that bug from these tests.
    /// </remarks>
    public void Merge(IReadOnlyList<CollectorHealth> health)
    {
        ArgumentNullException.ThrowIfNull(health);

        lock (_gate)
        {
            foreach (var entry in health)
            {
                _health[(entry.InstanceId, entry.Role)] = entry;
            }
        }
    }
}

/// <summary>Measurements, kept only as long as the test runs.</summary>
/// <remarks>
/// Raw samples only. Folding and retention are the intricate part and they are
/// tested against a real server, where the SQL that performs them can be wrong;
/// a fake that re-implemented them here would be testing this file.
/// </remarks>
internal sealed class InMemoryObservationStore : IObservationStore
{
    private readonly Dictionary<SeriesKey, List<Observation>> _series = [];

    public void Append(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        foreach (var observation in observations)
        {
            var key = new SeriesKey(
                observation.Entity, observation.Value.CounterName, observation.Value.Instance);

            if (!_series.TryGetValue(key, out var samples))
            {
                _series[key] = samples = [];
            }

            samples.RemoveAll(s => s.SampledAtUtc == observation.SampledAtUtc);
            samples.Add(observation);
        }
    }

    public SeriesResult Query(SeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_series.TryGetValue(query.Key, out var samples))
        {
            // Never recorded, which is not the same as recorded and empty.
            return new SeriesResult
            {
                Key = query.Key,
                Resolution = SeriesResolution.Raw,
                Exists = false,
            };
        }

        var points = samples
            .Where(s => s.SampledAtUtc >= query.FromUtc && s.SampledAtUtc < query.ToUtc)
            .OrderBy(s => s.SampledAtUtc)
            .Select(s => new AggregatedSample
            {
                StartUtc = s.SampledAtUtc,
                Min = s.Value.Raw,
                Max = s.Value.Raw,
                Sum = s.Value.Raw,
                Count = 1,
                Last = s.Value.Raw,
            })
            .ToList();

        return new SeriesResult
        {
            Key = query.Key,
            Resolution = SeriesResolution.Raw,
            Points = points,
            Unit = samples[^1].Value.Unit,
            Rollup = samples[^1].Value.Rollup,
            Exists = true,
        };
    }

    public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) =>
        [.. _series.Keys.Where(k => k.Entity == entity)];

    public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) => new();
}

/// <summary>Coverage, in memory.</summary>
/// <remarks>
/// Replace rather than merge, exactly as the Postgres store does: a property
/// the collector stopped asking about must stop being reported, and a fake
/// that accumulated would let a test pass against behaviour production does
/// not have.
/// </remarks>
internal sealed class InMemoryCoverageStore : ICoverageStore
{
    private readonly Dictionary<string, SourceCoverage> _coverage = new(StringComparer.Ordinal);

    public IReadOnlyList<SourceCoverage> Current => [.. _coverage.Values];

    public void Replace(
        string sourceInstanceId,
        IReadOnlyList<PropertyCoverage> coverage,
        DateTimeOffset measuredAtUtc) =>
        _coverage[sourceInstanceId] = new SourceCoverage
        {
            SourceInstanceId = sourceInstanceId,
            MeasuredAtUtc = measuredAtUtc,
            Properties = [.. coverage],
        };
}

/// <summary>Events, in memory.</summary>
/// <remarks>
/// The same rules as the Postgres store where they decide behaviour: a
/// duplicate is ignored rather than stored twice, the mark moves only with a
/// successful read and to the batch's highest key, a failure leaves the
/// mark alone, type ids are matched without regard to case, and reads are
/// capped the same way — <see cref="EventCollectionPipeline.KeepNewestPerGroup"/>
/// for <see cref="OfTypes"/>, newest first for <see cref="Find"/>.
/// </remarks>
internal sealed class InMemoryEventStore : IEventStore, IEventHistory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, long, DateTimeOffset), SourceEvent> _events = [];
    private readonly Dictionary<string, EventCursor> _cursors = new(StringComparer.Ordinal);

    public IReadOnlyList<EventCursor> Cursors
    {
        get
        {
            lock (_gate)
            {
                return [.. _cursors.Values];
            }
        }
    }

    public void Record(
        string sourceInstanceId,
        IReadOnlyList<SourceEvent> events,
        bool complete,
        DateTimeOffset readAtUtc)
    {
        lock (_gate)
        {
            foreach (var e in events)
            {
                _events.TryAdd((sourceInstanceId, e.Key, e.CreatedAtUtc), e with { SourceInstanceId = sourceInstanceId });
            }

            var previous = _cursors.GetValueOrDefault(sourceInstanceId);
            var newest = events.Count == 0 ? null : events.MaxBy(e => e.Key);

            _cursors[sourceInstanceId] = new EventCursor
            {
                SourceInstanceId = sourceInstanceId,
                Mark = newest is null
                    ? previous?.Mark
                    : new EventMark { Key = newest.Key, CreatedAtUtc = newest.CreatedAtUtc },
                LastAttemptUtc = readAtUtc,
                LastSuccessUtc = readAtUtc,
                LastGapUtc = complete ? previous?.LastGapUtc : readAtUtc,
            };
        }
    }

    public void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc)
    {
        lock (_gate)
        {
            _cursors[sourceInstanceId] =
                (_cursors.GetValueOrDefault(sourceInstanceId) ?? new EventCursor { SourceInstanceId = sourceInstanceId })
                with { LastAttemptUtc = attemptedAtUtc, LastFailure = detail };
        }
    }

    public IReadOnlyList<SourceEvent> Recent(int limit, string? sourceInstanceId = null)
    {
        lock (_gate)
        {
            return
            [
                .. _events.Values
                    .Where(e => sourceInstanceId is null || e.SourceInstanceId == sourceInstanceId)
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .ThenByDescending(e => e.Key)
                    .Take(Math.Clamp(limit, 1, EventCollectionPipeline.MaxRecent)),
            ];
        }
    }

    public int Prune(DateTimeOffset createdBeforeUtc)
    {
        lock (_gate)
        {
            var old = _events.Where(p => p.Value.CreatedAtUtc < createdBeforeUtc).Select(p => p.Key).ToList();

            foreach (var key in old)
            {
                _events.Remove(key);
            }

            return old.Count;
        }
    }

    public IReadOnlyList<SourceEvent> Find(
        string sourceInstanceId,
        IReadOnlyCollection<string> typeIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        lock (_gate)
        {
            var wanted = new HashSet<string>(typeIds, StringComparer.OrdinalIgnoreCase);

            return
            [
                .. _events.Values
                    .Where(e =>
                        e.SourceInstanceId == sourceInstanceId &&
                        wanted.Contains(e.TypeId) &&
                        e.CreatedAtUtc >= fromUtc &&
                        e.CreatedAtUtc <= toUtc)
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .ThenByDescending(e => e.Key)
                    .Take(EventCollectionPipeline.MaxMatching),
            ];
        }
    }

    public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc)
    {
        var wanted = new HashSet<string>(typeIds, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            return EventCollectionPipeline.KeepNewestPerGroup(
                _events.Values.Where(e => e.CreatedAtUtc >= createdSinceUtc && wanted.Contains(e.TypeId)));
        }
    }

    public DateTimeOffset? EarliestHeld(string sourceInstanceId)
    {
        lock (_gate)
        {
            return _events.Values
                .Where(e => e.SourceInstanceId == sourceInstanceId)
                .Select(e => (DateTimeOffset?)e.CreatedAtUtc)
                .Min();
        }
    }

    public EventCursor? Cursor(string sourceInstanceId)
    {
        lock (_gate)
        {
            return _cursors.GetValueOrDefault(sourceInstanceId);
        }
    }
}

/// <summary>Compliance findings and exceptions, held under one lock like the real store.</summary>
internal sealed class InMemoryComplianceStore : IComplianceStore
{
    private readonly Lock _gate = new();
    private List<ComplianceFinding> _findings = [];
    private readonly List<ComplianceWaiver> _exceptions = [];

    public IReadOnlyList<ComplianceFinding> Findings
    {
        get
        {
            lock (_gate)
            {
                return [.. _findings];
            }
        }
    }

    public IReadOnlyList<ComplianceWaiver> Exceptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _exceptions];
            }
        }
    }

    public void Evaluate(
        string catalogueRelease,
        DateTimeOffset nowUtc,
        Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);

        lock (_gate)
        {
            _findings =
            [
                .. _findings.Where(f => f.CatalogueRelease != catalogueRelease),
                .. evaluate([.. _findings.Where(f => f.CatalogueRelease == catalogueRelease)]),
            ];
        }
    }

    public ComplianceFinding? Mutate(
        string catalogueRelease,
        string controlId,
        EntityId entity,
        Func<ComplianceFinding, ComplianceFinding> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var index = _findings.FindIndex(f =>
                f.CatalogueRelease == catalogueRelease && f.ControlId == controlId && f.Entity == entity);

            if (index < 0)
            {
                return null;
            }

            var changed = change(_findings[index]);
            _findings[index] = changed;

            return changed;
        }
    }

    public void AddException(ComplianceWaiver exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _exceptions.Add(exception);
        }
    }

    public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc)
    {
        lock (_gate)
        {
            var index = _exceptions.FindIndex(e => e.Id == id && e.RemovedAtUtc is null);

            if (index < 0)
            {
                return false;
            }

            _exceptions[index] = _exceptions[index] with { RemovedBy = removedBy, RemovedAtUtc = removedAtUtc };
            return true;
        }
    }

    /// <summary>
    /// Always empty: unlike <see cref="PostgresComplianceStore"/>, this double
    /// does not record a transition when <see cref="Evaluate"/> changes a
    /// verdict, so there is nothing to read back. The smoke tests assert on
    /// the report's shape, not its history rows.
    /// </summary>
    public IReadOnlyList<ComplianceTransition> TransitionsSince(
        DateTimeOffset sinceUtc, string? catalogueRelease = null) => [];
}

/// <summary>SMTP settings, held only for the life of the test.</summary>
internal sealed class InMemorySmtpSettingsStore : ISmtpSettingsStore
{
    private readonly Lock _gate = new();
    private SmtpSettings _settings = new();

    public SmtpSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public void Save(SmtpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            // Same "blank means keep it" rule the real store follows -- see
            // PostgresSmtpSettingsStore.Save.
            _settings = settings.Password.IsEmpty
                ? settings with { Password = _settings.Password, PasswordSetUtc = _settings.PasswordSetUtc }
                : settings with { PasswordSetUtc = DateTimeOffset.UtcNow };
        }
    }
}

/// <summary>Report subscriptions, held only for the life of the test.</summary>
internal sealed class InMemoryReportSubscriptionStore : IReportSubscriptionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReportSubscription> _subscriptions = new(StringComparer.Ordinal);

    public IReadOnlyList<ReportSubscription> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions.Values.OrderBy(s => s.CreatedUtc)];
            }
        }
    }

    public ReportSubscription? Find(string id)
    {
        lock (_gate)
        {
            return _subscriptions.GetValueOrDefault(id);
        }
    }

    public bool Add(ReportSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            return _subscriptions.TryAdd(subscription.Id, subscription);
        }
    }

    public bool Update(ReportSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscription.Id, out var existing))
            {
                return false;
            }

            // Same guarantee the real store makes, for the same reason: see
            // IReportSubscriptionStore.Update.
            _subscriptions[subscription.Id] = subscription with
            {
                LastSentUtc = existing.LastSentUtc,
                LastError = existing.LastError,
            };
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            return _subscriptions.Remove(id);
        }
    }

    public void MarkDispatched(string id, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(id, out var existing))
            {
                _subscriptions[id] = existing with { LastSentUtc = atUtc, LastError = null };
            }
        }
    }

    public void MarkFailed(string id, string detail)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(id, out var existing))
            {
                _subscriptions[id] = existing with { LastError = detail };
            }
        }
    }
}
