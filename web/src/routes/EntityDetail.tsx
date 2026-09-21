import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { SeriesChart } from '@/components/SeriesChart'
import { AlertActions } from '@/components/AlertActions'
import { ago, healthStatus, severityStatus } from '@/lib/ui'
import type { RelationshipKind, RelationshipView, TimeToFullView } from '@/api/types'

/**
 * How an edge reads in a sentence, in each direction.
 *
 * Direction is meaning, not decoration: "this VM runs on that host" and "this
 * host runs that VM" are different facts, and a UI that renders both as a bare
 * arrow makes the operator do the translation.
 */
const PHRASING: Record<RelationshipKind, { outgoing: string; incoming: string }> = {
  PartOf: { outgoing: 'is part of', incoming: 'contains' },
  RunsOn: { outgoing: 'runs on', incoming: 'runs' },
  SameAs: { outgoing: 'is the same machine as', incoming: 'is the same machine as' },
  ConnectedTo: { outgoing: 'is connected to', incoming: 'is connected to' },
  BackedBy: { outgoing: 'is backed by', incoming: 'backs' },
  ManagedBy: { outgoing: 'is managed by', incoming: 'manages' },
}

/**
 * Entity detail: tier 2 of ADR-0007.
 *
 * The alerts on this page are the inbox's instances filtered to this entity —
 * the same objects with the same state, never a second list. That was the
 * actual defect ADR-0007 was written to prevent.
 */
export function EntityDetail() {
  const { id = '' } = useParams()

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['entity', id],
    queryFn: () => api.entity(id),
    refetchInterval: 30_000,
    retry: (count, err) => !(err instanceof ApiError && err.status === 404) && count < 2,
  })

  if (isError) {
    if (error instanceof ApiError && error.status === 404) {
      return (
        <Empty>
          Nothing here with that identifier.
          <div className="mt-1">
            It may have been forgotten after thirty days without being reported.
          </div>
        </Empty>
      )
    }

    return <LoadFailure what="This entity" error={error} />
  }

  if (isPending) return <Loading what="the entity" />

  const { entity, marks, relationships, alerts, timeToFull } = data

  return (
    <div className="space-y-6">
      <div>
        <Link to="/entities" className="text-sm text-muted-foreground underline underline-offset-2">
          ← Entity explorer
        </Link>
        <div className="mt-2 flex flex-wrap items-center gap-3">
          <h1 className="text-xl font-semibold">{entity.displayName}</h1>
          <StatusBadge status={healthStatus(entity.health)}>{entity.health}</StatusBadge>
          {entity.observationState !== 'Active' && (
            <span className="rounded-md border border-border px-2 py-0.5 text-xs text-muted-foreground">
              {entity.observationState === 'Vanished' ? 'Vanished' : 'In maintenance'}
            </span>
          )}
        </div>
        <div className="mt-1">
          <Identifier>
            {entity.kind} · {entity.source} · last seen {ago(entity.lastSeenUtc)}
          </Identifier>
        </div>
        {entity.observationState === 'Vanished' && (
          <Card className="mt-3 p-3 text-sm text-muted-foreground">
            A collector stopped reporting this. It is kept so its history survives a maintenance
            window or a hardware swap, and its health reads Unknown rather than whatever it was
            when we last saw it.
          </Card>
        )}
      </div>

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Alerts</h2>
        {alerts.length === 0 ? (
          <Empty>No alert is currently firing for this entity.</Empty>
        ) : (
          <ul className="space-y-2">
            {alerts.map((alert) => (
              <li key={alert.fingerprint}>
                <Card className="p-3">
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <div className="flex flex-wrap items-center gap-2">
                      <StatusBadge status={severityStatus(alert.severity)}>
                        {alert.severity}
                      </StatusBadge>
                      <span className="font-medium">{alert.title}</span>
                      {alert.state !== 'Open' && (
                        <span className="text-xs text-muted-foreground">{alert.state}</span>
                      )}
                    </div>
                    <span className="text-xs text-muted-foreground">
                      since {ago(alert.firstSeenUtc)}
                    </span>
                  </div>
                  {/*
                    The same actions as the inbox, on the same instances. An
                    alert acknowledged here is acknowledged there — that is the
                    whole point of one model. See ADR-0007.
                  */}
                  <AlertActions alert={alert} />
                </Card>
              </li>
            ))}
          </ul>
        )}
      </section>

      {timeToFull && (
        <section className="space-y-2">
          <h2 className="text-sm font-medium">Time to full</h2>
          <TimeToFull estimate={timeToFull} />
        </section>
      )}

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Measurements</h2>
        <SeriesChart entityId={entity.id} />
      </section>

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Connections</h2>
        {/*
          Clickable, which is the point. The product's end-to-end triangulation
          claim is only real if an operator can walk host → HBA → switch port →
          array without holding the chain in their head. See ADR-0007 §4.
        */}
        {relationships.length === 0 ? (
          <Empty>Nothing else is known to be connected to this yet.</Empty>
        ) : (
          <Card className="divide-y divide-border">
            {relationships.map((relationship) => (
              <Connection
                key={`${relationship.kind}-${relationship.isOutgoing}-${relationship.otherId}`}
                relationship={relationship}
              />
            ))}
          </Card>
        )}
      </section>

      {marks.length > 0 && (
        <section className="space-y-2">
          <h2 className="text-sm font-medium">Identity</h2>
          {/*
            Evidence, not identity. Whether two records are the same machine is
            the resolver's decision, made from every collector's marks at once —
            no single collector has enough to decide it. See ADR-0003.
          */}
          <Card className="divide-y divide-border">
            {marks.map((mark) => (
              <div
                key={`${mark.kind}-${mark.value}`}
                className="flex flex-wrap items-center justify-between gap-3 px-3 py-2"
              >
                <span className="text-sm text-muted-foreground">{mark.kind}</span>
                <span className="font-mono text-xs">{mark.value}</span>
                <Identifier>from {mark.source}</Identifier>
              </div>
            ))}
          </Card>
        </section>
      )}
    </div>
  )
}

/**
 * When a datastore fills at its current growth, or why that cannot be said.
 *
 * A refusal is shown as an answer, never hidden: "not filling" and "not
 * computed" must not look the same. And a date is never shown without the
 * window it was measured over — a days-to-full without its window is not a
 * number.
 */
function TimeToFull({ estimate }: { estimate: TimeToFullView }) {
  if (!estimate.isForecast) {
    return (
      <Card className="p-3 text-sm">
        <span className="font-medium">Cannot estimate:</span>{' '}
        <span className="text-muted-foreground">{refusal(estimate.summary)}</span>
        {estimate.reason && (
          <div className="mt-1">
            <Identifier>{estimate.reason}</Identifier>
          </div>
        )}
      </Card>
    )
  }

  const days = estimate.days ?? 0
  const date = estimate.fullAtUtc ? estimate.fullAtUtc.slice(0, 10) : '—'

  return (
    <Card className="p-3 text-sm">
      <div>
        <span className="font-medium">
          Fills in {days.toFixed(days < 10 ? 1 : 0)} days (on {date})
        </span>
        , based on {windowText(estimate)}
      </div>
      {estimate.growthBytesPerDay !== null && (
        <div className="mt-1 text-muted-foreground">
          Growing {(estimate.growthBytesPerDay / 1024 ** 3).toFixed(2)} GB a day at the current
          trend.
        </div>
      )}
    </Card>
  )
}

function windowText(estimate: TimeToFullView): string {
  if (!estimate.windowFromUtc || !estimate.windowToUtc) return 'no history'

  const from = new Date(estimate.windowFromUtc)
  const to = new Date(estimate.windowToUtc)
  const spanDays = (to.getTime() - from.getTime()) / 86_400_000

  return `${spanDays.toFixed(1)} days of history (${estimate.windowFromUtc.slice(0, 10)} to ${estimate.windowToUtc.slice(0, 10)}, ${estimate.pointsUsed} points)`
}

/** The server's sentence without its "Cannot estimate a fill date:" lead-in. */
function refusal(summary: string): string {
  const lead = 'Cannot estimate a fill date: '
  return summary.startsWith(lead) ? summary.slice(lead.length) : summary
}

function Connection({ relationship }: { relationship: RelationshipView }) {
  // Falls back to the raw kind rather than crashing. The vocabulary is closed
  // and typed, so a missing entry means the server sent a kind this build does
  // not know — a version skew, during which the page must still render.
  const phrasing = PHRASING[relationship.kind] as
    | { outgoing: string; incoming: string }
    | undefined

  const phrase = phrasing
    ? relationship.isOutgoing
      ? phrasing.outgoing
      : phrasing.incoming
    : relationship.kind

  return (
    <div className="flex flex-wrap items-center gap-2 px-3 py-2">
      <span className="text-sm text-muted-foreground">{phrase}</span>
      <Link
        to={`/entities/${encodeURIComponent(relationship.otherId)}`}
        className="text-sm underline underline-offset-2"
      >
        {relationship.otherName}
      </Link>
      <Identifier>{relationship.otherKind}</Identifier>
      <StatusBadge status={healthStatus(relationship.otherHealth)}>
        {relationship.otherHealth}
      </StatusBadge>
    </div>
  )
}
