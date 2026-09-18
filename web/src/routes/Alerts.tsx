import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, cn, severityStatus } from '@/lib/ui'

/**
 * The alert inbox: the primary triage surface.
 *
 * This is the flat list ADR-0007 §5.1 guarantees is always available. Event
 * grouping will be a view over it and never a replacement: no alert may live
 * only inside a group, or grouping becomes a way of hiding things.
 *
 * Every alert here is a lifecycle instance from the one store. An entity page
 * shows the same instances filtered, never its own list — the previous product
 * did keep its own, and the same alert could read as acknowledged on one screen
 * and open on another.
 */
export function Alerts() {
  const [params, setParams] = useSearchParams()

  const severity = params.get('severity') ?? ''
  const search = params.get('search') ?? ''

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['alerts', severity, search],
    queryFn: () => api.alerts({ severity: severity || undefined, search: search || undefined }),
    refetchInterval: 15_000,
    // The list must not blink back to a spinner every fifteen seconds while
    // someone is reading it.
    placeholderData: keepPreviousData,
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    setParams(next, { replace: true })
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Alert inbox</h1>
        {data !== undefined && (
          <span className="text-sm text-muted-foreground tabular">
            {data.items.length} of {data.total}
          </span>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        {['', 'Critical', 'Warning'].map((value) => (
          <button
            key={value || 'all'}
            type="button"
            onClick={() => setParam('severity', value)}
            className={cn(
              'rounded-md border border-border px-3 py-1 text-sm',
              severity === value ? 'bg-primary text-primary-on' : 'text-muted-foreground',
            )}
          >
            {value === '' ? 'All' : value}
          </button>
        ))}
        <input
          type="search"
          value={search}
          onChange={(event) => setParam('search', event.target.value)}
          placeholder="Search title or description"
          className="min-w-56 flex-1 rounded-md border border-border bg-card px-3 py-1 text-sm"
          aria-label="Search alerts"
        />
      </div>

      {isError ? (
        <LoadFailure what="Alerts" error={error} />
      ) : isPending ? (
        <Loading what="alerts" />
      ) : data.items.length === 0 ? (
        <Empty>
          Nothing is currently firing.
          <div className="mt-1">
            That is not the same as everything being healthy — check the collectors.
          </div>
        </Empty>
      ) : (
        <ul className="space-y-2">
          {data.items.map((alert) => (
            <li key={alert.fingerprint}>
              <Card className="p-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <StatusBadge status={severityStatus(alert.severity)}>
                        {alert.severity}
                      </StatusBadge>
                      <span className="font-medium">{alert.title}</span>
                      {alert.state !== 'Open' && (
                        <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                          {alert.state}
                        </span>
                      )}
                      {/*
                        Suppression is shown, never hidden. Maintenance stops
                        the paging, not the reporting — an operator working
                        inside the window still has to see what they are doing.
                      */}
                      {alert.suppressedByWindowId !== null && (
                        <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                          Notification suppressed
                        </span>
                      )}
                      {/*
                        Said out loud, because the product inferred it rather
                        than observing it. Principle 1: never present a guess as
                        a measurement.
                      */}
                      {alert.isDerived && (
                        <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                          Derived
                        </span>
                      )}
                    </div>
                    <div className="mt-1 text-sm text-muted-foreground">{alert.description}</div>
                    <div className="mt-1.5 flex flex-wrap items-center gap-3">
                      {alert.entityId !== null && (
                        <Link
                          to={`/entities/${encodeURIComponent(alert.entityId)}`}
                          className="text-sm underline underline-offset-2"
                        >
                          {alert.entityName ?? alert.entityId}
                        </Link>
                      )}
                      <Identifier>
                        {alert.source} · {alert.category}
                      </Identifier>
                    </div>
                  </div>
                  <div className="shrink-0 text-right text-xs text-muted-foreground">
                    <div>seen {ago(alert.lastSeenUtc)}</div>
                    <div>since {ago(alert.firstSeenUtc)}</div>
                  </div>
                </div>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
