import type { StatusName } from './ui'

/**
 * HPE's own words as a status (reference-approaches §10.8): FAULTY/DEFUNCT
 * red, SUSPECTED/DEGRADED yellow, SYNCING and OUT_OF_SCOPE informational (not
 * Centreon's "anything but SAFE warns"). Null — the source did not answer —
 * is Unknown, never SAFE (ADR-0026, §11.1). Presentation only; the alerts are
 * the collector's.
 */
export function simplivityStatus(value: string | null): StatusName {
  switch (value) {
    case null:
    case 'UNKNOWN':
      return 'Unknown'
    case 'ALIVE':
    case 'SAFE':
    case 'GREEN':
    case 'HEALTHY':
      return 'Healthy'
    case 'FAULTY':
    case 'DEFUNCT':
    case 'RED':
      return 'Critical'
    case 'SUSPECTED':
    case 'DEGRADED':
    case 'YELLOW':
      return 'Warning'
    default:
      return 'Info'
  }
}

/**
 * A counter card's status (SV6; eo-ux §3, one ramp per state): Critical when
 * any counted key is red in §10.8's words, so a FAULTY host never reads amber;
 * Warning for any other key that is not `good`; Unknown only when nothing else
 * is off; Healthy otherwise. `Unknown` is the API's key for "did not answer".
 * Pure so design/simplivity.check.ts can run it under node.
 */
export function counterStatus(counts: Record<string, number>, good: string): StatusName {
  const off = Object.keys(counts).filter((k) => counts[k] > 0 && k !== good && k !== 'Unknown')
  if (off.some((k) => simplivityStatus(k) === 'Critical')) return 'Critical'
  if (off.length > 0) return 'Warning'
  return (counts['Unknown'] ?? 0) > 0 ? 'Unknown' : 'Healthy'
}
