# UX critique, round 1 — Overview, Alerts, Events

Task U2, lane 4. Findings only: nothing here is implemented; each row becomes
its own small PR, briefed separately.

**Method.** Nobody in this lane can sign in to the running product, and a
screenshot is not evidence here, so every finding is read from source on
`main` @ `7328f1f`: the route, the shared components it uses, the generated
tokens, the copy. Checklist: `.claude/skills/eo-ux/SKILL.md` +
`references/components.md`; the `design:design-critique`,
`design:accessibility-review` (WCAG 2.2 AA applied, the skill quotes 2.1) and
`design:ux-copy` frameworks. Contrast figures come from the same OKLCH→sRGB
code as `web/design/validate-contrast.mjs`, run for pairs the gate does not
check today.

**Columns.** *Rule* cites eo-ux §, ADR §, reference-approaches §11.n or a WCAG
SC. *Kind*: `observatory` = token values in the observatory set only;
`structural` = shared code (layout, component, copy, gate). *Lines* is a rough
diff size. Rows are ordered by impact within each screen. Posture and
SimpliVity files are not touched by any row.

**Cross-screen findings** (X1–X3) are listed once, under Overview. Every
screen here uses `StatusBadge`, `Card` and the tokens, so fixing them fixes all
three screens.

---

## Overview (`web/src/routes/Overview.tsx`)

| # | Finding | Rule | Proposed change | Kind | Lines | Files |
|---|---|---|---|---|---|---|
| O1 | The Runner card disappears without a word when `/api/metrics` fails or is still loading (`{selfMetrics.data && …}`). A dead self-metrics endpoint looks exactly like "nothing to report", on the card whose job is to say whether the runner is working. | README principle 1; components.md `LoadFailure`/`Loading`; §11.8; ADR-0007 §6 | Render `<LoadFailure what="Runner metrics">` on error and `<Loading>` while pending, in the card's place. | structural | ~8 | `Overview.tsx` |
| O2 | The headline numbers lead nowhere. Critical / Warning / Unacknowledged / Failing collectors and the Estate health badges are not links. The operator reads "3 Critical" and then has to find and set the same filter by hand. | ADR-0007 §1 (one model, many views), §4; eo-ux §2; §11 Saga "Task-at-Hand" | Give `Metric` an optional `to`, link each card to the matching projection (`/alerts?severity=Critical`, `/alerts?state=Open` once A5 lands, `/collectors`), and each Estate badge to `/entities?health=…`. | structural | ~25 | `Overview.tsx`, `components/Primitives.tsx` |
| O3 | Two states the product tracks never reach the triage row. `OverviewView.suppressedAlerts` is fetched and not rendered, and Unknown alerts appear only in the Runner card, worded as a self-metric. An Unknown alert is still outside the inbox (ADR-0026) but it is a triage fact, not a runner fact. | §11.4 (silenced stays visible); §11.1 / ADR-0026 §3–4 (Unknown is its own state); eo-ux §4 | Add "Suppressed" and "Unknown" metrics to the top row, Unknown on the `Unknown` ramp and linked to the Unknown filter (A5). | structural | ~15 | `Overview.tsx` |
| X1 | **Status dot fails non-text contrast.** `StatusBadge` paints the dot with the `solid` role. On its own `surface` that measures 2.72:1 for Critical and 2.57:1 for Info in dark theme, under 3:1. The gate misses it because it never tests solid against surface. ADR-0008 §1 already names the right role: `border` is "dot, border, bar fill", and border against surface is ≥ 3.68:1 for every status in both themes. | WCAG 1.4.11; §11.3; ADR-0008 §1, §5 | Add a `dot` entry to `ramp()` (`bg-status-*-border`) and use it in `StatusBadge`. Add a `${s}: border / surface ≥ 3` rule to `validate-contrast.mjs`, so every set is checked. | structural | ~12 | `lib/ui.ts`, `components/Primitives.tsx`, `design/validate-contrast.mjs` |
| X2 | Every status has the same shape, a round dot. Text is always present, so WCAG 1.4.1 passes, but §11.3 adopts colour + **shape** + text: Critical and Warning badges differ only by hue at a glance. | §11.3 (Strato, Carbon); ADR-0008 §3 | Give `StatusBadge` one inline-SVG glyph per status: diamond for Critical, triangle for Warning, circle for Healthy, slashed circle for Unknown (Strato "Neutral"), `i` for Info. Keep `aria-hidden`, because the text still carries the meaning. | structural | ~30 | `components/Primitives.tsx` |
| O4 | The Runner card copy is internal vocabulary: "alert_history rows just appended", "Transitions/cycle", "Age-clamped", "Verdicts too old to trust". An operator cannot act on a table name. | eo-ux §6 (product copy is operator English); `design:ux-copy` (clear, no jargon) | Plain labels: "Alert changes last cycle", "Too old to trust", "Readings aged out to Unknown this cycle". Or move the card to Collectors, where the audience is the same. | structural | ~8 | `Overview.tsx` |
| X3 | Only colour is a token. eo-ux §8 defines a theme as colour, spacing scale, typography and density, but `tokens.mjs` holds colour alone. So the observatory set cannot express any of the density findings (A8), and every observatory PR is limited to hue and lightness. | eo-ux §8; ADR-0008 §4 | Add `space`, `radius`, `text` (size/leading) and `density` keys to both sets, emitted as CSS variables and mapped through `@theme inline`. Classic values reproduce today's Tailwind defaults exactly (the classic-unchanged diff stays empty). | structural | ~60 | `design/tokens.mjs`, `design/generate-css.mjs`, `styles/tokens.css` |
| O5 | Missing values show as `—` (em dash) in the Runner card, while eo-ux prescribes `-` or `[x]`/`[z]`. | eo-ux §6; §11.7 | Use `-` (or `[z]` with a legend for "no cycle yet"). | structural | ~2 | `Overview.tsx` |
| O6 | The metric row has no heading or grouping. It is four `div`s, so a screen-reader user lands on "Critical 3" with no context. The label and value are not associated. | WCAG 1.3.1 | Wrap the row in `<section aria-labelledby>` with a visually hidden `h2` ("Alerts"), and render `Metric` as `<dl>`/`<dt>`/`<dd>`. | structural | ~8 | `Overview.tsx`, `components/Primitives.tsx` |
| O7 | Light theme: card edges are barely there. `border` measures 1.35:1 on card and 1.29:1 on page (the gate floor is 1.3), and card vs page is 1.04:1. On the light Overview, the four metric cards and the Estate card read as one surface. | ADR-0008 §1; `design:design-critique` (hierarchy) | Observatory light `border` L 0.90 → 0.84 (1.63:1 on card, 1.56:1 on page). Dark is unaffected. | observatory | ~1 | `design/tokens.mjs` |

What works: failing collectors sit beside the alert counts, with "unknown, not
healthy" said in words; Vanished is kept separate from health; the two queries
fail independently; the count is the largest element (`Metric`).

---

## Alerts (`web/src/routes/Alerts.tsx`, `components/AlertActions.tsx`, `components/BulkBar.tsx`)

| # | Finding | Rule | Proposed change | Kind | Lines | Files |
|---|---|---|---|---|---|---|
| A1 | **Alerts past the first 50 can't be reached.** The API pages (`ReadModel.Alerts`, `limit = 50`), and the page shows "50 of N" with no way to see the rest. Nothing past row 50 is visible unless search happens to match it. Also the "flat list is always available" guarantee holds only for 50 rows. | eo-ux §7 (real pagination; the page size still has to be measured); §11.7; ADR-0007 §5.1 | Add `offset`/`limit` to the URL and Previous/Next controls with "51–100 of N". Keep the current 50 and label it "to be measured" per eo-ux §7. Clear the selection on page change, as a filter change already does. | structural | ~35 | `Alerts.tsx` |
| A2 | **The empty state claims something false under a filter.** With a severity or a search set, zero rows still says "Nothing is currently firing". That is untrue when Warnings are firing and the filter is Critical. | §11.8 (none vs filtered-out); eo-ux §6; README principle 1 | When a filter is active: "No alerts match these filters", with a "Clear filters" button. Keep the current text for the unfiltered case. | structural | ~10 | `Alerts.tsx` |
| A3 | **Clear has no friction.** Clear is permanent ("re-observing the same fault will not reopen it"), but it sits between Acknowledge and Silence with identical styling and fires on one click. In `BulkBar` it clears N alerts in one click. Silence already has a second step; Clear, the riskier action, does not. | §11.9 (Tasteful Friction); §11.4; ADR-0008 §2 (destructive shares critical's hue) | Two-step confirm, as Silence does: "Clear 12 alerts? They will not reopen if the fault is seen again." with "Clear 12" / "Keep". Give the confirm button destructive styling. That needs a `destructive` token pair, which ADR-0008 §2 names but `tokens.mjs` lacks, added to both sets. | structural | ~30 | `components/AlertActions.tsx`, `components/BulkBar.tsx`, `design/tokens.mjs` |
| A4 | Acknowledged and Silenced alerts look like Open ones. The only difference is a grey text chip, and they sort together by severity and then last-seen. §11.5 puts open items at the top. §11.4 lists "acknowledged colour identical to unacknowledged" as *rejected*. | §11.5; §11.4 (rejected list); eo-ux §4 | Visual: acknowledged and silenced rows get a reduced-emphasis severity badge (outline instead of fill) and a state chip on the `Info` ramp. Order: open first. That is a `ReadModel.Alerts` sort (backend) and **out of this lane**, see the questions below. Sorting on the client would be wrong once A1 paginates. | structural | ~15 web | `Alerts.tsx`, (`ReadModel.cs` — question) |
| A5 | There is no state filter, so Unknown alerts can't be reached. The API takes `state=Unknown`, and ADR-0026's design note says Unknown alerts are "listed under their own filter only", but the UI offers no such filter. The severity filter also omits `Info`, which is a real `AlertSeverity`. | §11.1; ADR-0026 §3–4; eo-ux §4 | Add a State chip row (Open / Acknowledged / Silenced / Unknown) bound to `?state=`, and add Info to severity. | structural | ~15 | `Alerts.tsx` |
| A6 | Toggle state isn't exposed to assistive tech. The severity chips are buttons with no `aria-pressed`. The Events / All alerts switch is a link plus a styled `span`, with no `aria-current`. | WCAG 4.1.2, 1.3.1 | `aria-pressed={severity === value}` on the chips. The shared switch lands in E6. | structural | ~4 | `Alerts.tsx` |
| A7 | The title doesn't follow `<what> — <where>`. The entity is a separate link under the description, so the row's heading line never says where. Verify against live titles first: some rule titles may already embed the entity. | eo-ux §6 | Render `{title} — {entityName}` on the first line, with the entity as the link. | structural | ~6 | `Alerts.tsx` (and the shared row, E1) |
| A8 | Each alert is a card of about 120 px, with description, meta line and three buttons, so a laptop screen holds roughly five alerts. At Kibar scale (1,100 VMs) that is scrolling, not triage. There is also no repeat-count column (§11.5, Veeam), and `AlertView` has no count field. | eo-ux §7 (density); §11.7 (name first, frozen header, numeric right-aligned); §11.5 | A table row per alert (severity, `<what> — <where>`, state, since, last seen), with actions in the row. The repeat count needs an API field (a question). Depends on X3 for the density tokens. | structural | ~120 | `Alerts.tsx`, shared row (E1) |
| A9 | Relative times ("seen 3m ago") have no absolute time available. vCenter events set `title={iso}`, alert rows do not. | `design:design-critique` (consistency) | Use `<time dateTime title>` in both places. | structural | ~4 | `Alerts.tsx` |

What works: filters live in the URL; a filter change drops the selection, so
you never act on rows you can't see; `keepPreviousData` stops the list
blinking; suppression and Derived are said out loud; `BulkBar` shows the count
before acting and reports alerts that "had already resolved".

---

## Events (`web/src/routes/Events.tsx`)

| # | Finding | Rule | Proposed change | Kind | Lines | Files |
|---|---|---|---|---|---|---|
| E1 | **One alert, two presentations.** Events' route-local `Row` has drifted from the Alerts row. It drops the "Notification suppressed" and "Derived" chips, drops "since", shows state as bare text instead of a chip, and its checkbox has no accessible name. The same alert tells the operator less in Events than in the inbox, and a derived alert reads as observed. | ADR-0007 §5 (same instance, same actions and presentation); README principle 1 (a guess is never shown as a measurement); WCAG 4.1.2; components.md (four unrelated `Row`s) | Promote one `AlertRow` to `web/src/components/` and use it in Alerts, Events, and the entity page's alert list. This is also where A4, A7 and A9 land once. | structural | ~70 (net ≈ −20) | new `components/AlertRow.tsx`, `Alerts.tsx`, `Events.tsx` |
| E2 | **The vCenter feed is silently truncated.** The client asks for `limit: 200` and filters those 200 in the browser. Nothing says only the newest 200 are shown, so "Nothing matches the filter" can be false for an older event still within `retentionDays`. The incident board has no paging or virtualization either. | eo-ux §1 (>100 rows virtualized), §7; §11.7; §11.8 | Say it: "Newest 200 of the last N days". Word the empty filter result as "Nothing in the newest 200 matches". Server-side search and paging are an API question. | structural | ~10 web | `Events.tsx` |
| E3 | "Possibly related" window is rounded to whole minutes. `Math.round(windowSeconds / 60)` renders a 30 s window, the example in ADR-0007 §5.2, as "within 0 minutes of each other". | ADR-0007 §5.2; `design:ux-copy` (accurate) | Format under 60 s as seconds ("within 30 seconds"). | structural | ~3 | `Events.tsx` |
| E4 | Incident and Suggestion headers are disclosure buttons without `aria-expanded`. The ▸/▾ glyph is read aloud. | WCAG 4.1.2, 1.1.1 | `aria-expanded={open}` + `aria-controls`; glyph `aria-hidden`. | structural | ~6 | `Events.tsx` |
| E5 | Group-header copy: "12 alerts · 8 things". Every other screen, and ADR-0007 §5.1's own example ("12 alerts · 8 entities"), says entities. | ADR-0007 §5.1; `design:ux-copy` (consistent terms) | `count(n, 'entity')`, with an irregular plural (`entities`). | structural | ~3 | `Events.tsx` |
| E6 | The view switch differs by page and by semantics. On Events it is "Events" (button) + "All alerts" (link) + "From vCenter" (button). On Alerts it is a link and a span, with no vCenter entry. None exposes the current view (`aria-current`/`aria-pressed`). The vCenter view is component state, so reload or Back loses it, and the header counts disappear while it is open. | ADR-0007 §5.1 (flat list one click away, both ways); WCAG 4.1.2; eo-ux §0 (reuse) | One shared `ViewSwitch` of three links with `aria-current="page"`. vCenter becomes `/events?view=vcenter`. | structural | ~25 | `Events.tsx`, `Alerts.tsx` (+ small shared component) |
| E7 | vCenter filter input: no label (placeholder only), and `bg-page` where the Alerts search uses `bg-card`. | WCAG 3.3.2, 4.1.2; consistency | `aria-label="Filter vCenter events"`; `bg-card`. | structural | ~2 | `Events.tsx` |
| E8 | Copy: "No vCenter's events have been read yet." is ungrammatical. vCenter severities render lowercase ("error", "warning") next to this product's Title-case badges. | `design:ux-copy`; eo-ux §6 | "No vCenter events have been read yet. They are read every inventory cycle." Capitalise the severity label. | structural | ~3 | `Events.tsx` |

What works: the header arithmetic ("N alerts · M in K events · J on their
own"); the explanation of why alerts were grouped; temporal suggestions are
never folded, dashed, and repeated below; each vCenter stream shows whether it
was read, with a gap warning.

---

## Counts

| Screen | Findings | Of which observatory |
|---|---|---|
| Overview (incl. cross-screen X1–X3) | 10 | 1 (O7) |
| Alerts | 9 | 0 |
| Events | 8 | 0 |

Most findings are structural. That is expected: the tokens already pass the
gate, and what the source shows is mostly missing states and copy, not
colour. X3 is what opens up more observatory work (density, spacing).

## Top 5 by impact

1. **A1**: alerts past 50 can't be reached; the inbox has no pagination.
2. **E1**: Events and Alerts render the same alert differently; Events hides Derived and suppression.
3. **A2**: the filtered empty state says "Nothing is currently firing".
4. **A3**: permanent Clear, single and bulk, has no confirmation.
5. **X1**: status dot below 3:1 (dark Critical 2.72, Info 2.57), unseen by the gate.

## Questions (not guessed)

- **A4 / A8 / E2** need API changes: open-first sort in `ReadModel.Alerts`, a
  repeat-count field on `AlertView`, and server-side search/paging for
  `/api/vcenter-events`. This lane is "no backend changes". Should these be
  briefed to another lane, or dropped?
- **A3** needs a `destructive` token pair (ADR-0008 §2 names it,
  `tokens.mjs` lacks it). Adding a key touches both sets. Is that acceptable
  as one structural PR, with classic taking critical's `solid` value?
