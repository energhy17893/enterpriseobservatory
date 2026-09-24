# ADR-0026: three-valued analysis rules (design note)

Status: **approved with review changes** (PR #69, 22 September 2026). The planner's three
mandatory changes are in: Z1, N measured (§3); Z2, vanished subjects (§2, §3); Z3, the age
clamp (§1.3). The answers are recorded in §5. This is a companion to
[ADR-0026](../adr/0026-evaluation-is-three-valued.md), which was accepted on 22 September
2026 together with the carry-forward limit (2 days).

**Evidence.** At least seven of the 14 remaining rules contain a live instance of the #63
class, and neither `Unevaluated` nor the silent-source carry reaches it. In the others, a
missing counter also resolves the alert. That is the ADR's "a third scope will forget it the
same way" argument, shown in the rules we already have (§3, the rows marked "today …
resolves").
Scope: the `IAnalysisRule` contract, `AlertReconciler`, `AlertLifecycle`, `MonitoringCycle`,
the alert schema, and the 14 rules that stay in `Application/Analysis`. K2 is moving the four
M8 continuity rules (HA, DRS, multipath, N+1) to compliance findings, whose verdict type
(`CheckVerdict`) already has three values (ADR-0024). Those four are not converted here.
Constraint: **no stored alert changes state because of the deploy.** The migration leaves
every open alert open.

## 0. What exists today (read from `main` + `docs/reference-principles`)

- `IAnalysisRule.Evaluate(RuleContext) → IReadOnlyList<AlertDefinition>`. A rule reports only
  the conditions it found. When a rule says nothing about a fingerprint, reconciliation
  treats that as "the problem is gone".
- Three patches keep "we did not look" from being read as "gone". Each one depends on the
  caller remembering to use it:
  - `RuleContext.Unevaluated` (an `ICollection<AlertFingerprint>`). Only
    `DatastoreTimeToFull`, `ClusterHighAvailability` and `ClusterNPlusOne` add to it.
  - `AlertReconciliationRequest.SilentSources` + `SourceOf`. These carry forward the alerts
    of an entity whose source did not answer. In the inventory scope this has existed since
    PR #37. The metric scope called `CarryForward([], null, unevaluated)` until **#63
    (5a1e109)** wired it in.
  - `GuardedRule`. A rule that throws becomes an "Analysis rule failed" alert, and every
    alert that rule held is **resolved without being rechecked**. The class's own remarks
    call this "the cost of this design".
- `AlertLifecycle.OnAbsent` resolves a confirmed alert on the **first** cycle it is absent
  (`ConditionCleared`). No hysteresis applies to closing an alert.
- On restart, `PostgresAlertStateStore` loads every instance in its constructor. The store
  is a singleton, so the state is already restored before the first cycle. What goes wrong
  is the first cycle's behaviour: a subject missing from that cycle is resolved, and its
  next observation opens it again as `Returned` and notifies.

## 1. The result type

### 1.1 Shape

```csharp
namespace EnterpriseObservatory.Application.Analysis;

/// What one rule concluded about one subject in one evaluation. Exactly three kinds;
/// the base is closed so a fourth cannot be added outside this file.
public abstract record SubjectVerdict
{
    private protected SubjectVerdict() { }

    /// Every fingerprint this rule can raise about this subject. The verdict speaks for
    /// all of them: a fingerprint listed here and not raised is absent (Present) or
    /// unknown (Unknown). Example: StorageLayerSplit covers three fingerprints per device.
    public required IReadOnlyList<AlertFingerprint> Covers { get; init; }

    /// The entity the subject belongs to, when there is one. Used by the freshness clamp (1.3).
    public EntityId? Entity { get; init; }
}

public sealed record ConditionPresent : SubjectVerdict
{
    /// One or more; each fingerprint must be in Covers.
    public required IReadOnlyList<AlertDefinition> Alerts { get; init; }
    /// Time of the newest input this verdict rests on (sample time, entity LastSeenUtc, event time).
    public required DateTimeOffset EvidenceAtUtc { get; init; }
}

public sealed record ConditionAbsent : SubjectVerdict
{
    public required DateTimeOffset EvidenceAtUtc { get; init; }
    /// Why the condition is not there. The history records it; it never changes the count.
    public AbsenceKind Because { get; init; } = AbsenceKind.ConditionCleared;
    public AlertFingerprint? SupersededBy { get; init; }   // with Because = Superseded
}

public sealed record Unknown : SubjectVerdict
{
    public required UnknownReason Reason { get; init; }
    public required string Detail { get; init; }   // e.g. "cpu.costop.summation not in this cycle's batch"
}

public enum AbsenceKind { ConditionCleared, SubjectRemoved, Superseded, Expired }

/// The ADR's four reasons plus three that the rules need. Every one means "we did not look"
/// or "we cannot tell". None of them is a verdict about the estate.
public enum UnknownReason
{
    NotReported,        // default: the rule said nothing about a subject it holds an alert for
    SourceSilent,       // source did not answer this cycle          ("kaynak cevap vermedi")
    InputNotCollected,  // counter/property missing for this subject ("sayaç toplanmıyor")
    InsufficientSeries, // too few points, no baseline, warm-up      ("seri yetersiz")
    InputStale,         // evidence older than the scope allows      ("girdi bayat")
    NotJudgeable,       // input present but below the level the rule can judge at
                        // (low load, too few peers, host in maintenance, deferred to faults)
    RuleFailed,         // the rule threw, or one subject's read threw
}

public sealed record ResolutionPolicy
{
    /// N: consecutive fresh ConditionAbsent evaluations that resolve a confirmed alert. >= 1.
    public required int ConsecutiveAbsent { get; init; }
}

public interface IAnalysisRule
{
    string RuleId { get; }
    RuleScope Scope { get; }
    ResolutionPolicy Resolution { get; }                          // no default: mandatory per rule
    IReadOnlyList<SubjectVerdict> Evaluate(RuleContext context);
}
```

`RuleContext` loses `Unevaluated`. It gains `ReportingSources` and one helper,
`EvidenceFor(EntityId) → DateTimeOffset?`. The helper returns the entity's `LastSeenUtc`
only if its source reported in this cycle, and null otherwise. A rule uses it to produce
`Unknown(SourceSilent)` itself rather than rely on the clamp.

A rule's own "I could not read" alerts become ordinary verdicts on a subject at the rule
level: `Datastore history could not be read`, `vCPU count could not be read`. They are
**Present** when the rule is blind and **Absent** when it is not. They stay visible, and they
resolve by the same N rule as everything else.

### 1.2 What replaces `Unevaluated` and `CarryForward`

The change is that **the default is flipped**. Today, a fingerprint a rule does not mention is
absent. From now on it is `Unknown(NotReported)`. The consequences:

| Today | After |
|---|---|
| `RuleContext.Unevaluated` collection, filled by 3 of 18 rules | **deleted**. A rule returns `Unknown(reason)` for the subject, or says nothing and gets `NotReported` |
| `AlertReconciliationRequest.Unevaluated`, `SilentSources`, `SourceOf` | **deleted**. They are replaced by `required IReadOnlyList<SubjectVerdict> Verdicts`, `required IReadOnlyCollection<string> ReportingSources` and `required RuleRoster Rules` (the rule ids of this scope with their `ResolutionPolicy`) |
| `MonitoringCycle.CarryForward` record, built twice | **deleted**. `Analyse` returns verdicts and the cycle passes them on unchanged |
| `GuardedRule` catches → alerts resolve unrechecked | catches → the failure alert, and **nothing else**. The rule's stored alerts receive no verdict and so become `Unknown(NotReported)`. `GuardedRule` sets the detail to "rule failed" (`RuleFailed`) |
| `AlertLifecycle.OnAbsent(instance, now)` | **removed**. It is replaced by `OnAbsent(instance, ConditionAbsent, ResolutionPolicy, now)` and `OnUnknown(instance, Unknown, rawRetention, now)` |

**Why the "pass an empty list" pattern no longer compiles.** The two parameters that #63 got
wrong, `CarryForward([], null, …)` and the omitted `Unevaluated`, no longer exist. So the old
call fails to compile, and there is no longer any argument through which "we could not look"
can be forgotten. An empty `Verdicts` list still compiles, but now it means "nothing is known"
and resolves nothing. That makes the forgetful call the safe one. There is exactly one way to
resolve an alert: construct a `ConditionAbsent`, which requires `EvidenceAtUtc`. The removed
`OnAbsent(instance, now)` overload takes every caller that skipped the policy with it.

Alerts that come from **direct producers**, not rules, stay two-valued with N = 1. These are
collection alerts ("Collector unreachable"), `Guarded` store-write failures, the
coverage-store failure, `GuardedRule`'s own alert, the compaction scope's alerts, and alerts
derived from flapping. In each case the producer runs every cycle and is its own evidence. A
stored instance with `rule_id IS NULL` is judged that way. This is the ADR's last clause: a
kind of alert may stay two-valued, "but the type says so", and here the type is `rule_id`.

### 1.3 Two structural clamps in the reconciler

A rule could still call `ConditionAbsent` on stale data (for example, an inventory rule that
reads a silent source's entities, which the graph keeps as last read). Two clamps catch this.
Together they replace today's silent-source carry-forward:

1. **Source clamp.** A `ConditionAbsent` or `ConditionPresent` whose `Entity` belongs to a
   source outside `ReportingSources` becomes `Unknown(SourceSilent)`.
2. **Age clamp** *(Z3)*. A verdict becomes `Unknown(InputStale)` when its `EvidenceAtUtc` is
   older than **2 × scope interval + that scope's read budget**. The read budget is the
   source timeout derived from `CollectionPolicy.IntervalShare` (0.8 × interval, at least
   10 s; `ForInterval`, #67). That gives 60 + 24 = **84 s** for the metric scope and
   600 + 240 = **840 s** for the inventory scope. The limit is computed from the policy and
   is never set as a separate number. **It is not tuned without a measurement.** After
   deploy, the number of verdicts this clamp catches is a package-D self-monitoring metric.
   If it stays zero, the clamp is left as it is.

This is what "Unknown never opens" means in the code: a clamped Present cannot raise a new
alert either.

## 2. State machine

States: **(none)**; **Pending** (raised, not confirmed); **Active** (`Open`, `Acknowledged`
and `Silenced`: the operator's sub-state is kept across every row below), which is either
*fresh* or *stale*; **Unknown** (new, from the 2-day rule); **Resolved**; **Cleared**
(`Resolved` + `ClearedByOperator`; re-opens on newer evidence, see the row below).

Instance fields: `EvidenceAtUtc` (the last fresh Present/Absent), `StaleSinceUtc`,
`StaleReason`, `StaleDetail` (all null when fresh), and `ConsecutiveAbsent`. `R` is raw
retention, `SeriesRetentionPolicy.Raw`, 2 days today. It is read from options, not copied, so
that if ADR-0017 changes the retention, the limit changes with it.

| State ↓ / input → | **Present** (fresh) | **Absent** (fresh) | **Unknown** |
|---|---|---|---|
| (none) | → Pending, or Active if hysteresis is already met (`Raised`, as today) | no-op | **no-op: never opens** |
| Pending | hits+1; confirm → Active (`Confirmed`, notify) | forget; counts as a cessation for flap detection (as today) | kept; hits unchanged (neither counted nor reset). Forgotten silently once `now − EvidenceAtUtc ≥ R` |
| Active, fresh | stays; `ConsecutiveAbsent = 0`; evidence updated; escalation rules as today | `ConsecutiveAbsent + 1`; **if it reaches N → Resolved** (`ConditionCleared`, or `SubjectRemoved`, `Superseded` or `Expired` as `Because` says); otherwise it stays Active ("resolving k/N") | → **stale**: `StaleSinceUtc = now`, reason and detail set, `ConsecutiveAbsent = 0`; history event `EvidenceLost`; **no notification** |
| Active, stale | → fresh (`EvidenceReturned` event); `ConsecutiveAbsent = 0`; no notification unless the severity rose | → fresh; `ConsecutiveAbsent = 1`; resolves only if N = 1 | stays stale (reason updated). **If `now − EvidenceAtUtc ≥ R` → Unknown** (`EvidenceExpired`) |
| **Unknown** | → back to the sub-state held before (read from the last transition's `From`); `EvidenceReturned`; **not re-notified** (ADR point 5) unless escalated | → Active fresh with `ConsecutiveAbsent = 1` (the count restarts); resolves at N | stays Unknown |
| Resolved | `ConditionReturned` → Active once hysteresis confirms (as today) | retire (as today) | kept unchanged |
| Cleared | evidence no newer than the clear: stays cleared, `LastSeenUtc` updated (the clear wins its own cycle); newer evidence: a new episode, `Raised` with "cleared by <actor> at <T>, condition reported again", notified as any raise | `ConsecutiveAbsent + 1`; retire at N | kept unchanged |

Invariants the tests should pin, one test each:

- **No `Unknown`-input edge leads to Resolved or retired.** The only exception is an
  unconfirmed Pending instance that expires after R, which was never shown to anyone.
- **Resolution requires N consecutive fresh Absent verdicts.** An Unknown in between resets
  the count. N is `IAnalysisRule.Resolution`, which has no default, so a new rule without it
  does not compile.
- **Unknown never creates an instance.**
- **Restore before the first evaluation.** Stale fields and `ConsecutiveAbsent` are
  persisted, so the 2-day clock and the count both survive a restart. Contract test: store
  with open alerts → one cycle with no observations and no reporting sources → **zero
  transitions to Resolved, zero notifications**. This is the restart symptom the ADR
  describes, made impossible to reproduce.
- **The expiry check runs after the cycle's verdict.** After a product outage longer than R,
  a first cycle with fresh data applies that data. It does not first flip everything to
  Unknown.
- **What is shown.** The "open" count becomes two numbers: *fresh* and *stale*. The Unknown
  state is outside `IsVisible` and outside the open count, and is listed under its own
  filter. Every view model carries `EvidenceAtUtc`, and the API cannot return a state without
  it (ADR point 3).
- **Housekeeping.** An instance whose entity is `Vanished` (or purged) retires with
  `SubjectRemoved`. An instance whose `rule_id` is no longer registered retires with
  `RuleRetired`, which is K2's and M3.3's path. Neither is a "condition cleared". **There is
  no second limit:** an alert may stay in Unknown indefinitely. It is out of the count and
  under its own filter, and that is accepted (decision §5.6).
- **Health (ADR-0018 note, decision §5.4).** A stale Critical keeps the entity **red**, with
  a "since <date>" mark. An alert in the Unknown state makes the entity **grey, not green**.
- **Vanished subjects below the entity** *(Z2)*. `RuleContext.OpenFingerprints(RuleId)` is
  offered read-only, and using it is **optional**. A rule that does not give every one of its
  alerts a verdict leaves them `NotReported`; after 2 days they are Unknown. Neither outcome
  fabricates anything. A rule may return `Absent(SubjectRemoved)` for a device or instance
  **only if it read a fresh table and the subject is absent from it** (as path-redundancy
  does with the host's path table). A rule that did not read the table cannot say this.

## 3. Every current rule

N is **measured where the data exists and a choice where it does not** *(Z1)*. The rule is
N = max(p95 of the number of consecutive 30 s absences after which the condition came back,
the proposed value). Where there is no data, the proposed value is kept, and it is anchored
only on Grafana's 2-evaluation staleness and our 2-hit opening hysteresis. "Default" below
means the rule does not mention the subject, so it gets `Unknown(NotReported)`, with
`SourceSilent` from the clamp where it applies.

### 3.1 Measurement (live database, read-only, 22 September 2026)

Sources: `alert_transition` pairs (`ConditionCleared` followed by `ConditionReturned` on the
same fingerprint; the gap ÷ 30 s gives the number of absences) and `flap_cessation`. The
window runs from **21 Sep 18:10 to 22 Sep 09:38 (~15 h)**, because every stored instance was
first seen at 18:10:54, after the last restart. Schema version 11.

The measurement has three limits, stated rather than hidden:

1. A retired instance takes its transitions with it (`ON DELETE CASCADE`). A condition that
   resolved and was then retired left only a `flap_cessation` row, which records the
   cessation and not the return.
2. Alerts with `IsDerived` are excluded from flap tracking, so their only record is the
   transition pairs.
3. A follow-up histogram query (absences per bucket) was **refused by the session's
   permission guard for production reads** and was not run again. The p50, p95 and max
   below come from the percentile query that did run.

| Rule | Sample | p95 (absences) | Chosen N |
|---|---|---|---|
| `storage-latency-blind-spot` | 399 Cleared→Returned pairs over 30 fps | **1** (p50 = p95 = 30 s; max 26 610 s) | **2** (proposed; above the p95) |
| `peer-outliers` | 23 cessations over 21 fps; 2 recurrences, 81 s and 111 s between cessations | ≤ 2 (upper bound; n = 2 is too small for a p95) | **3** (proposed kept) |
| `storage-layer-split` | 2 cessations, 2 fps, no recurrence | no data | **3** (proposed kept) |
| `dropped-packets` | 1 cessation, never returned | no data | **3** (proposed kept) |
| `datastore-time-to-full` (over-commit) | 11 cessations; the only "returns" (4 fps) span 21 Sep 14:17 → 18:10, which is a **product outage**, not a flap | no data | **2** (proposed kept) |
| `remote-logging` | 9 alerts, 0 cleared | no data | **1** (proposed kept) |
| `fault-counters`, `cpu-contention`, `memory-pressure`, `shared-volume-latency`, `storage-noisy-neighbour`, `storage-path-redundancy`, `vcenter-events`, filling, `collection-coverage` | no alert in the window | no data | proposed kept (see the table below) |

Also in the data: `vcenter-alarm` and `vm-stale-snapshot` come from the collector
(`VsphereInventorySource`), not from a rule. They are direct producers and stay two-valued at
N = 1 (§1.2). The blind-spot result is itself evidence for the design: all 399 of its flaps
are quiet cycles, and in this design a quiet cycle is `NotJudgeable`, not an absence. N was
never going to be the fix for them; the Unknown value is. The same limit should be measured
again after deploy, from a D metric that records the return time.

| Rule (scope) | Subject → fingerprints | Absent when | Unknown when (which input is missing) | N |
|---|---|---|---|---|
| `fault-counters` (M) | (entity, device instance, counter): 1 fp | the sample arrived and `Raw == 0` | no sample for that instance this cycle: default | **6** (3 min). Faults come in bursts, and one clean 30 s window is weak evidence |
| `peer-outliers` (M) | (volume, counter): 1 fp | ≥ `MinimumVantagePoints` readings, no outlier (including worst < 5 ms) | fewer vantage readings than 3 → `NotJudgeable` (today this **resolves**) | 3 |
| `cpu-contention` (M) | host: saturated fp. VM: victim, limit and width fps. Estate: width-unreadable fp | host: usage read, and below 85 % or fewer than 2 waiting. VM victim: measured with ≥ 3 measured siblings and not ≥ 3× the median. Limit: ready and maxlimited read, not both over. Width: costop and ready read, not over-wide. Estate: every measured VM has a width. A victim or limit verdict subsumed by the host verdict → `Absent(Superseded, by host fp)` | host usage missing → `InputNotCollected`. vCPU count missing (`Sizing` null) → `InputNotCollected` for victim and width. costop or maxlimited missing → `InputNotCollected` for width or limit. Fewer than 3 measured siblings → `NotJudgeable` | 3 |
| `memory-pressure` (M) | host fp; guest fp; limit fp | all four rate counters read and none active (host: or fewer than 2 pressured guests); the guest is covered by the host verdict → `Absent(Superseded)` | any of the four rates missing → `InputNotCollected` (a missing rate is "absent, not zero" in the code today; see §5.9). Memory limit unreadable (`Sizing` null) → `InputNotCollected` for the limit fp | **4** (swapping comes in bursts) |
| `storage-layer-split` (M) | device (entity, instance): queue, kernel and array fps | all three layer readings present and none dominates | any layer counter missing → `InputNotCollected` (today `return null` **resolves**). Host deferred to faults (`DeferToFaults`) → `NotJudgeable`. Today, deferral resolves every device on that host | 3 |
| `shared-volume-latency` (M) | (volume, counter): 1 fp | ≥ 3 mounting hosts, and either not elevated on all of them or one host is a peer outlier (then `peer-outliers` speaks) | fewer than 3 mounting hosts, or ops below 10/s (latency at idle is noise) → `NotJudgeable`. Ops above the busy line → `NotJudgeable` (defers to noisy-neighbour) | 3 |
| `storage-latency-blind-spot` (M) | volume: 1 fp | any latency reading ≥ 1 ms (measurement works), or SIOC active ≥ 1 % | fewer than 3 latency readings, or load below 1 op/s (**a quiet cycle, which resolves the alert today and raises it again on the next busy cycle**) → `NotJudgeable`. Load or SIOC counter missing → `InputNotCollected` | 2 (the condition is configuration) |
| `dropped-packets` (M) | (entity, rx/tx counter): 1 fp | dropped and packets both read, packets ≥ 100/s, drop % < 1 | packets counter missing → `InputNotCollected`. Packets below 100/s → `NotJudgeable` | 3 |
| `storage-noisy-neighbour` (M) | volume: 1 fp | latency read and not slow, **or** ≥ 4 measured residents with no culprit, **or** load < 1.5× typical | fewer than 4 measured residents → `NotJudgeable`. Load counters missing → `InputNotCollected`. No baseline (`typicalRate` null: first day, or a gap longer than retention) → `InsufficientSeries`. Baseline read throws → `RuleFailed` for **that volume** (today it throws the whole rule; move to a per-volume guard, as time-to-full does) | 3 |
| `storage-path-redundancy` (I) | (host, device): lost and down fps | host Active, source reported, device paths all working. Device no longer in a **freshly read** table → `Absent(SubjectRemoved)` (Z2: allowed only because the table was read fresh) | source silent → `SourceSilent` (clamp). Host `InMaintenance` → `NotJudgeable` (today this **resolves**). Empty path table → `InputNotCollected`. Coverage cannot tell "not read" apart here, because `VsphereClient` deliberately leaves `multipathInfo` out of coverage | 2 (10 min) |
| `remote-logging` (I) | host: 1 fp | `Syslog.global.logHost` read and non-empty | setting key absent (unread; today `continue` **resolves**) → `InputNotCollected`. Source silent → `SourceSilent` | 1 (a configuration value read fresh is definitive; retires after M3.3) |
| `vcenter-events` (I) | (condition, event subject, instance): 1 fp | a clear event newer than the raise (`EvidenceAtUtc` = time of the clear). The TTL expires on a condition **without** `ClearedBy` → `Absent(Expired)`: an occurrence, which the type declares two-valued | the TTL expires on a condition **with** `ClearedBy` and no clear was seen → `Unknown(InsufficientSeries, "no clear event")`. Events for that source not read this cycle → `SourceSilent` (needs a read watermark; see §5.7) | 1 (the clear is vCenter's own statement) |
| `datastore-time-to-full` (I) | datastore: filling fp (by id) and over-commit fp (by name). Estate: history-unreadable fp | Filling: a forecast beyond 30 days, or refused for `NotFilling`, `BeyondHorizon`, `BelowUsageFloor` or `NoSignificantTrend`. Over-commit: uncommitted read and ≤ free | refused for `TooFewPoints`, `WindowTooShort` or `StepChange` → `InsufficientSeries`. History query throws → `RuleFailed` (replaces `unevaluated.Add`). Capacity not read this cycle → default / `SourceSilent`. Uncommitted `null` → `InputNotCollected` (see §5.10). **`AlreadyFull` resolves "filling" today, and is proposed as Present Critical** | 3 (filling), 2 (over-commit) |
| `collection-coverage` (I) | (source, object type, property): 1 fp | property answered by ≥ 1 object in this source's snapshot | source returned no snapshot (today: not in `bySource`, and **resolves**, because the alert has no entity and the carry cannot reach it) → `SourceSilent` | 1 (the snapshot is the evidence) |
| `cluster-high-availability` (I) | **leaving, K2** → `eo-cont.ha-*` findings | | | |
| `drs-rule-violations` (I) | **leaving, K2** → `eo-cont.drs-rule` | | | |
| `multipath-single-point-of-failure` (I) | **leaving, K2** → `eo-cont.path-single*` | | | |
| `cluster-n-plus-one` (I) | **leaving, K2** → `eo-cont.n-plus-one-*` | | | |

At least seven of the 14 remaining rules (marked "today … resolves" in the table) contain a
live instance of the #63 class that neither `Unevaluated` nor the silent-source carry reaches.
In the others, a missing counter also resolves the alert. That is the ADR's "a third scope
will forget it the same way" argument, shown in the rules we already have.

**Ordering with K2.** Land K2 PR (2) first. Then the four leaving rules and their
`Unevaluated` calls are deleted rather than converted, and K2's own migration of their open
alerts into findings runs under the old reconciler, which it was written against.

## 4. Migration of stored alert state

Schema change: **migration N (assigned at merge)**. On `main`, 11 is the latest. K2 and
package A may take the next numbers.

```sql
-- migration N (assigned at merge): three-valued evaluation (ADR-0026)
ALTER TABLE alert_instance ADD COLUMN rule_id            text        NULL;  -- NULL = direct producer, two-valued
ALTER TABLE alert_instance ADD COLUMN evidence_at_utc    timestamptz NULL;
ALTER TABLE alert_instance ADD COLUMN stale_since_utc    timestamptz NULL;
ALTER TABLE alert_instance ADD COLUMN stale_reason       text        NULL;
ALTER TABLE alert_instance ADD COLUMN stale_detail       text        NULL;
ALTER TABLE alert_instance ADD COLUMN consecutive_absent integer     NOT NULL DEFAULT 0;

UPDATE alert_instance SET evidence_at_utc = last_seen_utc;
ALTER TABLE alert_instance ALTER COLUMN evidence_at_utc SET NOT NULL;

-- rule_id from the fingerprint's last segment (the check id), which every rule sets.
UPDATE alert_instance a SET rule_id = m.rule_id
FROM (VALUES ('fault-counter', 'fault-counters'),
             ('peer-outlier', 'peer-outliers'),
             ('cpu-host-saturated', 'cpu-contention'), /* … the full map, see below */
             ('storage-latency-blind-spot', 'storage-latency-blind-spot')) AS m(check_id, rule_id)
WHERE regexp_replace(a.fingerprint, '^.*\|', '') = m.check_id;

CREATE INDEX ix_alert_rule ON alert_instance (scope, rule_id);
```

- `state` and `alert_transition.reason` are `text` without a CHECK constraint. So the new
  values need no DDL: state `Unknown`, and reasons `EvidenceLost`, `EvidenceReturned`,
  `EvidenceExpired`, `SubjectRemoved`, `Superseded`, `Expired` and `RuleRetired`. The
  schema-version guard stops an older build from reading them.
- **The check-id → rule-id map lives in code,** as a table each rule exposes. A test asserts
  that the map covers every check id every registered rule can emit. An unmapped row would
  fall back to two-valued N = 1, which is today's behaviour, so no alert would close because
  of the deploy.
- Backfilling `evidence_at_utc = last_seen_utc` starts every open alert fresh. No alert
  becomes stale or Unknown because of the migration. The first cycle after deploy decides.
- `flap_history`, `alert_transition` and maintenance windows are unchanged. The store's
  `LoadInstances` / write-through code gains the six columns. `InMemoryStores` (tests) gains
  them too.

## 5. Decisions (planner review, PR #69) and what is still open

1. **N values:** measured per Z1. The results are in §3.1, and the per-rule table uses them.
2. **Direction decides.** A fresh Absent after the Unknown state starts the counter at 1, and
   resolution still needs N. There is no Unknown→Resolved edge.
3. **Unknown resets the absent count.** "Consecutive" means an uninterrupted run of fresh
   absences.
4. **Health, yes to both** (ADR-0018 note). A stale Critical keeps the entity red with a
   "since <date>" mark. An alert in the Unknown state makes the entity grey, not green.
5. **No daily digest for now.** The Unknown count appears on package D's self-monitoring
   screen.
6. **Unknown-state lifetime:** retired at vanished or purge, and nothing else. There is no
   second limit. "Unknown forever" is acceptable, because it is out of the count and under
   its own filter.
7. **Event freshness:** this goes to F's design note as the item *"per-source event read
   watermark"*. Until then, a source whose events were not read gets `NotReported`.
8. **Vanished subject** (Z2): recorded in §2. `OpenFingerprints(RuleId)` is available and
   optional. `Absent(SubjectRemoved)` at the device or instance level is allowed only if the
   rule read a fresh table.
9. **Memory counters: measure with the probe, then decide. Not measured yet.** The question
   is which of the four ratio counters (`mem.swapinRate`, `mem.swapoutRate`,
   `mem.compressionRate`, `mem.decompressionRate`, all `.average`) never arrive for which
   hosts and VMs on this estate. What exists without a measurement:
   - The collector asks for all four on **both** hosts and VMs (`VsphereCounter.Host` and
     `VsphereCounter.VirtualMachine`).
   - The counter map lists them as host-only (§5f).
   - The counter map records that none of the four was checked against a live vCenter (§5g,
     "Doğrulandı mı: Hayır").

   Reading them, from the probe or from the live `observation` series, needs a
   production-read permission that this session did not have. **Until it is measured, Absent
   requires all four,** which is the safe definition: the worst outcome is a stale memory
   alert, never a fabricated resolution. If a counter turns out to be "never on this
   estate", Absent is defined without it and a note goes into the counter map.
10. **Datastore:** `AlreadyFull` → Present Critical is **accepted**.
    **Uncommitted `null`: Coverage cannot tell the two cases apart, as the code is today.**
    - `VsphereClient`'s coverage remarks list `summary.uncommitted` among the properties
      **deliberately left out of coverage**, "which the vSphere API documents as optional".
    - Coverage is also counted per object type (`Asked`/`Answered`), not per datastore.
    - A datastore without thin disks and a datastore whose property was not answered both
      arrive as an absent property.

    So `null` stays `Unknown(InputNotCollected)`. Telling the two apart would need the
    collector to record, per datastore, that the property was requested and returned empty.
    That is a collector change, filed as a follow-up and not guessed at here.
11. **Direct producers at N = 1:** approved. These are collection alerts (including
    `vcenter-alarm` and `vm-stale-snapshot`), store failures, rule failures, compaction and
    flap alerts.
12. **Age clamp** (Z3): 2 × interval + read budget, which is 84 s for metrics and 840 s for
    inventory. It is not tuned until the D metric shows it catching verdicts (§1.3).

**Still open:** item 9's measurement and the follow-up on item 10.

Does not: change fingerprints, change opening hysteresis, change flap detection (a cessation
is counted at resolution, not at the first absence), convert K2's four rules, or change the
screens beyond the fields and counts §2 requires. K3 and the web package decide how "stale"
and "unknown" look.

## 7. Measured: the blind spot's quiet cycles (second code PR)

**What "quiet" means.** `storage-latency-blind-spot` asks one question: is the latency
measurement working on this volume? Its inputs are the volume's latency readings (a duration
from a vantage point), the load (operations a second, summed over read and write and over
every mounting host) and SIOC's active time. A cycle is **quiet** when the load is below
`MinimumOperationsPerSecond` (1 op/s). At idle a zero latency is an honest zero, so the cycle
cannot tell whether the measurement works. That is not evidence that it does, so a quiet cycle
is `Unknown(NotJudgeable)` and not `Absent`. The rule now answers in this order:

| Order | Input | Verdict |
|---|---|---|
| 1 | no latency reading for the volume this cycle | no verdict (`NotReported`) |
| 2 | fewer than 3 latency readings | `NotJudgeable` |
| 3 | any latency ≥ 1 ms | **Absent** (the measurement works) |
| 4 | SIOC active ≥ 1 % | **Absent** (the channel is there, whatever the load) |
| 5 | no load counter | `InputNotCollected` |
| 6 | load < 1 op/s (**quiet**) | `NotJudgeable` |
| 7 | no SIOC counter | `InputNotCollected` |
| 8 | otherwise | **Present** (blind) |

Step 4 moved ahead of the load check. Before this PR both gates only silenced the rule, so
their order did not matter. Now it does: SIOC running is an answer ("the channel exists"), and
the answer does not depend on the load.

**Evidence so far.** The live database held 399 Cleared→Returned pairs for this rule in the
~15 h before 22 September (§3.1, p50 = p95 = one 30 s absence), all on quiet cycles. After the
reconnect it held 29 `ConditionCleared`. The query below reclassifies every historical
`ConditionCleared` transition of the rule with the verdict the new code would have given it.
It is read-only, and it joins `alert_history` to the raw samples of the series the rule reads.

```sql
-- ADR-0026 §7: storage-latency-blind-spot's ConditionCleared transitions,
-- reclassified under the three-valued rule. Read-only.
-- Raw samples are kept 2 days (ADR-0017): run it before the transitions'
-- samples age out, or the older ones read as "no latency reading".
WITH cleared AS (
    SELECT h.id,
           extract(epoch FROM h.at_utc)::bigint       AS at_s,
           lower(split_part(h.fingerprint, '|', 4))   AS volume   -- the fingerprint's object: the datastore id
    FROM alert_history h
    WHERE h.reason = 'ConditionCleared'
      AND (h.rule_id = 'storage-latency-blind-spot'
           OR h.fingerprint LIKE '%|storage-latency-blind-spot')
),
cycle AS (
    -- Each series' newest sample the cycle could have read: at most 60 s
    -- (two metric intervals) before the transition.
    SELECT c.id, s.counter,
           (SELECT x.value FROM sample x
             WHERE x.series_id = s.id AND x.at_utc BETWEEN c.at_s - 60 AND c.at_s
             ORDER BY x.at_utc DESC LIMIT 1) AS value
    FROM cleared c
    JOIN series s ON lower(s.entity_id) = c.volume
    WHERE s.instance <> ''                                          -- from a vantage point (a host)
      AND (s.counter LIKE 'datastore.total%Latency.average'         -- latency, ms
           OR s.counter LIKE 'datastore.number%Averaged.average'    -- load, op/s
           OR s.counter = 'datastore.siocActiveTimePercentage.average')
),
judged AS (
    SELECT c.id,
           count(y.value) FILTER (WHERE y.counter LIKE 'datastore.total%Latency.average')  AS latency_readings,
           max(y.value)   FILTER (WHERE y.counter LIKE 'datastore.total%Latency.average')  AS worst_latency_ms,
           max(y.value)   FILTER (WHERE y.counter = 'datastore.siocActiveTimePercentage.average') AS sioc_pct,
           sum(y.value)   FILTER (WHERE y.counter LIKE 'datastore.number%Averaged.average') AS load_ops
    FROM cleared c
    LEFT JOIN cycle y ON y.id = c.id
    GROUP BY c.id
)
SELECT verdict, count(*) AS transitions
FROM (
    SELECT CASE
             WHEN latency_readings = 0 THEN '0 NotReported (no latency reading in the window)'
             WHEN latency_readings < 3 THEN '1 NotJudgeable (fewer than 3 latency readings)'
             WHEN worst_latency_ms >= 1 THEN '2 Absent (latency measured)'
             WHEN sioc_pct >= 1 THEN '3 Absent (SIOC active)'
             WHEN load_ops IS NULL THEN '4 InputNotCollected (no load counter)'
             WHEN load_ops < 1 THEN '5 NotJudgeable (quiet cycle: load < 1 op/s)'
             WHEN sioc_pct IS NULL THEN '6 InputNotCollected (no SIOC counter)'
             ELSE '7 Present (still blind: the clear was not the rule)'
           END AS verdict
    FROM judged
) v
GROUP BY ROLLUP (verdict)
ORDER BY verdict NULLS LAST;   -- the NULL row is the total
```

**Result:** *to be filled in from the live run.* Rows 1 and 5 together are the transitions
that would have been `NotJudgeable`. Row 5 alone is the "quiet" answer. Rows 2 and 3 are real
absences, which still resolve at N = 2.

### 7.1 Measured after #83: `Raised` rows, and what counts as history

After #83 the live `alert_history` held **307 `Raised` rows in 43 minutes** for the blind
spot's fingerprints, with only 4 `ConditionCleared` and 2 `ConditionReturned`. Many of them were
`Open → Open, Raised`. The mechanism was that a quiet cycle made the rule call the volume
absent. An unconfirmed (Pending) alert that is absent is forgotten, and the next busy cycle
raised it again as a **new episode** with its own `Raised` row at ordinal 0. Three changes
close it:

1. The quiet cycle is `NotJudgeable` (above). An Unknown verdict keeps a Pending alert with
   its hit count unchanged, so it is no longer forgotten.
2. **An unconfirmed alert writes no durable history.** Nobody saw it. Its opening row is
   written when it is confirmed, and one forgotten before then writes nothing. The flap tables
   still record that it was unstable.
3. **A history row is a transition.** That means an episode's opening, a state change
   (`from_state ≠ to_state`), or an operator action. Same-state system events
   (`EvidenceLost`, `EvidenceReturned` while still open, `SeverityDecreased`) are no longer
   recorded. What they said stays on the instance: `stale_since_utc`, `stale_reason`,
   `stale_detail`, and the pending "improved" notification. `EvidenceExpired` (→ Unknown) and
   `EvidenceReturned` from the Unknown state are state changes and are kept. Because the
   in-memory history and the stored history are the same list, a restart cannot shift the
   ordinal the next row is written at.

Check after deploy (read-only). The expected result is `raised ≤ new_fingerprints`:

```sql
SELECT count(*) FILTER (WHERE reason = 'Raised')                          AS raised,
       count(DISTINCT fingerprint) FILTER (WHERE ordinal = 0)             AS new_fingerprints,
       count(*) FILTER (WHERE from_state = to_state AND actor IS NULL
                          AND ordinal > 0)                               AS same_state_rows
FROM alert_history
WHERE at_utc >= now() - interval '1 hour'
  AND (rule_id = 'storage-latency-blind-spot' OR fingerprint LIKE '%|storage-latency-blind-spot');
```

### 7.2 What else this PR changed, rule by rule

| Rule / path | Before | Now |
|---|---|---|
| `storage-latency-blind-spot` | quiet cycle → Absent | quiet (< 1 op/s) or < 3 readings → `NotJudgeable`; missing load or SIOC counter → `InputNotCollected`; SIOC active → Absent whatever the load |
| `memory-pressure` | a missing rate read as "no pressure" | Absent only when all four rates arrived and are calm. A missing rate → `InputNotCollected` (naming the counters). Unread sizing → `InputNotCollected` for the limit alert. A guest covered by the host verdict or by its limit verdict → `Absent(Superseded)`. A held alert whose entity sent no rate → `InputNotCollected` |
| `fault-counters` | a missing sample resolved | Absent only on a zero that was read; a missing sample gets no verdict (`NotReported`) |
| `remote-logging` | an unread setting resolved | an unread `Syslog.global.logHost` → `InputNotCollected` |
| `collection-coverage` | a silent source's alerts resolved (they have no entity, so the clamp could not reach them) | a held alert of a source with no snapshot → `SourceSilent`, said by the rule. A property nobody was asked about → `NotJudgeable` |
| `datastore-time-to-full` | `AlreadyFull` resolved "filling"; refusals were absences | full (free ≤ 0 read this cycle, or `AlreadyFull`) → **Present Critical**. Too few points, too short a window, a step, or too little of the week read → `InsufficientSeries` with the refusal's words. Not filling, beyond the horizon, below the floor or no trend → Absent. Uncommitted `null` → `InputNotCollected`. A datastore not read this cycle → no verdict. A renamed datastore's old over-commit alert → `Absent(Superseded)`. History unreadable → Present, or Absent when every history was read |
| N+1 check (compliance) and datastore time-to-full | span ≥ 7 days only | the shared `HistoryCoverage`: span ≥ 7 days **and** ≥ 80 % of the week's expected points read, at the series' tier (**product policy**). Below that the answer is not evaluated / `InsufficientSeries`, with "X% of the 7 days read, 80% needed" |
| direct producers | "silence is its absence" | a producer signs `ProducersRun`. One that ran and did not see the condition resolves at N = 1. One that did not run leaves its alerts open and stale (`NotReported`). The cycle signs the collector runner (per configured source), each snapshot's own alerts, each store write actually attempted, the detail-level check per answered batch, and every guarded rule. Compaction signs its scope. Flap-derived alerts are the reconciler's own |
| retirement | — | an entity marked vanished → `SubjectRemoved` (a present verdict still outranks it). An open alert of a rule registered nowhere → `RuleRetired`, once it has gone one cycle unreported (so that `ContinuityAlarmTransition` can still close K2's alarms as moved). Without the roster, nothing retires |
| API | — | `AlertView.EvidenceAtUtc`, `IsStale`, `StaleSinceUtc`, `StaleReason`, `StaleDetail`. The overview's `FreshOpenAlerts`, `StaleOpenAlerts`, `UnknownAlerts`. `/alerts?state=Unknown` |

No schema change. `AbsenceKind.RuleRetired` and the reason `RuleRetired` are text in
`alert_history.reason`, which has no CHECK constraint.

### 7.3 Deferred

- **Grey health (ADR-0018 note).** Entity health is not yet derived from alerts in code:
  `Entity.EffectiveHealth` still comes from the collector. "Stale Critical keeps it red with
  'since'" and "Unknown → grey" need that derivation first.
- **The other converted-mechanically rules.** `peer-outliers`, `cpu-contention`,
  `storage-layer-split`, `shared-volume-latency`, `dropped-packets`, `storage-noisy-neighbour`,
  `storage-path-redundancy` and `vcenter-events` still use `TwoValuedVerdicts`. Their §3 rows
  (NotJudgeable below a load or peer floor, and so on) are the next PR.
- **A removed vCenter's "Collector unreachable".** The runner signs only the configured
  sources, so a source removed from the configuration leaves its alert stale, and then Unknown
  after 2 days. Retiring it needs the configuration change to be an event.
- **Purged entities** are not treated as vanished, because they look like "never in the
  graph". An alert on one ends as Unknown after 2 days.
