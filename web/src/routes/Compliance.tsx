import { useEffect, useState, type FormEvent } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, cn, type StatusName } from '@/lib/ui'
import { basisLabel } from '@/lib/basis'
import type {
  AuthStateView,
  ComplianceControlView,
  ComplianceExceptionView,
  ComplianceFindingView,
  FindingCountsView,
  FindingState,
} from '@/api/types'

const STATE_STATUS: Record<FindingState, StatusName> = {
  Failing: 'Warning',
  Accepted: 'Info',
  Excepted: 'Info',
  Passing: 'Healthy',
  NotEvaluated: 'Unknown',
}

const STATE_LABEL: Record<FindingState, string> = {
  Failing: 'Failing',
  Accepted: 'Accepted',
  Excepted: 'Excepted',
  Passing: 'Passing',
  NotEvaluated: 'Not evaluated',
}

const ORDER: FindingState[] = ['Failing', 'Accepted', 'Excepted', 'Passing', 'NotEvaluated']

/**
 * K3 §1.1: the catalogue a control comes from, as a first-class, always-
 * visible dimension -- distinct from `control.citation` ("basis:", what the
 * expectation rests on). Fixed order: SCG is the audit-standard catalogue,
 * eo-continuity is the product's own, listed second. Values match
 * `ComplianceSources` on the server exactly.
 */
const SOURCES = ['Broadcom SCG', 'eo-continuity'] as const
const SOURCE_FILTERS = ['All', ...SOURCES] as const
type SourceFilter = (typeof SOURCE_FILTERS)[number]

/** The server refuses longer; see ComplianceService. */
const MAX_REASON = 2000
const MAX_OWNER = 200

/**
 * Said beside the counts, never folded into them: a stale failing finding is
 * still failing. What it is not is current — its vCenter did not answer last
 * cycle, so the verdict is the last one that could be reached.
 */
function StaleBadge({ count }: { count: number }) {
  if (count === 0) return null
  return (
    <StatusBadge status="Unknown">
      Stale {count}
    </StatusBadge>
  )
}

const EXCEPTION_DURATIONS = [
  { label: '30 days', days: 30 },
  { label: '90 days', days: 90 },
  { label: '180 days', days: 180 },
  { label: '1 year', days: 365 },
] as const

function countOf(counts: FindingCountsView, state: FindingState): number {
  switch (state) {
    case 'Failing':
      return counts.failing
    case 'Accepted':
      return counts.accepted
    case 'Excepted':
      return counts.excepted
    case 'Passing':
      return counts.passing
    case 'NotEvaluated':
      return counts.notEvaluated
  }
}

/**
 * Compliance: how the estate is configured against Broadcom's Security
 * Configuration Guide.
 *
 * Deliberately not the alert inbox. A finding does not close itself and is
 * expected by the hundred on the first day; poured into the inbox it would
 * teach operators to stop reading alerts, which is what vROps' hardening
 * alarms did. Here a finding is fixed, accepted (somebody owns it — still
 * non-compliant), or excepted until a date.
 */
export function Compliance({ identity }: { identity: AuthStateView }) {
  // A report links here as /compliance#<controlId>: that control opens.
  const { hash } = useLocation()
  const [open, setOpen] = useState<string | null>(hash ? decodeURIComponent(hash.slice(1)) : null)
  const [showUnevaluated, setShowUnevaluated] = useState(false)
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>('All')

  const summary = useQuery({
    queryKey: ['compliance'],
    queryFn: api.compliance,
    refetchInterval: 60_000,
  })

  const loaded = summary.data !== undefined
  useEffect(() => {
    if (hash && loaded) document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView()
  }, [hash, loaded])

  const canAct = identity.role === 'Operator' || identity.role === 'Administrator'

  if (summary.isError) return <LoadFailure what="Compliance" error={summary.error} />
  if (summary.isPending) return <Loading what="compliance" />

  const data = summary.data

  if (data.catalogueProblem !== null) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-semibold">Compliance</h1>
        <LoadFailure what="The compliance catalogue" error={new Error(data.catalogueProblem)} />
      </div>
    )
  }

  const evaluated = data.controls.filter((c) => c.evaluated)
  const unevaluated = data.controls.filter((c) => !c.evaluated)

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold">Compliance</h1>
        <div className="mt-1 text-sm text-muted-foreground">
          Security Configuration Guide <Identifier>{data.catalogueName}</Identifier> release{' '}
          <Identifier>{data.catalogueRelease}</Identifier>
          {' · '}
          {data.lastEvaluatedUtc === null ? 'not evaluated yet' : `evaluated ${ago(data.lastEvaluatedUtc)}`}
        </div>
      </div>

      <Card className="p-4 text-sm text-muted-foreground">
        Only the {data.controls.length} controls whose shipped default misses the baseline are
        listed; {data.defaultControlsSkipped} more are met by an untouched installation. Findings
        stay here, not in the alert inbox, and do not close by themselves. Accepting one says you
        own it — it stays non-compliant. An exception takes it out of the count until a date.
      </Card>

      <div className="flex flex-wrap gap-2">
        {ORDER.map((state) => (
          <StatusBadge key={state} status={STATE_STATUS[state]}>
            {STATE_LABEL[state]} {countOf(data.totals, state)}
          </StatusBadge>
        ))}
        <StaleBadge count={data.totals.stale} />
      </div>

      {data.totals.stale > 0 && (
        <Card className="p-4 text-sm text-muted-foreground">
          {data.totals.stale} findings are stale: their vCenter did not answer in the last inventory
          cycle, so they show the last verdict that could be reached and when the host was last
          read — not the host as it is now.
        </Card>
      )}

      {/*
        K3 §1.1: catalogue is a narrowing control, not a hidden toggle -- "All"
        shows both sections by default. Two different things share the word
        "source" here (see ControlRow): this chip row is the catalogue, never
        the citation.
      */}
      <div className="flex flex-wrap gap-2">
        {SOURCE_FILTERS.map((value) => (
          <button
            key={value}
            type="button"
            onClick={() => setSourceFilter(value)}
            aria-pressed={sourceFilter === value}
            className={cn(
              'rounded-md border border-border px-3 py-1 text-sm',
              sourceFilter === value ? 'bg-primary text-primary-on' : 'text-muted-foreground',
            )}
          >
            {value}
          </button>
        ))}
      </div>

      {SOURCES.filter((source) => sourceFilter === 'All' || sourceFilter === source).map((source) => (
        <SourceSection
          key={source}
          source={source}
          controls={evaluated.filter((c) => c.source === source)}
          open={open}
          onToggle={(id) => setOpen(open === id ? null : id)}
          canAct={canAct}
        />
      ))}

      <Exceptions exceptions={data.exceptions} canAct={canAct} />

      <section className="space-y-2">
        <button
          type="button"
          onClick={() => setShowUnevaluated(!showUnevaluated)}
          className="text-sm font-medium hover:underline"
          aria-expanded={showUnevaluated}
        >
          {showUnevaluated ? '▾' : '▸'} Not evaluated — no data collected{' '}
          <span className="text-muted-foreground">
            ({unevaluated.filter((c) => sourceFilter === 'All' || sourceFilter === c.source).length})
          </span>
        </button>
        <div className="text-xs text-muted-foreground">
          These are not passing. This product does not yet read what they ask about, so it says
          nothing about them rather than guessing.
        </div>
        {showUnevaluated && (
          <Card className="divide-y divide-border">
            {unevaluated
              .filter((c) => sourceFilter === 'All' || sourceFilter === c.source)
              .map((control) => (
                <div key={control.controlId} className="p-3 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <Identifier>{control.controlId}</Identifier>
                    <span className="text-xs text-muted-foreground">{control.source}</span>
                    <span className="text-xs text-muted-foreground">{control.priority}</span>
                  </div>
                  <div className="mt-0.5">{control.title}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">{control.notEvaluatedReason}</div>
                </div>
              ))}
          </Card>
        )}
      </section>
    </div>
  )
}

/**
 * K3 §1.1: one catalogue's section -- SCG and eo-continuity are kept visually
 * apart (never a flat list with a source column) because a shared list blurs
 * "Broadcom's document" back into "ours". Collapsible so an operator who only
 * cares about one catalogue can fold the other away without the filter chips.
 */
function SourceSection({
  source,
  controls,
  open,
  onToggle,
  canAct,
}: {
  source: string
  controls: ComplianceControlView[]
  open: string | null
  onToggle: (controlId: string) => void
  canAct: boolean
}) {
  const [expanded, setExpanded] = useState(true)
  const failing = controls.reduce((n, c) => n + c.counts.failing + c.counts.notEvaluated, 0)

  return (
    <section className="space-y-2">
      <button
        type="button"
        onClick={() => setExpanded(!expanded)}
        aria-expanded={expanded}
        className="flex items-center gap-2 text-sm font-medium hover:underline"
      >
        {expanded ? '▾' : '▸'} {source}{' '}
        <span className="text-muted-foreground">
          ({controls.length} control{controls.length === 1 ? '' : 's'}
          {failing > 0 ? `, ${failing} failing or not evaluated` : ''})
        </span>
      </button>
      {expanded &&
        (controls.length === 0 ? (
          <Empty>This build evaluates no {source} control.</Empty>
        ) : (
          <Card className="divide-y divide-border">
            {controls.map((control) => (
              <ControlRow
                key={control.controlId}
                control={control}
                open={open === control.controlId}
                onToggle={() => onToggle(control.controlId)}
                canAct={canAct}
              />
            ))}
          </Card>
        ))}
    </section>
  )
}

function ControlRow({
  control,
  open,
  onToggle,
  canAct,
}: {
  control: ComplianceControlView
  open: boolean
  onToggle: () => void
  canAct: boolean
}) {
  return (
    <div id={control.controlId} className="scroll-mt-4">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={open}
        className="flex w-full flex-wrap items-start justify-between gap-3 p-3 text-left hover:bg-page"
      >
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <Identifier>{control.controlId}</Identifier>
            <span className="text-xs text-muted-foreground">{control.priority}</span>
          </div>
          <div className="mt-0.5 text-sm font-medium">{control.title}</div>
          <div className="mt-0.5 text-xs text-muted-foreground">
            <Identifier>{control.parameter}</Identifier> — baseline {control.baselineValue}
          </div>
          <div className="mt-0.5 text-xs text-muted-foreground">
            {control.source}
            {' · basis: '}
            {control.source === 'Broadcom SCG' && !control.citation
              ? 'the guide itself'
              : basisLabel(control.citation)}
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap gap-1.5">
          {ORDER.filter((state) => countOf(control.counts, state) > 0).map((state) => (
            <StatusBadge key={state} status={STATE_STATUS[state]}>
              {STATE_LABEL[state]} {countOf(control.counts, state)}
            </StatusBadge>
          ))}
          <StaleBadge count={control.counts.stale} />
        </div>
      </button>
      {open && <Findings control={control} canAct={canAct} />}
    </div>
  )
}

/**
 * K3 §1.2: the default finding list per control shows Failing and
 * NotEvaluated rows (Accepted/Excepted sit with Failing -- still
 * non-compliant, ADR-0024). Passing is not hidden, but collapsed to a single
 * count with a disclosure, so a control passing on hundreds of subjects (the
 * estate's own path-single: 290 of 290) does not bury the handful of live
 * failures under green rows an operator has learned to stop reading
 * (ADR-0024's rationale for keeping findings out of the alert inbox, applied
 * to this screen's own volume).
 */
function Findings({ control, canAct }: { control: ComplianceControlView; canAct: boolean }) {
  const [showPassing, setShowPassing] = useState(false)

  const findings = useQuery({
    queryKey: ['compliance', 'findings', control.controlId],
    queryFn: () => api.complianceFindings({ control: control.controlId }),
  })

  const passing = findings.data?.filter((f) => f.state === 'Passing') ?? []
  const rest = findings.data?.filter((f) => f.state !== 'Passing') ?? []

  return (
    <div className="space-y-3 border-t border-border bg-page p-3">
      {control.assessment !== '' && (
        <div className="text-xs text-muted-foreground">
          Check by hand: <Identifier>{control.assessment}</Identifier>
        </div>
      )}

      {canAct && (
        <ExceptionForm controlId={control.controlId} entityId={null} label="Except every host" />
      )}

      {findings.isError && <LoadFailure what="Findings" error={findings.error} />}
      {findings.isPending && <Loading what="findings" />}
      {findings.data !== undefined &&
        (findings.data.length === 0 ? (
          <Empty>No hosts have been evaluated against this control yet.</Empty>
        ) : (
          <>
            {rest.length === 0 ? (
              <Empty>Nothing failing or unevaluated for this control.</Empty>
            ) : (
              <Card className="divide-y divide-border">
                {rest.map((finding) => (
                  <FindingRow key={`${finding.entityId}-${finding.controlId}`} finding={finding} canAct={canAct} />
                ))}
              </Card>
            )}
            {passing.length > 0 && (
              <div className="text-xs text-muted-foreground">
                <button type="button" onClick={() => setShowPassing(!showPassing)} className="hover:underline">
                  {showPassing ? '▾' : '▸'} {passing.length} passed
                </button>
                {showPassing && (
                  <Card className="mt-2 divide-y divide-border">
                    {passing.map((finding) => (
                      <FindingRow
                        key={`${finding.entityId}-${finding.controlId}`}
                        finding={finding}
                        canAct={canAct}
                      />
                    ))}
                  </Card>
                )}
              </div>
            )}
          </>
        ))}
    </div>
  )
}

function FindingRow({ finding, canAct }: { finding: ComplianceFindingView; canAct: boolean }) {
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [excepting, setExcepting] = useState(false)

  const accept = useMutation({
    mutationFn: () => api.acceptFinding(finding.controlId, finding.entityId, reason),
    onSuccess: () => {
      setError(null)
      void queryClient.invalidateQueries({ queryKey: ['compliance'] })
    },
    onError: (cause: unknown) => setError(cause instanceof Error ? cause.message : String(cause)),
  })

  return (
    <div className="space-y-2 p-3 text-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <Link
              to={`/entities/${encodeURIComponent(finding.entityId)}`}
              className="font-medium hover:underline"
            >
              {finding.entityName || finding.entityId}
            </Link>
            <StatusBadge status={STATE_STATUS[finding.state]}>{STATE_LABEL[finding.state]}</StatusBadge>
            {finding.stale && (
              <StatusBadge status="Unknown">Stale — vCenter did not answer</StatusBadge>
            )}
          </div>
          <div className="mt-1 text-xs text-muted-foreground">
            {finding.observed === null ? (
              finding.reason
            ) : (
              <>
                observed <Identifier>{finding.observed === '' ? '(empty)' : finding.observed}</Identifier>
                {' · '}expected {finding.expected}
                {finding.reason !== null && <> · {finding.reason}</>}
              </>
            )}
          </div>
          {finding.acceptedBy !== null && (
            <div className="mt-1 text-xs text-muted-foreground">
              accepted by {finding.acceptedBy} {ago(finding.acceptedAtUtc)}
              {finding.acceptedReason ? ` — ${finding.acceptedReason}` : ''}
            </div>
          )}
        </div>
        <div className="shrink-0 text-right text-xs text-muted-foreground">
          <div>since {ago(finding.firstSeenUtc)}</div>
          <div>{finding.stale ? 'last read' : 'read'} {ago(finding.lastEvaluatedUtc)}</div>
        </div>
      </div>

      {canAct && finding.state === 'Failing' && (
        <div className="flex flex-wrap items-center gap-2">
          <input
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            placeholder="Why, or the change ticket"
            maxLength={MAX_REASON}
            className="min-w-48 flex-1 rounded-md border border-border bg-card px-2 py-1 text-xs"
          />
          <button
            type="button"
            disabled={accept.isPending}
            onClick={() => accept.mutate()}
            className="rounded-md border border-border px-2 py-1 text-xs hover:bg-card"
          >
            {accept.isPending ? 'Working…' : 'Accept'}
          </button>
          <button
            type="button"
            onClick={() => setExcepting(!excepting)}
            className="rounded-md border border-border px-2 py-1 text-xs hover:bg-card"
          >
            Exception…
          </button>
        </div>
      )}

      {canAct && excepting && (
        <ExceptionForm controlId={finding.controlId} entityId={finding.entityId} label="Except this host" />
      )}

      {error !== null && <ErrorLine message={error} />}
    </div>
  )
}

function ExceptionForm({
  controlId,
  entityId,
  label,
}: {
  controlId: string
  entityId: string | null
  label: string
}) {
  const queryClient = useQueryClient()
  const [reason, setReason] = useState('')
  const [owner, setOwner] = useState('')
  const [days, setDays] = useState<number>(90)
  const [error, setError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: () =>
      api.addComplianceException({
        controlId,
        entityId,
        reason,
        owner,
        expiresUtc: new Date(Date.now() + days * 86_400_000).toISOString(),
      }),
    onSuccess: () => {
      setReason('')
      setOwner('')
      setError(null)
      void queryClient.invalidateQueries({ queryKey: ['compliance'] })
    },
    onError: (cause: unknown) => setError(cause instanceof Error ? cause.message : String(cause)),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    add.mutate()
  }

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2 text-xs">
      <label className="block min-w-48 flex-1">
        <span className="mb-1 block text-muted-foreground">Reason</span>
        <input
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          placeholder="Why this does not apply"
          maxLength={MAX_REASON}
          className="w-full rounded-md border border-border bg-card px-2 py-1"
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-muted-foreground">Owner</span>
        <input
          value={owner}
          onChange={(event) => setOwner(event.target.value)}
          placeholder="Who answers for it"
          maxLength={MAX_OWNER}
          className="rounded-md border border-border bg-card px-2 py-1"
        />
      </label>
      <label className="block">
        <span className="mb-1 block text-muted-foreground">Until</span>
        <select
          value={days}
          onChange={(event) => setDays(Number(event.target.value))}
          className="rounded-md border border-border bg-card px-2 py-1"
        >
          {EXCEPTION_DURATIONS.map((duration) => (
            <option key={duration.days} value={duration.days}>
              {duration.label}
            </option>
          ))}
        </select>
      </label>
      <button
        type="submit"
        disabled={add.isPending}
        className={cn(
          'rounded-md px-2 py-1 font-medium',
          add.isPending ? 'bg-border text-muted-foreground' : 'bg-primary text-primary-on',
        )}
      >
        {add.isPending ? 'Working…' : label}
      </button>
      {error !== null && (
        <div className="w-full">
          <ErrorLine message={error} />
        </div>
      )}
    </form>
  )
}

function Exceptions({
  exceptions,
  canAct,
}: {
  exceptions: ComplianceExceptionView[]
  canAct: boolean
}) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)

  const remove = useMutation({
    mutationFn: (id: string) => api.removeComplianceException(id),
    onSuccess: () => {
      setError(null)
      void queryClient.invalidateQueries({ queryKey: ['compliance'] })
    },
    onError: (cause: unknown) => setError(cause instanceof Error ? cause.message : String(cause)),
  })

  return (
    <section className="space-y-2">
      <h2 className="text-sm font-medium">
        Exceptions <span className="text-muted-foreground">({exceptions.length})</span>
      </h2>
      {exceptions.length === 0 ? (
        <Empty>No exceptions. Every failing finding counts as non-compliant.</Empty>
      ) : (
        <Card className="divide-y divide-border">
          {exceptions.map((exception) => (
            <div key={exception.id} className="flex flex-wrap items-start justify-between gap-3 p-3 text-sm">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <Identifier>{exception.controlId}</Identifier>
                  <span className="text-xs text-muted-foreground">
                    {exception.entityId === null ? 'every host' : exception.entityId}
                  </span>
                  {exception.expired && <StatusBadge status="Warning">Expired</StatusBadge>}
                </div>
                <div className="mt-0.5">{exception.reason}</div>
                <div className="mt-0.5 text-xs text-muted-foreground">
                  owner {exception.owner} · recorded by {exception.createdBy} {ago(exception.createdAtUtc)}
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-3">
                <div className="text-right text-xs text-muted-foreground">
                  {exception.expired ? 'expired' : 'expires'} {ago(exception.expiresUtc)}
                </div>
                {canAct && (
                  <button
                    type="button"
                    onClick={() => remove.mutate(exception.id)}
                    className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
                  >
                    Remove
                  </button>
                )}
              </div>
            </div>
          ))}
        </Card>
      )}
      {error !== null && <ErrorLine message={error} />}
    </section>
  )
}

function ErrorLine({ message }: { message: string }) {
  return (
    <div className="rounded-md border border-status-critical-border bg-status-critical-surface px-2 py-1.5 text-xs text-status-critical-text">
      {message}
    </div>
  )
}
