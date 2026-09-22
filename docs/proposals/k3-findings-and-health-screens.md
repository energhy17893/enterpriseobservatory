# K3 — Findings and health screens (design note)

Status: **for planner review** (22 September 2026) — package K, ADR-0024 / ADR-0018 / ADR-0026.
Scope: K3 only — screens and reports over what K1 (engine) and K2 (M8 → `eo-continuity`) already
deliver. No engine change proposed here except the one item K1 flagged and left open (§6).
Read: ADR-0024, ADR-0018 (+ ADR-0026's health note), ADR-0026, `docs/proposals/adr-0026-three-valued-rules.md`,
`docs/proposals/k1-compliance-engine-generalisation.md`; code: `web/src/routes/Compliance.tsx`,
`Alerts.tsx`, `EntityDetail.tsx`, `reports/ContinuityReport.tsx`;
`src/EnterpriseObservatory.Api/ComplianceApi.cs`, `Contracts/Views.cs`,
`Projections/ReadModel.cs` (`ContinuityReport()`); `src/EnterpriseObservatory.Application/Compliance/ContinuityControls.cs`;
`src/EnterpriseObservatory.Domain/Compliance/ComplianceFinding.cs`, `Entity.cs`.

## 0. What exists today, read from `main` (7b0bb63, #85 merged)

- **Compliance screen (`Compliance.tsx`) shows one catalogue.** `ComplianceView` carries a single
  `CatalogueName`/`CatalogueRelease`/`CatalogueProblem` at the top, even though K1 already made
  `ComplianceService` hold a list of catalogues and each `ComplianceControlView` already carries
  its own `CatalogueName`. The screen groups by control, evaluated vs. not-evaluated, with no
  grouping or filter by catalogue. Passing findings are not shown at all today — the drill-down
  (`Findings`) lists every state together, ordered by state.
- **`eo-continuity` is live** (`ContinuityControls.All`, 18 controls) and evaluated the same way as
  SCG, through the same `ComplianceService`/`IComplianceStore`. Nothing in the API or the screen
  says which catalogue a control or finding came from beyond the one field noted above.
- **The continuity report (`ContinuityReport()` in `ReadModel.cs`) does not read the catalogue
  generically.** It hard-codes four groupings by control-id prefix or literal id: `IsHa`
  (`eo-cont.ha-`), `IsStoragePath` (`eo-cont.path-`), `IsNPlusOne` (`eo-cont.n-plus-one-`), and
  the literal `ContinuityControls.DrsRule`. The eight M8.4/M8.7 controls added since (cdrom,
  consolidation, single-host datastore, EVC, ESXi/vCenter certificates) are **not represented in
  this report at all** — they evaluate, they show on the Compliance screen, but `ContinuityReport`
  never looks at them. Cluster→host mapping for the storage-path group already uses the graph
  (`HostsOf`, the `PartOf` edge), so the pattern for "generic" exists; it is just not applied to
  every control.
- **Alert freshness (#85) exists in the API and is not in the web app.** `AlertView` carries
  `EvidenceAtUtc`, `IsStale`, `StaleSinceUtc`, `StaleReason`, `StaleDetail`; `OverviewView` carries
  `FreshOpenAlerts`, `StaleOpenAlerts`, `UnknownAlerts`; `/alerts` accepts `state=Unknown`. None of
  `Alerts.tsx`, `EntityDetail.tsx` or the overview route reads any of these fields. The inbox shows
  `alert.state` only when it is not `Open`, and `Unknown` is never distinguished from a plain open
  alert going stale.
- **Health is not derived from alerts.** `Entity.EffectiveHealth` (`Entity.cs:224`) is
  `ObservationState == Vanished ? Unknown : Health`, and `Health` is set by the collector
  (vSphere's own alarm rollup), not by `AlertLifecycle`. ADR-0018 says health must derive from the
  most severe alert against the entity; ADR-0026 §7.3 records this as **deferred**, not done. K3
  cannot ship the "stale Critical keeps it red" / "Unknown → grey" behaviour the ADRs describe
  without that derivation existing first. This note says so explicitly (§4) rather than silently
  scoping it out.

## 1. Findings screen

### 1.1 Source is a first-class, always-visible dimension

**Decision: `ComplianceView` and `ComplianceControlView` both carry an explicit `Source` field**
(rename of the existing ad-hoc `CatalogueName` on the control; add it at the top level too),
with exactly two values on this build: `"Broadcom SCG"` and `"eo-continuity"`. The screen's control
list defaults to **grouped by source**, two collapsible sections in a fixed order (SCG first,
`eo-continuity` second — SCG is the audit-standard one, continuity is the product's own), each
with its own control/finding counts. A **catalogue/source filter chip row** at the top
(`All · Broadcom SCG · eo-continuity`) narrows both sections to one; **on by default it shows
both**, so the filter is a narrowing control, not a hidden toggle an operator has to discover.

Why not a single flat list with a column: at 120 SCG + 18 `eo-continuity` controls (soon more, as
M9 lands: measured §1.2), a column is easy to miss and easy to mis-filter by; a section boundary
is not. It also matches the K1 rationale directly — SCG is Broadcom's document and must never read
as "ours"; a shared list blurs that line back in.

Each `ComplianceControlView` shows its `Source` field (renamed from `ComplianceControl.Source` —
the citation, e.g. "VMware KB 2004739" or "Product policy: …") next to the control id, inside the
row, same place it is today. **Two different things share the English word "source" here** and
the screen must not conflate them: the catalogue (SCG vs. `eo-continuity`, this section) and the
citation (`ComplianceControl.Source`, the threshold basis). The design keeps them visually apart —
catalogue as a section header and filter chip, citation as small text inside the row, labelled
"basis:" rather than "source:" to avoid the collision. (This is a rename in the screen's copy only;
`ComplianceControl.Source` in code keeps its name — see §5.)

### 1.2 Default view: Failing + NotEvaluated; Passing collapsed to a count

**Decision:** the default finding list per control shows **Failing and NotEvaluated rows only**.
Passing is not hidden — it is a single line, "**290 devices passed**" (the exact count, the
control's subject noun pluralised: "290 devices", "12 clusters", "1 rule"), with a `▸ Show passing`
disclosure that expands the same `FindingRow` list already used for Failing. Accepted and Excepted
findings show inline with Failing (same as today — an accepted finding is still non-compliant,
ADR-0024 §"Accepted" note) using the existing `Accepted`/`Excepted` badges.

Rationale, tied to the numbers this note is asked to design against (§1.3): a `path-single` check
on 10 hosts with ~30 shared devices each is on the order of 300 rows, of which 290 pass on this
estate. A screen that renders 290 green rows before an operator reaches the 9 or so live failures
teaches the same lesson ADR-0024 warns about for the alert inbox — stop reading. Collapsing Passing
to a count is the findings-screen equivalent of ADR-0018's "a cluster health badge alone says too
little; show a derived summary instead of yielding no information" (ADR-0018 reconsideration
note): here the summary is literal ("N passed"), not a badge.

This default is **per control**, not global: a control with zero Failing/NotEvaluated shows only
its "N passed" line and stays collapsed (no `▸`), and a control with only Failing rows never shows
an empty "Show passing" affordance if the count is zero — a `▸ Show passing (0)` line that expands
to nothing is worse than no line.

### 1.3 The numbers this design is checked against

Read live from the estate (22 September 2026), stated so a reviewer can sanity-check §1.1–§1.2
against them rather than against a hypothetical:

| Catalogue | Findings | Failing | Notes |
|---|---|---|---|
| Broadcom SCG | 120 controls, 10 hosts | 130+ | across the 10 hosts |
| `eo-continuity` | 769 findings | ~9 | 3 admission-control off, 2 network-warning silenced, 2 host certs expiring 15 Nov, 2 connected CD/DVD; plus a NotEvaluated band: N+1, heartbeat datastores, EVC |
| `eo-continuity`, `path-single` alone | 290 | 0 | all Passing on this estate — the case §1.2's collapse exists for |

Two things this table is evidence for, beyond the screen design:

- **NotEvaluated is not a rounding error here.** N+1 (needs 7 days of demand history, product
  policy — K1 §2 decision 2), heartbeat datastores and EVC each have a live NotEvaluated band on
  this estate today. The default view (§1.2) surfaces these next to Failing, not folded into
  "everything else," because a NotEvaluated N+1 check is exactly the "green while blind" case
  principle 1 forbids if it were ever miscounted as Passing.
- **769 vs. 130+ is why source separation (§1.1) is not cosmetic.** A flat count ("899 findings, 9
  failing") would bury the fact that most of the estate's finding *volume* is the product's own
  continuity catalogue evaluating per-device subjects (path-single's 290), while most of the
  *actionable* SCG volume is unrelated hardening drift. Conflating them in one number answers
  neither "is the SCG baseline held" nor "is continuity intact."

### 1.4 Acceptance and time-limited exception work identically in both catalogues

No change proposed to `ComplianceService.Accept` / `AddException` / `RemoveException` — they are
already catalogue-agnostic (keyed by `ControlId` + `EntityId` + `Subject`, never by catalogue
release for exceptions, per catalogue release for findings/acceptance). The screen change is
**only** that `ExceptionForm` and the accept button, already rendered per finding today
(`Compliance.tsx` `FindingRow`), keep working unchanged inside either source's section — no
special-casing for `eo-continuity`. The one real difference an operator will notice: `eo-continuity`
controls are far more likely to carry a non-empty `Subject` (a DRS rule uuid, an HBA name, a device
NAA) than SCG's (always `""`), which is why §6 below matters specifically for this catalogue.

## 2. Health (ADR-0018 extension) — shipped in #88 and #94 (22 September 2026); specified here, kept as the record

**Stated plainly: entity health is not derived from alerts in code today.** `Entity.EffectiveHealth`
reads the collector's own rollup. ADR-0018's rule ("a variable's health is the most severe alert
against it") and ADR-0026's extension (stale keeps it red with a mark; Unknown makes it grey) are
**not** something K3 can build a screen against yet — this section specifies what that derivation
must produce, as the contract K3's screens are written to, so the eventual implementation (a K-something
or D package item, not K3) has a concrete target and K3's screen code does not have to change shape
when it lands.

### 2.1 The derivation K3 assumes (spec, not implementation)

For an entity `e`, over its alerts where `EntityId == e.Id`:

1. Take the most severe alert by `Severity`, among alerts in `Open`/`Acknowledged`/`Silenced`
   sub-states (ADR-0026's "Active" superstate) — Unknown-state alerts are excluded from this step,
   handled in step 3.
2. If that alert `IsStale`, health is **Critical/Warning as its severity says, plus a stale mark**:
   the screen shows the severity colour with a "since `StaleSinceUtc`" annotation, not a separate
   colour. (A stale Critical is still red — ADR-0026 §"Sağlığa etkisi": staleness does not soften
   the colour, it explains why the colour might be behind reality.)
3. If the entity's *only* relevant alert(s) are in the **Unknown** state (no fresh or stale Active
   alert, at least one Unknown), health is **grey**, distinct from both "no alert" (green/Healthy)
   and any severity colour. Grey means "we do not know," never "acceptable."
4. No Active and no Unknown alert → Healthy (green), same as ADR-0018's base rule — an entity with
   no alert against it is healthy, full stop, no propagation from children (ADR-0018 §"Karar").
5. `ObservationState == Vanished` still overrides everything to Unknown/grey (unchanged from
   today's `EffectiveHealth`).

This needs a `HealthState` value or a paired field for "grey / Unknown" that today's three-value
`HealthState` (`Unknown`, `Healthy`, `Warning`, `Critical`) does not distinguish from "vanished /
never collected" Unknown. **Open engineering question, out of K3's scope to answer**, flagged for
whoever implements the derivation: does grey reuse `HealthState.Unknown` (then the screen needs a
second signal — e.g. "has an Unknown-state alert" — to tell "never observed" grey apart from "we
saw it, then lost evidence" grey), or does it need a fifth value? K3's screens (§2.2) are written
against **the distinction being visible**, not against a specific enum shape.

### 2.2 What the screens show, once the derivation exists

- **Entity explorer / entity detail badge:** `StatusBadge` gets a `Stale` variant (visually a
  striped/dimmed version of the severity colour, not a new colour) and an `Unknown` variant already
  used for compliance's `NotEvaluated` (`STATE_STATUS.NotEvaluated = 'Unknown'` in `Compliance.tsx`
  — reuse that mapping, do not invent a second grey). `EntityDetail.tsx`'s header badge
  (`entity.health`) gets a "since `StaleSinceUtc`" suffix when stale, sourced from the entity's
  worst alert, not recomputed on the client.
- **Alert-derived counts on the entity page and overview** use `FreshOpenAlerts` / `StaleOpenAlerts`
  / `UnknownAlerts` (already in `OverviewView`) as two badges next to the existing severity counts:
  "12 Critical (2 stale)" rather than a bare 12, and a separate "3 Unknown" chip that is explicitly
  outside the Critical/Warning/open counts (ADR-0026: Unknown is "out of the count and under its
  own filter" — never folded in).
- **Alerts inbox filter:** add `Unknown` as a fourth severity-row chip value (`['', 'Critical',
  'Warning', 'Unknown']` in `Alerts.tsx`), wired to `/alerts?state=Unknown` (already served, per
  #85). A stale-but-Active alert is not a separate filter chip — it is shown with a small "stale
  since …" annotation on its existing card (same pattern as Compliance's `StaleBadge`), because
  ADR-0026 point 3 says a stale alert stays in its severity's Active count; only Unknown is a
  distinct bucket.
- **`AlertActions`/inbox row:** show `StaleSinceUtc`/`StaleReason` as a one-line annotation
  ("stale since 3h ago — source silent") the same way `Compliance.tsx`'s `FindingRow` already
  shows `finding.stale` + "vCenter did not answer" — this is a proven pattern in this codebase,
  reused rather than redesigned.

## 3. The continuity report generalises to read the catalogue, not controls

**Decision: `ReadModel.ContinuityReport()` stops switching on control id and groups findings by
each control's declared component/entity kind, read from `ContinuityCatalogue`/`ComplianceControl`,
plus the graph's own containment edges** — the same mechanism `HostsOf` already uses for the
storage-path group, generalised to every control instead of three hard-coded predicates.

Concretely: each `ContinuityCheck`'s `IComplianceCheck.AppliesTo` (`EntityKind`) says which entity
kind its findings sit on. The report walks live clusters and, for each control in
`ContinuityCatalogue.Production`, resolves that control's findings onto the cluster either directly
(`AppliesTo == Cluster`, today's `Ha`/`Drs`/`NPlusOne`/`MaintEvc` case) or through the graph
(`AppliesTo == EsxiHost` → `PartOf` to the cluster, today's storage-path case; `AppliesTo == VirtualMachine`
→ `RunsOn` to a host, then `PartOf` to the cluster, for `MaintCdrom`/`MaintConsolidation`;
`AppliesTo == Datastore` → `BackedBy`/mount edges to hosts, then to their cluster, for
`MaintSingleHostDatastore`; `AppliesTo == EsxiHost` alone, no cluster, for `CertEsxi`, shown on the
host row or a new host-level section — see below). The grouping key becomes **"which cluster/host
this control's entity kind maps to," resolved once from `ContinuityCatalogue`'s registration order**,
not a growing list of `IsXxx(controlId)` predicates in `ReadModel.cs`.

Effect asked for by the task: **adding a control to `ContinuityControls.All` makes it appear in the
report automatically**, at whatever grouping its `AppliesTo` implies, with no `ReadModel.cs` change
— closing exactly the gap named in §0 (eight controls invisible to the report today).

Two things this does **not** try to solve in K3:

- **Host-level and datastore-level rows currently have no place in `ContinuityReportRow`**, which
  is per-cluster. `CertEsxi` (host), `MaintSingleHostDatastore` (datastore) and `MaintCdrom`/
  `MaintConsolidation` (VM) need either a rollup onto the cluster row (count of failing hosts/VMs/
  datastores under it — the same shape `StoragePathAffectedHosts` already uses) or a second table
  section. **Proposed: roll up to the cluster row as a count + affected-name list**, mirroring
  `StoragePath`/`StoragePathAffectedHosts` exactly, so the report stays one table. `CertVCenter`
  (the vCenter itself, not under any cluster) needs its own line above the cluster table — there is
  exactly one vCenter's worth of these findings per source, so a single small "vCenter certificate"
  card above the per-cluster table is proposed rather than a table row with no cluster.
- **This is a code change (`ReadModel.cs`, `Api/Contracts/Views.cs` for the report's row shape),
  not a K1/K2 engine change.** It belongs in K3's PR split (§7), separate from the pure-screen work,
  because `ContinuityReportRow`'s shape changes.

## 4. Known limit: a removed vCenter's "Collector unreachable" stays stale up to 2 days

Documented here rather than fixed, per the task: when a vCenter connection is deleted from
configuration, its `Collector unreachable` alert (a direct producer, N = 1, ADR-0026 §1.2) has no
mechanism today that says "this source is gone, not silent." It goes stale (no cycle signs it) and,
under ADR-0026's carry-forward limit, becomes **Unknown after 2 days** (raw retention) — not
retired, not resolved. An operator who removes a vCenter sees its unreachable alert sit in the
Unknown bucket for up to two days before it ages out of relevance on its own (it never actually
retires under today's rules — ADR-0026 §2 "Housekeeping": retirement needs `SubjectRemoved` or
`RuleRetired`, neither of which a configuration deletion produces).

**This is closed by package F's F4 (server-side cleanup scope) and F5 (learned state), not by K3.**
Those PRs are where a configuration removal becomes an event the runner acts on
(`docs/proposals/f-invert-collector-authority.md` §3.4–3.5); a `SourceRemoved` transition for the
collector's own direct-producer alerts is the natural place to close this, once that event exists.
K3's screens (§2.2) should render this state honestly in the meantime: an Unknown "Collector
unreachable" alert shows the same "stale since / Unknown" treatment as any other Unknown alert, with
no special copy implying it will resolve itself soon.

## 5. `AddExceptionCommand.Subject` default (K1's flagged item)

K1 (`k1-compliance-engine-generalisation.md` §3) fixed the **domain** meaning: `subject == null`
on `ComplianceWaiver` means "every subject of this control (and entity, if given)." `ComplianceApi`
carries this through unchanged — `AddExceptionCommand.Subject` is `string?`, and
`ComplianceService.AddException` treats blank/whitespace the same as null. The API doc comment
already says "Null or empty for every subject." **The gap K1 left open is at the UI layer**: nothing
in `Compliance.tsx`'s `ExceptionForm` lets an operator *choose* a subject — it is always called with
`entityId` (from the finding row or `null` for "every host") and never passes `subject` at all, so
every exception written from the screen today is implicitly "every subject of this control on this
entity" — which is harmless for SCG (subject is always `""`) but is exactly the wrong default for
`eo-continuity`'s per-subject controls: an operator meaning to except **one** failing HBA
(`eo-cont.path-single-hba`, subject `vmhba1`) who clicks "Except this host" today would, if the form
is not changed, except **every** HBA/device/rule subject of that control on that host — including
ones not yet failing.

**Proposed for K3:** `ExceptionForm` gains a `subject` prop. When the finding being excepted has a
non-empty `Subject` (i.e., an `eo-continuity` per-subject finding), the form is called with that
subject pre-filled and **defaults to "this subject only,"** with an explicit, separately-labelled
checkbox to widen it to "every subject of this control on this host" — an affirmative, visible
action, never the unlabelled default. For SCG findings (`Subject == ""`) the form behaves exactly as
today; no visible change. This mirrors the existing two-button pattern (`"Except every host"` on the
control row vs. `"Except this host"` on the finding row) by adding one more axis rather than a new
UI idiom. `AddExceptionCommand`/`ComplianceService.AddException` need no change — the API already
accepts `subject`; only the screen was defaulting it away by never sending it.

## 6. PR split

1. **K3.1 — Compliance screen: source sections + filter, Passing collapse.** `Api/ComplianceApi.cs`
   (`ComplianceView`/`ComplianceControlView`: rename the top-level catalogue fields, confirm
   per-control `CatalogueName`/`Source` are both present and distinct — §1.1), `Compliance.tsx`
   (grouping, filter chips, Passing-count collapse — §1.1–§1.2). No engine change. Tests: web
   component tests for the collapse/expand and filter; API contract test that `ComplianceView`
   exposes both catalogues' controls with distinguishable `Source`.
2. **K3.2 — Exception form subject default.** `Compliance.tsx` `ExceptionForm` + `FindingRow`
   (§6/§5 — pre-filled subject, explicit widen checkbox). No API change. Tests: a per-subject
   finding's exception defaults to that subject; the widen checkbox is required to submit a
   subject-less exception on a per-subject control.
3. **K3.3 — Continuity report generalisation.** `Application/Compliance/ContinuityControls.cs`
   (expose `AppliesTo` if not already resolvable per control), `Api/Projections/ReadModel.cs`
   (`ContinuityReport()` regrouping — §3), `Api/Contracts/Views.cs` (`ContinuityReportRow`/`View`
   shape for host/VM/datastore rollups and the vCenter-certificate card). Tests: a new continuity
   control (e.g. add a throwaway test-only check with `AppliesTo = VirtualMachine`) appears in the
   report with no `ReadModel.cs` change beyond the registration; the eight M8.4/M8.7 controls
   missing today (§0) show up. This is the one PR here with a real engine-adjacent surface
   (`ReadModel` reads compliance findings, does not touch `ComplianceService`) and should land after
   K3.1 so the report's per-cluster rollup can reuse whatever `Source`/label rendering K3.1 settles
   on for consistency.
4. **K3.4 — Alert freshness on the web.** `Alerts.tsx` (Unknown filter chip, stale annotation),
   `EntityDetail.tsx` (stale annotation on entity alerts), `Overview` route (fresh/stale/Unknown
   badges from `OverviewView`). No API change (#85 already shipped every field used). Independent of
   K3.1–K3.3; can land in parallel.
5. **Not in this PR split — separate work, not K3's:** the health-derivation engine change (§2) is
   specified but not implemented here; it needs its own design/PR once someone picks up the
   `HealthState`/grey-Unknown question in §2.1, and K3.4's stale/Unknown alert work does not depend
   on it (alert freshness is already computed; only *entity* health derivation is missing).

## 7. Open questions for the planner

1. **§2.1's grey-Unknown representation.** Does health-derivation reuse `HealthState.Unknown` with
   a side signal, or add a value? This blocks nothing in K3 (§2.2 is written against "the
   distinction is visible," not a specific enum), but whoever takes the health-derivation work
   needs an answer before writing it, and K3's badge component (`StatusBadge`'s `Stale`/`Unknown`
   variants) should be built to the answer once it exists rather than guessed at twice.
2. **Host/datastore/VM rollup shape for the continuity report (§3).** Proposed: roll up as a count +
   affected-name list on the cluster row, matching `StoragePath`/`StoragePathAffectedHosts`. An
   alternative is a second, entity-kind-keyed table below the cluster table (one row per failing
   host/VM/datastore, not rolled up). The rollup keeps one table and matches the existing pattern;
   the second-table alternative loses less detail on a large failing set. Needs a decision before
   K3.3's `Views.cs` shape is fixed.
3. **`CertVCenter`'s placement** (§3): a single card above the cluster table, or a `vCenterId` on
   `ContinuityReportView` even though there is normally one per estate. Proposed: the card, because
   a table row with an empty cluster column reads as a data-quality bug rather than a deliberate
   choice.
4. **Exception-widen checkbox copy (§5).** Needs UX review of the exact wording so "every subject of
   this control on this host" cannot be misread as "every control" — this note proposes the
   mechanism, not the final copy.
5. **Does K3.2's subject default apply retroactively to exceptions already written blank against
   `eo-continuity` controls before K3 ships?** No — out of scope. An existing subject-less exception
   on a per-subject control keeps meaning "every subject," per K1's decision; K3 only changes what
   the **form** defaults to for exceptions written from now on. Flagging this so the planner can
   decide whether a one-time audit of standing `eo-continuity` exceptions (are any of them
   accidentally-wide?) is worth a separate, non-K3 task.
