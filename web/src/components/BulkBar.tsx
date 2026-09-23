import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { cn } from '@/lib/ui'
import { ConfirmDestructive } from '@/components/Primitives'
import type { BulkActionView } from '@/api/types'

const SILENCE_OPTIONS = [
  { label: '1 hour', hours: 1 },
  { label: '4 hours', hours: 4 },
  { label: 'Tomorrow', hours: 24 },
] as const

/**
 * Acting on a selection.
 *
 * Appears only when something is selected, and says how many. Twenty alerts
 * from one failed switch is one problem an operator is taking on, not twenty
 * decisions — but it is still twenty rows in an audit trail, so the count is in
 * front of them before they press anything.
 */
export function BulkBar({
  selected,
  onDone,
}: {
  selected: string[]
  onDone: () => void
}) {
  const queryClient = useQueryClient()
  const [silencing, setSilencing] = useState(false)
  const [confirmingClear, setConfirmingClear] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  const act = useMutation({
    mutationFn: (run: () => Promise<BulkActionView>) => run(),
    onSuccess: (result) => {
      // Alerts that resolved between the screen being drawn and the button
      // being pressed are simply gone. Said out loud, because an operator who
      // asked for twenty and changed eighteen should be told rather than left
      // to count.
      setNote(
        result.missing > 0
          ? `${result.alerts.length} changed. ${result.missing} had already resolved.`
          : null,
      )
      setSilencing(false)
      onDone()
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) =>
      setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  if (selected.length === 0) {
    return null
  }

  return (
    <div className="sticky top-0 z-10 rounded-lg border border-primary bg-card p-3 shadow">
      <div className="flex flex-wrap items-center gap-3">
        <span className="text-sm font-medium tabular">{selected.length} selected</span>

        <button
          type="button"
          disabled={act.isPending}
          onClick={() => act.mutate(() => api.acknowledgeMany(selected))}
          className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
        >
          Acknowledge
        </button>

        <button
          type="button"
          disabled={act.isPending}
          onClick={() => setSilencing((open) => !open)}
          className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
        >
          Silence…
        </button>

        <button
          type="button"
          disabled={act.isPending}
          onClick={() => setConfirmingClear(true)}
          className="rounded-md border border-destructive px-2 py-1 text-xs hover:bg-destructive hover:text-destructive-on"
        >
          Clear
        </button>

        <button
          type="button"
          onClick={onDone}
          className="ml-auto text-xs text-muted-foreground hover:text-foreground"
        >
          Deselect
        </button>
      </div>

      {silencing && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <span className="text-xs text-muted-foreground">Silence all {selected.length} for</span>
          {SILENCE_OPTIONS.map((option) => (
            <button
              key={option.label}
              type="button"
              disabled={act.isPending}
              onClick={() =>
                act.mutate(() =>
                  api.silenceMany(
                    selected,
                    new Date(Date.now() + option.hours * 3600_000).toISOString(),
                  ),
                )
              }
              className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
            >
              {option.label}
            </button>
          ))}
        </div>
      )}

      {note !== null && (
        <div className={cn('mt-2 text-xs', 'text-muted-foreground')}>{note}</div>
      )}

      <ConfirmDestructive
        open={confirmingClear}
        title={`Clear ${selected.length} alert${selected.length === 1 ? '' : 's'}? This cannot be undone.`}
        confirmLabel={`Clear ${selected.length}`}
        onConfirm={() => {
          setConfirmingClear(false)
          act.mutate(() => api.clearMany(selected))
        }}
        onCancel={() => setConfirmingClear(false)}
      />
    </div>
  )
}
