import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { ago } from '@/lib/ui'
import type { HealthState } from '@/api/types'

const HEALTH_ORDER: HealthState[] = ['Critical', 'Warning', 'Healthy', 'Unknown']

/** Triage: what needs me right now. */
export function Overview() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['overview'],
    queryFn: api.overview,
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
    </div>
  )
}
