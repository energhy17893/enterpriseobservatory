/**
 * Self-check for `counterStatus` (SV6), same pattern as catalogues.check.ts:
 * plain node with built-in type stripping (Node >= 22.6), exit 1 on failure.
 *
 * Run:  node design/simplivity.check.ts   (from web/)
 */
import { counterStatus } from '../src/lib/simplivity.ts'

const cases: [string, Record<string, number>, string][] = [
  ['FAULTY 1 + ALIVE 25', { FAULTY: 1, ALIVE: 25 }, 'Critical'],
  ['SUSPECTED 1', { SUSPECTED: 1 }, 'Warning'],
  ['Unknown 2', { Unknown: 2 }, 'Unknown'],
  ['all ALIVE', { ALIVE: 26 }, 'Healthy'],
]

for (const [label, counts, want] of cases) {
  const got = counterStatus(counts, 'ALIVE')
  if (got !== want) {
    console.log(`FAIL  ${label}: got ${got} want ${want}`)
    process.exitCode = 1
  } else {
    console.log(`PASS  ${label}`)
  }
}
