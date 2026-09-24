import { useRef } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { Pager } from '@/components/Pager'
import { ago, cn, healthBasisShort, healthStatus } from '@/lib/ui'
import type { EntityKind, EntityView } from '@/api/types'

// EX4: the filter buttons and the Kind column say the same words. '' is "All".
const KIND_LABELS: Partial<Record<EntityKind | '', string>> = {
  '': 'All',
  EsxiHost: 'ESXi host',
  VirtualMachine: 'Virtual machine',
  Datastore: 'Datastore',
  Cluster: 'Cluster',
  VCenter: 'vCenter',
}
const KINDS = Object.keys(KIND_LABELS) as (EntityKind | '')[]

// Measured 24 Sep 2026 (/api/entities kind=VirtualMachine, reference-approaches
// §11): ~300 B per row on the wire, so 100 rows ≈ 30 KB a page, and Kibar's
// 1,100 VMs are 11 pages. The server derives health for the whole graph on
// every call whatever the limit, so this sizes payload and reading, not
// server work. Rows within a page stay virtualised.
const PAGE_SIZE = 100

// 52, not 44: a grey (Unknown) row needs a second, short line saying why
// (source silent / only unknown alerts / not observed) -- K3, a grey entity
// must not read as "just a different colour of green".
const ROW_HEIGHT = 52

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
  const offsetParam = Number(params.get('offset') ?? '0')
  const offset = Number.isFinite(offsetParam) && offsetParam > 0 ? offsetParam : 0
  const filtered = kind !== '' || search !== '' || includeVanished

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['entities', kind, search, includeVanished, offset],
    queryFn: () =>
      api.entities({
        kind: kind || undefined,
        search: search || undefined,
        includeVanished,
        offset,
        limit: PAGE_SIZE,
      }),
    refetchInterval: 30_000,
    placeholderData: keepPreviousData,
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    // A filter change makes the current page meaningless: back to its start.
    next.delete('offset')
    setParams(next, { replace: true })
  }

  function setOffset(value: number) {
    const next = new URLSearchParams(params)
    if (value <= 0) next.delete('offset')
    else next.set('offset', String(value))
    setParams(next, { replace: true })
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Entity explorer</h1>
        {data !== undefined && (
          <Pager offset={offset} pageSize={PAGE_SIZE} total={data.total} onOffset={setOffset} unit="entities" />
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
            {KIND_LABELS[value]}
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
        // EX2 (reference §11.8): "none at all" and "filtered out" are
        // different screens, each with its own next step.
        data.total > 0 ? (
          // A kept URL whose page no longer exists: not an empty estate.
          <Empty
            action={
              <button
                type="button"
                onClick={() => setOffset(0)}
                className="rounded-md border border-border px-3 py-1 text-xs text-foreground"
              >
                First page
              </button>
            }
          >
            This page is past the last of {data.total.toLocaleString('en-US')} entities.
          </Empty>
        ) : filtered ? (
          <Empty
            action={
              <button
                type="button"
                onClick={() => setParams({}, { replace: true })}
                className="rounded-md border border-border px-3 py-1 text-xs text-foreground"
              >
                Clear filters
              </button>
            }
          >
            No entity matches these filters.
          </Empty>
        ) : (
          <Empty
            action={
              <Link to="/collectors" className="text-xs underline underline-offset-2">
                Check the collectors
              </Link>
            }
          >
            Nothing has been collected yet.
          </Empty>
        )
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
                  <div className="truncate text-sm text-muted-foreground">
                    {KIND_LABELS[entity.kind] ?? entity.kind}
                  </div>
                  <div className="flex min-w-0 flex-col justify-center gap-0.5">
                    <div className="flex flex-wrap items-center gap-1">
                      <StatusBadge status={healthStatus(entity.health)}>{entity.health}</StatusBadge>
                      {entity.healthIsStale && <StatusBadge status="Unknown">stale</StatusBadge>}
                    </div>
                    {/*
                      K3: grey must say why, and a stale colour must say since
                      when -- both in words short enough for a table row. A
                      stale wins the line when both apply: "since" is the more
                      actionable fact once the colour itself is explained by
                      the badge text above.
                    */}
                    {entity.healthIsStale ? (
                      <span className="truncate text-[11px] text-muted-foreground">
                        since {ago(entity.healthStaleSinceUtc)}
                      </span>
                    ) : (
                      healthBasisShort(entity.healthBasis) && (
                        <span className="truncate text-[11px] text-muted-foreground">
                          {healthBasisShort(entity.healthBasis)}
                        </span>
                      )
                    )}
                  </div>
                  <div className="text-right tabular">{entity.alertCount || '-'}</div>
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
