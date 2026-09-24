import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { EMPTY, type StatusName } from '@/lib/ui'
import type { CapacityReportRow, CapacityReportView, TimeToFullView } from '@/api/types'

/**
 * M5.3: the capacity report.
 *
 * Same pattern as M5.1's alert report: no PDF library, this page *is* the
 * PDF via a print stylesheet, and CSV is generated server-side from the same
 * ReadModel query so the two exports can never disagree. See
 * routes/reports/AlertsReport.tsx.
 *
 * No filters -- every live datastore is on this report, same as the
 * datastore count an operator would want before it fills unexpectedly. The
 * sort itself is the triage: soonest fill date first.
 */
export function CapacityReport() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['reports', 'capacity'],
    queryFn: api.capacityReport,
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
        <h1 className="text-xl font-semibold">Capacity report</h1>
        <div className="flex gap-2">
          <a
            href={api.capacityReportCsvUrl()}
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

function ReportBody({ data }: { data: CapacityReportView }) {
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
        <div className="text-sm text-muted-foreground">Capacity report</div>
        <div className="mt-1 text-xs text-muted-foreground">
          Generated {new Date(data.generatedAtUtc).toISOString()} — {summary.totalDatastores} datastore
          {summary.totalDatastores === 1 ? '' : 's'}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2 [break-inside:avoid] sm:grid-cols-4">
        <Metric label="Total capacity" value={formatBytes(summary.totalCapacityBytes)} />
        <Metric label="Total used" value={formatBytes(summary.totalUsedBytes)} />
        <Metric label="Total free" value={formatBytes(summary.totalFreeBytes)} />
        <Metric label="Over-committed" value={summary.overcommittedCount} status={summary.overcommittedCount > 0 ? 'Warning' : undefined} />
        <Metric
          label="Filling within 7 days"
          value={summary.fillingWithin7Days}
          status={summary.fillingWithin7Days > 0 ? 'Critical' : undefined}
        />
        <Metric
          label="Filling within 30 days"
          value={summary.fillingWithin30Days}
          status={summary.fillingWithin30Days > 0 ? 'Warning' : undefined}
        />
        <Metric label="No estimate yet" value={summary.noEstimateCount} />
      </div>

      {summary.noEstimateCount > 0 && (
        <Card className="p-3 text-xs text-muted-foreground [break-inside:avoid]">
          <span className="font-medium text-foreground">Why {summary.noEstimateCount} of them have no estimate: </span>
          {Object.entries(summary.noEstimateByReason)
            .map(([reason, count]) => `${reason} (${count})`)
            .join(', ')}
          . Expected on an estate whose capacity history has only just started being recorded.
        </Card>
      )}

      {data.rows.length === 0 ? (
        <Empty>No live datastore was found.</Empty>
      ) : (
        <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2">Datastore</th>
                <th className="px-3 py-2">Type · source</th>
                <th className="px-3 py-2">Capacity</th>
                <th className="px-3 py-2">Used</th>
                <th className="px-3 py-2">Free</th>
                <th className="px-3 py-2">% used</th>
                <th className="px-3 py-2">Provisioned</th>
                <th className="px-3 py-2">Over-commit</th>
                <th className="px-3 py-2">Time to full</th>
              </tr>
            </thead>
            <tbody>
              {data.rows.map((row, index) => (
                <ReportRow key={index} row={row} />
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {/*
        The footer every printed page needs: what this was read from, and how
        fresh it can possibly be. Without it a reader cannot tell a five-minute
        reading from a stale one.
      */}
      <div className="border-t border-border pt-2 text-xs text-muted-foreground print:fixed print:bottom-0">
        Capacity figures are inventory-rhythm readings, taken roughly every 5 minutes. The
        fill-date estimate is fitted to up to 30 days of hourly history; see the reason column
        when one is not yet available.
      </div>
    </div>
  )
}

function ReportRow({ row }: { row: CapacityReportRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top font-medium">{row.name}</td>
      <td className="px-3 py-2 align-top text-muted-foreground">
        {row.datastoreType ?? <span className="text-muted-foreground">{EMPTY}</span>} · {row.source}
      </td>
      <td className="px-3 py-2 align-top tabular">{formatBytes(row.capacityBytes)}</td>
      <td className="px-3 py-2 align-top tabular">{formatBytes(row.usedBytes)}</td>
      <td className="px-3 py-2 align-top tabular">{formatBytes(row.freeBytes)}</td>
      <td className="px-3 py-2 align-top tabular">
        {row.percentUsed !== null ? `${row.percentUsed.toFixed(1)}%` : EMPTY}
      </td>
      <td className="px-3 py-2 align-top tabular">{formatBytes(row.provisionedBytes)}</td>
      <td className="px-3 py-2 align-top tabular">
        {row.overcommitRatio !== null ? (
          <StatusBadge status={row.overcommitRatio > 1 ? 'Warning' : 'Healthy'}>
            {row.overcommitRatio.toFixed(2)}×
          </StatusBadge>
        ) : (
          EMPTY
        )}
      </td>
      <td className="px-3 py-2 align-top">
        <TimeToFullCell estimate={row.timeToFull} />
      </td>
    </tr>
  )
}

/**
 * The same answer the datastore's own page gives: a date with its window, or
 * a refusal shown as an answer. Never blank -- "not computed" and "not
 * filling" must not look the same. See EntityDetail.tsx's TimeToFull, which
 * this is the compact table-cell form of.
 */
function TimeToFullCell({ estimate }: { estimate: TimeToFullView }) {
  if (!estimate.isForecast) {
    return (
      <div className="text-xs text-muted-foreground">
        <span className="font-medium text-foreground">Cannot estimate:</span> {refusal(estimate.summary)}
        {estimate.reason && (
          <div className="mt-0.5">
            <Identifier>{estimate.reason}</Identifier>
          </div>
        )}
      </div>
    )
  }

  const days = estimate.days ?? 0
  const status = fillStatus(days)
  const date = estimate.fullAtUtc?.slice(0, 10) ?? EMPTY

  return (
    <div className="text-xs">
      <StatusBadge status={status}>
        {days.toFixed(days < 10 ? 1 : 0)} days (on {date})
      </StatusBadge>
      {estimate.growthBytesPerDay !== null && (
        <div className="mt-1 text-muted-foreground">
          {formatBytes(estimate.growthBytesPerDay)}/day
        </div>
      )}
    </div>
  )
}

/** This product's own thresholds -- see DatastoreTimeToFullPolicy. */
function fillStatus(days: number): StatusName {
  if (days <= 7) return 'Critical'
  if (days <= 30) return 'Warning'
  return 'Healthy'
}

/** The server's sentence without its "Cannot estimate a fill date:" lead-in. */
function refusal(summary: string): string {
  const lead = 'Cannot estimate a fill date: '
  return summary.startsWith(lead) ? summary.slice(lead.length) : summary
}

/** Bytes as GB or TB, one decimal -- the unit a spreadsheet reader already thinks in. */
function formatBytes(bytes: number | null): string {
  if (bytes === null) return EMPTY

  const gb = bytes / 1024 ** 3
  return gb >= 1024 ? `${(gb / 1024).toFixed(2)} TB` : `${gb.toFixed(1)} GB`
}
