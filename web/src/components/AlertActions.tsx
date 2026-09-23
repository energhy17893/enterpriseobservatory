import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { cn } from '@/lib/ui'
import { ConfirmDestructive } from '@/components/Primitives'
import type { AlertView } from '@/api/types'

const SILENCE_OPTIONS = [
  { label: '1 hour', hours: 1 },
  { label: '4 hours', hours: 4 },
  { label: 'Tomorrow', hours: 24 },
] as const

/**
 * The three things an operator can do to an alert.
 *
 * Every one is attributed and recorded, so the buttons are deliberately plain
 * rather than reassuring: taking ownership of a problem and declaring it handled
 * are different claims, and both end up in a history somebody reads later.
 */
export function AlertActions({ alert }: { alert: AlertView }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const [silencing, setSilencing] = useState(false)
  const [confirmingClear, setConfirmingClear] = useState(false)

  const act = useMutation({
    mutationFn: (run: () => Promise<unknown>) => run(),
    onSuccess: () => {
      setError(null)
      setSilencing(false)
      // Everything showing alerts refetches: the inbox, the overview counts and
      // whatever entity page is open all read the same instances.
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) => {
      // Said next to the button that failed, not in a console. A refusal here
      // is usually "you are not signed in" or "that alert has resolved", and
      // both are things the operator can act on.
      setError(cause instanceof Error ? cause.message : String(cause))
    },
  })

  const busy = act.isPending
  const resolved = alert.state === 'Resolved'

  return (
    <div className="mt-2">
      <div className="flex flex-wrap items-center gap-2">
        {alert.state !== 'Acknowledged' && !resolved && (
          <Action
            busy={busy}
            onClick={() => act.mutate(() => api.acknowledge(alert.fingerprint))}
            title="Take ownership. Notifications stop; the alert stays in the inbox."
          >
            Acknowledge
          </Action>
        )}

        {!resolved && (
          <Action
            busy={busy}
            onClick={() => setSilencing((open) => !open)}
            title="Mute until a deadline, after which it returns on its own."
          >
            Silence…
          </Action>
        )}

        {!resolved && (
          <Action
            busy={busy}
            destructive
            onClick={() => setConfirmingClear(true)}
            title="Declare it handled. Re-observing the same fault will not reopen it."
          >
            Clear
          </Action>
        )}
      </div>

      <ConfirmDestructive
        open={confirmingClear}
        title={`Clear "${alert.title}"? This cannot be undone.`}
        confirmLabel="Clear"
        onConfirm={() => {
          setConfirmingClear(false)
          act.mutate(() => api.clear(alert.fingerprint))
        }}
        onCancel={() => setConfirmingClear(false)}
      />

      {silencing && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          {/*
            A deadline, never an indefinite mute. Something switched off for
            ever is something nobody remembers to switch back on.
          */}
          <span className="text-xs text-muted-foreground">Silence for</span>
          {SILENCE_OPTIONS.map((option) => (
            <Action
              key={option.label}
              busy={busy}
              onClick={() =>
                act.mutate(() =>
                  api.silence(
                    alert.fingerprint,
                    new Date(Date.now() + option.hours * 3600_000).toISOString(),
                  ),
                )
              }
            >
              {option.label}
            </Action>
          ))}
        </div>
      )}

      {error !== null && (
        <div className="mt-2 rounded-md border border-status-warning-border bg-status-warning-surface px-2 py-1 text-xs text-status-warning-text">
          {error}
        </div>
      )}
    </div>
  )
}

function Action({
  children,
  onClick,
  busy,
  title,
  destructive,
}: {
  children: string
  onClick: () => void
  busy: boolean
  title?: string
  /** A distinct destructive look (A3) — Clear is permanent, unlike Acknowledge. */
  destructive?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      title={title}
      className={cn(
        'rounded-md border px-2 py-1 text-xs',
        destructive ? 'border-destructive' : 'border-border',
        busy
          ? 'text-muted-foreground'
          : destructive
            ? 'text-foreground hover:bg-destructive hover:text-destructive-on'
            : 'text-foreground hover:bg-page',
      )}
    >
      {children}
    </button>
  )
}
