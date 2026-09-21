# K1 — Generalising the compliance engine (design note)

Status: proposal, for review before any code (package K, ADR-0024).
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
    public string Subject { get; init; } = "";          // "" = the entity itself
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
    public ISeriesReader? Series { get; init; }          // N+1 demand trend (K2)
}

public interface IComplianceCheck
{
    /// Which entities this check judges; the engine only hands it these.
    EntityKind AppliesTo { get; }

    /// Zero or more verdicts for one entity. Zero = "nothing to judge here"
    /// (e.g. a cluster with no DRS rules) — not a pass, no finding is written.
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
ALTER TABLE compliance_finding    ADD COLUMN subject text NOT NULL DEFAULT '';
ALTER TABLE compliance_finding    DROP CONSTRAINT compliance_finding_pkey;
ALTER TABLE compliance_finding    ADD PRIMARY KEY (catalogue_release, control_id, entity_id, subject);
ALTER TABLE compliance_transition ADD COLUMN subject text NOT NULL DEFAULT '';
ALTER TABLE compliance_exception  ADD COLUMN subject text NULL;   -- NULL = every subject
```

- Existing rows get `''` → SCG identity is unchanged in value, only wider in shape.
- Subject values, chosen to be stable across restarts and human-readable in a report:
  multipath → device NAA; DRS → rule name (vCenter's `ruleUuid` when present, name as display —
  open question 1); HA/N+1 → `''` (the cluster is the subject).
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
other failing), zero-verdict entity → no row, unread input → NotEvaluated with reason.

Does not: move M8 rules (K2), change screens (K3), notify on finding changes (known gap).

## 6. Open questions for review

1. DRS subject: rule name (readable, but renaming a rule orphans its finding and acceptance) vs
   `ruleUuid` (stable; vCenter 6.7+). Proposal: `ruleUuid` when present, else name; display name
   carried in `Observed`.
2. Series access for N+1 inside a pure evaluator: pass `ISeriesReader` in `CheckContext` (K2), or
   precompute demand outside and hand checks a snapshot? Proposal: snapshot, keeps `Evaluate` pure.
3. Evaluation rhythm: SCG runs after each inventory cycle; continuity checks read the same graph,
   so the same trigger — no new scheduler.
