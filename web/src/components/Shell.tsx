import { NavLink, Outlet } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { ago, cn, isStale } from '@/lib/ui'

/**
 * Top-level navigation, grouped by what the operator is trying to do.
 *
 * Not by vendor. The previous product had about sixty pages grouped as vSphere,
 * iLO, iDRAC, SimpliVity, OneView, Storage, NSX — which is a map of the
 * implementation, not of anyone's job. At three in the morning nobody wonders
 * whether to open the iLO page or the iDRAC page; they wonder what is on fire.
 * See ADR-0007.
 *
 * The vendor has not been removed, it has been moved: it is a filter inside
 * Investigate, and specialised vendor screens hang off it as deep views.
 */
const GROUPS = [
  {
    label: 'Triage',
    question: 'What needs me right now?',
    items: [
      { to: '/', label: 'Overview', end: true },
      { to: '/alerts', label: 'Alert inbox' },
    ],
  },
  {
    label: 'Investigate',
    question: 'Where is this coming from?',
    items: [
      { to: '/entities', label: 'Entity explorer' },
      { to: '/entities?kind=EsxiHost', label: 'Hosts' },
      { to: '/entities?kind=VirtualMachine', label: 'Virtual machines' },
      { to: '/entities?kind=Datastore', label: 'Datastores' },
    ],
  },
  {
    label: 'Configure',
    question: 'How do I set this up?',
    items: [{ to: '/collectors', label: 'Collectors' }],
  },
] as const

export function Shell() {
  return (
    <div className="flex min-h-screen">
      <nav
        className="w-60 shrink-0 border-r border-border bg-card px-3 py-4"
        aria-label="Main"
      >
        <div className="px-2 pb-4">
          <div className="font-semibold">Enterprise Observatory</div>
        </div>

        {GROUPS.map((group) => (
          <div key={group.label} className="mb-5">
            <div
              className="px-2 pb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground"
              title={group.question}
            >
              {group.label}
            </div>
            <ul>
              {group.items.map((item) => (
                <li key={item.to}>
                  <NavLink
                    to={item.to}
                    end={'end' in item ? item.end : false}
                    className={({ isActive }) =>
                      cn(
                        'block rounded-md px-2 py-1.5 text-sm',
                        isActive
                          ? 'bg-primary text-primary-on font-medium'
                          : 'text-muted-foreground hover:text-foreground',
                      )
                    }
                  >
                    {item.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>

      <div className="flex min-w-0 flex-1 flex-col">
        <Freshness />
        <main className="min-w-0 flex-1 p-6">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

/**
 * How current the screen is, said out loud.
 *
 * ADR-0007 §6 forbids presenting stale data as fresh. The rule matters most on
 * a wallboard that has been running for weeks, but it is the same rule
 * everywhere, so it lives in the shell rather than in one screen.
 *
 * It reports the oldest successful collector read, not the time of the last
 * HTTP call: a server that answers instantly with numbers from an hour ago is
 * the failure this exists to catch.
 */
function Freshness() {
  const { data, isError } = useQuery({
    queryKey: ['overview'],
    queryFn: api.overview,
    refetchInterval: 15_000,
  })

  const oldest = data?.oldestSuccessfulReadUtc ?? null
  const stale = isError || data === undefined || isStale(oldest)

  return (
    <div
      className={cn(
        'flex items-center justify-end gap-2 border-b border-border px-6 py-2 text-xs',
        stale ? 'text-status-warning-text' : 'text-muted-foreground',
      )}
    >
      {isError ? (
        <span>Not connected — nothing on this screen is current.</span>
      ) : data === undefined ? (
        <span>Connecting…</span>
      ) : (
        <span>
          Oldest collector read {ago(oldest)}
          {stale && ' — older than it should be'}
        </span>
      )}
    </div>
  )
}
