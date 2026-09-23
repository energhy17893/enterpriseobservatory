import { useState } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Empty, LoadFailure, Loading } from '@/components/Primitives'
import { AlertRow } from '@/components/AlertRow'
import { BulkBar } from '@/components/BulkBar'
import { cn } from '@/lib/ui'

// eo-ux §7: default page size is "to be measured" against the Kibar-sized
// estate (a render-time benchmark), not guessed. 50 matches the API's own
// undocumented default until that measurement exists — do not raise this
// without one.
const PAGE_SIZE = 50

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
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())

  const severity = params.get('severity') ?? ''
  const search = params.get('search') ?? ''
  const offsetParam = Number(params.get('offset') ?? '0')
  const offset = Number.isFinite(offsetParam) && offsetParam > 0 ? offsetParam : 0

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['alerts', severity, search, offset],
    queryFn: () =>
      api.alerts({
        severity: severity || undefined,
        search: search || undefined,
        offset,
        limit: PAGE_SIZE,
      }),
    refetchInterval: 15_000,
    // The list must not blink back to a spinner every fifteen seconds while
    // someone is reading it.
    placeholderData: keepPreviousData,
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    // A filter change makes the current page meaningless — go back to the
    // start of the (new) result set rather than stranding the operator past
    // its end.
    next.delete('offset')
    setParams(next, { replace: true })

    // A filter change makes the selection mean something else, so it goes.
    // Carrying it across would let an operator acknowledge a set they are no
    // longer looking at.
    setSelected(new Set())
  }

  function clearFilters() {
    const next = new URLSearchParams(params)
    next.delete('severity')
    next.delete('search')
    next.delete('offset')
    setParams(next, { replace: true })
    setSelected(new Set())
  }

  function setOffset(next: number) {
    const nextParams = new URLSearchParams(params)
    if (next <= 0) nextParams.delete('offset')
    else nextParams.set('offset', String(next))
    setParams(nextParams, { replace: true })

    // Same reasoning as a filter change: the rows on screen are about to be
    // different ones, so a selection made against the old page doesn't carry.
    setSelected(new Set())
  }

  function toggle(fingerprint: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (!next.delete(fingerprint)) next.add(fingerprint)
      return next
    })
  }

  const visible = data?.items ?? []
  const allSelected = visible.length > 0 && visible.every((a) => selected.has(a.fingerprint))

  // Shown when the result is empty: distinguishes "no alerts anywhere" from
  // "no alerts match what's typed/selected" — see eo-ux §6, reference §11.8.
  const activeFilters = [
    severity && `Severity: ${severity}`,
    search && `Search: "${search}"`,
  ].filter((v): v is string => Boolean(v))

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">All alerts</h1>
        {data !== undefined && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            {/* WCAG 4.1.3: the range changes on every page/filter change and
                must be announced without the operator having to look. */}
            <span className="tabular" role="status" aria-live="polite">
              {data.total === 0
                ? '0 of 0'
                : `${offset + 1}–${Math.min(offset + PAGE_SIZE, data.total)} of ${data.total}`}
            </span>
            <button
              type="button"
              aria-label="Previous page of alerts"
              disabled={offset === 0}
              onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
              className="rounded-md border border-border px-2 py-1 text-xs disabled:opacity-40"
            >
              Previous
            </button>
            <button
              type="button"
              aria-label="Next page of alerts"
              disabled={offset + PAGE_SIZE >= data.total}
              onClick={() => setOffset(offset + PAGE_SIZE)}
              className="rounded-md border border-border px-2 py-1 text-xs disabled:opacity-40"
            >
              Next
            </button>
          </div>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        {/* The switch works both ways: grouping is a view, not a mode. */}
        <Link
          to="/events"
          className="rounded-md border border-border px-3 py-1 text-sm text-muted-foreground hover:text-foreground"
        >
          Events
        </Link>
        <span className="rounded-md border border-border bg-primary px-3 py-1 text-sm text-primary-on">
          All alerts
        </span>
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

      {visible.length > 0 && (
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={allSelected}
            onChange={() =>
              setSelected(allSelected ? new Set() : new Set(visible.map((a) => a.fingerprint)))
            }
          />
          Select all {visible.length} shown
        </label>
      )}

      <BulkBar selected={[...selected]} onDone={() => setSelected(new Set())} />

      {isError ? (
        <LoadFailure what="Alerts" error={error} />
      ) : isPending ? (
        <Loading what="alerts" />
      ) : data.items.length === 0 ? (
        activeFilters.length > 0 ? (
          <Empty
            action={
              <button
                type="button"
                onClick={clearFilters}
                className="rounded-md border border-border px-3 py-1 text-xs text-foreground"
              >
                Clear filters
              </button>
            }
          >
            No alerts match this filter.
            <div className="mt-1">{activeFilters.join(' · ')}</div>
          </Empty>
        ) : (
          <Empty>
            Nothing is currently firing.
            <div className="mt-1">
              That is not the same as everything being healthy — check the collectors.
            </div>
          </Empty>
        )
      ) : (
        <ul className="space-y-2">
          {data.items.map((alert) => (
            <li key={alert.fingerprint}>
              <AlertRow
                alert={alert}
                selected={selected.has(alert.fingerprint)}
                onToggle={() => toggle(alert.fingerprint)}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
