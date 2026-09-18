import type { ReactNode } from 'react'
import { cn, ramp, type StatusName } from '@/lib/ui'

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={cn('rounded-lg border border-border bg-card', className)}>{children}</div>
  )
}

export function StatusBadge({ status, children }: { status: StatusName; children: ReactNode }) {
  const tone = ramp(status)

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-xs font-medium',
        tone.surface,
        tone.border,
        tone.text,
      )}
    >
      {/*
        A dot as well as the colour. Colour alone fails for the roughly one in
        twelve men with a colour vision deficiency, and this product's entire
        job is communicating status.
      */}
      <span className={cn('size-1.5 rounded-full', tone.solid)} aria-hidden="true" />
      {children}
    </span>
  )
}

/**
 * A number with its label, for the triage row.
 *
 * The count is the largest thing on the card because it is what someone reads
 * from across the room.
 */
export function Metric({
  label,
  value,
  status,
  hint,
}: {
  label: string
  value: number | string
  status?: StatusName
  hint?: string
}) {
  const tone = status ? ramp(status) : null

  return (
    <Card className="p-4">
      <div className="text-xs uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={cn('mt-1 text-3xl font-semibold tabular', tone?.text)}>{value}</div>
      {hint !== undefined && <div className="mt-1 text-xs text-muted-foreground">{hint}</div>}
    </Card>
  )
}

/** Monospace, for anything an operator compares character by character. */
export function Identifier({ children }: { children: ReactNode }) {
  return <span className="font-mono text-xs text-muted-foreground">{children}</span>
}

/**
 * What to show when a request failed.
 *
 * Never an empty list. "Nothing is wrong" and "we could not ask" must not look
 * the same — that is product principle 1 in a component.
 */
export function LoadFailure({ what, error }: { what: string; error: unknown }) {
  const detail = error instanceof Error ? error.message : String(error)

  return (
    <Card className={cn('p-4', ramp('Unknown').surface)}>
      <div className="font-medium">{what} could not be loaded.</div>
      <div className="mt-1 text-sm text-muted-foreground">
        {detail} Nothing on this screen should be taken as current.
      </div>
    </Card>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return (
    <Card className="p-8 text-center text-sm text-muted-foreground">{children}</Card>
  )
}

export function Loading({ what }: { what: string }) {
  return <Card className="p-8 text-center text-sm text-muted-foreground">Loading {what}…</Card>
}
