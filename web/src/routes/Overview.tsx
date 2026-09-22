import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { ago, cn, ramp } from '@/lib/ui'
import type { HealthState } from '@/api/types'

const HEALTH_ORDER: HealthState[] = ['Critical', 'Warning', 'Healthy', 'Unknown']

/** Triage: what needs me right now. */
export function Overview() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['overview'],
    queryFn: api.overview,
    refetchInterval: 15_000,
  })

  // Its own query, and allowed to fail on its own: a self-metrics hiccup
  // must not blank the alert counts above it, and the reverse.
  const selfMetrics = useQuery({
    queryKey: ['self-metrics'],
    queryFn: api.selfMetrics,
    refetchInterval: 15_000,
  })

  if (isError) return <LoadFailure what="The overview" error={error} />
  if (isPending) return <Loading what="the overview" />

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold">Overview</h1>

      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <Metric
          label="Critical"
          value={data.criticalAlerts}
          status={data.criticalAlerts > 0 ? 'Critical' : undefined}
        />
        <Metric
          label="Warning"
          value={data.warningAlerts}
          status={data.warningAlerts > 0 ? 'Warning' : undefined}
        />
        <Metric
          label="Unacknowledged"
          value={data.unacknowledgedAlerts}
          hint="Nobody has taken these yet"
        />
        {/*
          Failing collectors sit beside the alert counts rather than in a
          settings page, because they change what those counts mean: anything a
          failing collector covers is unknown, not quiet.
        */}
        <Metric
          label="Failing collectors"
          value={data.failingCollectors}
          status={data.failingCollectors > 0 ? 'Unknown' : undefined}
          hint={
            data.failingCollectors > 0
              ? 'What they cover is unknown, not healthy'
              : `Oldest read ${ago(data.oldestSuccessfulReadUtc)}`
          }
        />
      </div>

      <Card className="p-4">
        <h2 className="text-sm font-medium">Estate</h2>
        <div className="mt-3 flex flex-wrap gap-2">
          {HEALTH_ORDER.map((health) => (
            <StatusBadge key={health} status={health}>
              {health} · {data.entitiesByHealth[health] ?? 0}
            </StatusBadge>
          ))}
          {/*
            Vanished is shown next to the health counts but is deliberately a
            different kind of fact. These are things a collector stopped
            reporting: kept for thirty days so their history survives a
            maintenance window or a hardware swap, and never counted as healthy.
            See ADR-0004.
          */}
          {data.vanishedEntities > 0 && (
            <StatusBadge status="Unknown">Vanished · {data.vanishedEntities}</StatusBadge>
          )}
        </div>
        <div className="mt-3 text-xs text-muted-foreground">
          <Link to="/entities" className="underline underline-offset-2">
            Open the entity explorer
          </Link>
        </div>
      </Card>

      {/*
        Self-monitoring (Package D): the service was twice dead for hours in
        one week and nothing but its own log said so. This card is the one
        place that answers "is the runner itself still working" without
        reading a log file.
      */}
      {selfMetrics.data && (
        <Card className="p-4">
          <h2 className="text-sm font-medium">Runner</h2>
          <div className="mt-3 grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
            <RunnerStat
              label="Inventory cycle"
              value={
                selfMetrics.data.inventory.atUtc
                  ? `${selfMetrics.data.inventory.durationSeconds.toFixed(1)}s`
                  : '—'
              }
              hint={
                selfMetrics.data.inventory.atUtc
                  ? `Last ran ${ago(selfMetrics.data.inventory.atUtc)}`
                  : 'No cycle yet'
              }
            />
            <RunnerStat
              label="Observation cycle"
              value={
                selfMetrics.data.observation.atUtc
                  ? `${selfMetrics.data.observation.durationSeconds.toFixed(1)}s`
                  : '—'
              }
              hint={
                selfMetrics.data.observation.atUtc
                  ? `Last ran ${ago(selfMetrics.data.observation.atUtc)}`
                  : 'No cycle yet'
              }
            />
            <RunnerStat
              label="Transitions/cycle"
              value={
                selfMetrics.data.inventory.transitionsAppended +
                selfMetrics.data.observation.transitionsAppended
              }
              hint="alert_history rows just appended"
            />
            <RunnerStat
              label="Unknown alerts"
              value={selfMetrics.data.unknownAlerts}
              status={selfMetrics.data.unknownAlerts > 0 ? 'Unknown' : undefined}
            />
            <RunnerStat
              label="Age-clamped"
              value={
                selfMetrics.data.inventory.ageClampedToUnknown +
                selfMetrics.data.observation.ageClampedToUnknown
              }
              hint="Verdicts too old to trust this cycle"
            />
            <RunnerStat
              label="Collection gaps"
              value={`${selfMetrics.data.openGaps} open`}
              status={selfMetrics.data.unrecoverableGaps > 0 ? 'Warning' : undefined}
              hint={`${selfMetrics.data.unrecoverableGaps} unrecoverable`}
            />
          </div>
        </Card>
      )}
    </div>
  )
}

/** One number in the runner card — smaller than {@link Metric}, for a denser row. */
function RunnerStat({
  label,
  value,
  status,
  hint,
}: {
  label: string
  value: number | string
  status?: HealthState
  hint?: string
}) {
  const tone = status ? ramp(status) : null

  return (
    <div>
      <div className="text-xs uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className={cn('mt-1 text-lg font-semibold tabular', tone?.text)}>{value}</div>
      {hint !== undefined && <div className="mt-0.5 text-xs text-muted-foreground">{hint}</div>}
    </div>
  )
}
