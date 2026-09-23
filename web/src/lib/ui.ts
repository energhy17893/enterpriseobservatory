import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'
import type { AlertSeverity, FindingState, HealthBasis, HealthState } from '@/api/types'

/** The shadcn/ui class helper, kept so generated components drop in unchanged. */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/**
 * The one place a status becomes a colour.
 *
 * Components never name a palette colour directly (`bg-red-500` is forbidden by
 * ADR-0006); they name a status and get the token ramp for it. That is what
 * makes the contrast gate in CI meaningful — if colours were chosen at call
 * sites, validating the tokens would prove nothing about the screens.
 */
const RAMPS = {
  Healthy: {
    surface: 'bg-status-healthy-surface',
    border: 'border-status-healthy-border',
    text: 'text-status-healthy-text',
    solid: 'bg-status-healthy-solid text-status-healthy-solid-on',
    dot: 'bg-status-healthy-border',
  },
  Warning: {
    surface: 'bg-status-warning-surface',
    border: 'border-status-warning-border',
    text: 'text-status-warning-text',
    solid: 'bg-status-warning-solid text-status-warning-solid-on',
    dot: 'bg-status-warning-border',
  },
  Critical: {
    surface: 'bg-status-critical-surface',
    border: 'border-status-critical-border',
    text: 'text-status-critical-text',
    solid: 'bg-status-critical-solid text-status-critical-solid-on',
    dot: 'bg-status-critical-border',
  },
  Info: {
    surface: 'bg-status-info-surface',
    border: 'border-status-info-border',
    text: 'text-status-info-text',
    solid: 'bg-status-info-solid text-status-info-solid-on',
    dot: 'bg-status-info-border',
  },
  Unknown: {
    surface: 'bg-status-unknown-surface',
    border: 'border-status-unknown-border',
    text: 'text-status-unknown-text',
    solid: 'bg-status-unknown-solid text-status-unknown-solid-on',
    dot: 'bg-status-unknown-border',
  },
} as const

export type StatusName = keyof typeof RAMPS

export function ramp(status: StatusName) {
  return RAMPS[status]
}

/**
 * Health as a status name.
 *
 * Unknown is a status of its own, never folded into healthy. "We cannot see it"
 * and "it is fine" are the two claims product principle 1 refuses to confuse.
 */
export function healthStatus(health: HealthState): StatusName {
  return health
}

export function severityStatus(severity: AlertSeverity): StatusName {
  return severity
}

/**
 * K3: a human sentence for `EntityView.healthBasis` -- why the badge says what
 * it says, not only what colour it is. Kept short enough for a row; the two
 * "no alert" cases and the two "no answer" cases read differently on purpose
 * (ADR-0018/ADR-0026): a quiet, reporting source is healthy; a silent one is
 * Unknown even with zero alerts, because absence of evidence is not evidence.
 */
export function healthBasisLabel(basis: HealthBasis): string {
  switch (basis) {
    case 'NoAlertsSourceReporting':
      return 'no alert, source reporting'
    case 'NoAlertsSourceSilent':
      return 'no alert, but source went silent'
    case 'Alerts':
      return 'from its own alerts'
    case 'UnknownAlerts':
      return 'only Unknown alerts against it'
    case 'NotObserved':
      return 'never observed'
  }
}

/**
 * K3: the same fact as {@link healthBasisLabel}, short enough for a table row
 * or a badge. Only the three bases that ever produce grey (Unknown) need a
 * phrase here -- a green or coloured entity does not say why, only a grey one
 * has to (ADR-0018/ADR-0026: grey means "we do not know", and an operator
 * reading a list of them needs to tell "source silent" apart from "only
 * Unknown alerts" apart from "never observed" without opening each one).
 */
export function healthBasisShort(basis: HealthBasis): string | null {
  switch (basis) {
    case 'NoAlertsSourceSilent':
      return 'source silent'
    case 'UnknownAlerts':
      return 'only unknown alerts'
    case 'NotObserved':
      return 'not observed'
    case 'NoAlertsSourceReporting':
    case 'Alerts':
      return null
  }
}

/**
 * A finding's state as a status name, the compliance screen's mapping:
 * failing is a warning (not an outage), accepted and excepted are decisions
 * on record, not evaluated is unknown -- never folded into passing.
 */
export function findingStatus(state: FindingState): StatusName {
  switch (state) {
    case 'Failing':
      return 'Warning'
    case 'Accepted':
    case 'Excepted':
      return 'Info'
    case 'Passing':
      return 'Healthy'
    case 'NotEvaluated':
      return 'Unknown'
  }
}

export function findingLabel(state: FindingState): string {
  return state === 'NotEvaluated' ? 'Not evaluated' : state
}
/**
 * How long ago something happened, in words.
 *
 * Deliberately blunt at the coarse end: past a day, the exact figure stops
 * being the point and "3d ago" is what an operator needs to see.
 */
export function ago(iso: string | null, now: number = Date.now()): string {
  if (iso === null) {
    return 'never'
  }

  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000))

  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`

  return `${Math.floor(seconds / 86_400)}d ago`
}

/**
 * Whether an answer is old enough that presenting it as current would be a lie.
 *
 * ADR-0007 §6 forbids showing stale data as fresh. This is the threshold the
 * interface uses to start saying so — generous enough that a slow cycle does
 * not cry wolf, short enough that a disconnected wallboard admits it long
 * before anyone makes a decision on it.
 */
export const STALE_AFTER_MS = 120_000

export function isStale(iso: string | null, now: number = Date.now()): boolean {
  return iso === null || now - Date.parse(iso) > STALE_AFTER_MS
}
