import { useEffect, useId, useRef, type ReactNode } from 'react'
import { ago, cn, ramp, type StatusName } from '@/lib/ui'

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

        Uses the `border` role, not `solid`: `solid` is tuned against its own
        `solidOn` text, not against `surface`, and on this badge's own
        background it drops as low as 2.57:1 (dark Info) — under WCAG 2.2
        1.4.11's 3:1 floor for a non-text mark. `border` is the role ADR-0008
        §1 names for "dot, border, bar fill" and measures >= 3.68:1 against
        `surface` in every set and theme (validate-contrast.mjs checks this).
      */}
      <span className={cn('size-1.5 rounded-full', tone.dot)} aria-hidden="true" />
      {children}
    </span>
  )
}

/**
 * The one stale mark (ADR-0007 §6, eo-ux §4): a stale answer is said, never
 * shown as fresh. Colour, dot and the word "stale" (reference §11.3) on the
 * Unknown ramp.
 *
 * `{ sinceUtc }` marks one thing ("stale" / "stale since 3h ago");
 * `{ count }` says how many of a set are stale ("Stale 4"), and renders
 * nothing at zero. A stale failing finding is still failing -- the count is
 * said beside the verdicts, never folded into them.
 */
export function StaleBadge(props: { sinceUtc?: string | null } | { count: number }) {
  if ('count' in props) {
    if (props.count === 0) return null
    return <StatusBadge status="Unknown">Stale {props.count}</StatusBadge>
  }
  return (
    <StatusBadge status="Unknown">
      stale{props.sinceUtc && <> since {ago(props.sinceUtc)}</>}
    </StatusBadge>
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

export function Empty({
  children,
  action,
}: {
  children: ReactNode
  /** e.g. a "Clear filters" button — kept out of `children` so callers don't have to lay out their own button row. */
  action?: ReactNode
}) {
  return (
    <Card className="p-8 text-center text-sm text-muted-foreground">
      {children}
      {action !== undefined && <div className="mt-3">{action}</div>}
    </Card>
  )
}

export function Loading({ what }: { what: string }) {
  return <Card className="p-8 text-center text-sm text-muted-foreground">Loading {what}…</Card>
}

/**
 * A destructive confirmation step (A3: Clear is permanent and previously
 * fired on one click, single and bulk, styled identically to Acknowledge).
 *
 * Uses a native `<dialog>` — no library needed for a modal. `showModal`
 * traps focus and puts it on the first focusable element, which is `Keep`
 * (DOM order), so the safe choice is the keyboard default; `Escape` fires the
 * dialog's own `cancel` event, handled below the same way as clicking `Keep`.
 * Focus returns to whatever opened the dialog on close, tracked via
 * `document.activeElement` at open time rather than a caller-supplied ref, so
 * this works for the single-row and the bulk-bar trigger alike.
 */
export function ConfirmDestructive({
  open,
  title,
  confirmLabel,
  onConfirm,
  onCancel,
}: {
  open: boolean
  title: string
  confirmLabel: string
  onConfirm: () => void
  onCancel: () => void
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const opener = useRef<Element | null>(null)
  const titleId = useId()

  useEffect(() => {
    if (open) {
      opener.current = document.activeElement
      ref.current?.showModal()
    } else {
      ref.current?.close()
      if (opener.current instanceof HTMLElement) {
        opener.current.focus()
      }
    }
  }, [open])

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      className="rounded-lg border border-destructive bg-card p-4 text-foreground backdrop:bg-black/40"
      onCancel={(event) => {
        // Escape reaches here by default; treat it exactly like Keep.
        event.preventDefault()
        onCancel()
      }}
    >
      <p id={titleId} className="max-w-sm text-sm">
        {title}
      </p>
      <div className="mt-4 flex justify-end gap-2">
        <button
          type="button"
          autoFocus
          onClick={onCancel}
          className="rounded-md border border-border px-3 py-1 text-xs text-foreground hover:bg-page"
        >
          Keep
        </button>
        <button
          type="button"
          onClick={onConfirm}
          className="rounded-md border border-destructive bg-destructive px-3 py-1 text-xs text-destructive-on"
        >
          {confirmLabel}
        </button>
      </div>
    </dialog>
  )
}
