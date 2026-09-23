import { Link } from 'react-router-dom'
import { Card, Identifier, StatusBadge } from '@/components/Primitives'
import { AlertActions } from '@/components/AlertActions'
import { ago, cn, severityStatus } from '@/lib/ui'
import type { AlertView } from '@/api/types'

/**
 * One alert, as ADR-0007 §5 requires: the same row, the same actions, the
 * same selection state, wherever an alert is shown. The inbox (`Alerts.tsx`)
 * and the grouped view (`Events.tsx`) both render this — neither owns its
 * own copy.
 */
export function AlertRow({
  alert,
  selected,
  onToggle,
}: {
  alert: AlertView
  selected: boolean
  onToggle: () => void
}) {
  return (
    <Card className={cn('p-3', selected && 'border-primary')}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <input
          type="checkbox"
          checked={selected}
          onChange={onToggle}
          className="mt-1"
          aria-label={`Select alert: ${alert.title}`}
        />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge status={severityStatus(alert.severity)}>{alert.severity}</StatusBadge>
            <span className="font-medium">{alert.title}</span>
            {alert.state !== 'Open' && (
              <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                {alert.state}
              </span>
            )}
            {/*
              Suppression is shown, never hidden. Maintenance stops the
              paging, not the reporting — an operator working inside the
              window still has to see what they are doing.
            */}
            {alert.suppressedByWindowId !== null && (
              <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                Notification suppressed
              </span>
            )}
            {/*
              Said out loud, because the product inferred it rather than
              observing it. Principle 1: never present a guess as a
              measurement.
            */}
            {alert.isDerived && (
              <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                Derived
              </span>
            )}
          </div>
          <div className="mt-1 text-sm text-muted-foreground">{alert.description}</div>
          <div className="mt-1.5 flex flex-wrap items-center gap-3">
            {alert.entityId !== null && (
              <Link
                to={`/entities/${encodeURIComponent(alert.entityId)}`}
                className="text-sm underline underline-offset-2"
              >
                {alert.entityName ?? alert.entityId}
              </Link>
            )}
            <Identifier>
              {alert.source} · {alert.category}
            </Identifier>
          </div>
          <AlertActions alert={alert} />
        </div>
        <div className="shrink-0 text-right text-xs text-muted-foreground">
          <div>seen {ago(alert.lastSeenUtc)}</div>
          <div>since {ago(alert.firstSeenUtc)}</div>
        </div>
      </div>
    </Card>
  )
}
