import { Link } from 'react-router-dom'
import { Card } from '@/components/Primitives'

/**
 * The reports index: M5's whole point, one screen at a time.
 *
 * Market research says this is the first thing a buyer looks at, so it gets
 * its own place in the nav rather than living as a button on the alert
 * inbox. M5.2 (compliance) and M5.3 (capacity) add a card here each; nothing
 * about this screen is specific to alerts.
 */
export function Reports() {
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

        <Link to="/reports/compliance">
          <Card className="h-full p-4 hover:border-primary">
            <div className="font-medium">Compliance report</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Findings, exceptions and change history, in the format an auditor asks for. CSV and printable.
            </div>
          </Card>
        </Link>

        <Card className="h-full p-4 text-muted-foreground">
          <div className="font-medium">Capacity report</div>
          <div className="mt-1 text-sm">Coming soon.</div>
        </Card>
      </div>
    </div>
  )
}
