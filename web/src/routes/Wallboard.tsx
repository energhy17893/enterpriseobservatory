import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { AlertView, HealthState } from '@/api/types'
import { EMPTY, STALE_AFTER_MS, ago, cn, collectorRoleLabel, isStale, ramp, type StatusName } from '@/lib/ui'

/**
 * The NOC wallboard (U4): one fixed 16:9 screen read from across the room.
 *
 * ADR-0007 §6 and reference-approaches.md §11.10: a separate view over the
 * same read model, understood in five seconds, nothing to click, no scrolling,
 * no rotation, constant tile sizes. A stale screen says so in words and loses
 * its colour -- it never keeps showing an old value as if it were current, and
 * never goes blank either.
 *
 * Fetching is React Query's interval; the only timer here re-renders the
 * "N ago" texts and is cleared on unmount, so weeks of uptime hold no more
 * than one screen's worth of data.
 */

const REFRESH_MS = 15_000
// Forecasts and posture move slowly and cost the server more; still well
// inside STALE_AFTER_MS so a healthy tile never reads as stale.
const SLOW_REFRESH_MS = 60_000

const FRAME_STYLE = {
  // 4 m, §11.10 rule 12, to be measured.
  // 80 px font size puts Fira Code's cap height (0.706 em) at ~56 px, the
  // >= 55 px the rule derives for 4 m at 96 dpi / 1080p.
  '--wallboard-value': '80px',
  // Exactly 16:9 inside any viewport, never scaled: text sizes are px, so a
  // smaller screen letterboxes instead of shrinking the type (§11.10 rejects
  // scaled-down layouts).
  width: 'min(100vw, calc(100vh * 16 / 9))',
  height: 'min(100vh, calc(100vw * 9 / 16))',
} as CSSProperties

const VALUE = 'font-mono leading-none text-[length:var(--wallboard-value)] tabular'
const LABEL = 'font-sans text-[24px] leading-tight'

function hhmm(ms: number): string {
  return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
}

function minutes(ms: number): number {
  return Math.max(0, Math.floor(ms / 60_000))
}

export function Wallboard() {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 10_000)
    return () => clearInterval(id)
  }, [])

  // Same key and interval as Shell's Freshness: one read model, one cache entry.
  const overview = useQuery({ queryKey: ['overview'], queryFn: api.overview, refetchInterval: REFRESH_MS })
  const critical = useQuery({
    queryKey: ['alerts', { severity: 'Critical', state: 'Open', limit: 5 }],
    queryFn: () => api.alerts({ severity: 'Critical', state: 'Open', limit: 5 }),
    refetchInterval: REFRESH_MS,
  })
  const warning = useQuery({
    queryKey: ['alerts', { severity: 'Warning', state: 'Open', limit: 3 }],
    queryFn: () => api.alerts({ severity: 'Warning', state: 'Open', limit: 3 }),
    refetchInterval: REFRESH_MS,
  })
  const collectors = useQuery({ queryKey: ['collectors'], queryFn: api.collectors, refetchInterval: REFRESH_MS })
  const capacity = useQuery({
    queryKey: ['reports', 'capacity'],
    queryFn: api.capacityReport,
    refetchInterval: SLOW_REFRESH_MS,
  })
  const compliance = useQuery({ queryKey: ['compliance'], queryFn: api.compliance, refetchInterval: SLOW_REFRESH_MS })

  const o = overview.data
  const oldest = o?.oldestSuccessfulReadUtc ?? null
  // Loading is not stale; an error or an old read is. On an error the last
  // values stay on screen, under the grey frame.
  const stale = overview.isError || (o !== undefined && isStale(oldest, now))

  return (
    <div className="flex h-screen w-screen items-center justify-center overflow-hidden bg-page text-foreground">
      <div
        style={FRAME_STYLE}
        className={cn(
          'grid grid-cols-3 grid-rows-[auto_minmax(0,1fr)_minmax(0,1fr)] gap-4 overflow-hidden border-4 p-4',
          stale ? cn(ramp('Unknown').surface, ramp('Unknown').border) : 'border-transparent',
        )}
      >
        <header className="col-span-3 flex items-baseline justify-between gap-8">
          <span className={cn(LABEL, 'font-semibold')}>Enterprise Observatory</span>
          {stale ? (
            <span className={cn('font-sans font-semibold leading-none text-[length:var(--wallboard-value)]')}>
              {oldest === null ? 'STALE' : `STALE since ${hhmm(Date.parse(oldest))} · ${minutes(now - Date.parse(oldest))} min`}
            </span>
          ) : (
            <span className={LABEL}>{o === undefined ? 'Connecting…' : `Updated ${ago(o.generatedAtUtc, now)}`}</span>
          )}
          <span className={cn(LABEL, 'font-mono')}>
            {stale && o !== undefined && <span className="mr-6">updated {ago(o.generatedAtUtc, now)}</span>}
            {hhmm(now)}
          </span>
        </header>

        <Tile label="Critical open alerts" queries={[overview, critical]} now={now} frameStale={stale}>
          {(muted) => (
            <AlertTile count={o?.criticalAlerts} status="Critical" alerts={critical.data?.items} muted={muted} now={now} />
          )}
        </Tile>

        <Tile label="Warning open alerts" queries={[overview, warning]} now={now} frameStale={stale}>
          {(muted) => (
            <AlertTile count={o?.warningAlerts} status="Warning" alerts={warning.data?.items} muted={muted} now={now} />
          )}
        </Tile>

        <Tile label="Entity health" queries={[overview]} now={now} frameStale={stale}>
          {(muted) => o && <HealthTile counts={o.entitiesByHealth} muted={muted} />}
        </Tile>

        <Tile label="Collectors" queries={[collectors]} now={now} frameStale={stale}>
          {(muted) => {
            const all = collectors.data ?? []
            const down = all.filter((c) => c.health === 'Critical' || c.health === 'Warning')
            const unknown = all.filter((c) => c.health === 'Unknown')
            const shown = [...down, ...unknown].slice(0, 4)
            const hidden = down.length + unknown.length - shown.length
            return (
              <>
                <Value status={down.length > 0 ? 'Critical' : 'Healthy'} muted={muted}>
                  {down.length}
                </Value>
                <div className={LABEL}>
                  down · {all.length - down.length - unknown.length} up · {unknown.length} unknown
                </div>
                <ul className="mt-2 space-y-1">
                  {shown.map((c) => (
                    <li key={`${c.instanceId}/${c.role}`} className={cn(LABEL, 'truncate')}>
                      {collectorRoleLabel(c.role)} — {c.instanceId} · {ago(c.lastSuccessUtc, now)}
                    </li>
                  ))}
                  {hidden > 0 && <li className={cn(LABEL, 'text-muted-foreground')}>+{hidden} more</li>}
                  {shown.length === 0 && o !== undefined && (
                    <li className={cn(LABEL, 'text-muted-foreground')}>oldest read {ago(oldest, now)}</li>
                  )}
                </ul>
              </>
            )
          }}
        </Tile>

        <Tile label="Fullest datastores" queries={[capacity]} now={now} frameStale={stale}>
          {() => {
            const rows = (capacity.data?.rows ?? [])
              .filter((r) => r.percentUsed !== null)
              .sort((a, b) => b.percentUsed! - a.percentUsed!)
              .slice(0, 5)
            return (
              <ul className="space-y-3">
                {rows.map((r) => (
                  <li key={`${r.source}/${r.name}`}>
                    <div className={cn(LABEL, 'flex justify-between gap-4')}>
                      <span className="truncate">{r.name}</span>
                      <span className="font-mono">{Math.round(r.percentUsed!)}%</span>
                    </div>
                    {/* Length, not colour, carries the magnitude (§11.10 rule 9). */}
                    <div className="h-3 bg-border">
                      <div className="h-full bg-muted-foreground" style={{ width: `${Math.min(100, r.percentUsed!)}%` }} />
                    </div>
                  </li>
                ))}
                {capacity.data !== undefined && rows.length === 0 && <li className={LABEL}>{EMPTY}</li>}
              </ul>
            )
          }}
        </Tile>

        <Tile label="Compliance findings" queries={[compliance]} now={now} frameStale={stale}>
          {(muted) => {
            const t = compliance.data?.totals
            return (
              t && (
                <>
                  <Value status={t.failing > 0 ? 'Warning' : 'Healthy'} muted={muted}>
                    {t.failing}
                  </Value>
                  <div className={LABEL}>failing</div>
                  <div className={cn(LABEL, 'mt-2 text-muted-foreground')}>
                    {t.accepted} accepted · {t.excepted} excepted · {t.notEvaluated} not evaluated
                  </div>
                </>
              )
            )
          }}
        </Tile>
      </div>
    </div>
  )
}

/**
 * One fixed box. Unknown when any of its answers never arrived; stale on its
 * own when the oldest of them is older than STALE_AFTER_MS. A muted tile keeps
 * its values but not their status colour.
 */
function Tile({
  label,
  queries,
  now,
  frameStale,
  children,
}: {
  label: string
  queries: { data: unknown; dataUpdatedAt: number }[]
  now: number
  frameStale: boolean
  children: (muted: boolean) => ReactNode
}) {
  const unknown = queries.some((q) => q.data === undefined)
  const age = unknown ? 0 : now - Math.min(...queries.map((q) => q.dataUpdatedAt))
  const tileStale = age > STALE_AFTER_MS
  const muted = frameStale || tileStale
  const grey = ramp('Unknown')

  return (
    <section
      className={cn(
        'flex min-h-0 flex-col overflow-hidden border p-5',
        frameStale ? grey.border : tileStale ? cn('bg-card', grey.border) : 'border-border bg-card',
      )}
    >
      <div className="mb-3 flex items-baseline justify-between gap-4">
        <h2 className={cn(LABEL, 'font-medium', frameStale ? grey.text : 'text-muted-foreground')}>{label}</h2>
        {tileStale && <span className={cn(LABEL, grey.text)}>stale · {minutes(age)} min</span>}
      </div>
      {unknown ? (
        <>
          <span
            className={cn('inline-flex w-fit items-center gap-3 border px-3 py-1', LABEL, grey.surface, grey.border, grey.text)}
          >
            <span className={cn('size-3 rounded-full', grey.dot)} aria-hidden="true" />
            Unknown
          </span>
          <span className={cn(VALUE, 'mt-3')}>{EMPTY}</span>
        </>
      ) : (
        children(muted)
      )}
    </section>
  )
}

function Value({ status, muted, children }: { status: StatusName; muted: boolean; children: ReactNode }) {
  return <div className={cn(VALUE, muted ? 'text-foreground' : ramp(status).text)}>{children}</div>
}

function AlertTile({
  count,
  status,
  alerts,
  muted,
  now,
}: {
  count: number | undefined
  status: 'Critical' | 'Warning'
  alerts: AlertView[] | undefined
  muted: boolean
  now: number
}) {
  return (
    <>
      <Value status={count ? status : 'Healthy'} muted={muted}>
        {count ?? EMPTY}
      </Value>
      <ul className="mt-3 space-y-1">
        {(alerts ?? []).map((a) => (
          <li key={a.fingerprint} className={cn(LABEL, 'flex gap-4')}>
            <span className="min-w-0 flex-1 truncate">
              {a.title}
              {a.entityName && ` — ${a.entityName}`}
            </span>
            <span className="shrink-0 font-mono text-muted-foreground">{ago(a.firstSeenUtc, now)}</span>
          </li>
        ))}
      </ul>
    </>
  )
}

const HEALTH_ORDER: HealthState[] = ['Healthy', 'Warning', 'Critical', 'Unknown']

/**
 * Four numbers and one bar. The bar is of known entities only: Unknown is
 * counted, never folded into a proportion (ADR-0026).
 */
function HealthTile({ counts, muted }: { counts: Record<HealthState, number>; muted: boolean }) {
  const known = counts.Healthy + counts.Warning + counts.Critical

  return (
    <>
      <div className="grid grid-cols-2 gap-x-6 gap-y-3">
        {HEALTH_ORDER.map((h) => (
          <div key={h}>
            <Value status={h} muted={muted}>
              {counts[h] ?? EMPTY}
            </Value>
            <div className={cn(LABEL, 'flex items-center gap-2')}>
              <span className={cn('size-3 rounded-full', muted ? ramp('Unknown').dot : ramp(h).dot)} aria-hidden="true" />
              {h.toLowerCase()}
            </div>
          </div>
        ))}
      </div>
      <div className="mt-4 flex h-5 gap-0.5 bg-border">
        {known > 0 &&
          (['Healthy', 'Warning', 'Critical'] as const).map((h) => (
            <div
              key={h}
              className={muted ? ramp('Unknown').dot : ramp(h).dot}
              style={{ width: `${(counts[h] / known) * 100}%` }}
            />
          ))}
      </div>
    </>
  )
}
