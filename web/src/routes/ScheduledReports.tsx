import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago } from '@/lib/ui'
import type { ReportSubscriptionCommand, ReportSubscriptionView } from '@/api/types'

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']

const BLANK: ReportSubscriptionCommand = {
  recipients: [''],
  frequency: 'Daily',
  dayOfWeek: 'Monday',
  hourLocal: 7,
  timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
  kind: 'Alerts',
  isEnabled: true,
}

/**
 * Standing subscriptions to scheduled email reports (roadmap M5.4).
 *
 * Available to an operator, not only an administrator — declaring "I want the
 * weekly alert digest" is closer to declaring a maintenance window than it is
 * to reconfiguring the relay every subscriber sends through, which stays on
 * the Email screen. See ReportsApi's remarks.
 */
export function ScheduledReports() {
  const [editing, setEditing] = useState<ReportSubscriptionCommand | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['report-subscriptions'],
    queryFn: api.reportSubscriptions,
  })

  if (isError) return <LoadFailure what="Report subscriptions" error={error} />
  if (isPending) return <Loading what="report subscriptions" />

  function edit(subscription: ReportSubscriptionView) {
    setEditingId(subscription.id)
    setEditing({
      recipients: subscription.recipients,
      frequency: subscription.frequency,
      dayOfWeek: subscription.dayOfWeek,
      hourLocal: subscription.hourLocal,
      timeZoneId: subscription.timeZoneId,
      kind: subscription.kind,
      isEnabled: subscription.isEnabled,
    })
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Scheduled reports</h1>
        <button
          type="button"
          onClick={() => {
            setEditingId(null)
            setEditing(BLANK)
          }}
          className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
        >
          New subscription
        </button>
      </div>

      {editing !== null && (
        <Editor
          command={editing}
          id={editingId}
          onChange={setEditing}
          onDone={() => {
            setEditing(null)
            setEditingId(null)
          }}
        />
      )}

      {data.length === 0 ? (
        <Empty>
          No scheduled reports.
          <div className="mt-1">Nothing goes out until a subscription is added.</div>
        </Empty>
      ) : (
        data.map((subscription) => (
          <Row key={subscription.id} subscription={subscription} onEdit={() => edit(subscription)} />
        ))
      )}
    </div>
  )
}

function Row({
  subscription,
  onEdit,
}: {
  subscription: ReportSubscriptionView
  onEdit: () => void
}) {
  const queryClient = useQueryClient()

  const remove = useMutation({
    mutationFn: () => api.removeReportSubscription(subscription.id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['report-subscriptions'] }),
  })

  return (
    <Card className="p-3">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{subscription.kind} report</span>
            {!subscription.isEnabled && <StatusBadge status="Unknown">disabled</StatusBadge>}
            {subscription.lastError !== null && (
              <StatusBadge status="Critical">last attempt failed</StatusBadge>
            )}
          </div>

          <div className="mt-1 text-sm text-muted-foreground">{subscription.recipients.join(', ')}</div>

          <Identifier>
            {' '}
            {subscription.frequency === 'Weekly'
              ? `${subscription.dayOfWeek}s`
              : 'Every day'}{' '}
            at {String(subscription.hourLocal).padStart(2, '0')}:00 {subscription.timeZoneId}
            {subscription.lastSentUtc !== null && ` · last attempt ${ago(subscription.lastSentUtc)}`}
          </Identifier>

          {subscription.lastError !== null && (
            <div className="mt-1 text-xs text-critical-on">{subscription.lastError}</div>
          )}
        </div>

        <div className="flex shrink-0 gap-2">
          <button type="button" onClick={onEdit} className="rounded-md border border-border px-2 py-1 text-sm">
            Edit
          </button>
          <button
            type="button"
            onClick={() => remove.mutate()}
            disabled={remove.isPending}
            className="rounded-md border border-border px-2 py-1 text-sm"
          >
            Remove
          </button>
        </div>
      </div>
    </Card>
  )
}

function Editor({
  command,
  id,
  onChange,
  onDone,
}: {
  command: ReportSubscriptionCommand
  id: string | null
  onChange: (command: ReportSubscriptionCommand) => void
  onDone: () => void
}) {
  const queryClient = useQueryClient()
  const [note, setNote] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => {
      const cleaned = { ...command, recipients: command.recipients.map((r) => r.trim()).filter((r) => r !== '') }
      return id === null
        ? api.addReportSubscription(cleaned)
        : api.updateReportSubscription(id, cleaned)
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['report-subscriptions'] })
      onDone()
    },
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  function set<K extends keyof ReportSubscriptionCommand>(key: K, value: ReportSubscriptionCommand[K]) {
    onChange({ ...command, [key]: value })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate()
  }

  return (
    <Card className="p-4">
      <form onSubmit={submit} className="space-y-3">
        <h2 className="font-medium">{id === null ? 'New subscription' : 'Edit subscription'}</h2>

        <label className="block text-sm">
          <span className="font-medium">Recipients</span>
          <textarea
            value={command.recipients.join('\n')}
            onChange={(e) => set('recipients', e.target.value.split('\n'))}
            placeholder={'one address per line'}
            rows={3}
            required
            className="w-full rounded-md border border-border bg-page px-2 py-1"
          />
        </label>

        <div className="grid gap-3 sm:grid-cols-3">
          <label className="block text-sm">
            <span className="font-medium">Frequency</span>
            <select
              value={command.frequency}
              onChange={(e) => set('frequency', e.target.value as ReportSubscriptionCommand['frequency'])}
              className="w-full rounded-md border border-border bg-page px-2 py-1"
            >
              <option value="Daily">Daily</option>
              <option value="Weekly">Weekly</option>
            </select>
          </label>

          {command.frequency === 'Weekly' && (
            <label className="block text-sm">
              <span className="font-medium">Day</span>
              <select
                value={command.dayOfWeek}
                onChange={(e) => set('dayOfWeek', e.target.value)}
                className="w-full rounded-md border border-border bg-page px-2 py-1"
              >
                {DAYS.map((day) => (
                  <option key={day} value={day}>
                    {day}
                  </option>
                ))}
              </select>
            </label>
          )}

          <label className="block text-sm">
            <span className="font-medium">Hour (local)</span>
            <input
              type="number"
              min={0}
              max={23}
              value={command.hourLocal}
              onChange={(e) => set('hourLocal', Number(e.target.value))}
              className="w-full rounded-md border border-border bg-page px-2 py-1"
            />
          </label>
        </div>

        <label className="block text-sm">
          <span className="font-medium">Time zone</span>
          <input
            value={command.timeZoneId}
            onChange={(e) => set('timeZoneId', e.target.value)}
            placeholder="Europe/Istanbul"
            className="w-full rounded-md border border-border bg-page px-2 py-1"
          />
          <span className="mt-0.5 block text-xs text-muted-foreground">An IANA time zone id.</span>
        </label>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={command.isEnabled}
            onChange={(e) => set('isEnabled', e.target.checked)}
          />
          <span>Enabled</span>
        </label>

        {note !== null && <div className="text-sm text-critical-on">{note}</div>}

        <div className="flex flex-wrap gap-2">
          <button
            type="submit"
            disabled={save.isPending}
            className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
          >
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
          <button type="button" onClick={onDone} className="rounded-md border border-border px-3 py-1.5 text-sm">
            Cancel
          </button>
        </div>
      </form>
    </Card>
  )
}