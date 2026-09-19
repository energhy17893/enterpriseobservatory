import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading } from '@/components/Primitives'
import { ago, cn } from '@/lib/ui'
import type { AuthStateView, MaintenanceWindowView } from '@/api/types'

const DURATIONS = [
  { label: '1 hour', hours: 1 },
  { label: '4 hours', hours: 4 },
  { label: 'Overnight (12h)', hours: 12 },
  { label: 'A day', hours: 24 },
] as const

/**
 * Planned work.
 *
 * A window suppresses notification and never observation. The alerts still
 * appear, carrying the name of the window that silenced them — which is what
 * makes the window useful afterwards as an account of what actually broke
 * during the work, rather than a way of pretending nothing did.
 */
export function Maintenance({ identity }: { identity: AuthStateView }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)

  const windows = useQuery({
    queryKey: ['maintenance'],
    queryFn: api.maintenanceWindows,
    refetchInterval: 30_000,
  })

  const act = useMutation({
    mutationFn: (run: () => Promise<unknown>) => run(),
    onSuccess: () => {
      setError(null)
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : String(cause)),
  })

  const canDeclare = identity.role === 'Operator' || identity.role === 'Administrator'

  if (windows.isError) return <LoadFailure what="Maintenance windows" error={windows.error} />
  if (windows.isPending) return <Loading what="maintenance windows" />

  const active = windows.data.filter((w) => w.active)
  const scheduled = windows.data.filter((w) => w.scheduled)
  const past = windows.data.filter((w) => !w.active && !w.scheduled)

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold">Maintenance</h1>

      <Card className="p-4 text-sm text-muted-foreground">
        A window stops notifications going out. It does not stop anything being watched, and it
        does not hide the alerts — they stay in the inbox marked with the window that silenced
        them, so you can see afterwards what actually broke during the work.
      </Card>

      {canDeclare && <Declare onDone={() => act.mutate(async () => {})} />}

      <Group title="Active now" windows={active} onEnd={(id) => act.mutate(() => api.endMaintenanceWindow(id))} canEnd={canDeclare} empty="Nothing is being suppressed right now." />
      <Group title="Scheduled" windows={scheduled} onEnd={(id) => act.mutate(() => api.endMaintenanceWindow(id))} canEnd={canDeclare} empty="No planned work is scheduled." />
      <Group title="Finished" windows={past} onEnd={null} canEnd={false} empty="No past windows yet." />

      {error !== null && (
        <div className="rounded-md border border-status-critical-border bg-status-critical-surface px-2 py-1.5 text-xs text-status-critical-text">
          {error}
        </div>
      )}
    </div>
  )
}

function Group({
  title,
  windows,
  onEnd,
  canEnd,
  empty,
}: {
  title: string
  windows: MaintenanceWindowView[]
  onEnd: ((id: string) => void) | null
  canEnd: boolean
  empty: string
}) {
  return (
    <section className="space-y-2">
      <h2 className="text-sm font-medium">{title}</h2>
      {windows.length === 0 ? (
        <Empty>{empty}</Empty>
      ) : (
        <Card className="divide-y divide-border">
          {windows.map((window) => (
            <div key={window.id} className="flex flex-wrap items-start justify-between gap-3 p-3">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{window.title}</span>
                  {window.entities.length === 0 ? (
                    // Said plainly. Estate-wide is what a power test really is,
                    // and it is also blunt: everything goes quiet, including
                    // the failure the test was meant to reveal.
                    <span className="rounded-md border border-status-warning-border bg-status-warning-surface px-1.5 py-0.5 text-xs text-status-warning-text">
                      Whole estate
                    </span>
                  ) : (
                    <span className="text-xs text-muted-foreground">
                      {window.entities.length} entities
                    </span>
                  )}
                </div>
                {window.reason !== '' && (
                  <div className="mt-1 text-sm text-muted-foreground">{window.reason}</div>
                )}
                <Identifier>
                  declared by {window.declaredBy} {ago(window.declaredAtUtc)}
                </Identifier>
              </div>

              <div className="flex shrink-0 items-center gap-3">
                <div className="text-right text-xs text-muted-foreground">
                  <div>starts {ago(window.startUtc)}</div>
                  <div>ends {ago(window.endUtc)}</div>
                </div>
                {onEnd !== null && canEnd && (
                  <button
                    type="button"
                    onClick={() => onEnd(window.id)}
                    className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
                  >
                    {window.scheduled ? 'Cancel' : 'End now'}
                  </button>
                )}
              </div>
            </div>
          ))}
        </Card>
      )}
    </section>
  )
}

function Declare({ onDone }: { onDone: () => void }) {
  const queryClient = useQueryClient()
  const [title, setTitle] = useState('')
  const [reason, setReason] = useState('')
  const [hours, setHours] = useState<number>(4)
  const [error, setError] = useState<string | null>(null)

  const declare = useMutation({
    mutationFn: () => {
      const start = new Date()
      const end = new Date(start.getTime() + hours * 3600_000)

      return api.declareMaintenanceWindow({
        title,
        reason,
        startUtc: start.toISOString(),
        endUtc: end.toISOString(),
        // Estate-wide for now. Narrowing to chosen entities belongs with the
        // entity explorer's selection, which is the natural place to pick them.
        entities: [],
      })
    },
    onSuccess: () => {
      setTitle('')
      setReason('')
      setError(null)
      void queryClient.invalidateQueries({ queryKey: ['maintenance'] })
      onDone()
    },
    onError: (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : String(cause)),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    declare.mutate()
  }

  return (
    <Card className="p-4">
      <h2 className="text-sm font-medium">Declare a window</h2>
      <form onSubmit={submit} className="mt-3 flex flex-wrap items-end gap-2">
        <label className="block">
          <span className="mb-1 block text-xs text-muted-foreground">What</span>
          <input
            value={title}
            onChange={(event) => setTitle(event.target.value)}
            placeholder="Firmware campaign"
            className="rounded-md border border-border bg-page px-2 py-1.5 text-sm"
          />
        </label>
        <label className="block min-w-56 flex-1">
          <span className="mb-1 block text-xs text-muted-foreground">Why</span>
          <input
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            placeholder="HBA firmware across the cluster"
            className="w-full rounded-md border border-border bg-page px-2 py-1.5 text-sm"
          />
        </label>
        <label className="block">
          <span className="mb-1 block text-xs text-muted-foreground">For</span>
          <select
            value={hours}
            onChange={(event) => setHours(Number(event.target.value))}
            className="rounded-md border border-border bg-page px-2 py-1.5 text-sm"
          >
            {DURATIONS.map((duration) => (
              <option key={duration.label} value={duration.hours}>
                {duration.label}
              </option>
            ))}
          </select>
        </label>
        <button
          type="submit"
          disabled={declare.isPending}
          className={cn(
            'rounded-md px-3 py-1.5 text-sm font-medium',
            declare.isPending ? 'bg-border text-muted-foreground' : 'bg-primary text-primary-on',
          )}
        >
          {declare.isPending ? 'Working…' : 'Declare'}
        </button>
      </form>

      {/*
        A deadline is required and there is no indefinite option. A window
        nobody remembers to end is a monitoring system that has quietly stopped
        notifying — the same argument as a silence needing a deadline.
      */}
      <div className="mt-2 text-xs text-muted-foreground">
        Starts now and ends on its own. You can end it early if the work finishes.
      </div>

      {error !== null && (
        <div className="mt-2 rounded-md border border-status-critical-border bg-status-critical-surface px-2 py-1.5 text-xs text-status-critical-text">
          {error}
        </div>
      )}
    </Card>
  )
}
