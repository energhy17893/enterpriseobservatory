import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import type { ContinuityReportRow, ContinuityReportView } from '@/api/types'

/**
 * M8.10: the continuity report.
 *
 * Same pattern as M5.1's alert report and M5.3's capacity report: no PDF
 * library, this page *is* the PDF via a print stylesheet, and CSV is
 * generated server-side from the same ReadModel query so the two exports can
 * never disagree. See routes/reports/CapacityReport.tsx.
 *
 * One row per cluster: HA (M8.1) and DRS (M8.3) findings on the cluster
 * itself, storage-path redundancy findings rolled up from the hosts under
 * it, and a placeholder pair of columns for the N+1 rule a parallel change
 * is still building -- present here from day one so this report needs no
 * change the day that rule ships.
 *
 * The one thing this page must not do is say "all clear" where the input was
 * never read: HA and DRS depend on cluster inventory settings this estate's
 * collector has not wired up yet in some deployments, and a row of zeros
 * would otherwise look identical to a cluster that was actually checked and
 * found healthy. See summary.note, which is set only when that gap is real.
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
        <Metric label="HA findings" value={summary.byRule['cluster-ha-scorecard'] ?? 0} />
        <Metric label="DRS findings" value={summary.byRule['drs-rule-violation'] ?? 0} />
        <Metric
          label="Storage path findings"
          value={
            (summary.byRule['multipath-single-point-of-failure'] ?? 0) +
            (summary.byRule['storage-path-redundancy'] ?? 0)
          }
        />
        <Metric label="N+1 findings" value={summary.byRule['cluster-n-plus-one'] ?? 0} />
        <Metric
          label="Clusters with a critical finding"
          value={summary.clustersWithCriticalCount}
          status={summary.clustersWithCriticalCount > 0 ? 'Critical' : undefined}
        />
        <Metric label="Critical" value={summary.bySeverity.Critical ?? 0} status={(summary.bySeverity.Critical ?? 0) > 0 ? 'Critical' : undefined} />
        <Metric label="Warning" value={summary.bySeverity.Warning ?? 0} />
      </div>

      {!summary.haInputsCollected && summary.note && (
        <Card className="border-amber-500/50 p-3 text-xs [break-inside:avoid]">
          <span className="font-medium text-foreground">HA / DRS not collected yet: </span>
          <span className="text-muted-foreground">{summary.note}</span>
        </Card>
      )}

      {summary.clustersWithCriticalCount > 0 && (
        <Card className="p-3 text-xs text-muted-foreground [break-inside:avoid]">
          <span className="font-medium text-foreground">
            {summary.clustersWithCriticalCount} cluster{summary.clustersWithCriticalCount === 1 ? '' : 's'} with a
            critical finding:{' '}
          </span>
          {summary.clustersWithCriticalNames.join(', ')}
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
        Counts are the open findings each rule currently carries for that cluster or its hosts. "Not
        collected" on the HA column means the setting has never been read, not that it passed.
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
          <CountCell critical={row.haCriticalCount} warning={row.haWarningCount} />
        ) : (
          <span className="text-xs text-muted-foreground">Not collected</span>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        {row.haSettingsCollected ? (
          <CountCell critical={row.drsCriticalCount} warning={row.drsWarningCount} />
        ) : (
          <span className="text-xs text-muted-foreground">Not collected</span>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell critical={row.storagePathCriticalCount} warning={row.storagePathWarningCount} />
        {row.storagePathAffectedHosts.length > 0 && (
          <div className="mt-1 text-xs text-muted-foreground">{row.storagePathAffectedHosts.join(', ')}</div>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <CountCell critical={row.nPlusOneCriticalCount} warning={row.nPlusOneWarningCount} />
      </td>
    </tr>
  )
}

/** Critical and warning counts, side by side -- zero is shown plainly, never dressed up as "all clear". */
function CountCell({ critical, warning }: { critical: number; warning: number }) {
  if (critical === 0 && warning === 0) {
    return <span className="tabular text-muted-foreground">0</span>
  }

  return (
    <div className="flex flex-wrap gap-1">
      {critical > 0 && <StatusBadge status="Critical">{critical} critical</StatusBadge>}
      {warning > 0 && <StatusBadge status="Warning">{warning} warning</StatusBadge>}
    </div>
  )
}
