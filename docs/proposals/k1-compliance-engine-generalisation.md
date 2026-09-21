# K1 — Generalising the compliance engine (design note)

Status: accepted with review changes (22 September 2026) — package K, ADR-0024.
Scope: K1 only — the engine. K2 (moving M8 rules) and K3 (screens) build on it.
Constraint: **SCG behaviour does not change**; every existing compliance test passes untouched.

## 1. What exists today

- `ComplianceEvaluation.Evaluate(catalogue, entities, previous, now, checks, reportingSources)` walks
  **EsxiHost only** and judges each bound control with `SettingChecks.Judge(SettingCheck, control, host)`.
- A control binds to a `SettingCheck` by the guide's parameter name, or by control id when the
  guide says `N/A`. Unbound controls stay "not evaluated — no data collected".
- Finding identity: `(catalogue_release, control_id, entity_id)` — PK of `compliance_finding`.
  Transitions and the carry-over in `Continue()` use the same key. Exceptions match
  `control_id` + optional `entity_id` (release-independent).
- `ComplianceService` holds **one** catalogue; the store partitions findings by release
  (`IComplianceStore.Evaluate(release, …)` replaces only that release's rows).

## 2. Evaluator interface

One abstraction for "a control's check", independent of entity kind:

```csharp
/// What a check concluded about one subject of one entity.
public sealed record CheckVerdict
{
    public string Subject { get; init; } = "";          // "" = the entity itself; see 3.1
    public string? SubjectLabel { get; init; }          // display only, never identity
    public required ComplianceVerdict Verdict { get; init; }  // Passing | Failing | NotEvaluated
    public required string Expected { get; init; }
    public string? Observed { get; init; }
    public string? Reason { get; init; }                // required for NotEvaluated
}

/// Estate-wide facts a check may need beyond its own entity.
public sealed record CheckContext
{
    public required EntityGraph Graph { get; init; }    // placement, datastore marks, sharing
    public required DateTimeOffset NowUtc { get; init; }
    public DemandSnapshot? Demand { get; init; }         // N+1 (K2): precomputed, with its age
}

public interface IComplianceCheck
{
    /// Which entities this check judges; the engine only hands it these.
    EntityKind AppliesTo { get; }

    /// One verdict per subject the check can see, Passing included (3.2).
    /// Zero only when the subject cannot exist on this entity (a cluster
    /// with no DRS rules) — not a pass, no finding is written.
    IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context);
}
```

- **SettingCheck stays** as data and gets a thin adapter, `HostSettingCheck : IComplianceCheck`
  (`AppliesTo = EsxiHost`, one verdict, `Subject = ""`), calling the existing `SettingChecks.Judge`.
  Binding by parameter/id is unchanged, and so is `AppliesToHosts` for SCG — this is the whole
  SCG path, with the same inputs and outputs.
- `BoundControl.Check` becomes `IComplianceCheck?` (SCG binding produces the adapter).
- `Evaluate` loop: for each evaluated bound control → entities of `check.AppliesTo` that are not
  Vanished → `Judge` → one finding per returned verdict. Stale marking, `readAt` stamping and
  `Continue()` are unchanged, keyed by `(ControlId, Entity, Subject)`.
- Why "zero verdicts" is allowed: DRS and multipath are per-subject; an entity with no subjects
  must not produce a fake "Passing" row. A check that **could not read** its input returns one
  `NotEvaluated` verdict with the reason (e.g. `configurationEx` unread → "HA settings not read",
  never "HA disabled").

## 3. Subject in the finding identity (schema: migration 11)

Identity becomes `(catalogue_release, control_id, entity_id, subject)`, `subject = ''` for
everything that has none (all SCG findings).

```sql
-- migration 11 (10 is taken by the subscription audit columns)
ALTER TABLE compliance_finding    ADD COLUMN subject       text NOT NULL DEFAULT '';
ALTER TABLE compliance_finding    ADD COLUMN subject_label text NULL;
ALTER TABLE compliance_finding    DROP CONSTRAINT compliance_finding_pkey;
ALTER TABLE compliance_finding    ADD PRIMARY KEY (catalogue_release, control_id, entity_id, subject);
ALTER TABLE compliance_transition ADD COLUMN subject         text NOT NULL DEFAULT '';
ALTER TABLE compliance_transition ADD COLUMN subject_label   text NULL;
ALTER TABLE compliance_transition ADD COLUMN accepted_by     text NULL;  -- carried when a subject leaves
ALTER TABLE compliance_transition ADD COLUMN accepted_reason text NULL;
ALTER TABLE compliance_exception  ADD COLUMN subject text NULL;   -- NULL = every subject
```

- Existing rows get `''` → SCG identity is unchanged in value, only wider in shape.

### 3.1 Principle: the subject is the cause, not the symptom

**The subject is the thing the operator will fix.** One cause must be one finding, however many
things it affects. Every K2 check justifies its subject by answering "what will the operator
change?":

| Control | Entity | Subject | Why | Observed |
|---|---|---|---|---|
| `eo-cont.path-single-hba` | host | HBA name (`vmhba1`) | the fix is a second HBA, once per host | "all paths of 30 shared devices go through vmhba1" |
| `eo-cont.path-single-target` | host | target port WWPN | the fix is zoning/cabling to a second port | affected device count + first few NAAs |
| `eo-cont.path-single` | host | device NAA | the cause really is per device (one LUN presented over one path) | the one path |
| `eo-cont.drs-rule` | cluster | DRS `ruleUuid` (name only if no uuid) | the fix is per rule | violating VMs/hosts |
| `eo-cont.ha-*`, `eo-cont.n-plus-one-*` | cluster | `''` | one cluster setting | the setting / demand vs capacity |

A host with one HBA and 30 shared devices is therefore **one** finding, not 30.

**Display label is separate from identity.** The finding gets a `SubjectLabel` (e.g. the DRS rule
name) for display. `Observed` stays "what was observed", never an identity label; renaming a DRS
rule changes the label, and the finding and its acceptance survive (identity = `ruleUuid`).

### 3.2 Principle: a check returns a verdict for every subject it can see

"Zero verdicts" is allowed **only** for an entity on which the subject cannot exist (a host with no
shared devices, a cluster with no DRS rules). For every subject the check can see it returns a
verdict, **Passing included**, so a fix is recorded as a `Failing → Passing` transition and an
auditor can see when it was fixed.

Two events must not look the same:

- **Fixed:** the subject is still there and now passes → `Passing` row, `Failing → Passing`
  transition. Nothing is deleted.
- **Subject gone:** the device was removed, the rule deleted, the HBA pulled. The store's
  per-release replace already writes a transition with `to_verdict = NULL` ("left the
  evaluation") for a row that is not returned. K1 makes that transition carry the finding's
  **acceptance** (who/why) and its last `Observed` and `SubjectLabel`, so the history keeps the
  decision and the reason; the row goes, the record does not.

**Passing rows are stored for every control, including `path-single`.** Sizing on this estate: 1240
FC paths over ~10 hosts is a few hundred device×host pairs, comparable to SCG's control×host rows,
and HBA/target subjects are bounded by adapters and ports per host. Not storing Passing rows would
turn every fix into a row disappearing, which is exactly the "fixed vs gone" ambiguity above. One
uniform rule is preferred over a special case.
- `ComplianceWaiver` gains `Subject?`; matching = control ∧ (entity null or equal) ∧
  (subject null or equal). An exception written today (no subject) keeps covering what it covers.
- `ComplianceFinding` gains `Subject` (default `""`). View/CSV/API expose it as a column; K3
  decides how the screen shows it.

## 4. Catalogues and versioning

- **Two catalogues, evaluated independently:** the SCG release chosen today (unchanged), plus the
  product's own **`eo-continuity`**, defined in code (`ContinuityCatalogue.Build()`), never
  written into the vendored SCG CSV.
- `ComplianceService` takes `IReadOnlyList<ComplianceCatalogue>` (SCG first). `Evaluate` runs the
  store's per-release replace once per catalogue — the existing partitioning already guarantees
  one catalogue's evaluation cannot touch the other's rows. `Controls()`/`Findings()` return
  both, each tagged with its catalogue name (K3's "source" column).
- **Release id:** `eo-continuity-1`. Rule for bumping: *adding* a control does not bump; changing
  what an existing control id **means** gets a **new control id** instead of a new release. So the
  release changes only when the catalogue is restructured, and acceptances (which live on the
  finding row, per release) survive normal growth. Exceptions are release-independent already.
- Control ids: `eo-cont.ha-enabled`, `eo-cont.ha-admission-control`, …, `eo-cont.drs-rule`,
  `eo-cont.path-single`, `eo-cont.path-single-hba`, `eo-cont.path-single-target`,
  `eo-cont.n-plus-one-cpu`, `eo-cont.n-plus-one-mem` (final list in K2).
- Binding for `eo-continuity`: by control id → check, directly (no parameter matching).

## 5. What K1 delivers (and does not)

Delivers: the interface + SCG adapter, subject in identity (migration 11, store + in-memory),
multi-catalogue service, an `eo-continuity-1` catalogue with **one** cluster-level check and
**one** subject-bearing check used only by tests (so K1 proves the shape without moving M8 yet).
Done = all existing compliance/SCG tests pass unchanged; new tests show a cluster check, a
per-subject check (two subjects on one entity → two findings, exception on one subject leaves the
other failing), zero-verdict entity → no row, unread input → NotEvaluated with reason, a fixed
subject → `Failing → Passing` transition with the row kept, a vanished subject → `to_verdict NULL`
transition carrying its acceptance, a DRS-style rename (same subject, new label) keeps the
acceptance, and a silent source → Stale, never Passing.

Does not: move M8 rules (K2), change screens (K3), notify on finding changes (known gap).

## 6. Decisions (review of 22 September 2026)

1. **DRS subject = `ruleUuid`** (this estate runs vCenter 8.x); the name only when no uuid is
   reported. The name travels in `SubjectLabel`, not in `Observed`, so a rename keeps the finding
   and its acceptance.
2. **N+1 gets a snapshot**, `Evaluate` stays pure: `CheckContext` carries a precomputed demand
   snapshot, not an `ISeriesReader`. **The snapshot's age is part of the verdict:** too little or
   stale history → `NotEvaluated` with the reason ("no 7 days of demand history"), never a
   `Passing` from old numbers.
3. **Same trigger** as SCG, no new scheduler. **`eo-continuity` uses the same `reportingSources`
   as SCG:** a silent vCenter's clusters are marked `Stale` with their last verdict; they never
   turn into "HA disabled" or into `Passing`.
