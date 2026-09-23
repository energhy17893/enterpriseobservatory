/**
 * Self-check for `orderCatalogues` -- the web has no test runner (see
 * package.json: `build`/`typecheck`/`lint` only), so this follows the
 * pattern `design/validate-contrast.mjs` already uses: a plain script run
 * with `node`, asserting and exiting 1 on failure. Lives outside `src/`
 * (like the rest of `design/`) so `tsc -b`'s `src` project never sees it --
 * a script meant for `node`, not the app bundle.
 *
 * Run:  node design/catalogues.check.ts   (from web/)
 */
import { orderCatalogues } from '../src/lib/catalogues.ts'

type Stub = { id: string; owner: string }

function assertOrder(label: string, input: Stub[], expectedIds: string[]) {
  const got = orderCatalogues(input).map((c) => c.id)
  const gotStr = got.join(',')
  const wantStr = expectedIds.join(',')
  if (gotStr !== wantStr) {
    console.log(`FAIL  ${label}: got [${gotStr}] want [${wantStr}]`)
    process.exitCode = 1
    return
  }
  console.log(`PASS  ${label}`)
}

// Four catalogues, Broadcom already first: unchanged, all four kept.
assertOrder(
  'four catalogues, registration order',
  [
    { id: 'scg', owner: 'Broadcom' },
    { id: 'eo-continuity', owner: 'Product' },
    { id: 'eo-bestpractice', owner: 'Product' },
    { id: 'eo-simplivity', owner: 'Product' },
  ],
  ['scg', 'eo-continuity', 'eo-bestpractice', 'eo-simplivity'],
)

// Two catalogues given in reverse order: Broadcom still moves first, and
// the product catalogue keeps its place -- same view either way the API
// happens to register them.
assertOrder(
  'two catalogues, reverse order',
  [
    { id: 'eo-continuity', owner: 'Product' },
    { id: 'scg', owner: 'Broadcom' },
  ],
  ['scg', 'eo-continuity'],
)

if (process.exitCode) {
  console.log('BASARISIZ: orderCatalogues failed one or more cases.')
} else {
  console.log('All orderCatalogues cases passed.')
}
