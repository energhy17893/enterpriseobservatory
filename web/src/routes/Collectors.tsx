import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, healthStatus } from '@/lib/ui'

/**
 * The monitoring system observing itself.
 *
 * A screen rather than a log file, because "is the monitoring actually working"
 * has to be answerable from inside the product. In the previous one it was
 * answerable only by reading logs on the server, which meant in practice that
 * nobody answered it until something had already been missed. See ADR-0005.
 */
export function Collectors() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['collectors'],
    queryFn: api.collectors,
    refetchInterval: 15_000,
  })

  if (isError) return <LoadFailure what="Collectors" error={error} />
  if (isPending) return <Loading what="collectors" />

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Collectors</h1>

      {data.length === 0 ? (
        <Empty>
          No collector has run yet.
          <div className="mt-1">
            Nothing is being monitored — every entity would read as Unknown rather than healthy.
          </div>
        </Empty>
      ) : (
        <Card className="divide-y divide-border">
          {data.map((collector) => (
            <div
              key={`${collector.instanceId}-${collector.role}`}
              className="flex flex-wrap items-start justify-between gap-3 p-3"
            >
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{collector.instanceId}</span>
                  {/*
                    The role is shown because the two fail independently: an
                    account may be able to list inventory and still be denied
                    metrics by the platform's statistics level. "We cannot list
                    your inventory" and "we cannot read your metrics" have
                    different fixes. See ADR-0009.
                  */}
                  <Identifier>
                    {collector.role === 'Inventory' ? 'inventory' : 'metrics'}
                  </Identifier>
                  <StatusBadge status={healthStatus(collector.health)}>
                    {collector.health}
                  </StatusBadge>
                  {collector.isBackingOff && (
                    <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                      Backing off
                    </span>
                  )}
                </div>
                {collector.lastFailureDetail !== null && (
                  <div className="mt-1 text-sm text-muted-foreground">
                    {collector.lastFailureDetail}
                  </div>
                )}
              </div>
              <div className="shrink-0 text-right text-xs text-muted-foreground">
                <div>last success {ago(collector.lastSuccessUtc)}</div>
                {collector.consecutiveFailures > 0 && (
                  <div className="tabular">
                    {collector.consecutiveFailures} consecutive failures
                  </div>
                )}
              </div>
            </div>
          ))}
        </Card>
      )}
    </div>
  )
}
