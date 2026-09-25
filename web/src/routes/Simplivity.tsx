import { useState, type ReactNode } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, Metric, StatusBadge } from '@/components/Primitives'
import { AlertRow } from '@/components/AlertRow'
import { BulkBar } from '@/components/BulkBar'
import { Pager } from '@/components/Pager'
import { ago, cn, EMPTY, healthStatus, type StatusName } from '@/lib/ui'
import { counterStatus, simplivityStatus } from '@/lib/simplivity'
import type {
  SimplivityBackupView,
  SimplivityClusterView,
  SimplivityHardwareView,
  SimplivityHostView,
  SimplivitySourceView,
  SimplivityVmView,
} from '@/api/types'

// eo-ux §7, measured 24 Sep (reference-approaches §11): entity-like tables page
// at 100; alert sections page at 50 like the alert inbox (A1).
const TABLE_PAGE_SIZE = 100
const ALERT_PAGE_SIZE = 50

/** A value the source did not give: grey and said so, never blank and never "fine". */
export function SimplivityValue({ value }: { value: string | null }) {
  return value === null ? (
    <StatusBadge status="Unknown">Unknown</StatusBadge>
  ) : (
    <StatusBadge status={simplivityStatus(value)}>{value}</StatusBadge>
  )
}

/** ADR-0027: a value kept while its source is silent says so, with when it was read. */
export function CarriedForward({ readAtUtc }: { readAtUtc: string }) {
  return (
    <span title={readAtUtc}>
      <StatusBadge status="Unknown">carried forward · read {ago(readAtUtc)}</StatusBadge>
    </span>
  )
}

function arbiterStatus(cluster: SimplivityClusterView): StatusName {
  if (cluster.arbiterConnected === null) return 'Unknown'
  if (cluster.arbiterConnected) return 'Healthy'
  return cluster.arbiterRequired ? 'Critical' : 'Warning'
}

function yesNo(value: boolean | null) {
  return value === null ? EMPTY : value ? 'yes' : 'no'
}

function dash(value: string | number | null) {
  return value === null ? EMPTY : value
}

/**
 * SimpliVity: a deep view under Investigate (ADR-0007 tier 3).
 *
 * A projection of the `simplivity.*` annotations the SimpliVity source folds
 * onto the vSphere hosts, clusters and VMs — HPE's own model, laid out as
 * `svt-federation-show`, the Admin Guide's status cards and the Upgrade
 * Guide's "Check the federation" (reference-approaches §10.8). It computes no
 * alert and no finding: the alerts below are the inbox's instances filtered
 * by source, and by category for the platform's SimpliVity event rules.
 */
export function Simplivity() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['simplivity'],
    queryFn: api.simplivity,
    refetchInterval: 30_000,
  })

  if (isError) return <LoadFailure what="The SimpliVity view" error={error} />
  if (isPending) return <Loading what="SimpliVity" />

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-xl font-semibold">SimpliVity</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Federation, storage HA and backups as each SimpliVity connection reports them, on the
          vSphere hosts, clusters and VMs they belong to.
        </p>
      </div>

      {data.sources.length === 0 ? (
        <Empty action={<Link to="/connections" className="text-sm underline underline-offset-2">Connections</Link>}>
          No SimpliVity connection is configured.
        </Empty>
      ) : (
        data.sources.map((source) => (
          <Source key={source.instanceId} source={source} rpoHours={data.backupRpoHours} />
        ))
      )}

      {/* The event rules raise these with source "platform": no connection's section lists them. */}
      <InboxAlerts
        filter={{ category: 'SimpliVity' }}
        heading="Alerts in category SimpliVity"
        level="h2"
        empty="No alert in category SimpliVity is in the inbox."
      />
    </div>
  )
}

/** Good out of all; Unknown is its own number in the hint, never in either side. */
function counter(counts: Record<string, number>, good: string) {
  const total = Object.values(counts).reduce((sum, n) => sum + n, 0)
  const ok = counts[good] ?? 0
  const unknown = counts['Unknown'] ?? 0

  return { value: total === 0 ? EMPTY : `${ok}/${total}`, status: counterStatus(counts, good), hint: unknown > 0 ? `${unknown} unknown` : undefined }
}

function Source({ source, rpoHours }: { source: SimplivitySourceView; rpoHours: number | null }) {
  const hosts = counter(source.hostStates, 'ALIVE')
  const vms = counter(source.vmHaStatuses, 'SAFE')
  const arbiters = counter(source.arbitersConnected, 'true')
  const stale = source.backups.olderThanRpo.length

  return (
    <section className="space-y-4" aria-labelledby={`svt-${source.instanceId}`}>
      {/* §11.6: status strip on top, sections below. */}
      <div className="flex flex-wrap items-center gap-3">
        <h2 id={`svt-${source.instanceId}`} className="text-lg font-semibold">
          {source.instanceId}
        </h2>
        <StatusBadge status={source.collectorHealth === null ? 'Unknown' : healthStatus(source.collectorHealth)}>
          collector {source.collectorHealth ?? 'never ran'}
        </StatusBadge>
        {source.readAtUtc !== null &&
          (source.reporting ? (
            <Identifier>read {ago(source.readAtUtc)}</Identifier>
          ) : (
            <CarriedForward readAtUtc={source.readAtUtc} />
          ))}
        {source.partialFailures > 0 && (
          <Link to="/collectors" className="text-xs underline underline-offset-2">
            {source.partialFailures} could not be folded
          </Link>
        )}
      </div>
      {source.lastFailureDetail !== null && !source.reporting && (
        <Card className="p-3 text-sm text-muted-foreground">{source.lastFailureDetail}</Card>
      )}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Metric label="Hosts ALIVE" {...hosts} />
        <Metric label="VMs storage HA SAFE" {...vms} />
        <Metric label="Arbiters connected" {...arbiters} />
        <Metric
          label="Backups older than RPO"
          value={rpoHours === null ? EMPTY : stale}
          status={rpoHours === null ? 'Unknown' : stale > 0 ? 'Warning' : 'Healthy'}
          hint={`${source.backups.withBackup} VMs with a backup, ${source.backups.withoutBackup} without`}
        />
      </div>

      <Federation source={source} />
      <StorageHa rows={source.notSafeVms} />
      <Backups rows={source.backups.olderThanRpo} rpoHours={rpoHours} />
      <Hardware rows={source.hardware} />
      <InboxAlerts
        filter={{ source: source.instanceId }}
        heading={`Open alerts from ${source.instanceId}`}
        level="h3"
        empty="No alert from this connection is open."
      />
    </section>
  )
}

function Th({ children, right }: { children: ReactNode; right?: boolean }) {
  return (
    <th scope="col" className={cn('px-3 py-2 font-medium', right ? 'text-right' : 'text-left')}>
      {children}
    </th>
  )
}

/** §11.7: frozen header, name first, numbers right-aligned. */
function Table({ head, children }: { head: ReactNode; children: ReactNode }) {
  return (
    <Card className="max-h-[70vh] overflow-auto">
      <table className="w-full text-sm">
        <thead className="sticky top-0 bg-card text-xs uppercase tracking-wide text-muted-foreground">
          <tr className="border-b border-border">{head}</tr>
        </thead>
        <tbody className="divide-y divide-border">{children}</tbody>
      </table>
    </Card>
  )
}

function EntityLink({ id, name }: { id: string; name: string }) {
  return (
    <Link to={`/entities/${encodeURIComponent(id)}`} className="underline underline-offset-2">
      {name}
    </Link>
  )
}

function RowMark({ row }: { row: { carriedForward: boolean; readAtUtc: string } }) {
  return row.carriedForward ? <CarriedForward readAtUtc={row.readAtUtc} /> : null
}

/** svt-federation-show: cluster → its hosts, State, Arbiter, Version. */
function Federation({ source }: { source: SimplivitySourceView }) {
  const empty = source.clusters.length === 0 && source.otherHosts.length === 0

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium">Federation</h3>
      {empty ? (
        <Empty>
          {source.reporting ? 'This connection reports no cluster or host.' : 'Nothing read from this connection yet.'}
        </Empty>
      ) : (
        <Table
          head={
            <>
              <Th>Cluster / host</Th>
              <Th>State · arbiter</Th>
              <Th>Upgrade</Th>
              <Th>Version</Th>
              <Th>Virtual controller</Th>
              <Th right>Hosts</Th>
            </>
          }
        >
          {source.clusters.map((cluster) => (
            <ClusterRows key={cluster.entityId} cluster={cluster} />
          ))}
          {source.otherHosts.length > 0 && (
            <tr>
              <td colSpan={6} className="px-3 py-2 text-xs text-muted-foreground">
                Hosts whose vSphere cluster carries no SimpliVity cluster
              </td>
            </tr>
          )}
          {source.otherHosts.map((host) => (
            <HostRow key={host.entityId} host={host} />
          ))}
        </Table>
      )}
    </div>
  )
}

function ClusterRows({ cluster }: { cluster: SimplivityClusterView }) {
  const alive = cluster.hosts.filter((h) => h.state === 'ALIVE').length

  return (
    <>
      <tr className="bg-page/50">
        <td className="px-3 py-2 font-medium">
          <EntityLink id={cluster.entityId} name={cluster.name} /> <RowMark row={cluster} />
        </td>
        <td className="px-3 py-2">
          <StatusBadge status={arbiterStatus(cluster)}>
            arbiter{' '}
            {cluster.arbiterConnected === null ? 'Unknown' : cluster.arbiterConnected ? 'connected' : 'disconnected'}
          </StatusBadge>
          <div className="mt-0.5 text-xs text-muted-foreground">
            required {yesNo(cluster.arbiterRequired)} · configured {yesNo(cluster.arbiterConfigured)}
          </div>
        </td>
        <td className="px-3 py-2">
          {cluster.upgradeState === null ? <SimplivityValue value={null} /> : cluster.upgradeState}
        </td>
        <td className="px-3 py-2 font-mono text-xs">{dash(cluster.version)}</td>
        {/* §11.7 [z]: a cluster has no virtual controller -- not applicable, not missing. */}
        <td className="px-3 py-2" title="not applicable">
          {EMPTY}
        </td>
        {/* §11.6's layer counter: ALIVE out of the hosts folded here, of the members SimpliVity lists. */}
        <td className="px-3 py-2 text-right tabular">
          {alive}/{cluster.hosts.length} ALIVE
          {cluster.members !== null && cluster.members !== cluster.hosts.length && (
            <div className="text-xs text-muted-foreground">{cluster.members} members</div>
          )}
        </td>
      </tr>
      {cluster.hosts.map((host) => (
        <HostRow key={host.entityId} host={host} indent />
      ))}
    </>
  )
}

function HostRow({ host, indent }: { host: SimplivityHostView; indent?: boolean }) {
  return (
    <tr>
      <td className={cn('px-3 py-2', indent && 'pl-8')}>
        <EntityLink id={host.entityId} name={host.name} /> <RowMark row={host} />
      </td>
      <td className="px-3 py-2">
        <SimplivityValue value={host.state} />
      </td>
      <td className="px-3 py-2">
        {host.upgradeState === null ? <SimplivityValue value={null} /> : host.upgradeState}
      </td>
      <td className="px-3 py-2 font-mono text-xs">{dash(host.version)}</td>
      <td className="px-3 py-2 font-mono text-xs">{dash(host.virtualControllerName)}</td>
      <td className="px-3 py-2 text-right">{EMPTY}</td>
    </tr>
  )
}

/** Client-side pages over a list the server sent whole (at most the estate's VMs). */
function usePage<T>(rows: T[], unit: string) {
  const [offset, setOffset] = useState(0)
  const start = offset < rows.length ? offset : 0

  const pager =
    rows.length > TABLE_PAGE_SIZE ? (
      <div className="flex justify-end">
        <Pager offset={start} pageSize={TABLE_PAGE_SIZE} total={rows.length} onOffset={setOffset} unit={unit} />
      </div>
    ) : null

  return { rows: rows.slice(start, start + TABLE_PAGE_SIZE), pager }
}

/**
 * The Admin Guide's "Non-HA virtual machines": every VM whose storage HA is
 * not SAFE. DEFUNCT and DEGRADED lead and are emphasised; SYNCING is
 * transient; a VM whose status was not given is Unknown, listed last.
 */
function StorageHa({ rows }: { rows: SimplivityVmView[] }) {
  const { rows: page, pager } = usePage(rows, 'VMs')

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium">Storage HA — VMs not SAFE</h3>
      {rows.length === 0 ? (
        <Empty>Every VM this connection reports is SAFE.</Empty>
      ) : (
        <>
          <Table
            head={
              <>
                <Th>VM</Th>
                <Th>Storage HA</Th>
                <Th right>Resynchronization</Th>
              </>
            }
          >
            {page.map((vm) => {
              const status = simplivityStatus(vm.haStatus)
              return (
                <tr key={vm.entityId} className={cn((status === 'Critical' || status === 'Warning') && 'font-medium')}>
                  <td className="px-3 py-2">
                    <EntityLink id={vm.entityId} name={vm.name} /> <RowMark row={vm} />
                  </td>
                  <td className="px-3 py-2">
                    <SimplivityValue value={vm.haStatus} />
                    {vm.haStatus === 'SYNCING' && <span className="ml-2 text-xs text-muted-foreground">transient</span>}
                  </td>
                  <td className="px-3 py-2 text-right tabular">{dash(vm.resynchronizationProgress)}</td>
                </tr>
              )
            })}
          </Table>
          {pager}
        </>
      )}
    </div>
  )
}

function Backups({ rows, rpoHours }: { rows: SimplivityBackupView[]; rpoHours: number | null }) {
  const { rows: page, pager } = usePage(rows, 'backups')

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium">Backups older than the RPO</h3>
      {rpoHours === null ? (
        <Empty>No backup-freshness policy is in the catalogue, so no age is judged here.</Empty>
      ) : (
        <>
          <p className="text-xs text-muted-foreground">
            Newest PROTECTED SimpliVity backup older than {rpoHours} hours — the backup-freshness policy
            (M8.8). VMs with no SimpliVity backup are not listed: another product may protect them.
          </p>
          {rows.length === 0 ? (
            <Empty>No VM's newest SimpliVity backup is older than {rpoHours} hours.</Empty>
          ) : (
            <>
              <Table
                head={
                  <>
                    <Th>VM</Th>
                    <Th>Newest PROTECTED backup (UTC)</Th>
                    <Th right>Age</Th>
                    <Th>Type</Th>
                  </>
                }
              >
                {page.map((backup) => (
                  <tr key={backup.entityId}>
                    <td className="px-3 py-2">
                      <EntityLink id={backup.entityId} name={backup.name} /> <RowMark row={backup} />
                    </td>
                    <td className="px-3 py-2 font-mono text-xs">
                      {backup.lastBackupUtc.slice(0, 16).replace('T', ' ')}
                    </td>
                    <td className="px-3 py-2 text-right tabular">{ago(backup.lastBackupUtc)}</td>
                    <td className="px-3 py-2">{dash(backup.type)}</td>
                  </tr>
                ))}
              </Table>
              {pager}
            </>
          )}
        </>
      )}
    </div>
  )
}

/** "RED 1 · GREEN 23": anything not good first; null when nothing was counted. */
function driveCounts(counts: Record<string, number>) {
  const good = (word: string) => (simplivityStatus(word) === 'Healthy' ? 1 : 0)
  const entries = Object.entries(counts).sort(([a], [b]) => good(a) - good(b))
  return entries.length === 0 ? null : entries.map(([word, n]) => `${word} ${n}`).join(' · ')
}

/** HPE's SSD wear rule (§10.8): ≤10% warns, ≤5% is critical. */
function lifeStatus(life: number | null): StatusName {
  if (life === null) return 'Unknown'
  return life <= 5 ? 'Critical' : life <= 10 ? 'Warning' : 'Healthy'
}

/**
 * svt-hardware-show: per host its tree's colour, RAID card, battery,
 * accelerator, physical drives and the lowest SSD life. A host whose tree was
 * not read, or a part HPE answers empty (no accelerator card), is Unknown.
 */
function Hardware({ rows }: { rows: SimplivityHardwareView[] }) {
  return (
    <div className="space-y-2">
      <h3 className="text-sm font-medium">Hardware</h3>
      {rows.length === 0 ? (
        <Empty>No host's hardware has been read from this connection.</Empty>
      ) : (
        <Table
          head={
            <>
              <Th>Host</Th>
              <Th>Hardware</Th>
              <Th>RAID · battery</Th>
              <Th>Accelerator</Th>
              <Th>Physical drives</Th>
              <Th right>Min SSD life</Th>
              <Th right>Rebuilding</Th>
            </>
          }
        >
          {rows.map((host) => {
            const statuses = driveCounts(host.driveStatuses)
            const healths = driveCounts(host.driveHealths)
            return (
              <tr key={host.entityId}>
                <td className="px-3 py-2">
                  <EntityLink id={host.entityId} name={host.name} /> <RowMark row={host} />
                </td>
                <td className="px-3 py-2">
                  <SimplivityValue value={host.status} />
                </td>
                <td className="px-3 py-2">
                  <SimplivityValue value={host.raidStatus} /> <SimplivityValue value={host.batteryHealth} />
                  {host.batteryPercentCharged !== null && (
                    <span className="ml-1 text-xs text-muted-foreground">{host.batteryPercentCharged}%</span>
                  )}
                </td>
                <td className="px-3 py-2">
                  <SimplivityValue value={host.acceleratorStatus} />
                </td>
                <td className="px-3 py-2">
                  {statuses === null ? (
                    <SimplivityValue value={null} />
                  ) : (
                    <>
                      <span className="tabular">{statuses}</span>
                      {healths !== null && <div className="text-xs text-muted-foreground">{healths}</div>}
                    </>
                  )}
                </td>
                <td className="px-3 py-2 text-right tabular">
                  {host.minLifeRemaining === null ? (
                    <SimplivityValue value={null} />
                  ) : (
                    <StatusBadge status={lifeStatus(host.minLifeRemaining)}>{host.minLifeRemaining}%</StatusBadge>
                  )}
                </td>
                <td className="px-3 py-2 text-right tabular">{dash(host.drivesRebuilding)}</td>
              </tr>
            )
          })}
        </Table>
      )}
    </div>
  )
}

/**
 * The inbox's own instances, filtered (ADR-0007 §5) — not a second list. A
 * connection's REST alerts carry its instance id as source and category
 * "Availability"; the platform's SimpliVity event rules carry source
 * "platform" and category "SimpliVity" (ADR-0027 rule 6). Two filters, two
 * sections, never merged.
 */
function InboxAlerts({
  filter,
  heading,
  level: Heading,
  empty,
}: {
  filter: { source: string } | { category: string }
  heading: string
  level: 'h2' | 'h3'
  empty: string
}) {
  const [offset, setOffset] = useState(0)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['alerts', filter, offset],
    queryFn: () => api.alerts({ ...filter, offset, limit: ALERT_PAGE_SIZE }),
    refetchInterval: 15_000,
    placeholderData: keepPreviousData,
  })

  function toggle(fingerprint: string) {
    setSelected((current) => {
      const next = new Set(current)
      if (!next.delete(fingerprint)) next.add(fingerprint)
      return next
    })
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Heading className={Heading === 'h2' ? 'text-lg font-semibold' : 'text-sm font-medium'}>{heading}</Heading>
        {data !== undefined && data.total > ALERT_PAGE_SIZE && (
          <Pager
            offset={offset}
            pageSize={ALERT_PAGE_SIZE}
            total={data.total}
            unit="alerts"
            onOffset={(next) => {
              setOffset(next)
              setSelected(new Set())
            }}
          />
        )}
      </div>
      {isError ? (
        <LoadFailure what="Alerts" error={error} />
      ) : isPending ? (
        <Loading what="alerts" />
      ) : data.items.length === 0 ? (
        <Empty>{empty}</Empty>
      ) : (
        <>
          <BulkBar selected={[...selected]} onDone={() => setSelected(new Set())} />
          <ul className="space-y-2">
            {data.items.map((alert) => (
              <li key={alert.fingerprint}>
                <AlertRow
                  alert={alert}
                  selected={selected.has(alert.fingerprint)}
                  onToggle={() => toggle(alert.fingerprint)}
                />
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  )
}
