import { useState } from 'react'
import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, ConfirmDestructive, Empty, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { AlertRow } from '@/components/AlertRow'
import { BulkBar } from '@/components/BulkBar'
import { Pager } from '@/components/Pager'
import { cn, groupCountLabel, severityStatus } from '@/lib/ui'
import type { AlertGroupView, AlertView, BulkActionView } from '@/api/types'

// eo-ux §7: default page size is "to be measured" against the Kibar-sized
// estate (a render-time benchmark), not guessed. 50 matches the API's own
// undocumented default until that measurement exists — do not raise this
// without one.
const PAGE_SIZE = 50

/**
 * The alert inbox: the primary triage surface.
 *
 * Grouped by rule and source by default (A9): a rule firing on 84 datastores
 * is one row, not 84. This is a view over the same flat list ADR-0007 §5.1
 * guarantees is always available — the "Flat" toggle switches to it, and
 * never the other way around, since grouping that cannot be left is hiding
 * things (ADR-0021: identity stays per-alert; grouping is a list concern).
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
  const flat = params.get('flat') === 'true'
  const offsetParam = Number(params.get('offset') ?? '0')
  const offset = Number.isFinite(offsetParam) && offsetParam > 0 ? offsetParam : 0

  const flatQuery = useQuery({
    queryKey: ['alerts', severity, search, offset],
    queryFn: () =>
      api.alerts({ severity: severity || undefined, search: search || undefined, offset, limit: PAGE_SIZE }),
    refetchInterval: 15_000,
    // The list must not blink back to a spinner every fifteen seconds while
    // someone is reading it.
    placeholderData: keepPreviousData,
    enabled: flat,
  })

  const groupedQuery = useQuery({
    queryKey: ['alertGroups', severity, search, offset],
    queryFn: () =>
      api.alertGroups({ severity: severity || undefined, search: search || undefined, offset, limit: PAGE_SIZE }),
    refetchInterval: 15_000,
    placeholderData: keepPreviousData,
    enabled: !flat,
  })

  const { data: flatData, isPending: flatPending, isError: flatErrored, error: flatError } = flatQuery
  const { data: groupedData, isPending: groupedPending, isError: groupedErrored, error: groupedError } = groupedQuery

  const isPending = flat ? flatPending : groupedPending
  const isError = flat ? flatErrored : groupedErrored
  const error = flat ? flatError : groupedError
  const total = flat ? flatData?.total : groupedData?.total

  // Every alert currently on screen, group membership flattened out — what
  // "select all shown" and the bulk bar act on either way.
  const visible: AlertView[] = flat
    ? flatData?.items ?? []
    : (groupedData?.items ?? []).flatMap((g) => g.alerts)
  const allSelected = visible.length > 0 && visible.every((a) => selected.has(a.fingerprint))

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    // A filter (or view) change makes the current page meaningless — go back
    // to the start of the (new) result set rather than stranding the operator
    // past its end.
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

  function toggleMany(fingerprints: string[]) {
    const already = fingerprints.every((f) => selected.has(f))
    setSelected((current) => {
      const next = new Set(current)
      for (const f of fingerprints) {
        if (already) next.delete(f)
        else next.add(f)
      }
      return next
    })
  }

  // Shown when the result is empty: distinguishes "no alerts anywhere" from
  // "no alerts match what's typed/selected" — see eo-ux §6, reference §11.8.
  const activeFilters = [
    severity && `Severity: ${severity}`,
    search && `Search: "${search}"`,
  ].filter((v): v is string => Boolean(v))

  const empty = flat ? flatData?.items.length === 0 : groupedData?.items.length === 0

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">All alerts</h1>
        {total !== undefined && (
          <Pager
            offset={offset}
            pageSize={PAGE_SIZE}
            total={total}
            onOffset={setOffset}
            unit={flat ? 'alerts' : 'groups'}
          />
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
        {/*
          Grouped is the default; Flat is one click away and persisted in the
          URL the same way the filters are, so a bookmarked or shared link
          reproduces the same view (A9, ADR-0021: grouping cannot be a way of
          hiding things, so leaving it is always available).
        */}
        <button
          type="button"
          onClick={() => setParam('flat', flat ? '' : 'true')}
          className={cn(
            'rounded-md border border-border px-3 py-1 text-sm',
            flat ? 'bg-primary text-primary-on' : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {flat ? 'Flat' : 'Grouped'}
        </button>
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
      ) : empty ? (
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
      ) : flat ? (
        <ul className="space-y-2">
          {(flatData?.items ?? []).map((alert) => (
            <li key={alert.fingerprint}>
              <AlertRow
                alert={alert}
                selected={selected.has(alert.fingerprint)}
                onToggle={() => toggle(alert.fingerprint)}
              />
            </li>
          ))}
        </ul>
      ) : (
        <ul className="space-y-2">
          {(groupedData?.items ?? []).map((group) =>
            group.count === 1 ? (
              <li key={group.key}>
                <AlertRow
                  alert={group.alerts[0]}
                  selected={selected.has(group.alerts[0].fingerprint)}
                  onToggle={() => toggle(group.alerts[0].fingerprint)}
                />
              </li>
            ) : (
              <li key={group.key}>
                <GroupRow
                  group={group}
                  selected={selected}
                  onToggle={toggle}
                  onToggleMany={toggleMany}
                />
              </li>
            ),
          )}
        </ul>
      )}
    </div>
  )
}

/**
 * One rule's alerts, folded. A disclosure: collapsed by default, its name
 * says how many are inside so a screen reader announces the fold, not just
 * "button". Acknowledge/Clear act on every member at once — the same commands
 * the bulk bar sends, scoped to this group rather than the whole selection.
 */
function GroupRow({
  group,
  selected,
  onToggle,
  onToggleMany,
}: {
  group: AlertGroupView
  selected: ReadonlySet<string>
  onToggle: (fingerprint: string) => void
  onToggleMany: (fingerprints: string[]) => void
}) {
  const [open, setOpen] = useState(false)
  const [confirmingClear, setConfirmingClear] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const queryClient = useQueryClient()

  const fingerprints = group.alerts.map((a) => a.fingerprint)
  const groupSelected = fingerprints.every((f) => selected.has(f))
  const label = `${group.title} — ${groupCountLabel(group.count, group.entityKind)}`

  const act = useMutation({
    mutationFn: (run: () => Promise<BulkActionView>) => run(),
    onSuccess: (result) => {
      setNote(
        result.missing > 0
          ? `${result.alerts.length} changed. ${result.missing} had already resolved.`
          : null,
      )
      setConfirmingClear(false)
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-3 p-3">
        <input
          type="checkbox"
          checked={groupSelected}
          onChange={() => onToggleMany(fingerprints)}
          aria-label={`Select group: ${label}`}
        />

        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((current) => !current)}
          className="flex flex-1 flex-wrap items-center gap-2 text-left hover:underline"
        >
          <StatusBadge status={severityStatus(group.severity)}>{group.severity}</StatusBadge>
          <span className="font-medium">{label}</span>
          <span className="text-xs text-muted-foreground">{open ? '▾' : '▸'}</span>
        </button>

        <button
          type="button"
          disabled={act.isPending}
          onClick={() => act.mutate(() => api.acknowledgeMany(fingerprints))}
          className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
        >
          Acknowledge all
        </button>
        <button
          type="button"
          disabled={act.isPending}
          onClick={() => setConfirmingClear(true)}
          className="rounded-md border border-destructive px-2 py-1 text-xs hover:bg-destructive hover:text-destructive-on"
        >
          Clear all
        </button>
      </div>

      {note !== null && (
        <div className="px-3 pb-2 text-xs text-muted-foreground">{note}</div>
      )}

      {open && (
        <div className="space-y-2 border-t border-border p-3">
          {group.alerts.map((alert) => (
            <AlertRow
              key={alert.fingerprint}
              alert={alert}
              selected={selected.has(alert.fingerprint)}
              onToggle={() => onToggle(alert.fingerprint)}
            />
          ))}
        </div>
      )}

      <ConfirmDestructive
        open={confirmingClear}
        title={`Clear ${group.count} alerts? Clear marks it resolved now; if the condition is reported again it re-opens. To stop it for a while, use Silence.`}
        confirmLabel={`Clear ${group.count}`}
        onConfirm={() => act.mutate(() => api.clearMany(fingerprints))}
        onCancel={() => setConfirmingClear(false)}
      />
    </Card>
  )
}
