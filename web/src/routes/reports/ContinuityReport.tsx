import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { EMPTY } from '@/lib/ui'
import { basisLabel } from '@/lib/basis'
import type {
  ContinuityControlInfo,
  ContinuityControlRow,
  ContinuityReportRow,
  ContinuityReportView,
  ContinuityStateCounts,
  ContinuityVCenterSection,
} from '@/api/types'

/**
 * M8.10 / K3: the continuity report.
 *
 * Same pattern as M5.1's alert report and M5.3's capacity report: no PDF
 * library, this page *is* the PDF via a print stylesheet, and CSV is
 * generated server-side from the same ReadModel query so the two exports can
 * never disagree. See routes/reports/CapacityReport.tsx.
 *
 * The page reads the catalogue, it does not know controls: every eo-continuity
 * control is placed by the entity kind its check applies to (data.controls).
 *   - vCenter: controls on the vCenter itself (its certificate) and the alarms
 *     vCenter raised on itself;
 *   - one row per cluster: each cluster-level control, and what is under the
 *     cluster (hosts, VMs, datastores) rolled up;
 *   - one summary row per host/VM/datastore control -- never one row per
 *     entity: counts, at most ten failing names, "+N", and a link to the
 *     control on the Compliance screen. The report is for an auditor.
 *
 * Counts are by state: failing, accepted (owned, with a reason), excepted
 * (waived until a date), not evaluated (with a reason), and stale (the vCenter
 * did not answer). The one thing this page must not do is say "all clear"
 * where the input was never read -- see summary.note.
 */
export function ContinuityReport() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['reports', 'continuity'],
    queryFn: api.continuityReport,
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
        <h1 className="text-xl font-semibold">Continuity report</h1>
        <div className="flex gap-2">
          <a
            href={api.continuityReportCsvUrl()}
            className="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-card"
          >
            Download CSV
          </a>
          <button
            type="button"
            onClick={() => window.print()}
            className="rounded-md bg-primary px-3 py-1.5 text-sm text-primary-on"
          >
            Print / Save as PDF
          </button>
        </div>
      </div>

      {isError ? (
        <LoadFailure what="The report" error={error} />
      ) : isPending ? (
        <Loading what="the report" />
      ) : (
        <ReportBody data={data} />
      )}
    </div>
  )
}

function ReportBody({ data }: { data: ContinuityReportView }) {
  const { summary } = data
  const totals = summary.totals
  const byId = new Map(data.controls.map((c) => [c.controlId, c]))
  const clusterControls = data.controls.filter((c) => c.scope === 'Cluster')

  return (
    <div className="space-y-4">
      {/*
        The printed header. ADR-0007 §6: a printed page cannot be refreshed to
        check whether it is stale, so it has to say, permanently, when this
        was true.
      */}
      <div className="border-b border-border pb-3">
        <div className="text-lg font-semibold">Enterprise Observatory</div>
        <div className="text-sm text-muted-foreground">Continuity report — eo-continuity catalogue</div>
        <div className="mt-1 text-xs text-muted-foreground">
          Generated {new Date(data.generatedAtUtc).toISOString()} — {summary.totalClusters} cluster
          {summary.totalClusters === 1 ? '' : 's'}, {data.controls.length} controls
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2 [break-inside:avoid] sm:grid-cols-4">
        <Metric label="Failing" value={totals.failing} status={totals.failing > 0 ? 'Warning' : undefined} />
        <Metric label="Accepted" value={totals.accepted} />
        <Metric label="Excepted" value={totals.excepted} />
        <Metric label="Not evaluated" value={totals.notEvaluated} status={totals.notEvaluated > 0 ? 'Unknown' : undefined} />
        <Metric label="Passing" value={totals.passing} />
        <Metric label="Stale" value={totals.stale} status={totals.stale > 0 ? 'Unknown' : undefined} />
        <Metric
          label="Clusters with a failing finding"
          value={summary.clustersWithFailingCount}
          status={summary.clustersWithFailingCount > 0 ? 'Warning' : undefined}
        />
      </div>

      {summary.note && (
        <Card className="border-amber-500/50 p-3 text-xs [break-inside:avoid]">
          <span className="font-medium text-foreground">Not all clear: </span>
          <span className="text-muted-foreground">{summary.note}</span>
        </Card>
      )}

      {data.vCenters.map((vc) => (
        <VCenterCard key={vc.vCenterId} section={vc} byId={byId} />
      ))}

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Clusters</h2>
        {data.rows.length === 0 ? (
          <Empty>No live cluster was found.</Empty>
        ) : (
          <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-border text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Cluster</th>
                  <th className="px-3 py-2">Cluster controls not passing</th>
                  <th className="px-3 py-2">Hosts, VMs and datastores under it</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((row) => (
                  <ClusterRow key={row.clusterId} row={row} byId={byId} />
                ))}
              </tbody>
            </table>
          </Card>
        )}
        {clusterControls.length > 0 && (
          <div className="text-xs text-muted-foreground">
            Cluster controls: {clusterControls.map((c) => c.controlId).join(', ')}.
          </div>
        )}
      </section>

      <section className="space-y-2">
        <h2 className="text-sm font-medium">Hosts, virtual machines and datastores, by control</h2>
        {data.controlRows.length === 0 ? (
          <Empty>The catalogue has no host, VM or datastore control.</Empty>
        ) : (
          <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-border text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Control</th>
                  <th className="px-3 py-2">Findings</th>
                  <th className="px-3 py-2">Failing</th>
                </tr>
              </thead>
              <tbody>
                {data.controlRows.map((row) => (
                  <ControlSummaryRow key={row.controlId} row={row} />
                ))}
              </tbody>
            </table>
          </Card>
        )}
      </section>

      {/*
        The footer every printed page needs: what this was read from, and how
        fresh it can possibly be.
      */}
      <div className="border-t border-border pt-2 text-xs text-muted-foreground print:fixed print:bottom-0">
        Counts are the eo-continuity findings, by state. "basis:" is what a control's expectation rests
        on. Accept or except a finding on the Compliance screen. "HA not collected" means the cluster
        configuration has never been read, not that it passed.
      </div>
    </div>
  )
}

function ControlTitle({ info, controlId }: { info: Pick<ContinuityControlInfo, 'title' | 'citation'> | undefined; controlId: string }) {
  return (
    <div>
      <Link to={`/compliance#${encodeURIComponent(controlId)}`} className="font-mono text-xs hover:underline">
        {controlId}
      </Link>
      {info && <span className="ml-2">{info.title}</span>}
      <div className="text-xs text-muted-foreground">basis: {basisLabel(info?.citation)}</div>
    </div>
  )
}

function VCenterCard({
  section,
  byId,
}: {
  section: ContinuityVCenterSection
  byId: Map<string, ContinuityControlInfo>
}) {
  return (
    <section className="space-y-2 [break-inside:avoid]">
      <h2 className="text-sm font-medium">
        vCenter <span className="font-normal text-muted-foreground">{section.vCenterName} · {section.source}</span>
      </h2>
      <Card className="divide-y divide-border p-0">
        {section.controls.map((c) => {
          const finding = section.findings.find((f) => f.controlId === c.controlId)
          return (
            <div key={c.controlId} className="flex flex-wrap items-start justify-between gap-3 p-3 text-sm">
              <div className="min-w-0">
                <ControlTitle info={byId.get(c.controlId)} controlId={c.controlId} />
                {finding?.observed && (
                  <div className="mt-0.5 text-xs text-muted-foreground">observed: {finding.observed}</div>
                )}
              </div>
              <CountCell counts={c.counts} />
            </div>
          )
        })}
        <div className="p-3 text-sm">
          <div className="text-xs text-muted-foreground">Alarms raised on the vCenter itself</div>
          {section.alarms.length === 0 ? (
            <div className="mt-1 text-xs text-muted-foreground">None open.</div>
          ) : (
            <ul className="mt-1 space-y-1">
              {section.alarms.map((a, i) => (
                <li key={`${a.title}-${i}`} className="flex flex-wrap items-center gap-2">
                  <StatusBadge status={a.severity === 'Critical' ? 'Critical' : a.severity === 'Warning' ? 'Warning' : 'Info'}>
                    {a.severity}
                  </StatusBadge>
                  <span>{a.title}</span>
                  {a.state !== 'Open' && <span className="text-xs text-muted-foreground">{a.state}</span>}
                  {a.isStale && <StatusBadge status="Unknown">stale</StatusBadge>}
                </li>
              ))}
            </ul>
          )}
        </div>
      </Card>
    </section>
  )
}

function open(counts: ContinuityStateCounts): number {
  return counts.failing + counts.accepted + counts.excepted + counts.notEvaluated
}

function ClusterRow({ row, byId }: { row: ContinuityReportRow; byId: Map<string, ContinuityControlInfo> }) {
  const notPassing = row.controls.filter((c) => open(c.counts) > 0)
  const checked = row.controls.filter((c) => open(c.counts) + c.counts.passing > 0).length

  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top font-medium">
        {row.clusterName}
        <div className="text-xs font-normal text-muted-foreground">{row.source}</div>
        {!row.haSettingsCollected && (
          <div className="text-xs font-normal text-muted-foreground">HA not collected</div>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        {notPassing.length === 0 ? (
          <span className="text-xs text-muted-foreground">
            {checked === 0 ? EMPTY : `none (${checked} of ${row.controls.length} controls checked)`}
          </span>
        ) : (
          <ul className="space-y-1">
            {notPassing.map((c) => (
              <li key={c.controlId} className="flex flex-wrap items-center gap-2">
                <Link to={`/compliance#${encodeURIComponent(c.controlId)}`} className="text-xs hover:underline">
                  {byId.get(c.controlId)?.title ?? c.controlId}
                </Link>
                <CountCell counts={c.counts} />
              </li>
            ))}
          </ul>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell counts={row.contained} />
        {row.containedAffectedNames.length > 0 && (
          <div className="mt-1 text-xs text-muted-foreground">
            <Names names={row.containedAffectedNames} more={row.containedAffectedMore} />
          </div>
        )}
      </td>
    </tr>
  )
}

function ControlSummaryRow({ row }: { row: ContinuityControlRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top">
        <ControlTitle info={row} controlId={row.controlId} />
        <div className="text-xs text-muted-foreground">applies to: {row.appliesTo}</div>
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell counts={row.counts} />
      </td>
      <td className="px-3 py-2 align-top text-xs">
        {row.failingNames.length === 0 ? (
          <span className="text-muted-foreground">{EMPTY}</span>
        ) : (
          <Names names={row.failingNames} more={row.moreFailing} />
        )}
      </td>
    </tr>
  )
}

/** At most ten names, then "+N" -- the server already cut the list. */
function Names({ names, more }: { names: string[]; more: number }) {
  return (
    <>
      {names.join(', ')}
      {more > 0 && <span className="text-muted-foreground">, +{more}</span>}
    </>
  )
}

/**
 * The non-passing states side by side. Nothing at all -- no finding in any
 * state -- is shown as a dash, not a zero: it was not looked at. Only passing
 * findings are "0", which is then a checked result.
 */
function CountCell({ counts }: { counts: ContinuityStateCounts }) {
  const any = open(counts) + counts.passing > 0

  if (!any) {
    return <span className="text-xs text-muted-foreground">{EMPTY}</span>
  }

  if (open(counts) === 0) {
    return <span className="tabular text-muted-foreground">0 ({counts.passing} passing)</span>
  }

  return (
    <div className="flex flex-wrap gap-1">
      {counts.failing > 0 && <StatusBadge status="Warning">{counts.failing} failing</StatusBadge>}
      {counts.accepted > 0 && <StatusBadge status="Info">{counts.accepted} accepted</StatusBadge>}
      {counts.excepted > 0 && <StatusBadge status="Info">{counts.excepted} excepted</StatusBadge>}
      {counts.notEvaluated > 0 && (
        <StatusBadge status="Unknown">{counts.notEvaluated} not evaluated</StatusBadge>
      )}
      {counts.passing > 0 && <span className="text-xs text-muted-foreground">{counts.passing} passing</span>}
      {counts.stale > 0 && <StatusBadge status="Unknown">{counts.stale} stale</StatusBadge>}
    </div>
  )
}
