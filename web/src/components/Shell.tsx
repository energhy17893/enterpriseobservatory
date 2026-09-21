import { Link, Outlet, useLocation } from 'react-router-dom'
import type { ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { ago, cn, isStale } from '@/lib/ui'
import type { AuthStateView } from '@/api/types'

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
      { to: '/events', label: 'Events' },
      { to: '/alerts', label: 'All alerts' },
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
    // A group of its own, not a Triage item. Findings are months-long and
    // expected by the hundred; next to the inbox they would read as work that
    // needs doing tonight, which is the mistake this screen exists to avoid.
    label: 'Assess',
    question: 'Is it set up correctly?',
    items: [{ to: '/compliance', label: 'Compliance' }],
  },
  {
    // What the market research says the buyer looks at first. M5.2
    // (compliance) and M5.3 (capacity) land here beside the alert report.
    label: 'Reports',
    question: 'What do I hand someone else?',
    items: [{ to: '/reports', label: 'Reports' }],
  },
  {
    label: 'Configure',
    question: 'How do I set this up?',
    items: [
      { to: '/connections', label: 'Connections' },
      { to: '/maintenance', label: 'Maintenance' },
      { to: '/collectors', label: 'Collectors' },
      { to: '/reports/scheduled', label: 'Scheduled reports' },
      { to: '/email', label: 'Email' },
      { to: '/accounts', label: 'Accounts' },
    ],
  },
] as const

export function Shell({ identity }: { identity: AuthStateView }) {
  return (
    <div className="flex min-h-screen">
      <nav
        // Hidden for every screen when printing, not only the report pages:
        // an operator who hits Ctrl+P from any screen should not get the
        // sidebar in the PDF. See the roadmap's decision against a PDF
        // library -- this is half of what stands in for one.
        className="w-60 shrink-0 border-r border-border bg-card px-3 py-4 print:hidden"
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
                  <NavItem to={item.to} exact={'end' in item ? item.end : false}>
                    {item.label}
                  </NavItem>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>

      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center justify-between gap-4 border-b border-border px-6 py-2 text-xs print:hidden">
          <Identity identity={identity} />
          <Freshness />
        </div>
        <main className="min-w-0 flex-1 p-6 print:p-0">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

/**
 * Who you are, and the way out.
 *
 * The role is shown because it decides what the buttons will do. An operator
 * who does not know they are a viewer reads a refused action as a bug.
 */
function Identity({ identity }: { identity: AuthStateView }) {
  const queryClient = useQueryClient()

  return (
    <div className="flex items-center gap-2 text-muted-foreground">
      <span>
        {identity.username}
        {identity.role !== null && ` · ${identity.role}`}
      </span>
      <button
        type="button"
        onClick={async () => {
          await api.signOut()
          void queryClient.invalidateQueries()
        }}
        className="rounded-md border border-border px-2 py-0.5 hover:text-foreground"
      >
        Sign out
      </button>
    </div>
  )
}

/**
 * One navigation entry.
 *
 * Active state is worked out here rather than left to NavLink, because several
 * of these differ only by query string — Hosts, Virtual machines and Datastores
 * are all /entities. NavLink matches on the path alone and lit all four at
 * once, which is a menu telling the operator they are in four places.
 */
function NavItem({ to, exact, children }: { to: string; exact: boolean; children: ReactNode }) {
  const location = useLocation()
  const [path, search = ''] = to.split('?')
  const wanted = new URLSearchParams(search).get('kind')
  const current = new URLSearchParams(location.search).get('kind')

  const pathMatches = exact
    ? location.pathname === path
    : location.pathname === path || location.pathname.startsWith(`${path}/`)

  // The filter has to match too. On an entity's own page there is no filter, so
  // the unfiltered explorer lights up — which is where "back" goes.
  const isActive = pathMatches && wanted === current

  return (
    <Link
      to={to}
      className={cn(
        'block rounded-md px-2 py-1.5 text-sm',
        isActive
          ? 'bg-primary text-primary-on font-medium'
          : 'text-muted-foreground hover:text-foreground',
      )}
    >
      {children}
    </Link>
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
        'flex items-center justify-end gap-2',
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
