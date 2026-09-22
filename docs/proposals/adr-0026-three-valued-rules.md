# ADR-0026: three-valued analysis rules (design note)

Status: proposal, for the planner to review **before any code is written**. It is a
companion to [ADR-0026](../adr/0026-evaluation-is-three-valued.md), which was accepted on
22 September 2026 together with the carry-forward limit (2 days).
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
2. **Age clamp.** A verdict whose `EvidenceAtUtc` is older than `2 × scope interval` becomes
   `Unknown(InputStale)`. The intervals are 30 s for metrics and 5 min for inventory.

This is what "Unknown never opens" means in the code: a clamped Present cannot raise a new
alert either.

## 2. State machine

States: **(none)**; **Pending** (raised, not confirmed); **Active** (`Open`, `Acknowledged`
and `Silenced`: the operator's sub-state is kept across every row below), which is either
*fresh* or *stale*; **Unknown** (new, from the 2-day rule); **Resolved**; **Cleared**
(`Resolved` + `ClearedByOperator`, sticky).

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
| Cleared (sticky) | stays cleared; `LastSeenUtc` updated (as today) | `ConsecutiveAbsent + 1`; retire at N | kept unchanged |

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
- **Housekeeping.** An instance whose entity is `Vanished` retires with `SubjectRemoved`. An
  instance whose `rule_id` is no longer registered retires with `RuleRetired`, which is K2's
  and M3.3's path. Neither is a "condition cleared".

## 3. Every current rule

N is a **choice, not a citation.** Grafana marks a missing series stale after 2 evaluations,
and our opening hysteresis is 2 hits for a warning. Those are the only anchors. Before merge,
measure cessations per rule from `flap_history` on the live estate. Rules that flap need a
larger N (ölç, sonra öner). "Default" below means the rule does not mention the subject, so it
gets `Unknown(NotReported)`, with `SourceSilent` from the clamp where it applies.

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
| `storage-path-redundancy` (I) | (host, device): lost and down fps | host Active, source reported, device paths all working. Device no longer in a **freshly read** table → `Absent(SubjectRemoved)` (needs §5.8) | source silent → `SourceSilent` (clamp). Host `InMaintenance` → `NotJudgeable` (today this **resolves**). Path table not read (coverage blind on `multipathInfo`) → `InputNotCollected` | 2 (10 min) |
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

## 5. Open questions

1. **N values** (§3) are proposals. Measure cessations per rule from `flap_history` on the
   live estate before merge. Is the planner happy to ship them as proposed and adjust later?
2. **ADR wording vs "no Unknown→resolved edge".** The ADR says that after the 2 days, the
   first fresh evaluation "either reopens or resolves". This design reads that as *decides
   the direction*: a fresh Absent restarts the count at 1, and resolution still needs N. The
   two readings differ only for N ≥ 2. Please confirm.
3. **Does Unknown reset or pause the absent count?** The proposal is reset: "consecutive"
   means an uninterrupted run of fresh absences.
4. **Health (ADR-0018).** Does a stale Critical keep the entity red with an "as of" marker?
   Does an Unknown-state alert make the entity's health *unknown* (grey) rather than green?
   The proposal is yes to both. This needs an ADR-0018 note.
5. **Notifications.** No per-alert notification on stale or on entering Unknown; the
   "Collector unreachable" alert is the page. Is a daily digest of Unknown-state alerts
   wanted?
6. **Unknown-state lifetime.** It retires on `Vanished` / entity purge. Otherwise it stays
   forever. Or does it get a second limit?
7. **Event freshness.** `IEventReader` has no per-source "read up to" watermark. Without one,
   "events for that source were not read" cannot be told apart from "no clear arrived". Is a
   watermark added in the event pipeline (package F?), or accepted as `NotReported` for now?
8. **A subject that disappears within a freshly read entity** (a device, a NIC instance, a
   property). A rule cannot say `Absent(SubjectRemoved)` about something it no longer sees.
   The proposal is a read-only `context.OpenFingerprints(RuleId)`, so a rule can return a
   verdict for each alert it holds. The alternative is to accept that these become Unknown
   after 2 days.
9. **Memory pressure with partial counters.** Should Absent require all four rates? If the
   live estate never delivers one of them (for example, compression at some levels), every
   memory alert would go stale and never resolve. The fix belongs in the counter map (§3
   "measure first"), but that needs checking first.
10. **Datastore behaviour changes.** `AlreadyFull` → Present Critical (today it resolves
    "filling"). Uncommitted `null` means both "no thin disks" and "not read". Is coverage
    enough to tell them apart?
11. **Direct producers at N = 1.** Confirm that collection, store-failure, rule-failure,
    compaction and flap alerts stay two-valued.
12. **Age clamp at `2 × interval`.** Is it too tight for the inventory scope when a vCenter
    answers slowly but correctly (a 5 min cycle that takes 6 min)?

Does not: change fingerprints, change opening hysteresis, change flap detection (a cessation
is counted at resolution, not at the first absence), convert K2's four rules, or change the
screens beyond the fields and counts §2 requires. K3 and the web package decide how "stale"
and "unknown" look.
