import { useRef } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, cn, healthStatus } from '@/lib/ui'
import type { EntityView } from '@/api/types'

const KINDS = ['', 'EsxiHost', 'VirtualMachine', 'Datastore', 'Cluster'] as const

const ROW_HEIGHT = 44

/**
 * The entity explorer: tier 1 of ADR-0007.
 *
 * Uniform columns across every type — name, kind, health, alert count, last
 * seen — because this screen's job is finding and comparing, not showing
 * everything a type can be. Type-specific depth lives one and two clicks in.
 */
export function Entities() {
  const [params, setParams] = useSearchParams()

  const kind = params.get('kind') ?? ''
  const search = params.get('search') ?? ''
  const includeVanished = params.get('includeVanished') === 'true'

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['entities', kind, search, includeVanished],
    queryFn: () =>
      api.entities({
        kind: kind || undefined,
        search: search || undefined,
        includeVanished,
        limit: 500,
      }),
    refetchInterval: 30_000,
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
        <h1 className="text-xl font-semibold">Entity explorer</h1>
        {data !== undefined && (
          <span className="text-sm text-muted-foreground tabular">
            {data.items.length} of {data.total}
          </span>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {KINDS.map((value) => (
          <button
            key={value || 'all'}
            type="button"
            onClick={() => setParam('kind', value)}
            className={cn(
              'rounded-md border border-border px-3 py-1 text-sm',
              kind === value ? 'bg-primary text-primary-on' : 'text-muted-foreground',
            )}
          >
            {value === '' ? 'All' : value}
          </button>
        ))}
        <input
          type="search"
          value={search}
          onChange={(event) => setParam('search', event.target.value)}
          placeholder="Search by name"
          className="min-w-56 flex-1 rounded-md border border-border bg-card px-3 py-1 text-sm"
          aria-label="Search entities"
        />
        <label className="flex items-center gap-2 text-sm text-muted-foreground">
          <input
            type="checkbox"
            checked={includeVanished}
            onChange={(event) => setParam('includeVanished', event.target.checked ? 'true' : '')}
          />
          Include vanished
        </label>
      </div>

      {isError ? (
        <LoadFailure what="Entities" error={error} />
      ) : isPending ? (
        <Loading what="entities" />
      ) : data.items.length === 0 ? (
        <Empty>No entity matches. If nothing has been collected yet, check the collectors.</Empty>
      ) : (
        <EntityTable rows={data.items} />
      )}
    </div>
  )
}

/**
 * Virtualised because an estate has thousands of virtual machines.
 *
 * ADR-0006 made this a rule rather than an optimisation: the previous product
 * rendered every row into the DOM and the tables were the slowest thing in it.
 */
function EntityTable({ rows }: { rows: EntityView[] }) {
  const scrollRef = useRef<HTMLDivElement>(null)

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  })

  return (
    <Card className="overflow-hidden">
      <div className="grid grid-cols-[1fr_9rem_7rem_5rem_7rem] gap-3 border-b border-border px-3 py-2 text-xs uppercase tracking-wide text-muted-foreground">
        <div>Name</div>
        <div>Kind</div>
        <div>Health</div>
        <div className="text-right">Alerts</div>
        <div className="text-right">Last seen</div>
      </div>

      <div ref={scrollRef} className="max-h-[calc(100vh-18rem)] overflow-auto">
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
          {virtualizer.getVirtualItems().map((virtualRow) => {
            const entity = rows[virtualRow.index]!

            return (
              <div
                key={entity.id}
                className="absolute inset-x-0 border-b border-border/50"
                style={{ height: virtualRow.size, transform: `translateY(${virtualRow.start}px)` }}
              >
                <Link
                  to={`/entities/${encodeURIComponent(entity.id)}`}
                  className="grid h-full grid-cols-[1fr_9rem_7rem_5rem_7rem] items-center gap-3 px-3 hover:bg-page"
                >
                  <div className="min-w-0 truncate">
                    {entity.displayName}
                    {entity.observationState === 'Vanished' && (
                      <span className="ml-2 text-xs text-muted-foreground">(vanished)</span>
                    )}
                    {entity.observationState === 'InMaintenance' && (
                      <span className="ml-2 text-xs text-muted-foreground">(maintenance)</span>
                    )}
                  </div>
                  <Identifier>{entity.kind}</Identifier>
                  <div className="flex flex-wrap items-center gap-1">
                    <StatusBadge status={healthStatus(entity.health)}>{entity.health}</StatusBadge>
                    {entity.healthIsStale && <StatusBadge status="Unknown">stale</StatusBadge>}
                  </div>
                  <div className="text-right tabular">{entity.alertCount || ''}</div>
                  <div className="text-right text-xs text-muted-foreground">
                    {ago(entity.lastSeenUtc)}
                  </div>
                </Link>
              </div>
            )
          })}
        </div>
      </div>
    </Card>
  )
}
