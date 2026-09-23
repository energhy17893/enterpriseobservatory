using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Alerts;

/// <summary>
/// Which sources answered this cycle, and who owns an entity — the evidence
/// behind every verdict about an entity (ADR-0026 design note §1.3).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the silent-source carry. It lists the sources that
/// <em>answered</em>, not the ones that did not, so the forgetful value is the
/// safe one: an empty list means nothing answered, and a verdict resting on an
/// entity of a source that did not answer becomes
/// <see cref="UnknownReason.SourceSilent"/> — never a resolution.
/// </para>
/// <para>
/// An entity whose owner is not known (purged from the graph, or never in it)
/// is not clamped: nothing says its source was silent.
/// </para>
/// </remarks>
public sealed record EvidenceSources
{
    /// <summary>Sources that returned a snapshot or a batch this cycle.</summary>
    public required IReadOnlyCollection<string> Reporting { get; init; }

    /// <summary>Which source an entity belongs to, or null when it is not known.</summary>
    public required Func<EntityId, string?> OwnerOf { get; init; }

    /// <summary>
    /// Whether an entity is gone from a source that answered: marked vanished
    /// in the graph. Its alerts resolve as <see cref="AbsenceKind.SubjectRemoved"/>,
    /// never as "condition cleared" (design note §2).
    /// </summary>
    /// <remarks>
    /// "Nothing vanished" by default, which is the safe answer: it retires
    /// nothing. A purged entity is not claimed here — it is not in the graph,
    /// and not being in the graph is also what an entity never read looks like.
    /// </remarks>
    public Func<EntityId, bool> IsVanished { get; init; } = static _ => false;

    /// <summary>
    /// For a scope whose alerts belong to no source, such as compaction: nothing
    /// reported, and nothing is owned.
    /// </summary>
    public static EvidenceSources None { get; } = new() { Reporting = [], OwnerOf = static _ => null };

    /// <summary>The source that owns <paramref name="entity"/> and did not report, if there is one.</summary>
    internal string? SilentOwnerOf(EntityId? entity) =>
        entity is { } id &&
        OwnerOf(id) is { } owner &&
        !Reporting.Contains(owner, StringComparer.Ordinal)
            ? owner
            : null;
}

/// <summary>
/// A direct producer's signature that it ran this cycle, and which stored
/// alerts it speaks for (ADR-0026).
/// </summary>
/// <remarks>
/// <para>
/// A direct producer — a collector, a store write, a rule's failure guard —
/// is two-valued at N = 1: what it ran and did not report is gone. That holds
/// only for a producer that <em>ran</em>. One that did not (a write that was
/// not attempted because there was nothing to write, a source that was not
/// polled) has told us nothing, and its alerts stay open and go stale.
/// </para>
/// <para>
/// So a producer signs: without a signature that speaks for an alert, the
/// alert is "not reported" — the same flipped default rules have. Forgetting
/// to sign keeps an alarm open; it cannot close one.
/// </para>
/// </remarks>
public sealed record ProducerRun
{
    /// <summary>Who ran: for the detail an operator reads, never for matching.</summary>
    public required string Producer { get; init; }

    /// <summary>Whether a stored alert is one this producer would have raised.</summary>
    public required Func<AlertFingerprint, bool> SpeaksFor { get; init; }

    /// <summary>A producer whose alerts are exactly these fingerprints.</summary>
    public static ProducerRun For(string producer, params IEnumerable<AlertFingerprint> fingerprints)
    {
        var own = fingerprints.ToHashSet();

        return new ProducerRun { Producer = producer, SpeaksFor = own.Contains };
    }

    /// <summary>A producer whose alerts are the ones <paramref name="speaksFor"/> recognises.</summary>
    public static ProducerRun Where(string producer, Func<AlertFingerprint, bool> speaksFor) =>
        new() { Producer = producer, SpeaksFor = speaksFor };
}

/// <summary>
/// Everything one reconciliation pass needs.
/// </summary>
/// <remarks>
/// <see cref="Observed"/>, <see cref="Evaluations"/> and <see cref="Stored"/>
/// must describe the same scope. Slicing happens at the store — see
/// <c>AlertScopes</c>.
/// </remarks>
public sealed record AlertReconciliationRequest
{
    /// <summary>
    /// The evaluation this pass is reconciling. See <c>AlertScopes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required, and required for a reason that is not tidiness. Until this
    /// existed the scope was stamped onto alerts by whoever remembered to —
    /// the two collection pipelines did, the analysis rules did not, and both
    /// stores stamped again on the way in to cover for them. Three places
    /// spelling out the same fact is three places to disagree, and they did:
    /// a rule's alert read "" from the live cache and "observation" from the
    /// database row beside it, so the answer depended on how long the process
    /// had been up.
    /// </para>
    /// <para>
    /// Now it is said once, here, and <see cref="AlertReconciler"/> applies it
    /// to everything it returns.
    /// </para>
    /// </remarks>
    public required string Scope { get; init; }

    /// <summary>
    /// What the direct producers found this cycle: collection alerts, store
    /// failures, rule failures, compaction. Two-valued at N = 1.
    /// </summary>
    /// <remarks>
    /// Its alert not being here is its absence only when its producer signed
    /// <see cref="ProducersRun"/> — and even then not when the alert is about
    /// an entity of a source that did not report, which is unknown.
    /// </remarks>
    public IReadOnlyList<AlertDefinition> Observed { get; init; } = [];

    /// <summary>
    /// The direct producers that ran this cycle (ADR-0026). A stored alert with
    /// no rule that none of them speaks for is "not reported": open, stale.
    /// </summary>
    /// <remarks>
    /// Empty by default, and the empty list is the safe one: it resolves no
    /// direct producer's alert. Flap-derived alerts need no signature — this
    /// reconciler re-derives them itself every cycle.
    /// </remarks>
    public IReadOnlyList<ProducerRun> ProducersRun { get; init; } = [];

    /// <summary>
    /// Every rule registered in the product, across scopes. An open alert of a
    /// rule not in it resolves as <see cref="AbsenceKind.RuleRetired"/> (K2's
    /// moved alarms, M3.3's remote logging); null retires nothing.
    /// </summary>
    public IReadOnlyCollection<string>? RegisteredRules { get; init; }

    /// <summary>
    /// What each rule of this scope concluded, with its N (ADR-0026).
    /// </summary>
    /// <remarks>
    /// An alert a rule holds and gives no verdict about is
    /// <see cref="UnknownReason.NotReported"/>: kept open, marked stale. An
    /// empty list therefore resolves nothing a rule holds, which is the point
    /// — "pass an empty list" used to mean "everything cleared" (#63).
    /// </remarks>
    public required IReadOnlyList<RuleEvaluation> Evaluations { get; init; }

    /// <summary>Who answered this cycle, for the source clamp.</summary>
    public required EvidenceSources Sources { get; init; }

    /// <summary>
    /// How long an alert may go without fresh evidence before it moves to
    /// <see cref="AlertLifecycleState.Unknown"/>: raw retention, read from the
    /// retention policy rather than copied (ADR-0017, ADR-0026).
    /// </summary>
    public required TimeSpan RawRetention { get; init; }

    /// <summary>
    /// The age clamp: a verdict whose evidence is older than this is
    /// <see cref="UnknownReason.InputStale"/>. Null leaves it off.
    /// </summary>
    /// <remarks>
    /// 2 × the scope's interval + its read budget, computed from the collection
    /// policy and never set as a number of its own (design note §1.3, Z3).
    /// </remarks>
    public TimeSpan? EvidenceLimit { get; init; }

    /// <summary>Instances currently stored for this scope.</summary>
    public IReadOnlyList<AlertInstance> Stored { get; init; } = [];

    /// <summary>Flap counters, which outlive the instances they describe.</summary>
    public IReadOnlyList<FlapHistory> FlapHistories { get; init; } = [];

    public IReadOnlyList<MaintenanceWindow> MaintenanceWindows { get; init; } = [];

    public HysteresisPolicy Hysteresis { get; init; } = HysteresisPolicy.Default;

    public FlapPolicy Flap { get; init; } = FlapPolicy.Default;

    public required DateTimeOffset NowUtc { get; init; }
}

/// <summary>What the caller must persist and act on.</summary>
public sealed record AlertReconciliationResult
{
    /// <summary>The full set of instances after this cycle.</summary>
    /// <remarks>
    /// Every one of them carries <see cref="AlertReconciliationRequest.Scope"/>,
    /// whatever the definition it came from said and whatever the stored
    /// instance said before. A store may therefore file this result exactly as
    /// it is given it, and must.
    /// </remarks>
    public IReadOnlyList<AlertInstance> Instances { get; init; } = [];

    /// <summary>Instances that should be deleted from storage. Their history stays.</summary>
    public IReadOnlyList<AlertFingerprint> Retired { get; init; } = [];

    /// <summary>
    /// Instances owing a notification that is not suppressed.
    /// </summary>
    /// <remarks>
    /// Each carries its <see cref="AlertInstance.PendingNotification"/> so the
    /// dispatcher can choose a channel: paging someone for "it got better"
    /// is how notification fatigue starts.
    /// </remarks>
    public IReadOnlyList<AlertInstance> ToNotify { get; init; } = [];

    public IReadOnlyList<FlapHistory> FlapHistories { get; init; } = [];

    /// <summary>Instances visible in the alert inbox right now.</summary>
    public IReadOnlyList<AlertInstance> Visible =>
        [.. Instances.Where(i => i.IsVisible)];

    /// <summary>
    /// How many <c>alert_history</c> rows this cycle's write will append.
    /// </summary>
    /// <remarks>
    /// Computed the same way <c>PostgresAlertStateStore.AppendHistory</c>
    /// decides what to write — a confirmed instance whose episode matches the
    /// one stored appends only the transitions past what is already there;
    /// a new episode appends its whole history; an unconfirmed instance
    /// appends nothing (Package D). Kept here rather than read back from
    /// storage so the count is available to a caller that never touches the
    /// database, and so a runaway write path shows up on the very cycle it
    /// happens rather than after the next query.
    /// </remarks>
    public int TransitionsAppended { get; init; }

    /// <summary>
    /// How many verdicts this cycle were clamped to <see cref="AlertLifecycleState.Unknown"/>
    /// by the age rule (ADR-0026 §Z3: evidence older than 2 × the scope's
    /// interval plus its read budget), rather than by a source going silent.
    /// </summary>
    /// <remarks>
    /// Counted at the fingerprint-decision level, not per instance, so it
    /// reflects what the rule actually judged this cycle. See
    /// <see cref="UnknownReason.InputStale"/>.
    /// </remarks>
    public int AgeClampedToUnknown { get; init; }
}

/// <summary>
/// Folds one cycle's verdicts into the stored alert state.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no clock, no storage, no dispatch. The caller supplies the time and
/// persists the outcome, which keeps the whole of this behaviour testable
/// without infrastructure.
/// </para>
/// <para>
/// This is the single place alert state is advanced, and the single place an
/// alert's scope is decided. See ADR-0007.
/// </para>
/// <para>
/// Three-valued since ADR-0026. Each fingerprint gets one decision for the
/// cycle: present, absent or unknown. Present wins over unknown, and unknown
/// over absent — when two verdicts disagree about the same fingerprint, the
/// one that cannot fabricate a resolution is kept. Only an absent decision
/// can resolve, and only at the rule's N.
/// </para>
/// </remarks>
public static class AlertReconciler
{
    public static AlertReconciliationResult Reconcile(AlertReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Blank is the value the defect actually had, so it is the one worth
        // refusing. `required` stops a caller forgetting the field; this stops
        // a caller passing a scope it never worked out, which would file real
        // alerts under a slice no cycle ever reconciles — they would be
        // invisible in every inbox and never resolve.
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Evaluations);
        ArgumentNullException.ThrowIfNull(request.Sources);

        var stored = request.Stored.ToDictionary(i => i.Fingerprint);
        var flaps = request.FlapHistories.ToDictionary(f => f.Fingerprint);
        var decisions = Decide(request, stored);

        // Count this cycle's cessations before deciding what is flapping, so a
        // signal that stops on this very cycle is judged on current evidence.
        // A cessation is a resolution (or an unconfirmed alert forgotten), not
        // the first absence: an alert "resolving 1/3" has not stopped firing.
        var absences = new Dictionary<AlertFingerprint, AbsenceResult>();

        foreach (var (fingerprint, instance) in stored)
        {
            if (decisions.GetValueOrDefault(fingerprint) is not Gone gone)
            {
                continue;
            }

            var absence = AlertLifecycle.OnAbsent(instance, gone.Absence, gone.Resolution, request.NowUtc);
            absences[fingerprint] = absence;

            // Derived alerts are excluded: tracking the instability of the
            // instability detector recurses and tells an operator nothing.
            if (instance.IsDerived || !absence.CeasedFiring)
            {
                continue;
            }

            var history = flaps.TryGetValue(fingerprint, out var existing)
                ? existing
                : new FlapHistory
                {
                    Fingerprint = fingerprint,
                    ObjectName = instance.Title,
                    Scope = request.Scope,
                };

            flaps[fingerprint] = history.RecordCeased(request.NowUtc, request.Flap);
        }

        // A signal that will not hold still is a problem in its own right, and
        // one that leaves no instance behind. It is re-derived every cycle and
        // then treated exactly like anything a direct producer reported.
        foreach (var history in flaps.Values)
        {
            var derived = FlapDetection.Evaluate(history, request.NowUtc, request.Flap);
            if (derived is not null)
            {
                decisions[derived.Fingerprint] = new Seen(derived, request.NowUtc, null);
                absences.Remove(derived.Fingerprint);
            }
        }

        var next = new Dictionary<AlertFingerprint, AlertInstance>();
        var retired = new List<AlertFingerprint>();

        foreach (var (fingerprint, decision) in decisions)
        {
            stored.TryGetValue(fingerprint, out var existing);

            switch (decision)
            {
                case Seen seen:
                    next[fingerprint] = AlertLifecycle.OnObserved(
                        existing,
                        seen.Definition,
                        request.Hysteresis,
                        request.NowUtc,
                        request.MaintenanceWindows,
                        seen.EvidenceAtUtc) with
                    {
                        RuleId = seen.RuleId ?? existing?.RuleId,
                    };
                    break;

                case Gone gone when existing is not null:
                    Keep(absences.TryGetValue(fingerprint, out var absence)
                        ? absence
                        : AlertLifecycle.OnAbsent(existing, gone.Absence, gone.Resolution, request.NowUtc));
                    break;

                // Unknown never opens: with nothing stored, there is nothing to do.
                case Blind blind when existing is not null:
                    Keep(AlertLifecycle.OnUnknown(existing, blind.Unknown, request.RawRetention, request.NowUtc));
                    break;
            }

            void Keep(AbsenceResult result)
            {
                if (result.Instance is null)
                {
                    retired.Add(fingerprint);
                }
                else
                {
                    next[fingerprint] = result.Instance;
                }
            }
        }

        // Stamped once, on the way out, over everything: an instance that
        // reaches a caller without passing through this line does not exist.
        var instances = next.Values
            .Select(i => i with { Scope = request.Scope })
            .ToList();

        return new AlertReconciliationResult
        {
            Instances = instances,
            Retired = retired,
            ToNotify = [.. instances.Where(i => i.ShouldNotify)],
            FlapHistories = [.. flaps.Values.Select(f => f with { Scope = request.Scope })],
            TransitionsAppended = instances.Sum(i => AppendedRowCount(i, stored.GetValueOrDefault(i.Fingerprint))),
            AgeClampedToUnknown = decisions.Values.Count(
                d => d is Blind { Unknown.Reason: UnknownReason.InputStale }),
        };
    }

    /// <summary>
    /// How many <c>alert_history</c> rows writing <paramref name="instance"/>
    /// would append, mirroring <c>PostgresAlertStateStore.AppendHistory</c>
    /// exactly so the count matches what actually gets written.
    /// </summary>
    private static int AppendedRowCount(AlertInstance instance, AlertInstance? previous)
    {
        if (!instance.IsConfirmed)
        {
            return 0;
        }

        var from = previous is { IsConfirmed: true } && previous.FirstSeenUtc == instance.FirstSeenUtc
            ? Math.Min(previous.History.Count, instance.History.Count)
            : 0;

        return from >= instance.History.Count ? 0 : instance.History.Count - from;
    }

    /// <summary>One decision per fingerprint, from every producer and every stored instance.</summary>
    private static Dictionary<AlertFingerprint, Decision> Decide(
        AlertReconciliationRequest request, Dictionary<AlertFingerprint, AlertInstance> stored)
    {
        var decisions = new Dictionary<AlertFingerprint, Decision>();

        void Offer(AlertFingerprint fingerprint, Decision decision)
        {
            if (!decisions.TryGetValue(fingerprint, out var current) || Outranks(decision, current))
            {
                decisions[fingerprint] = decision;
            }
        }

        // Info never enters the lifecycle. Dropping it here rather than at each
        // call site means a new alert source cannot forget to.
        foreach (var alert in request.Observed.Where(a => AlertLifecycle.EntersLifecycle(a.Severity)))
        {
            Offer(alert.Fingerprint, new Seen(alert, request.NowUtc, null));
        }

        foreach (var evaluation in request.Evaluations)
        {
            foreach (var verdict in evaluation.Verdicts)
            {
                switch (Clamp(verdict, request))
                {
                    case ConditionPresent present:
                        var raised = present.Alerts
                            .Where(a => AlertLifecycle.EntersLifecycle(a.Severity))
                            .ToList();

                        foreach (var alert in raised)
                        {
                            Offer(alert.Fingerprint, new Seen(alert, present.EvidenceAtUtc, evaluation.RuleId));
                        }

                        // Covered and not raised: this verdict says it is absent.
                        foreach (var fingerprint in present.Covers.Except(raised.Select(a => a.Fingerprint)))
                        {
                            Offer(fingerprint, new Gone(
                                new AlertAbsence { EvidenceAtUtc = present.EvidenceAtUtc },
                                evaluation.Resolution));
                        }

                        break;

                    case ConditionAbsent absent:
                        foreach (var fingerprint in absent.Covers)
                        {
                            Offer(fingerprint, new Gone(
                                new AlertAbsence { EvidenceAtUtc = absent.EvidenceAtUtc, Because = absent.Because },
                                absent.Resolution ?? evaluation.Resolution));
                        }

                        break;

                    case Unknown unknown:
                        foreach (var fingerprint in unknown.Covers)
                        {
                            Offer(fingerprint, new Blind(
                                new AlertUnknown { Reason = unknown.Reason, Detail = unknown.Detail }));
                        }

                        break;
                }
            }
        }

        var roster = request.Evaluations.Select(e => e.RuleId).ToHashSet(StringComparer.Ordinal);

        foreach (var (fingerprint, instance) in stored)
        {
            if (decisions.ContainsKey(fingerprint))
            {
                continue;
            }

            // A resolved alert of a rule that is no longer registered (K2's
            // four, M3.3's remote logging) is over and nothing will ever speak
            // for it again: it retires, and its history stays. An open one is
            // left "not reported" below, so the path that retires the rule —
            // ContinuityAlarmTransition for K2 — decides how it closes.
            if (instance is { RuleId: { } retiredRule, State: AlertLifecycleState.Resolved } &&
                !roster.Contains(retiredRule))
            {
                decisions[fingerprint] = new Gone(
                    new AlertAbsence { EvidenceAtUtc = request.NowUtc }, ResolutionPolicy.Immediate);
                continue;
            }

            // An open alert of a rule that is registered nowhere: nothing will
            // ever speak for it again, so it is over -- as "rule retired", not
            // as "condition cleared". Only with the roster in hand (a request
            // without it retires nothing), and only once it has already gone a
            // cycle unreported: the path that retired the rule gets that cycle
            // to close it its own way (ContinuityAlarmTransition's "moved to
            // compliance finding" for K2's four).
            if (instance.RuleId is { } unregistered &&
                request.RegisteredRules is { } registered &&
                !registered.Contains(unregistered, StringComparer.Ordinal) &&
                (instance.State == AlertLifecycleState.Unknown ||
                 instance is { IsStale: true, StaleReason: UnknownReason.NotReported }))
            {
                decisions[fingerprint] = new Gone(
                    new AlertAbsence { EvidenceAtUtc = request.NowUtc, Because = AbsenceKind.RuleRetired },
                    ResolutionPolicy.Immediate);
                continue;
            }

            decisions[fingerprint] = instance.RuleId is { } ruleId
                // The flipped default: a rule that said nothing about an alert
                // it holds has not found the condition gone.
                ? new Blind(new AlertUnknown
                {
                    Reason = UnknownReason.NotReported,
                    Detail = $"'{ruleId}' gave no verdict about this alert this cycle",
                })
                : DirectProducer(request, instance);
        }

        // A subject gone from a source that answered: whatever was said or
        // not said about it, the alert is over as "subject removed". Only a
        // present verdict outranks it -- something is still being seen.
        foreach (var (fingerprint, instance) in stored)
        {
            if (instance.Entity is { } entity &&
                request.Sources.IsVanished(entity) &&
                decisions.GetValueOrDefault(fingerprint) is not Seen)
            {
                decisions[fingerprint] = new Gone(
                    new AlertAbsence { EvidenceAtUtc = request.NowUtc, Because = AbsenceKind.SubjectRemoved },
                    ResolutionPolicy.Immediate);
            }
        }

        return decisions;
    }

    /// <summary>
    /// A direct producer's alert that was not reported this cycle.
    /// </summary>
    /// <remarks>
    /// Absent (N = 1) only when a producer that speaks for it signed the cycle
    /// as run — "ran and did not see it". A producer that did not run has not
    /// looked, and its alert stays open and stale; so does one about an entity
    /// of a source that did not answer, for the reason EntityGraph.Merge keeps
    /// that source's entities. Flap-derived alerts are this reconciler's own,
    /// re-derived above every cycle, so it is always their producer.
    /// </remarks>
    private static Decision DirectProducer(AlertReconciliationRequest request, AlertInstance instance)
    {
        var ran = FlapDetection.IsDerivedFingerprint(instance.Fingerprint) ||
                  request.ProducersRun.Any(p => p.SpeaksFor(instance.Fingerprint));

        if (!ran)
        {
            return new Blind(new AlertUnknown
            {
                Reason = UnknownReason.NotReported,
                Detail = "no producer that raises this alert ran this cycle",
            });
        }

        // Owned by the source that raised it (ADR-0027): one that answered and
        // did not see it again has looked, whoever owns the entity. SimpliVity
        // raises on a VM vSphere owns; a silent vCenter says nothing about it.
        var raiserAnswered = instance.Source.Length > 0 &&
                             request.Sources.Reporting.Contains(instance.Source, StringComparer.Ordinal);

        return !raiserAnswered && request.Sources.SilentOwnerOf(instance.Entity) is { } silent
            ? new Blind(new AlertUnknown
            {
                Reason = UnknownReason.SourceSilent,
                Detail = $"source '{silent}' did not report this cycle",
            })
            : new Gone(new AlertAbsence { EvidenceAtUtc = request.NowUtc }, ResolutionPolicy.Immediate);
    }

    /// <summary>
    /// The two structural clamps (design note §1.3): a verdict that rests on a
    /// silent source, or on evidence older than the scope allows, is unknown.
    /// </summary>
    /// <remarks>
    /// Applied to present verdicts as well as absent ones: "unknown never
    /// opens" means a clamped present cannot raise a new alert either.
    /// </remarks>
    private static SubjectVerdict Clamp(SubjectVerdict verdict, AlertReconciliationRequest request)
    {
        var evidence = verdict switch
        {
            ConditionPresent present => present.EvidenceAtUtc,
            ConditionAbsent absent => absent.EvidenceAtUtc,
            _ => (DateTimeOffset?)null,
        };

        if (evidence is not { } at)
        {
            return verdict;
        }

        // Nothing answered at all: nothing this cycle is evidence of anything,
        // whether or not the verdict names an entity. This is the restart
        // symptom's cycle — no observations, no reporting sources.
        if (request.Sources.Reporting.Count == 0)
        {
            return Unknown(verdict, UnknownReason.SourceSilent, "no source reported this cycle");
        }

        if (request.Sources.SilentOwnerOf(verdict.Entity) is { } silent)
        {
            return Unknown(verdict, UnknownReason.SourceSilent, $"source '{silent}' did not report this cycle");
        }

        if (request.EvidenceLimit is { } limit && request.NowUtc - at > limit)
        {
            return Unknown(
                verdict,
                UnknownReason.InputStale,
                $"evidence from {at:u} is older than the {limit} this scope allows");
        }

        return verdict;
    }

    private static Unknown Unknown(SubjectVerdict verdict, UnknownReason reason, string detail) => new()
    {
        // A present verdict's raised fingerprints are covered too, whether or
        // not the rule listed them.
        Covers = verdict is ConditionPresent present
            ? [.. present.Covers.Union(present.Alerts.Select(a => a.Fingerprint))]
            : verdict.Covers,
        Entity = verdict.Entity,
        Reason = reason,
        Detail = detail,
    };

    private static bool Outranks(Decision candidate, Decision current) => (candidate, current) switch
    {
        // Two readings of one problem are one problem; the worse one wins
        // rather than whichever producer happened to run last.
        (Seen a, Seen b) => a.Definition.Severity > b.Definition.Severity,
        _ => Rank(candidate) > Rank(current),
    };

    private static int Rank(Decision decision) => decision switch
    {
        Seen => 3,
        Blind => 2,
        _ => 1,
    };

    private abstract record Decision;

    private sealed record Seen(AlertDefinition Definition, DateTimeOffset EvidenceAtUtc, string? RuleId) : Decision;

    private sealed record Gone(AlertAbsence Absence, ResolutionPolicy Resolution) : Decision;

    private sealed record Blind(AlertUnknown Unknown) : Decision;
}
