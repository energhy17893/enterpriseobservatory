import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, healthStatus, severityStatus } from '@/lib/ui'
import type { RelationshipKind, RelationshipView } from '@/api/types'

/**
 * How an edge reads in a sentence, in each direction.
 *
 * Direction is meaning, not decoration: "this VM runs on that host" and "this
 * host runs that VM" are different facts, and a UI that renders both as a bare
 * arrow makes the operator do the translation.
 */
const PHRASING: Record<RelationshipKind, { outgoing: string; incoming: string }> = {
  Contains: { outgoing: 'contains', incoming: 'is contained by' },
  RunsOn: { outgoing: 'runs on', incoming: 'runs' },
  ConnectedTo: { outgoing: 'is connected to', incoming: 'is connected to' },
  DependsOn: { outgoing: 'depends on', incoming: 'is depended on by' },
  SameAs: { outgoing: 'is the same machine as', incoming: 'is the same machine as' },
  PartOf: { outgoing: 'is part of', incoming: 'has part' },
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

  const { entity, marks, relationships, alerts } = data

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
                <Card className="flex flex-wrap items-center justify-between gap-3 p-3">
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
                </Card>
              </li>
            ))}
          </ul>
        )}
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

function Connection({ relationship }: { relationship: RelationshipView }) {
  const phrase = relationship.isOutgoing
    ? PHRASING[relationship.kind].outgoing
    : PHRASING[relationship.kind].incoming

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
