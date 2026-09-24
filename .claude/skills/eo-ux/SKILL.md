---
name: eo-ux
description: Checklist for Enterprise Observatory's web UI (web/src) — component reuse, information architecture, colour/token rules, three-valued "unknown" presentation, writing style, density, and the classic/observatory theme split. Use for any screen or component PR in web/; every rule below cites its source (ADR §, code file, design note, or reference §11) — do not invent a rule without one.
---

# Enterprise Observatory UI skill

A screen or component PR brief that says "per eo-ux" means: check every rule
in this file that applies, cite the ones you followed, and flag any you
deliberately didn't. This is a checklist, not an essay — if a rule needs more
context, follow its source reference.

## 0. Before you touch a screen

1. **Read `references/components.md` first.** It lists every component that
   actually exists in `web/src/components` and the route-local ones that look
   reusable but aren't yet, each with a file path. Never invent a component
   name — if the brief names one that isn't there, say so and either reuse the
   nearest real one or propose promoting it.
2. **State whether the PR changes the `observatory` token set (visual) or
   shared code (structural)** — see §6. This is a required line in the PR
   description, not optional framing.

## 1. Stack and visual language — ADR-0006

- React 19 + TypeScript SPA, shadcn/ui (Radix) + Tailwind CSS v4. No other
  component library.
- Fira Sans for labels/body; **Fira Code for identifiers, WWNs, IPs, UUIDs,
  and numeric/tabular columns.** (`Identifier` in
  `web/src/components/Primitives.tsx` already does this — reuse it rather
  than a bare `<span>`.)
- Visual language is Minimalism/Swiss: grid-based, high contrast, no
  decoration. No hero/CTA marketing patterns — this is an ops console.
  (ADR-0006, rejected-suggestions table.)
- Dark theme is primary; light theme is fully supported, not an afterthought.
- No raw palette classes in a component (`bg-blue-500` etc. is forbidden) —
  always go through a status ramp or a token class (ADR-0006 shadcn rule;
  ADR-0008 §4; `web/src/lib/ui.ts` `ramp()`).
- Lists over ~100 rows must be virtualized (`react-window`/`react-virtual`) —
  ADR-0006 table.

## 2. Information architecture — ADR-0007 §1–6

- **§1 — one model, many views.** No screen owns its own alert list or health
  computation; every view projects the same underlying state. When adding a
  screen, identify which existing read model it projects — never add a
  parallel one.
- **§2 — top-level grouping is by operator intent, not vendor.** The five
  groups are Triage / Investigate / Assess / Reports / Configure (Turkish:
  Triyaj / İnceleme / Analiz / Kanıt / Yapılandırma). Vendor name (vSphere,
  iLO, Dell…) is never a top-level nav axis; it lives as a filter/deep-view
  inside Investigate.
- **§3 — Investigate has three tiers**: (1) entity explorer, cross-type,
  uniform columns (name, type, health, alert count, last seen); (2) entity
  detail, one entity, relationship navigation; (3) deep view, domain-specific
  tool (iLO hardware panel, MPIO path matrix, fabric radar). A vendor menu
  item opens tier 1 filtered to that vendor (a saved view), not a bespoke
  page.
- **§4 — entity-centred navigation.** Relationships (ADR-0004) should be
  clickable: host → HBA → switch port → array, etc.
- **§5 — an alert lives in one place.** Entity pages show alerts scoped to
  that entity using the *same* instances/actions as the inbox — never a
  second list with its own state.
  - §5.1 event grouping: a group header must show the real count ("SFP
    failure · 12 alerts · 8 entities"); the flat list is always one click away
    (`Events`/`All alerts` toggle); no alert lives only inside a group — it is
    still counted and findable in the flat list. Cf. reference §11.5: an alert
    list's default grouping should be by rule/reason, with time/severity/
    entity-type alternates (vROps, Grafana), and a repeat-count column
    (Veeam ONE).
  - §5.2 two correlation classes: topological (graph-proven) folds by
    default; temporal ("fired within 30s") never auto-folds, it's offered as
    "possibly related."
- **§6 — Wallboard is a separate view, not Triage in fullscreen.** Different
  read distance, no interaction, sparse typography, must show explicit
  staleness ("last updated 14 min ago") rather than silently freezing a value
  — never violate principle 1 by showing stale data as current.

## 3. Colour and tokens — ADR-0008 §1–5

- **§1 — role ramp, not one colour per status.** Every status
  (healthy/warning/critical/info/unknown) needs five role values:
  `surface`, `border` (≥3:1 against page/card), `solid`, `solidOn` (≥4.5:1
  against `solid`), `text` (≥4.5:1 against `surface` *and* card). Never reuse
  one status colour across text/dot/border/theme — that's the exact bug this
  ADR fixes.
- **§2 — action colour is never a status hue.** `primary` = violet (hue 285),
  not any status colour; `destructive` intentionally shares critical's hue.
  A button must never be coloured warning-amber or info-blue.
- **§3 — colour never carries information alone.** Status is colour + icon +
  text, always. (`StatusBadge` in `Primitives.tsx` already pairs a dot with
  text — copy that pattern, don't strip the text/icon to "just show the dot".)
  Cf. reference §11.3: every status needs colour + shape + text (Dynatrace
  Strato, IBM Carbon, WCAG 1.4.1), and the status icon itself must hit 3:1
  (WCAG 1.4.11).
- **§4 — `web/design/tokens.mjs` is the single source of truth.** CSS is
  generated (`generate-css.mjs`) into `web/src/styles/tokens.css` — never
  hand-edit the generated file, never define a token value outside
  `tokens.mjs`.
- **§5 — contrast (and gamut) is a CI gate.** `web/design/validate-contrast.mjs`
  checks every role threshold and sRGB gamut; a component change that pushes
  a colour value must pass this, not be eyeballed.

## 4. Unknown, staleness, and "basis" — ADR-0026, K3 §1, ADR-0008 §3

- **Unknown/grey is its own state, never folded into a percentage or into
  "healthy."** "We don't know" and "it's fine" are different claims
  (README principle 1; ADR-0026 §3–4; `web/src/lib/ui.ts` `healthStatus`,
  `healthBasisLabel`/`healthBasisShort`). A count of "290 devices passed"
  must not silently include NotEvaluated/Unknown subjects. Cf. reference
  §11.1: grey is a separate state that never reads as OK (PRTG, Datadog,
  Carbon, Strato all agree; Zabbix's opposite default is a documented
  complaint) and takes the lowest priority when statuses combine (PRTG).
- **"No data" and "evaluation failed" are different axes** — reference
  §11.2 (Grafana separates rule *health* from rule *state*; our `up` +
  `collector_health` give the same split). Don't conflate a rule that never
  ran with one that ran and found nothing.
- **A stale answer is not shown as fresh.** Mark it explicitly ("stale since
  …"), never silently update the display with an old value (ADR-0007 §6;
  `web/src/lib/ui.ts` `isStale`/`STALE_AFTER_MS`; the one `StaleBadge` in
  `web/src/components/Primitives.tsx`, used by Entities, EntityDetail and
  Compliance).
- **Acknowledged/silenced is its own state, distinct from resolved** —
  reference §11.4: evaluation keeps running, notification stops, the item
  stays visible in the list (Grafana, Veeam ONE, PRTG); a resolve must not be
  transient (rejecting Datadog's auto-revert-on-next-evaluation behaviour).
- **Two different "sources" must stay visually distinct** (K3 §1.1): the
  *catalogue* (e.g. "Broadcom SCG" vs. "eo-continuity" — shown as a section
  header / filter chip) is not the same thing as the *citation* for a
  control's threshold (e.g. "VMware KB 2004739"), which is shown inline in
  the row labelled **"basis:"**, never "source:" — see §5 below and
  `web/src/lib/basis.ts` (`basisLabel`, `NO_CITATION`).
- **A finding always carries a "basis:" line** — even when it cites nothing,
  it must say so ("no citation — product policy"), never omit the line
  (`web/src/lib/basis.ts`).

## 5. Findings that repeat — README principle 4

- **A control that fires many times is one finding with a count, not N
  findings.** README §4 ("Gürültü operatörün düşmanıdır" — noise is the
  operator's enemy): fingerprint dedup, hysteresis, and persistent lifecycle
  state prevent the same problem being reported over and over. Apply the same
  logic in UI copy: show the count ("12 alerts"), not 12 repeated rows framed
  as 12 separate problems (see also ADR-0007 §5.1; reference §11.5's repeat-
  count column, §11.9's Tasteful Friction — more friction on destructive or
  silencing actions, not on reading a count).

## 6. Writing rules

- **Alert title pattern: `<what> — <where>`.** E.g. "SFP failure — switch
  port 3/12", not "switch port 3/12: SFP failure" or a bare "SFP failure."
- **Every finding carries a `basis:` line** (see §4 above).
- **All UI text is English.** (Design notes/ADRs are Turkish; the product's
  own strings are not — see the existing English copy in
  `web/src/components/Primitives.tsx`, `AlertActions.tsx`, `web/src/lib/ui.ts`.)
- **Empty tables and cells never render blank.** A missing value is `-` or
  `[x]`/`[z]` with a legend, never an empty cell (reference §11.7, UK
  Analysis Function's "not available"/"not applicable" symbols — "NA" is
  rejected there as ambiguous). Empty *screens* split into three distinct
  messages — loading / genuinely none / filtered out — each with a next
  action, not one generic "no results" (reference §11.8; NN/g, Saga,
  Atlassian).

## 7. Density and paging — measured, not guessed

Kibar's measured estate: **1,100 VMs, 119 datastores, 59 hosts.** List
screens (entity explorer, compliance findings, alerts) need a default page
size and filters sized to that estate, not to a demo dataset.

- **Default page size: to be measured.** Do not hardcode a number in a PR
  without a measurement backing it (a virtualized-list frame budget or a
  render-time benchmark on the Kibar-sized estate). Record the decision as
  "to be measured" until that measurement exists, per
  `memory/measurement-query-assumptions.md`'s rule: a published threshold
  needs a measurement file and a test behind it.
- Filters are load-bearing at this scale, not a nice-to-have — a 1,100-row
  list without a working filter is not shippable.
- **No infinite scroll — use real pagination.** Reference §11.7: NN/g's
  finding for goal-directed search tasks (which triage/investigate are) is
  that pagination preserves position memory where infinite scroll does not;
  first column is a human-readable name, header row is frozen, numeric
  columns are right-aligned.

## 8. Themes — decided by Ertuğrul, 2026-09-23

A **theme is the visual layer only**: colour ramps, spacing scale,
typography, density.

- `web/design/tokens.mjs` holds two token sets: `classic` (today's set,
  unchanged) and `observatory` (new).
- `generate-css.mjs` emits each set × dark/light (4 CSS variants total).
- `validate-contrast.mjs` validates **every** set — the gate applies to both,
  not just `classic`.
- Theme selection is per-viewer, stored in `localStorage`, **default
  `classic`.**
- **Layout, navigation, paging, and copy are NOT theme** — they are identical
  across both token sets (ADR-0007). A theme swap must never change which
  screens exist, how they're organized, page sizes, or wording.
- **Every UI PR states which it changes**: the `observatory` token set
  (visual-only, e.g. a new colour value or spacing scale) or shared code
  (structural, e.g. a new screen, a layout change, new copy) — never both
  silently in one PR without saying so.

## §11 references: operator UI patterns (reference-approaches.md §11, 23 Sep 2026)

Source: `docs/reference-approaches.md` §11 (branch `docs/ux-reference`,
not merged into this branch — cite it, don't depend on its file existing
here; it merges to `main` separately, before this skill's PR). Primary
sources there: Grafana docs + Saga design system, Dynatrace docs + Strato,
Aria Operations 8.16/8.18, Veeam ONE, Datadog/Zabbix/PRTG docs, WCAG 2.2,
NN/g, IBM Carbon, Atlassian, UK Analysis Function.

Adopted rules (already folded into §2–§7 above; listed here as the single
citable set, reference-approaches.md §11 numbering):

1. **§11.1** — Grey/Unknown is a separate state, never reads as OK (PRTG,
   Datadog, Carbon, Strato agree; Zabbix's opposite default is a documented
   complaint, ZBXNEXT-4116). Lowest priority when statuses combine (PRTG).
2. **§11.2** — "No data" and "evaluation error" are separate; rule health is
   a separate axis from rule state (Grafana; our `up` + `collector_health`).
3. **§11.3** — Every status is colour + shape + text (Strato, Carbon, WCAG
   1.4.1); the status icon itself needs 3:1 (WCAG 1.4.11).
4. **§11.4** — Accepted/silenced is a separate state: evaluation continues,
   notification stops, item stays visible (Grafana, Veeam, PRTG). Resolve
   must not be transient (Datadog's auto-revert is rejected).
5. **§11.5** — Alert list: open items always sort to the top (Dynatrace);
   default grouping is by rule, alternates by time/severity/entity-type
   (vROps, Grafana); a repeat-count column (Veeam ONE).
6. **§11.6** — Entity/host page: status/property/open-alert strip at top,
   sections below (Dynatrace, vROps); a layer counter like "1/33".
7. **§11.7** — Tables: real pagination, no infinite scroll; first column is
   the human-readable name; frozen header; numeric columns right-aligned;
   empty cell is `-` or `[x]`/`[z]`, never blank.
8. **§11.8** — Empty state is three distinct messages (loading / none /
   filtered-out), each with an action (NN/g, Saga, Atlassian).
9. **§11.9** — Destructive or silencing actions get more friction ("Tasteful
   Friction," Saga) than reading or acknowledging.

**Explicitly rejected** (reference-approaches.md §11): blinking new/resolved
problems (Zabbix — draw attention only to what's meaningful); acknowledged
colour identical to unacknowledged (Zabbix's default); a third
Health/Risk/Efficiency score axis on top of the three-valued model (vROps —
adds an axis our ADR-0026 model doesn't have, and its own Unknown visual is
undocumented); purple for Unknown (Carbon's Undefined colour); transient
Resolve (Datadog).

**Not found / left open** (reference-approaches.md §11): Grafana's official
state-colour table; vROps's -1 badge visual; Veeam ONE's grey/unknown state;
Dynatrace's acknowledge pattern; Strato's empty-state screen; Atlassian's
default table row/page size.

## See also

- `references/components.md` — the verified component catalogue (path +
  status for every name this skill or a PR brief might use).
