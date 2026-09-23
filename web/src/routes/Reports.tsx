import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card } from '@/components/Primitives'
import { orderCatalogues } from '@/lib/catalogues'

/**
 * The reports index: M5's whole point, one screen at a time.
 *
 * Market research says this is the first thing a buyer looks at, so it gets
 * its own place in the nav rather than living as a button on the alert
 * inbox. M5.2 (compliance) and M5.3 (capacity) add a card here each; nothing
 * about this screen is specific to alerts.
 */
export function Reports() {
  // P2: one compliance-report card per registered catalogue -- Broadcom
  // first, then every product catalogue (eo-continuity, eo-simplivity,
  // eo-bestpractice, ...) as the API loads them -- rather than one card
  // assuming a single vendor guide. The continuity catalogue's own
  // auditor-format report (ContinuityReport.tsx) keeps its separate card
  // below -- this list is the generic per-control/per-finding report.
  const { data } = useQuery({ queryKey: ['compliance'], queryFn: api.compliance })
  const catalogues = orderCatalogues(data?.catalogues ?? [])

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Reports</h1>
      <p className="text-sm text-muted-foreground">
        Every report is a CSV to hand a spreadsheet, or a printable page you save as a PDF
        from the browser.
      </p>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Link to="/reports/scheduled">
          <Card className="h-full p-4 hover:border-primary">
            <div className="font-medium">Scheduled reports</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Send a report by email on a daily or weekly schedule.
            </div>
          </Card>
        </Link>
        <Link to="/reports/alerts">
          <Card className="h-full p-4 hover:border-primary">
            <div className="font-medium">Alert / finding report</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Open alerts, and alerts resolved in a chosen range. CSV and printable.
            </div>
          </Card>
        </Link>

        {catalogues.map((catalogue) => (
          <Link key={catalogue.id} to={`/reports/compliance?catalogue=${encodeURIComponent(catalogue.id)}`}>
            <Card className="h-full p-4 hover:border-primary">
              <div className="font-medium">{catalogue.name} compliance report</div>
              <div className="mt-1 text-sm text-muted-foreground">
                Findings, exceptions and change history, in the format an auditor asks for. CSV and printable.
              </div>
            </Card>
          </Link>
        ))}

        <Link to="/reports/capacity">
          <Card className="h-full p-4 hover:border-primary">
            <div className="font-medium">Capacity report</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Every datastore, worst fill date first, with the over-commit and no-estimate counts. CSV and printable.
            </div>
          </Card>
        </Link>

        <Link to="/reports/continuity">
          <Card className="h-full p-4 hover:border-primary">
            <div className="font-medium">Continuity report</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Every cluster's HA, DRS and storage-path redundancy posture, one row each. CSV and printable.
            </div>
          </Card>
        </Link>
      </div>
    </div>
  )
}
