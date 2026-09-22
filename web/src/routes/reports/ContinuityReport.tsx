import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import type { ContinuityReportRow, ContinuityReportView, ContinuityStateCounts } from '@/api/types'

/**
 * M8.10: the continuity report.
 *
 * Same pattern as M5.1's alert report and M5.3's capacity report: no PDF
 * library, this page *is* the PDF via a print stylesheet, and CSV is
 * generated server-side from the same ReadModel query so the two exports can
 * never disagree. See routes/reports/CapacityReport.tsx.
 *
 * One row per cluster: HA (M8.1), DRS (M8.3) and N+1 (M8.2) findings on the
 * cluster itself, and the multipath findings (M8.6) of the hosts under it --
 * read from the eo-continuity compliance findings (ADR-0024), counted by state:
 * failing, accepted (owned, with a reason), excepted (waived until a date),
 * not evaluated (with a reason), and stale (the vCenter did not answer).
 *
 * The one thing this page must not do is say "all clear" where the input was
 * never read: HA and DRS depend on cluster HA configuration that has not
 * been read yet in some deployments -- not collected by this version, not
 * yet read since startup, or not permitted for the service account -- and a
 * row of zeros would otherwise look identical to a cluster that was actually
 * checked and found healthy. See summary.note, which is set only when that
 * gap is real.
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

  return (
    <div className="space-y-4">
      {/*
        The printed header. ADR-0007 §6: a printed page cannot be refreshed to
        check whether it is stale, so it has to say, permanently, when this
        was true.
      */}
      <div className="border-b border-border pb-3">
        <div className="text-lg font-semibold">Enterprise Observatory</div>
        <div className="text-sm text-muted-foreground">Continuity report</div>
        <div className="mt-1 text-xs text-muted-foreground">
          Generated {new Date(data.generatedAtUtc).toISOString()} — {summary.totalClusters} cluster
          {summary.totalClusters === 1 ? '' : 's'}
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

      {summary.clustersWithFailingCount > 0 && (
        <Card className="p-3 text-xs text-muted-foreground [break-inside:avoid]">
          <span className="font-medium text-foreground">
            {summary.clustersWithFailingCount} cluster{summary.clustersWithFailingCount === 1 ? '' : 's'} with a
            failing finding:{' '}
          </span>
          {summary.clustersWithFailingNames.join(', ')}
        </Card>
      )}

      {data.rows.length === 0 ? (
        <Empty>No live cluster was found.</Empty>
      ) : (
        <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2">Cluster</th>
                <th className="px-3 py-2">HA</th>
                <th className="px-3 py-2">DRS</th>
                <th className="px-3 py-2">Storage path</th>
                <th className="px-3 py-2">N+1</th>
              </tr>
            </thead>
            <tbody>
              {data.rows.map((row) => (
                <ReportRow key={row.clusterId} row={row} />
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {/*
        The footer every printed page needs: what this was read from, and how
        fresh it can possibly be.
      */}
      <div className="border-t border-border pt-2 text-xs text-muted-foreground print:fixed print:bottom-0">
        Counts are the continuity findings for that cluster or its hosts, by state. Accept or except a
        finding on the Compliance screen. "Not collected" on HA and DRS means the cluster configuration
        has never been read, not that it passed.
      </div>
    </div>
  )
}

function ReportRow({ row }: { row: ContinuityReportRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top font-medium">
        {row.clusterName}
        <div className="text-xs font-normal text-muted-foreground">{row.source}</div>
      </td>
      <td className="px-3 py-2 align-top">
        {row.haSettingsCollected ? (
          <CountCell counts={row.ha} />
        ) : (
          <span className="text-xs text-muted-foreground">Not collected</span>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        {row.haSettingsCollected ? (
          <CountCell counts={row.drs} />
        ) : (
          <span className="text-xs text-muted-foreground">Not collected</span>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell counts={row.storagePath} />
        {row.storagePathAffectedHosts.length > 0 && (
          <div className="mt-1 text-xs text-muted-foreground">{row.storagePathAffectedHosts.join(', ')}</div>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell counts={row.nPlusOne} />
      </td>
    </tr>
  )
}

/**
 * The non-passing states side by side. Nothing at all -- no finding in any
 * state -- is shown as a dash, not a zero: it was not looked at. Only passing
 * findings are "0", which is then a checked result.
 */
function CountCell({ counts }: { counts: ContinuityStateCounts }) {
  const any =
    counts.failing + counts.accepted + counts.excepted + counts.notEvaluated + counts.passing > 0

  if (!any) {
    return <span className="text-xs text-muted-foreground">—</span>
  }

  const open = counts.failing + counts.accepted + counts.excepted + counts.notEvaluated

  if (open === 0) {
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
      {counts.stale > 0 && <StatusBadge status="Unknown">{counts.stale} stale</StatusBadge>}
    </div>
  )
}