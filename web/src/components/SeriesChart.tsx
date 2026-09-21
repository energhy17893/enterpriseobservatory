import { useMemo, useState } from 'react'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading } from '@/components/Primitives'
import { cn } from '@/lib/ui'
import type { SeriesPointView, SeriesResolution, SeriesView } from '@/api/types'

const RANGES = [
  { label: '1h', hours: 1 },
  { label: '6h', hours: 6 },
  { label: '24h', hours: 24 },
  { label: '7d', hours: 24 * 7 },
  { label: '30d', hours: 24 * 30 },
] as const

const RESOLUTION_LABEL: Record<SeriesResolution, string> = {
  Raw: 'as sampled',
  FiveMinutes: '5-minute buckets',
  OneHour: 'hourly buckets',
}

/**
 * One counter over time.
 *
 * Draws the range between the minimum and maximum of each bucket behind the
 * average, rather than the average alone. Those two extra numbers are the
 * reason a bucket stores five: a host pinned at 100% for two minutes inside an
 * hour averages to about 3%, and a chart of averages simply does not contain
 * the thing the operator came to look for.
 */
export function SeriesChart({ entityId }: { entityId: string }) {
  const [hours, setHours] = useState<number>(1)
  const [selected, setSelected] = useState<string | null>(null)

  const options = useQuery({
    queryKey: ['series-options', entityId],
    queryFn: () => api.seriesFor(entityId),
  })

  const counters = options.data ?? []

  // Counter first, device second. One flat list stopped working the day a
  // host started reporting its storage per device and per path: 615 options
  // on one select, in which the eight numbers anybody actually opens the page
  // for are indistinguishable from the six hundred they might drill into.
  const byCounter = useMemo(() => {
    const groups = new Map<string, string[]>()

    for (const option of counters) {
      const instances = groups.get(option.counter)
      if (instances) instances.push(option.instance)
      else groups.set(option.counter, [option.instance])
    }

    // The aggregate first within each counter, then devices in name order, so
    // choosing a counter lands on the summary rather than on whichever device
    // the server happened to return first.
    for (const instances of groups.values()) {
      instances.sort((a, b) => (a === '' ? -1 : b === '' ? 1 : a.localeCompare(b)))
    }

    return groups
  }, [counters])

  const names = useMemo(() => [...byCounter.keys()].sort(), [byCounter])

  const active = selected ?? (counters.length > 0 ? key(counters[0]!) : null)
  const [counter, instance] = active ? split(active) : [null, '']

  const instances = counter ? (byCounter.get(counter) ?? []) : []

  const to = useMemo(() => new Date(), [hours, entityId])
  const from = new Date(to.getTime() - hours * 3600_000)

  const series = useQuery({
    queryKey: ['series', entityId, counter, instance, hours],
    queryFn: () =>
      api.series(entityId, counter!, {
        instance: instance || undefined,
        from: from.toISOString(),
        to: to.toISOString(),
      }),
    enabled: counter !== null,
    refetchInterval: 30_000,
    placeholderData: keepPreviousData,
  })

  if (options.isError) return <LoadFailure what="Counters" error={options.error} />
  if (options.isPending) return <Loading what="counters" />

  if (counters.length === 0) {
    return (
      <Empty>
        No measurement has been recorded for this yet.
        <div className="mt-1">
          Metrics are sampled separately from inventory — if this entity was only just discovered,
          the first samples are a cycle away.
        </div>
      </Empty>
    )
  }

  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <select
            value={counter ?? ''}
            onChange={(event) => {
              const next = event.target.value
              // Land on the summary, not on device number one.
              setSelected(key({ counter: next, instance: byCounter.get(next)?.[0] ?? '' }))
            }}
            className="max-w-full rounded-md border border-border bg-page px-2 py-1 text-sm"
            aria-label="Counter"
          >
            {names.map((name) => (
              <option key={name} value={name}>
                {name}
                {(byCounter.get(name)?.length ?? 0) > 1 &&
                  ` (${byCounter.get(name)!.length})`}
              </option>
            ))}
          </select>

          {instances.length > 1 && (
            <select
              value={instance}
              onChange={(event) =>
                setSelected(key({ counter: counter!, instance: event.target.value }))
              }
              className="max-w-full rounded-md border border-border bg-page px-2 py-1 text-sm"
              aria-label="Device"
            >
              {instances.map((name) => (
                <option key={name} value={name}>
                  {/* An empty instance is vCenter's own figure for the whole
                      entity, or ours across its devices — not a nameless
                      device, and it should not read as one. */}
                  {name === '' ? 'all devices' : name}
                </option>
              ))}
            </select>
          )}
        </div>

        <div className="flex gap-1">
          {RANGES.map((range) => (
            <button
              key={range.label}
              type="button"
              onClick={() => setHours(range.hours)}
              className={cn(
                'rounded-md border border-border px-2 py-1 text-xs',
                hours === range.hours ? 'bg-primary text-primary-on' : 'text-muted-foreground',
              )}
            >
              {range.label}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-4">
        {series.isError ? (
          <LoadFailure what="This series" error={series.error} />
        ) : series.isPending ? (
          <Loading what="the series" />
        ) : (
          <Plot series={series.data} />
        )}
      </div>
    </Card>
  )
}

function Plot({ series }: { series: SeriesView }) {
  const points = series.points

  if (!series.exists) {
    return (
      <div className="py-10 text-center text-sm text-muted-foreground">
        This counter has never been recorded for this entity.
      </div>
    )
  }

  if (points.length === 0) {
    // Deliberately not the same message as "never recorded". One says the
    // platform does not give us this; the other says nothing arrived in the
    // window being looked at, which is usually a collector problem.
    return (
      <div className="py-10 text-center text-sm text-muted-foreground">
        Nothing was recorded in this window.
      </div>
    )
  }

  const width = 900
  const height = 220
  const pad = { top: 8, right: 8, bottom: 20, left: 46 }

  const lo = Math.min(...points.map((p) => p.min))
  const hi = Math.max(...points.map((p) => p.max))
  const span = hi - lo || 1
  const first = Date.parse(points[0]!.atUtc)
  const last = Date.parse(points[points.length - 1]!.atUtc)
  const duration = last - first || 1

  const x = (p: SeriesPointView) =>
    pad.left + ((Date.parse(p.atUtc) - first) / duration) * (width - pad.left - pad.right)

  const y = (value: number) =>
    pad.top + (1 - (value - lo) / span) * (height - pad.top - pad.bottom)

  // The band is drawn as a closed path: the maxima left to right, then the
  // minima back again.
  const band =
    points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p)},${y(p.max)}`).join(' ') +
    ' ' +
    [...points].reverse().map((p) => `L${x(p)},${y(p.min)}`).join(' ') +
    ' Z'

  const line = points
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p)},${y(series.rollup === 'Latest' ? p.last : p.average)}`)
    .join(' ')

  return (
    <div>
      <svg
        viewBox={`0 0 ${width} ${height}`}
        className="w-full"
        role="img"
        aria-label={`${series.counter} over time`}
      >
        {[0, 0.5, 1].map((fraction) => (
          <g key={fraction}>
            <line
              x1={pad.left}
              x2={width - pad.right}
              y1={y(lo + span * fraction)}
              y2={y(lo + span * fraction)}
              stroke="var(--border)"
              strokeWidth="1"
            />
            <text
              x={pad.left - 6}
              y={y(lo + span * fraction) + 4}
              textAnchor="end"
              fontSize="11"
              fill="var(--muted-foreground)"
            >
              {format(lo + span * fraction, series.unit)}
            </text>
          </g>
        ))}

        {/* Minimum to maximum, behind the line. Without it the chart is a
            statement about the mean and nothing else. */}
        <path d={band} fill="var(--primary)" opacity="0.18" />
        <path d={line} fill="none" stroke="var(--primary)" strokeWidth="1.5" />
      </svg>

      <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
        <span>
          {series.unit !== '' && `${series.unit} · `}
          {/*
            Always said out loud. A chart drawing hourly averages without
            saying so reads as a live measurement, which is the quiet kind of
            misrepresentation product principle 1 is about.
          */}
          {RESOLUTION_LABEL[series.resolution]}
          {series.resolution !== 'Raw' && ' (shaded band is min to max)'}
        </span>
        <span className="tabular">
          {points.length} points
          {series.truncated && ' · older points not shown'}
        </span>
      </div>
    </div>
  )
}

// Binary, as vSphere itself shows a datastore's size. Plain thousands would
// label a ten-terabyte volume "10995116.3M", which is a number nobody reads.
const BYTE_UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'] as const

function format(value: number, unit = ''): string {
  const magnitude = Math.abs(value)

  if (unit === 'bytes') {
    let scaled = value
    let step = 0
    while (Math.abs(scaled) >= 1024 && step < BYTE_UNITS.length - 1) {
      scaled /= 1024
      step++
    }
    return `${scaled.toFixed(Math.abs(scaled) >= 10 || step === 0 ? 0 : 1)} ${BYTE_UNITS[step]}`
  }

  if (magnitude >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
  if (magnitude >= 1_000) return `${(value / 1_000).toFixed(1)}k`
  if (magnitude >= 10) return value.toFixed(0)

  return value.toFixed(2)
}

function key(option: { counter: string; instance: string }): string {
  return `${option.counter} ${option.instance}`
}

function split(value: string): [string, string] {
  const [counter, instance = ''] = value.split(' ')
  return [counter!, instance]
}
