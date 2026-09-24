# Component catalogue (verified 2026-09-23)

Every entry below was confirmed by grep/glob against `web/src` on this branch.
Do not add a name here without a matching path. If a screen brief names a
component below marked "does not exist yet," either reuse the nearest real
one or open a proposal to promote a route-local function into
`web/src/components` — never invent the file.

## Shared components — `web/src/components/`

| Component | Path | Notes |
|---|---|---|
| `Card` | `web/src/components/Primitives.tsx:4` | Base container, `rounded-lg border border-border bg-card`. |
| `StatusBadge` | `web/src/components/Primitives.tsx:10` | **This is the pill component — there is no `StatusPill`.** Colour + dot + text, per ADR-0008 §3. Takes a `StatusName` (`Healthy`/`Warning`/`Critical`/`Info`/`Unknown`). |
| `Metric` | `web/src/components/Primitives.tsx:39` | Big-number card for triage rows; count is the largest element ("read from across the room"). |
| `Identifier` | `web/src/components/Primitives.tsx:62` | Monospace (Fira Code) span for WWN/IP/UUID-style values. |
| `LoadFailure` | `web/src/components/Primitives.tsx:72` | **The empty-state-for-errors component** — a failed request, styled with the `Unknown` ramp. Distinct from "no rows": never let a failed fetch render as if the list is simply empty (README principle 1). |
| `Empty` | `web/src/components/Primitives.tsx:85` | Plain "nothing here" row for a genuinely empty result set. |
| `Loading` | `web/src/components/Primitives.tsx:91` | Loading placeholder card. |
| `AlertActions` | `web/src/components/AlertActions.tsx:20` | Acknowledge / silence / resolve controls for one `AlertView`. Every action is attributed (ADR-0013). |
| `AlertRow` | `web/src/components/AlertRow.tsx` | The one alert row (ADR-0007 §5) — badge/state/`Notification suppressed`/`Derived`, entity link, `AlertActions`, selection checkbox with an accessible name (`Select alert: <title>`). Used by both `Alerts.tsx` (flat inbox) and `Events.tsx` (grouped/ungrouped/suggestions). Props: `alert: AlertView`, `selected: boolean`, `onToggle: () => void`. |
| `BulkBar` | `web/src/components/BulkBar.tsx:21` | Appears when rows are multi-selected; shows the count before any destructive action (README principle 4 — many rows, one decision, still N audit entries). |
| `Pager` | `web/src/components/Pager.tsx` | The A1 pager: "a–b of total" (announced, `aria-live`) plus Previous/Next, disabled at the ends. Props: `offset`, `pageSize`, `total`, `onOffset(next)`, `unit?` (plural noun, e.g. `"entities"`). The caller keeps `offset` in the URL and deletes it on any filter change. Used by `Alerts.tsx`, `Events.tsx` (vCenter events) and `Entities.tsx`. Reference §11.7: real pagination, not infinite scroll. |
| `Shell` | `web/src/components/Shell.tsx:69` | App frame / nav shell, takes `identity: AuthStateView`. |
| `SeriesChart` | `web/src/components/SeriesChart.tsx` | Time-series chart component (binary-detected by grep; not text-inspected in this pass — read it directly if you need its props). |

## Names the brief in this task expected, and what's actually there

| Expected name | Reality |
|---|---|
| `StatusPill` | **Does not exist under that name.** The real component is `StatusBadge` (`Primitives.tsx:10`). Use that. |
| `FindingRow` | **Not a shared component — it's a route-local function**, defined separately in two places: `web/src/routes/Compliance.tsx:544` and `web/src/routes/reports/ComplianceReport.tsx:340` (different props/shape in each). There is no single reusable `FindingRow` in `web/src/components`. |
| scorecard | **Not a shared component.** `CatalogueScorecard` is a route-local function in `web/src/routes/Compliance.tsx:103`, backed by `CatalogueScorecardView` (`web/src/api/types.ts:612`) and `scorecardStatus()` (`Compliance.tsx:68`). |
| empty-state row | Two different real things, don't conflate them: `Empty` (`Primitives.tsx:85`, genuinely no rows) vs. `LoadFailure` (`Primitives.tsx:72`, the request failed). Neither is called "empty-state row" in code. |
| unknown/stale row | **Does not exist as one reusable component.** Handled ad hoc per screen: `StaleBadge` is route-local to `web/src/routes/Compliance.tsx:87`; the entity list's stale/unknown treatment is inline JSX in `web/src/routes/Entities.tsx` (~line 161, `entity.healthIsStale` + `StatusBadge status="Unknown"`) and `web/src/routes/EntityDetail.tsx` (~line 79). All three duplicate similar logic. |
| "accepted" badge | **Does not exist as a badge/component.** "Accepted" is plain inline text next to a finding in `web/src/routes/Compliance.tsx` (~line 586: `finding.acceptedBy`/`acceptedReason`), not a badge component. `FindingState` includes `'Accepted'` (`web/src/api/types.ts`), mapped to the `Info` status ramp by `findingStatus()` in `web/src/lib/ui.ts:120`. |

**These five gaps (`FindingRow` ×2, `CatalogueScorecard`, the duplicated
stale/unknown handling, and the missing accepted badge) are candidates for
promotion to shared `web/src/components`, not decided here — that's U2's
call, made against real screen work, not speculatively in this skill.**

## Supporting, non-component code worth knowing

| File | What it's for |
|---|---|
| `web/src/lib/ui.ts` | `cn()`, `ramp()` (the only place a status becomes a Tailwind class), `healthStatus`, `severityStatus`, `healthBasisLabel`/`healthBasisShort`, `findingStatus`/`findingLabel`, `ago()`, `isStale`/`STALE_AFTER_MS`. Start here before writing a new status→colour mapping. |
| `web/src/lib/basis.ts` | `basisLabel()`/`NO_CITATION` — the "basis:" line text, shared with the CSV/mailed report wording (see §4/§6 of SKILL.md). |
| `web/design/tokens.mjs` | Single source of truth for all colour tokens (ADR-0008 §4). |
| `web/design/generate-css.mjs` | Generates `web/src/styles/tokens.css` from `tokens.mjs` — never hand-edit the generated file. |
| `web/design/validate-contrast.mjs` | CI contrast/gamut gate (ADR-0008 §5). |

## Route-local row/badge functions (not in `web/src/components`, listed for awareness)

These exist and work, but are not shared — each is defined inside its own
route file and duplicated where similar UI is needed elsewhere:

- `StaleBadge` — `web/src/routes/Compliance.tsx:87`
- `ControlRow` — `web/src/routes/Compliance.tsx:421`
- `FindingRow` — `web/src/routes/Compliance.tsx:544`
- `ControlRow` (different one) / `FindingRow` / `RemovedExceptionRow` / `HistoryRow` — `web/src/routes/reports/ComplianceReport.tsx:326,340,388,401`
- `ClusterRow`, `ControlSummaryRow` — `web/src/routes/reports/ContinuityReport.tsx:256,299`
- `Row` — `web/src/routes/Accounts.tsx:124`, `web/src/routes/Connections.tsx:313`, `web/src/routes/ScheduledReports.tsx:107` (three unrelated `Row` functions, same name, different shape — don't assume they're interchangeable; `Events.tsx`'s former `Row` is now the shared `AlertRow` component, see above)
- `ReportRow` — `web/src/routes/reports/AlertsReport.tsx:245`, `web/src/routes/reports/CapacityReport.tsx:145`
