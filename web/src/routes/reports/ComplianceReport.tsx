import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { cn, type StatusName } from '@/lib/ui'
import type {
  ComplianceControlView,
  ComplianceExceptionView,
  ComplianceReportFindingRow,
  ComplianceReportTransitionRow,
  ComplianceReportView,
  FindingState,
} from '@/api/types'

const STATE_STATUS: Record<FindingState, StatusName> = {
  Failing: 'Warning',
  Accepted: 'Info',
  Excepted: 'Info',
  Passing: 'Healthy',
  NotEvaluated: 'Unknown',
}

/**
 * M5.2: the compliance report, in the format an auditor asks for.
 *
 * Copies the shape M5.1 set: filters that are print:hidden, a header and
 * summary that print, tables for the sections an auditor reads in order --
 * per-control summary, per-finding detail (the acceptance or exception that
 * covers each one, in full), removed exceptions (audit evidence in their own
 * right), change history over the chosen period, and the controls this
 * product never evaluated at all, each with its reason. No PDF library, for
 * the same licensing reason as the alert report: this page *is* the PDF,
 * through the browser's own print dialog.
 */
export function ComplianceReport() {
  const [params, setParams] = useSearchParams()

  const control = params.get('control') ?? ''
  const entity = params.get('entity') ?? ''
  const from = params.get('from') ?? ''
  const to = params.get('to') ?? ''

  const query = {
    control: control || undefined,
    entity: entity || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    to: to ? new Date(`${to}T23:59:59.999`).toISOString() : undefined,
  }

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['reports', 'compliance', control, entity, from, to],
    queryFn: () => api.complianceReport(query),
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value === '') next.delete(key)
    else next.set(key, value)
    setParams(next, { replace: true })
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
        <h1 className="text-xl font-semibold">Compliance report</h1>
        <div className="flex gap-2">
          <a
            href={api.complianceReportCsvUrl(query, 'findings')}
            className="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-card"
          >
            Download findings CSV
          </a>
          <a
            href={api.complianceReportCsvUrl(query, 'history')}
            className="rounded-md border border-border px-3 py-1.5 text-sm hover:bg-card"
          >
            Download history CSV
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
        <Filter label="Control">
          <input
            value={control}
            onChange={(event) => setParam('control', event.target.value)}
            className="w-40 rounded-md border border-border bg-page px-2 py-1 text-sm"
            placeholder="All controls"
          />
        </Filter>
        <Filter label="Entity">
          <input
            value={entity}
            onChange={(event) => setParam('entity', event.target.value)}
            className="w-40 rounded-md border border-border bg-page px-2 py-1 text-sm"
            placeholder="All hosts"
          />
        </Filter>
        <Filter label="History from">
          <input
            type="date"
            value={from}
            onChange={(event) => setParam('from', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <Filter label="History to">
          <input
            type="date"
            value={to}
            onChange={(event) => setParam('to', event.target.value)}
            className="rounded-md border border-border bg-page px-2 py-1 text-sm"
          />
        </Filter>
        <span className="pb-1 text-xs text-muted-foreground">
          The scope and history filters apply to every section below. Default history window is 30 days.
        </span>
      </Card>

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

function Filter({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function ReportBody({ data }: { data: ComplianceReportView }) {
  const notEvaluated = data.controls.filter((c) => !c.evaluated)
  const evaluated = data.controls.filter((c) => c.evaluated)

  return (
    <div className="space-y-6">
      {/*
        The printed header. An auditor reading this on paper must be told,
        permanently, when it was true, against which catalogue edition, over
        what scope, and whether any of it is stale -- a printed page cannot be
        refreshed to check. See ADR-0007 §6.
      */}
      <div className="border-b border-border pb-3">
        <div className="text-lg font-semibold">Enterprise Observatory</div>
        <div className="text-sm text-muted-foreground">Compliance report</div>
        <div className="mt-1 text-xs text-muted-foreground">
          Catalogue {data.catalogueName || '—'} · release {data.catalogueRelease || '—'} — generated{' '}
          {new Date(data.generatedAtUtc).toISOString()} — scope: {data.scope}
        </div>
        <div className="mt-1 text-xs text-muted-foreground">
          {data.lastEvaluatedUtc === null
            ? 'No evaluation has run for this scope.'
            : `Evaluated as of ${new Date(data.lastEvaluatedUtc).toISOString()}.`}
        </div>
        {data.staleCount > 0 && (
          <div className="mt-1 text-xs text-status-warning-text">
            {data.staleCount} finding{data.staleCount === 1 ? '' : 's'} stale — the source for{' '}
            {data.staleEntityNames.join(', ')} did not report in the last cycle. These verdicts are the
            last one this product could reach, not a current reading.
          </div>
        )}
      </div>

      <div className="grid grid-cols-2 gap-2 [break-inside:avoid] sm:grid-cols-5">
        <Metric label="Failing" value={data.totals.failing} status="Warning" />
        <Metric label="Accepted" value={data.totals.accepted} />
        <Metric label="Excepted" value={data.totals.excepted} />
        <Metric label="Passing" value={data.totals.passing} status="Healthy" />
        <Metric label="Not evaluated" value={data.totals.notEvaluated} status="Unknown" />
      </div>

      <Section title="Summary by control">
        {evaluated.length === 0 ? (
          <Empty>No control in this catalogue was evaluated for this scope.</Empty>
        ) : (
          <Table
            head={['Control', 'Title', 'Priority', 'Failing', 'Passing', 'Accepted', 'Excepted']}
          >
            {evaluated.map((control) => (
              <ControlRow key={control.controlId} control={control} />
            ))}
          </Table>
        )}
      </Section>

      <Section title="Finding detail">
        {data.findings.length === 0 ? (
          <Empty>No finding matches this report's scope.</Empty>
        ) : (
          <Table
            head={[
              'Control',
              'Host',
              'State',
              'Observed',
              'Expected',
              'Last evaluated',
              'Acceptance / exception',
            ]}
          >
            {data.findings.map((row, index) => (
              <FindingRow key={index} row={row} />
            ))}
          </Table>
        )}
      </Section>

      <Section title="Removed exceptions" subtitle="Withdrawn exceptions stay on the record as audit evidence.">
        {data.removedExceptions.length === 0 ? (
          <Empty>No exception in this scope has been withdrawn.</Empty>
        ) : (
          <Table head={['Control', 'Entity', 'Owner', 'Reason', 'Removed by', 'Removed at']}>
            {data.removedExceptions.map((exception) => (
              <RemovedExceptionRow key={exception.id} exception={exception} />
            ))}
          </Table>
        )}
      </Section>

      <Section
        title="Change history"
        subtitle={`${new Date(data.historyFromUtc).toISOString()} to ${new Date(data.historyToUtc).toISOString()}`}
      >
        {data.historyTruncated && (
          <div className="mb-2 text-xs text-status-warning-text">
            More transitions matched this scope and period than this report shows below — narrow the
            control, entity or date range to see the rest.
          </div>
        )}
        {data.history.length === 0 ? (
          <Empty>No verdict changed in this period.</Empty>
        ) : (
          <Table head={['Control', 'Entity id', 'From', 'To', 'When']}>
            {data.history.map((row, index) => (
              <HistoryRow key={index} row={row} />
            ))}
          </Table>
        )}
      </Section>

      <Section
        title="Not evaluated — no data collected"
        subtitle="Controls this product could not judge for this scope, and why."
      >
        {notEvaluated.length === 0 ? (
          <Empty>Every control in this catalogue was evaluated.</Empty>
        ) : (
          <Table head={['Control', 'Title', 'Reason']}>
            {notEvaluated.map((control) => (
              <tr key={control.controlId} className="border-b border-border [break-inside:avoid] last:border-0">
                <td className="px-3 py-2 align-top">
                  <Identifier>{control.controlId}</Identifier>
                </td>
                <td className="px-3 py-2 align-top">{control.title}</td>
                <td className="px-3 py-2 align-top text-muted-foreground">
                  {control.notEvaluatedReason ?? '—'}
                </td>
              </tr>
            ))}
          </Table>
        )}
      </Section>
    </div>
  )
}

function Section({
  title,
  subtitle,
  children,
}: {
  title: string
  subtitle?: string
  children: ReactNode
}) {
  return (
    <div className="space-y-2 [break-inside:avoid-page]">
      <div>
        <h2 className="text-sm font-semibold">{title}</h2>
        {subtitle && <div className="text-xs text-muted-foreground">{subtitle}</div>}
      </div>
      {children}
    </div>
  )
}

function Table({ head, children }: { head: string[]; children: ReactNode }) {
  return (
    <Card className="overflow-x-auto p-0 print:border-0 print:bg-transparent">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-border text-xs text-muted-foreground">
          <tr>
            {head.map((label) => (
              <th key={label} className="px-3 py-2">{label}</th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </Card>
  )
}

function ControlRow({ control }: { control: ComplianceControlView }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top"><Identifier>{control.controlId}</Identifier></td>
      <td className="px-3 py-2 align-top">{control.title}</td>
      <td className="px-3 py-2 align-top text-muted-foreground">{control.priority}</td>
      <td className="px-3 py-2 align-top tabular">{control.counts.failing}</td>
      <td className="px-3 py-2 align-top tabular">{control.counts.passing}</td>
      <td className="px-3 py-2 align-top tabular">{control.counts.accepted}</td>
      <td className="px-3 py-2 align-top tabular">{control.counts.excepted}</td>
    </tr>
  )
}

function FindingRow({ row }: { row: ComplianceReportFindingRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top">
        <div className="font-medium">{row.controlId}</div>
        <div className="text-xs text-muted-foreground">{row.controlTitle}</div>
      </td>
      <td className="px-3 py-2 align-top">
        {row.entityName}
        <Identifier> · {row.entityId}</Identifier>
      </td>
      <td className="px-3 py-2 align-top">
        <StatusBadge status={STATE_STATUS[row.state]}>{row.state}</StatusBadge>
        {row.stale && (
          <div className="mt-1">
            <StatusBadge status="Unknown">Stale</StatusBadge>
          </div>
        )}
        {row.state === 'NotEvaluated' && row.notEvaluatedReason && (
          <div className="mt-1 text-xs text-muted-foreground">{row.notEvaluatedReason}</div>
        )}
      </td>
      <td className="px-3 py-2 align-top text-xs text-muted-foreground">{row.observed ?? '—'}</td>
      <td className="px-3 py-2 align-top text-xs text-muted-foreground">{row.expected}</td>
      <td className={cn('px-3 py-2 align-top tabular text-xs')}>{formatUtc(row.lastEvaluatedUtc)}</td>
      <td className="px-3 py-2 align-top text-xs">
        {row.acceptedBy !== null && (
          <div>
            Accepted by {row.acceptedBy}<br />
            {formatUtc(row.acceptedAtUtc)}
            {row.acceptedReason && <div className="text-muted-foreground">{row.acceptedReason}</div>}
          </div>
        )}
        {row.exceptionId !== null && (
          <div className={row.acceptedBy !== null ? 'mt-1' : undefined}>
            Excepted by {row.exceptionOwner} (recorded by {row.exceptionCreatedBy})<br />
            {formatUtc(row.exceptionCreatedAtUtc)} until {formatUtc(row.exceptionExpiresUtc)}
            {row.exceptionReason && <div className="text-muted-foreground">{row.exceptionReason}</div>}
          </div>
        )}
        {row.acceptedBy === null && row.exceptionId === null && (
          <span className="text-muted-foreground">—</span>
        )}
      </td>
    </tr>
  )
}

function RemovedExceptionRow({ exception }: { exception: ComplianceExceptionView }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top"><Identifier>{exception.controlId}</Identifier></td>
      <td className="px-3 py-2 align-top">{exception.entityId ?? 'All entities'}</td>
      <td className="px-3 py-2 align-top">{exception.owner}</td>
      <td className="px-3 py-2 align-top text-muted-foreground">{exception.reason}</td>
      <td className="px-3 py-2 align-top text-xs">{exception.removedBy ?? '—'}</td>
      <td className="px-3 py-2 align-top tabular text-xs">{formatUtc(exception.removedAtUtc)}</td>
    </tr>
  )
}

function HistoryRow({ row }: { row: ComplianceReportTransitionRow }) {
  return (
    <tr className="border-b border-border [break-inside:avoid] last:border-0">
      <td className="px-3 py-2 align-top"><Identifier>{row.controlId}</Identifier></td>
      <td className="px-3 py-2 align-top text-xs">{row.entityId}</td>
      <td className="px-3 py-2 align-top">{row.from ?? <span className="text-muted-foreground">new</span>}</td>
      <td className="px-3 py-2 align-top">{row.to ?? <span className="text-muted-foreground">removed</span>}</td>
      <td className="px-3 py-2 align-top tabular text-xs">{formatUtc(row.atUtc)}</td>
    </tr>
  )
}

/** ISO-8601 UTC, the same wire format the CSV export uses -- unambiguous on a printed page. */
function formatUtc(iso: string | null): string {
  return iso === null ? '—' : new Date(iso).toISOString().replace('.000Z', 'Z')
}
