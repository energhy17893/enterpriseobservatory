import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { SeriesChart } from '@/components/SeriesChart'
import { AlertActions } from '@/components/AlertActions'
import { ago, findingLabel, findingStatus, healthBasisLabel, healthStatus, ramp, severityStatus } from '@/lib/ui'
import { basisLabel } from '@/lib/basis'
import { CarriedForward, SimplivityValue } from '@/routes/Simplivity'
import type {
  AnnotationView,
  ClusterFailoverResourceView,
  ClusterFailoverView,
  HaScorecardView,
  RelationshipKind,
  RelationshipView,
  TimeToFullView,
} from '@/api/types'

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

  const { entity, marks, relationships, alerts, timeToFull, haScorecard, clusterFailover, annotations } = data

  return (
    <div className="space-y-6">
      <div>
        <Link to="/entities" className="text-sm text-muted-foreground underline underline-offset-2">
          ← Entity explorer
        </Link>
        <div className="mt-2 flex flex-wrap items-center gap-3">
          <h1 className="text-xl font-semibold">{entity.displayName}</h1>
          <StatusBadge status={healthStatus(entity.health)}>{entity.health}</StatusBadge>
          {entity.healthIsStale && (
            <StatusBadge status="Unknown">
              stale{entity.healthStaleSinceUtc && <> since {ago(entity.healthStaleSinceUtc)}</>}
            </StatusBadge>
          )}
          {entity.observationState !== 'Active' && (
            <span className="rounded-md border border-border px-2 py-0.5 text-xs text-muted-foreground">
              {entity.observationState === 'Vanished' ? 'Vanished' : 'In maintenance'}
            </span>
          )}
        </div>
        <div className="mt-1">
          <Identifier>
            {entity.kind} · {entity.source} · last seen {ago(entity.lastSeenUtc)} · health{' '}
            {healthBasisLabel(entity.healthBasis)}
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

      {annotations.length > 0 && <Annotations annotations={annotations} />}

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

      {haScorecard && (
        <section className="space-y-2">
          <h2 className="text-sm font-medium">HA scorecard</h2>
          <HaScorecard card={haScorecard} />
        </section>
      )}

      {clusterFailover && (
        <section className="space-y-2">
          <h2 className="text-sm font-medium">N+1: if the largest host fails</h2>
          <ClusterFailover failover={clusterFailover} />
        </section>
      )}

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Measurements</h2>
        <SeriesChart entityId={entity.id} />
      </section>

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Relationships</h2>
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

/**
 * A cluster's vSphere HA configuration, in the platform's own words.
 *
 * Every row is shown even when its value is unread, as a dash rather than
 * being omitted — a missing HA setting is itself worth seeing, not something
 * to quietly leave off the card. das.ignoreRedundantNetWarning gets its own
 * called-out row because setting it to true does not fix the underlying risk,
 * it only hides vCenter's own warning about it (see roadmap M8.1).
 */
function HaScorecard({ card }: { card: HaScorecardView }) {
  const rows: { label: string; value: string; bad?: boolean }[] = [
    { label: 'HA enabled', value: yesNo(card.enabled), bad: card.enabled === false },
    {
      label: 'Admission control',
      value: yesNo(card.admissionControlEnabled),
      bad: card.admissionControlEnabled === false,
    },
    { label: 'Admission control policy', value: card.admissionControlPolicyType ?? '—' },
    { label: 'Host monitoring', value: card.hostMonitoring ?? '—', bad: card.hostMonitoring === 'disabled' },
    { label: 'VM monitoring', value: card.vmMonitoring ?? '—' },
    { label: 'APD response', value: card.apdResponse ?? '—', bad: card.apdResponse === 'disabled' },
    { label: 'PDL response', value: card.pdlResponse ?? '—', bad: card.pdlResponse === 'disabled' },
    {
      label: 'Heartbeat datastores',
      value: card.heartbeatDatastoreCount === null ? '—' : String(card.heartbeatDatastoreCount),
      bad: card.heartbeatDatastoreCount !== null && card.heartbeatDatastoreCount < 2,
    },
    {
      label: 'Heartbeat datastore policy',
      value: card.heartbeatDatastoreCandidatePolicy ?? '—',
    },
  ]

  return (
    <div className="space-y-2">
      <Card className="divide-y divide-border">
        {rows.map((row) => (
          <div
            key={row.label}
            className="flex flex-wrap items-center justify-between gap-3 px-3 py-2 text-sm"
          >
            <span className="text-muted-foreground">{row.label}</span>
            <span className={row.bad ? `font-medium ${ramp('Critical').text}` : 'font-medium'}>
              {row.value}
            </span>
          </div>
        ))}
      </Card>

      {card.redundantNetworkWarningSilenced && (
        <Card className="p-3 text-sm">
          <span className={`font-medium ${ramp('Warning').text}`}>Hidden risk: </span>
          <span className="text-muted-foreground">
            das.ignoreRedundantNetWarning is set to true. This cluster&apos;s HA management
            network has no redundant path, and vCenter&apos;s own warning about it has been
            silenced rather than fixed — the risk is unchanged, only invisible.
          </span>
        </Card>
      )}

      {/*
        The cluster's HA continuity findings (ADR-0024), every state shown:
        a passing row is evidence it was checked, a not-evaluated row says
        why it could not be. Accepting or excepting is done on the
        compliance screen.
      */}
      {card.findings.length === 0 ? (
        <div className="text-xs text-muted-foreground">
          The HA continuity checks have not been evaluated for this cluster yet.
        </div>
      ) : (
        <ul className="space-y-2">
          {card.findings.map((finding) => (
            <li key={`${finding.controlId}|${finding.subject}`}>
              <Card className="p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <StatusBadge status={findingStatus(finding.state)}>{findingLabel(finding.state)}</StatusBadge>
                  <span className="font-medium">{finding.title}</span>
                  {finding.stale && <span className="text-xs text-muted-foreground">(stale)</span>}
                </div>
                <div className="mt-1 text-sm text-muted-foreground">
                  {finding.state === 'NotEvaluated' ? finding.reason : finding.observed ?? '—'}
                  {finding.acceptedBy && ` — accepted by ${finding.acceptedBy}: ${finding.acceptedReason ?? ''}`}
                </div>
                {/*
                  K3: labelled "basis:", not "source:" -- on these screens
                  "source" means the catalogue (Compliance.tsx's SCG /
                  eo-continuity sections); this is the citation the control's
                  expectation rests on. Same word, same treatment as the
                  Compliance screen and the continuity report.
                */}
                <div className="mt-1 text-xs text-muted-foreground">basis: {basisLabel(finding.source)}</div>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/**
 * If the largest host in this cluster failed right now, would the survivors
 * still hold every running VM's demand -- and if not today, until when.
 *
 * The unit is host-equivalents (a share of one host's own capacity), not MHz
 * or GB: no collector this product runs reads a host's CPU or memory capacity
 * in absolute units, so "largest host" is approximated as any one host, one
 * host-equivalent of capacity. That approximation is exact on a cluster built
 * from identical hosts and is named on the card rather than hidden.
 */
function ClusterFailover({ failover }: { failover: ClusterFailoverView }) {
  return (
    <div className="space-y-2">
      <Card className="p-3 text-sm text-muted-foreground">
        {failover.hostCount} live hosts. Losing the largest one leaves{' '}
        {failover.cpu.availableAfterFailoverHosts.toFixed(2)} host-equivalents of headroom for CPU
        and the same for memory, after keeping the survivors under this product&apos;s 90%
        utilisation ceiling. &quot;Largest host&quot; is approximated as any one host -- see below.
      </Card>
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <ClusterFailoverResource label="CPU" resource={failover.cpu} />
        <ClusterFailoverResource label="Memory" resource={failover.memory} />
      </div>
    </div>
  )
}

function ClusterFailoverResource({
  label,
  resource,
}: {
  label: string
  resource: ClusterFailoverResourceView
}) {
  if (resource.holdsNow === null || resource.demandHosts === null) {
    return (
      <Card className="p-3 text-sm">
        <div className="font-medium">{label}</div>
        <div className="mt-1 text-muted-foreground">
          Unknown: at least one host&apos;s current usage could not be read this cycle.
        </div>
      </Card>
    )
  }

  return (
    <Card className="p-3 text-sm">
      <div className="flex items-center justify-between gap-3">
        <span className="font-medium">{label}</span>
        <span className={`font-medium ${resource.holdsNow ? '' : ramp('Critical').text}`}>
          {resource.holdsNow ? 'Holds now' : 'Fails now'}
        </span>
      </div>
      <div className="mt-1 text-muted-foreground">
        {resource.demandHosts.toFixed(2)} of {resource.availableAfterFailoverHosts.toFixed(2)}{' '}
        host-equivalents of demand.
      </div>
      {resource.date && (
        <div className={`mt-1 ${resource.date.isForecast && (resource.date.days ?? 0) <= 30 ? ramp('Warning').text : 'text-muted-foreground'}`}>
          {resource.date.summary}
        </div>
      )}
    </Card>
  )
}

function yesNo(value: boolean | null): string {
  return value === null ? '—' : value ? 'Yes' : 'No'
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

/** The status keys HPE's own tools show as a state (reference-approaches §10.8). */
const SIMPLIVITY_STATES = new Set(['state', 'ha_status'])

/**
 * What another source says about this entity (ADR-0027) — for SimpliVity, a
 * strip right under the header (§11.6). A key the source did not answer is
 * Unknown, never blank; a value kept while the source is silent says
 * "carried forward" and when it was read.
 */
function Annotations({ annotations }: { annotations: AnnotationView[] }) {
  const groups = new Map<string, AnnotationView[]>()
  for (const a of annotations) {
    const key = `${a.namespace} ${a.source}`
    groups.set(key, [...(groups.get(key) ?? []), a])
  }

  return (
    <>
      {[...groups.entries()].map(([key, values]) => {
        const first = values[0]!
        return (
          <section key={key} className="space-y-2">
            <h2 className="text-sm font-medium">
              {first.namespace === 'simplivity' ? (
                <Link to="/simplivity" className="underline underline-offset-2">
                  SimpliVity
                </Link>
              ) : (
                first.namespace
              )}
            </h2>
            <Card className="flex flex-wrap items-center gap-x-5 gap-y-2 p-3 text-sm">
              {values.map((a) => (
                <div key={a.key} className="flex items-center gap-1.5">
                  <span className="text-muted-foreground">{a.key.replaceAll('_', ' ')}</span>
                  {a.value === null || SIMPLIVITY_STATES.has(a.key) ? (
                    <SimplivityValue value={a.value} />
                  ) : (
                    <span className="font-mono text-xs">{a.value}</span>
                  )}
                </div>
              ))}
              <Identifier>from {first.source}</Identifier>
              {first.carriedForward && <CarriedForward readAtUtc={first.readAtUtc} />}
            </Card>
          </section>
        )
      })}
    </>
  )
}
