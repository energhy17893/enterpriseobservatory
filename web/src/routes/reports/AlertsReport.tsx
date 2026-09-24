import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { EMPTY, cn, severityStatus } from '@/lib/ui'
import type { AlertLifecycleState, AlertReportRow, AlertSeverity } from '@/api/types'

const SEVERITIES: AlertSeverity[] = ['Critical', 'Warning', 'Info']
const STATES: AlertLifecycleState[] = ['Open', 'Acknowledged', 'Silenced', 'Resolved']

/**
 * M5.1: the alert/finding report.
 *
 * PDF is deliberately not generated on the server -- see the roadmap's
 * decision against a PDF library, whose licences carry a per-copy cost that
 * is a real risk for a product sold rather than run as a service. This page
 * *is* the PDF: a print stylesheet turns it into one page per section with
 * no navigation and no buttons, and "Print / Save as PDF" just calls the
 * browser's own print dialog. CSV is generated server-side by the same
 * ReadModel query, through Api/Reports/CsvWriter.cs, so the two exports can
 * never disagree about what the report contains.
 *
 * The pattern M5.2 and M5.3 are meant to copy: filters that are print:hidden,
 * a summary and a header that print, one table, one CSV link built from the
 * same query the JSON view used.
 */
export function AlertsReport() {
  const [params, setParams] = useSearchParams()

  const severity = params.get('severity') ?? ''
  const state = params.get('state') ?? ''
  const category = params.get('category') ?? ''
  const source = params.get('source') ?? ''
  const from = params.get('from') ?? ''
  const to = params.get('to') ?? ''

  const query = {
    severity: severity || undefined,
    state: state || undefined,
    category: category || undefined,
    source: source || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    // Inclusive of the whole day picked, not just its first instant.
    to: to ? new Date(`${to}T23:59:59.999`).toISOString() : undefined,
  }

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['reports', 'alerts', severity, state, category, source, from, to],
    queryFn: () => api.alertsReport(query),
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    setParams(next, { replace: true })
  }

  const filterSummary = describeFilters({ severity, state, category, source, from, to })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
        <h1 className="text-xl font-semibold">Alert / finding report</h1>
        <div className="flex gap-2">
          <a
            href={api.alertsReportCsvUrl(query)}
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

      <Card className="flex flex-wrap items-end gap-2 p-3 print:hidden">
        <Filter label="Severity">
          <select
            value={severity}
            onChange={(event) => setParam('severity', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          >
            <option value="">All</option>
            {SEVERITIES.map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </select>
        </Filter>
        <Filter label="State">
          <select
            value={state}
            onChange={(event) => setParam('state', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          >
            <option value="">All</option>
            {STATES.map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </select>
        </Filter>
        <Filter label="Category">
          <input
            value={category}
            onChange={(event) => setParam('category', event.target.value)}
            className="w-32 rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <Filter label="Source">
          <input
            value={source}
            onChange={(event) => setParam('source', event.target.value)}
            className="w-32 rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <Filter label="From">
          <input
            type="date"
            value={from}
            onChange={(event) => setParam('from', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <Filter label="To">
          <input
            type="date"
            value={to}
            onChange={(event) => setParam('to', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <span className="pb-1 text-xs text-muted-foreground">
          Applies to alerts resolved in this window. Open alerts are always included.
        </span>
      </Card>

      {isError ? (
        <LoadFailure what="The report" error={error} />
      ) : isPending ? (
        <Loading what="the report" />
      ) : (
        <ReportBody data={data} filterSummary={filterSummary} />
      )}
    </div>
  )
}

function Filter({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function describeFilters(filters: {
  severity: string
  state: string
  category: string
  source: string
  from: string
  to: string
}): string {
  const parts = [
    filters.severity && `severity ${filters.severity}`,
    filters.state && `state ${filters.state}`,
    filters.category && `category "${filters.category}"`,
    filters.source && `source "${filters.source}"`,
  ].filter(Boolean)

  const range = filters.from || filters.to
    ? `resolved ${filters.from || 'the start'} to ${filters.to || 'now'}`
    : 'resolved in the last 7 days'

  return parts.length > 0 ? `${parts.join(', ')} — ${range}` : `All open and ${range}`
}

function ReportBody({
  data,
  filterSummary,
}: {
  data: Awaited<ReturnType<typeof api.alertsReport>>
  filterSummary: string
}) {
  return (
    <div className="space-y-4">
      {/*
        The printed header. ADR-0007 §6 says stale data must never be
        presented as fresh -- on paper that means saying, permanently, when
        this was true and under what filter, because a printed page cannot be
        refreshed to check.
      */}
      <div className="border-b border-border pb-3">
        <div className="text-lg font-semibold">Enterprise Observatory</div>
        <div className="text-sm text-muted-foreground">Alert / finding report</div>
        <div className="mt-1 text-xs text-muted-foreground">
          Generated {new Date(data.generatedAtUtc).toISOString()} — {filterSummary}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2 [break-inside:avoid] sm:grid-cols-4">
        <Metric label="Total" value={data.summary.total} />
        <Metric label="Critical" value={data.summary.bySeverity.Critical ?? 0} status="Critical" />
        <Metric label="Warning" value={data.summary.bySeverity.Warning ?? 0} status="Warning" />
        <Metric label="Open" value={data.summary.byState.Open ?? 0} />
      </div>

      {data.rows.length === 0 ? (
        <Empty>Nothing matches this report's filter.</Empty>
      ) : (
        <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2">Severity</th>
                <th className="px-3 py-2">Title</th>
                <th className="px-3 py-2">Entity</th>
                <th className="px-3 py-2">Category · source</th>
                <th className="px-3 py-2">State</th>
                <th className="px-3 py-2">First seen</th>
                <th className="px-3 py-2">Last seen</th>
                <th className="px-3 py-2">Acknowledged</th>
                <th className="px-3 py-2">Cleared</th>
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
    </div>
  )
}

function ReportRow({ row }: { row: AlertReportRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top">
        <StatusBadge status={severityStatus(row.severity)}>{row.severity}</StatusBadge>
      </td>
      <td className="px-3 py-2 align-top">
        <div className="font-medium">{row.title}</div>
        {row.isDerived && (
          <span className="text-xs text-muted-foreground">Derived</span>
        )}
      </td>
      <td className="px-3 py-2 align-top">
        {row.entityName !== null ? (
          <>
            {row.entityName}
            {row.entityKind !== null && (
              <Identifier> · {row.entityKind}</Identifier>
            )}
          </>
        ) : (
          <span className="text-muted-foreground">{EMPTY}</span>
        )}
      </td>
      <td className="px-3 py-2 align-top text-muted-foreground">
        {row.category} · {row.source}
      </td>
      <td className="px-3 py-2 align-top">{row.state}</td>
      <td className={cn('px-3 py-2 align-top tabular text-xs')}>{formatUtc(row.firstSeenUtc)}</td>
      <td className="px-3 py-2 align-top tabular text-xs">{formatUtc(row.lastSeenUtc)}</td>
      <td className="px-3 py-2 align-top text-xs">
        {row.acknowledgedBy !== null
          ? <>{row.acknowledgedBy}<br />{formatUtc(row.acknowledgedAtUtc)}</>
          : <span className="text-muted-foreground">{EMPTY}</span>}
      </td>
      <td className="px-3 py-2 align-top text-xs">
        {row.clearedBy !== null
          ? <>{row.clearedBy}<br />{formatUtc(row.clearedAtUtc)}</>
          : <span className="text-muted-foreground">{EMPTY}</span>}
      </td>
    </tr>
  )
}

/** ISO-8601 UTC, the same wire format the CSV export uses -- unambiguous on a printed page. */
function formatUtc(iso: string | null): string {
  return iso === null ? EMPTY : new Date(iso).toISOString().replace('.000Z', 'Z')
}
