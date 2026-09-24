import { useState } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { AlertRow } from '@/components/AlertRow'
import { BulkBar } from '@/components/BulkBar'
import { Pager } from '@/components/Pager'
import { ago, cn, severityStatus } from '@/lib/ui'
import type {
  EventFeedView,
  EventObjectView,
  EventView,
  RelationshipKind,
  SourceEventView,
  SuggestionView,
} from '@/api/types'
import type { StatusName } from '@/lib/ui'

/** English, so a count of one does not read as a defect. */
function count(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`
}

/** How a link in the explanation reads as a sentence. */
const PHRASING: Record<RelationshipKind, string> = {
  PartOf: 'is part of',
  RunsOn: 'runs',
  SameAs: 'is the same machine as',
  ConnectedTo: 'is connected to',
  BackedBy: 'backs',
  ManagedBy: 'manages',
}

/**
 * The inbox, grouped into what is actually going wrong.
 *
 * Three rules keep grouping from becoming a way of hiding things, and all three
 * come from ADR-0007 §5.1: the header carries the real counts, the flat list is
 * always one click away, and no alert lives only inside a group. The last of
 * those is checked by a test rather than trusted.
 */
type View = 'incidents' | 'vcenter'

export function Events() {
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  // In the URL with the feed's page and search, so a reload or a shared link
  // lands on the same page of the same tab.
  const [params, setParams] = useSearchParams()
  const view: View = params.get('view') === 'vcenter' ? 'vcenter' : 'incidents'

  function setView(next: View) {
    setParams(next === 'vcenter' ? { view: next } : {}, { replace: true })
  }

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['events'],
    queryFn: api.events,
    refetchInterval: 15_000,
    placeholderData: keepPreviousData,
  })

  function toggle(fingerprint: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (!next.delete(fingerprint)) next.add(fingerprint)
      return next
    })
  }

  if (view === 'vcenter') {
    return (
      <div className="space-y-4">
        <h1 className="text-xl font-semibold">Events</h1>
        <Tabs view={view} onChange={setView} />
        <VcenterEvents />
      </div>
    )
  }

  if (isError) return <LoadFailure what="Events" error={error} />
  if (isPending) return <Loading what="events" />

  const grouped = data.events.reduce((sum, e) => sum + e.alertCount, 0)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Events</h1>
        {/*
          The arithmetic, out loud. It is how an operator satisfies themselves
          that folding has not lost anything.
        */}
        <span className="text-sm text-muted-foreground tabular">
          {count(data.totalAlerts, 'alert')} · {grouped} in{' '}
          {count(data.events.length, 'event')} · {data.ungrouped.length} on their own
        </span>
      </div>

      <Tabs view={view} onChange={setView} />

      <BulkBar selected={[...selected]} onDone={() => setSelected(new Set())} />

      {data.events.length === 0 && data.ungrouped.length === 0 ? (
        <Empty>
          Nothing is currently firing.
          <div className="mt-1">
            That is not the same as everything being healthy — check the collectors.
          </div>
        </Empty>
      ) : (
        <>
          {data.events.map((incident) => (
            <Incident
              key={incident.id}
              incident={incident}
              selected={selected}
              onToggle={toggle}
            />
          ))}

          {data.suggestions.map((suggestion, index) => (
            <Suggestion key={index} suggestion={suggestion} selected={selected} onToggle={toggle} />
          ))}

          {data.ungrouped.length > 0 && (
            <section className="space-y-2">
              <h2 className="text-sm font-medium">On their own</h2>
              {data.ungrouped.map((alert) => (
                <AlertRow
                  key={alert.fingerprint}
                  alert={alert}
                  selected={selected.has(alert.fingerprint)}
                  onToggle={() => toggle(alert.fingerprint)}
                />
              ))}
            </section>
          )}
        </>
      )}
    </div>
  )
}

function Incident({
  incident,
  selected,
  onToggle,
}: {
  incident: EventView
  selected: ReadonlySet<string>
  onToggle: (fingerprint: string) => void
}) {
  const [open, setOpen] = useState(false)

  return (
    <Card className="overflow-hidden">
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        className="flex w-full flex-wrap items-center gap-3 p-3 text-left hover:bg-page"
      >
        <StatusBadge status={severityStatus(incident.severity)}>{incident.severity}</StatusBadge>
        <span className="font-medium">{incident.title}</span>
        {/*
          The real counts, in the header. An operator will not accept a folded
          group without seeing what was folded into it.
        */}
        <span className="text-sm text-muted-foreground tabular">
          {count(incident.alertCount, 'alert')} · {count(incident.entityCount, 'thing')}
        </span>
        <span className="ml-auto text-xs text-muted-foreground">
          since {ago(incident.firstSeenUtc)}
        </span>
        <span className="text-xs text-muted-foreground">{open ? '▾' : '▸'}</span>
      </button>

      <div className="border-t border-border px-3 py-2">
        <div className="text-xs text-muted-foreground">
          Starting at{' '}
          <Link
            to={`/entities/${encodeURIComponent(incident.rootId)}`}
            className="underline underline-offset-2"
          >
            {incident.rootName}
          </Link>
        </div>

        {/*
          Why these were grouped. ADR-0007 calls this a requirement rather than
          a nicety: topological correlation is only as good as the graph, so a
          wrong group has to be visibly wrong rather than merely wrong.
        */}
        {incident.explanation.length > 0 && (
          <ul className="mt-1 space-y-0.5">
            {incident.explanation.map((link, index) => (
              <li key={index} className="text-xs text-muted-foreground">
                <span className="font-mono">{link.fromName}</span> {PHRASING[link.kind]}{' '}
                <span className="font-mono">{link.toName}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {open && (
        <div className="space-y-2 border-t border-border p-3">
          {incident.alerts.map((alert) => (
            <AlertRow
              key={alert.fingerprint}
              alert={alert}
              selected={selected.has(alert.fingerprint)}
              onToggle={() => onToggle(alert.fingerprint)}
            />
          ))}
        </div>
      )}
    </Card>
  )
}

function Suggestion({
  suggestion,
  selected,
  onToggle,
}: {
  suggestion: SuggestionView
  selected: ReadonlySet<string>
  onToggle: (fingerprint: string) => void
}) {
  const [open, setOpen] = useState(false)

  return (
    <Card className="border-dashed p-3">
      {/*
        Never folded. The evidence is only that these appeared together, which
        is as true of one failure as of several unrelated ones at lunchtime —
        so the product points rather than claims. See ADR-0007 §5.2.
      */}
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        className="flex w-full flex-wrap items-center gap-2 text-left"
      >
        <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
          Possibly related
        </span>
        <span className="text-sm">
          {suggestion.alerts.length} alerts started within{' '}
          {Math.round(suggestion.windowSeconds / 60)} minutes of each other
        </span>
        <span className="ml-auto text-xs text-muted-foreground">{open ? '▾' : '▸'}</span>
      </button>

      <div className="mt-1 text-xs text-muted-foreground">
        Nothing in the topology connects these. They are listed separately below as well.
      </div>

      {open && (
        <div className="mt-2 space-y-2">
          {suggestion.alerts.map((alert) => (
            <AlertRow
              key={alert.fingerprint}
              alert={alert}
              selected={selected.has(alert.fingerprint)}
              onToggle={() => onToggle(alert.fingerprint)}
            />
          ))}
        </div>
      )}
    </Card>
  )
}

const TAB = 'rounded-md border border-border px-3 py-1 text-sm'
const TAB_ON = 'bg-primary text-primary-on'
const TAB_OFF = 'text-muted-foreground hover:text-foreground'

function Tabs({ view, onChange }: { view: View; onChange: (view: View) => void }) {
  return (
    <div className="flex flex-wrap gap-2">
      <button
        type="button"
        onClick={() => onChange('incidents')}
        className={cn(TAB, view === 'incidents' ? TAB_ON : TAB_OFF)}
      >
        Events
      </button>
      {/* Always one click away. Grouping that cannot be left is hiding. */}
      <Link to="/alerts" className={cn(TAB, TAB_OFF)}>
        All alerts
      </Link>
      {/*
        What vCenter itself recorded, beside this product's conclusions rather
        than mixed into them. Turning these into alerts is a separate step.
      */}
      <button
        type="button"
        onClick={() => onChange('vcenter')}
        className={cn(TAB, view === 'vcenter' ? TAB_ON : TAB_OFF)}
      >
        From vCenter
      </button>
    </div>
  )
}

// eo-ux §7: the page size is "to be measured"; 50 is the API's default, the
// same as the alert list's, until a render measurement says otherwise.
const EVENT_PAGE_SIZE = 50

/** vCenter's own severity, on this product's status ramp. */
function eventStatus(severity: string | null): StatusName {
  if (severity === 'error') return 'Critical'
  if (severity === 'warning') return 'Warning'
  return 'Info'
}

/**
 * The event stream vCenter reported, newest first.
 *
 * The streams are shown above the list, always. An empty list means nothing
 * only when the stream beside it says it was read recently; a stream that
 * could not be read turns the same empty list into "we have not been able to
 * ask", and the screen has to say which one it is.
 */
function VcenterEvents() {
  // E2: paged and searched on the server, the A1 pattern — offset and search
  // in the URL, "x–y of N" announced. The client used to take the newest 200
  // and filter those, so "nothing matches" could be false for an older event.
  const [params, setParams] = useSearchParams()
  const search = params.get('search') ?? ''
  const offsetParam = Number(params.get('offset') ?? '0')
  const offset = Number.isFinite(offsetParam) && offsetParam > 0 ? offsetParam : 0

  const { data, isPending, isError, error } = useQuery<EventFeedView>({
    queryKey: ['vcenter-events', search, offset],
    queryFn: () => api.vcenterEvents({ search: search || undefined, offset, limit: EVENT_PAGE_SIZE }),
    refetchInterval: 30_000,
    placeholderData: keepPreviousData,
  })

  function setParam(key: 'search' | 'offset', value: string) {
    const next = new URLSearchParams(params)
    if (value === '' || value === '0') next.delete(key)
    else next.set(key, value)
    // A new search makes the current page meaningless: back to its start.
    if (key === 'search') next.delete('offset')
    setParams(next, { replace: true })
  }

  if (isError) return <LoadFailure what="vCenter events" error={error} />
  if (isPending) return <Loading what="vCenter events" />

  const total = data.total

  return (
    <div className="space-y-3">
      {data.streams.length === 0 ? (
        <Card className="p-3 text-sm text-muted-foreground">
          No vCenter's events have been read yet. They are read on the inventory cycle.
        </Card>
      ) : (
        <Card className="space-y-1 p-3">
          {data.streams.map((stream) => (
            <div key={stream.sourceInstanceId} className="flex flex-wrap items-center gap-2 text-sm">
              <span className="font-mono">{stream.sourceInstanceId}</span>
              {stream.lastFailure !== null ? (
                <>
                  <StatusBadge status="Unknown">not read</StatusBadge>
                  <span className="text-muted-foreground">
                    last read {ago(stream.lastSuccessUtc)} · {stream.lastFailure}
                  </span>
                </>
              ) : (
                <span className="text-muted-foreground">read {ago(stream.lastSuccessUtc)}</span>
              )}
              {stream.lastGapUtc !== null && (
                <span className="text-xs text-muted-foreground">
                  · some events were missed {ago(stream.lastGapUtc)}
                </span>
              )}
            </div>
          ))}
          <div className="text-xs text-muted-foreground">
            Kept for {data.retentionDays} days.
          </div>
        </Card>
      )}

      <div className="flex flex-wrap items-center justify-between gap-3">
        <input
          type="search"
          value={search}
          onChange={(event) => setParam('search', event.target.value)}
          placeholder="Search type, message, user, host or VM"
          aria-label="Search vCenter events"
          className="w-full max-w-md rounded-md border border-border bg-page px-3 py-1.5 text-sm"
        />
        <Pager
          offset={offset}
          pageSize={EVENT_PAGE_SIZE}
          total={total}
          onOffset={(next) => setParam('offset', String(next))}
          unit="vCenter events"
        />
      </div>

      {data.events.length === 0 ? (
        total > 0 ? (
          // A kept URL whose page has since aged out: not an empty feed either.
          <Empty
            action={
              <button
                type="button"
                onClick={() => setParam('offset', '0')}
                className="rounded-md border border-border px-3 py-1 text-xs text-foreground"
              >
                First page
              </button>
            }
          >
            This page is past the last of {total} events.
          </Empty>
        ) : search !== '' ? (
          // A2: a search that found nothing is not an empty feed.
          <Empty
            action={
              <button
                type="button"
                onClick={() => setParam('search', '')}
                className="rounded-md border border-border px-3 py-1 text-xs text-foreground"
              >
                Clear search
              </button>
            }
          >
            No vCenter events in the last {data.retentionDays} days match this search.
            <div className="mt-1">Search: "{search}"</div>
          </Empty>
        ) : (
          <Empty>No vCenter events recorded in the last {data.retentionDays} days.</Empty>
        )
      ) : (
        <div className="space-y-2">
          {data.events.map((e) => (
            <VcenterEvent key={`${e.sourceInstanceId}|${e.key}|${e.createdAtUtc}`} event={e} />
          ))}
        </div>
      )}
    </div>
  )
}

function VcenterEvent({ event }: { event: SourceEventView }) {
  const objects: [string, EventObjectView | null][] = [
    ['VM', event.virtualMachine],
    ['host', event.host],
    ['cluster', event.computeResource],
    ['datastore', event.datastore],
  ]

  return (
    <Card className="p-3">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            {event.severity !== null && (
              <StatusBadge status={eventStatus(event.severity)}>{event.severity}</StatusBadge>
            )}
            {/* The type id, not the class: every esx.problem is an EventEx. */}
            <span className="font-mono text-sm">{event.typeId}</span>
          </div>
          <div className="mt-1 text-sm">{event.message}</div>
          <div className="mt-1 flex flex-wrap gap-x-3 text-sm text-muted-foreground">
            {objects.map(([label, value]) =>
              value === null ? null : (
                <span key={label}>
                  {label}{' '}
                  {value.entityId === null ? (
                    value.name
                  ) : (
                    <Link
                      to={`/entities/${encodeURIComponent(value.entityId)}`}
                      className="underline underline-offset-2"
                    >
                      {value.name}
                    </Link>
                  )}
                </span>
              ),
            )}
            {event.userName !== null && <span>by {event.userName}</span>}
          </div>
          <Identifier>
            {' '}
            {event.sourceInstanceId} · {event.eventClass} · #{event.key}
          </Identifier>
        </div>
        <div
          className="shrink-0 text-right text-xs text-muted-foreground"
          title={event.createdAtUtc}
        >
          {ago(event.createdAtUtc)}
        </div>
      </div>
    </Card>
  )
}