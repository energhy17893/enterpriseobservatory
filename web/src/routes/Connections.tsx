import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, cn, ramp } from '@/lib/ui'
import type {
  AuthStateView,
  ConnectionCommand,
  ConnectionView,
  DatabaseActionView,
  ProbeView,
} from '@/api/types'

/**
 * The kinds this screen knows about, in the order they are offered.
 *
 * A kind is more than a label: it is what the API validates the command
 * against (see ConnectionsApi.UnknownKind) and what
 * SourceConnectionCatalogue.ProbeAsync uses to pick a prober. All three have
 * a collector now — vSphere from the start, Redfish since M6.1 (#178),
 * SimpliVity since S3 (#146) — so every kind offered here can be polled.
 */
const KIND_LABELS: Record<string, string> = {
  vsphere: 'vSphere',
  redfish: 'Redfish (iLO/iDRAC)',
  simplivity: 'SimpliVity',
}

const KIND_ADDRESS_PLACEHOLDERS: Record<string, string> = {
  vsphere: 'https://vcenter.example.local',
  redfish: 'https://ilo-host/',
  simplivity: 'https://ovc-ip-or-mva/',
}

const KINDS = Object.keys(KIND_LABELS)

function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind
}

function blank(kind: string): ConnectionCommand {
  return {
    instanceId: '',
    kind,
    baseAddress: '',
    username: '',
    password: '',
    acceptUntrustedCertificate: false,
    pageSize: 250,
    isEnabled: true,
  }
}

/**
 * The systems the product reads from.
 *
 * This screen exists because of how the first live run went. The password was
 * supplied through a shell, the shell rewrote it before the product ever saw
 * it, and what came back was "vCenter rejected the credentials" — true, and
 * indistinguishable from having typed it wrong. A form has no quoting rules.
 *
 * Three things are load-bearing here and none of them is decoration:
 * the password is write-only and is never sent back to this screen; Test is a
 * separate button from Save, so a credential can be checked before anything
 * starts using it; and a connection that came from configuration is shown but
 * not editable, because an edit here would be undone by the next restart.
 */
export function Connections({ identity }: { identity: AuthStateView }) {
  const [editing, setEditing] = useState<ConnectionCommand | null>(null)
  const [isNew, setIsNew] = useState(false)

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['connections'],
    queryFn: api.connections,
  })

  if (identity.role !== 'Administrator') {
    return (
      <div className="space-y-4">
        <h1 className="text-xl font-semibold">Connections</h1>
        <Empty>
          Only an administrator can see the connection list.
          <div className="mt-1">
            It names every management endpoint in the estate and the account that reads it.
          </div>
        </Empty>
      </div>
    )
  }

  if (isError) return <LoadFailure what="Connections" error={error} />
  if (isPending) return <Loading what="connections" />

  function edit(connection: ConnectionView) {
    setIsNew(false)
    setEditing({
      instanceId: connection.instanceId,
      kind: connection.kind,
      baseAddress: connection.baseAddress,
      username: connection.username,
      // Never prefilled. Nothing can read it back, so an empty field here
      // means "leave the stored one alone" rather than "there isn't one".
      password: '',
      acceptUntrustedCertificate: connection.acceptUntrustedCertificate,
      pageSize: connection.pageSize,
      isEnabled: connection.isEnabled,
    })
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Connections</h1>
        <button
          type="button"
          onClick={() => {
            setIsNew(true)
            setEditing(blank('vsphere'))
          }}
          className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
        >
          Add a connection
        </button>
      </div>

      <DatabaseCard />

      {editing !== null && (
        <Editor
          command={editing}
          isNew={isNew}
          onChange={setEditing}
          onDone={() => setEditing(null)}
        />
      )}

      {data.length === 0 ? (
        <Empty>
          Nothing is being read.
          <div className="mt-1">
            Every screen in the product will be empty until a connection is added — which is not
            the same as an estate with nothing wrong in it.
          </div>
        </Empty>
      ) : (
        // Grouped by kind rather than one flat list. An estate with vCenters,
        // iLOs and SimpliVity controllers in it reads as three different
        // inventories, not one list where the kind is a word in a footer line.
        groupByKind(data).map(([kind, connections]) => (
          <div key={kind} className="space-y-2">
            <h2 className="text-sm font-medium text-muted-foreground">
              {kindLabel(kind)} <span className="text-xs">({connections.length})</span>
            </h2>
            <div className="space-y-2">
              {connections.map((connection) => (
                <Row
                  key={connection.instanceId}
                  connection={connection}
                  onEdit={() => edit(connection)}
                />
              ))}
            </div>
          </div>
        ))
      )}
    </div>
  )
}

/**
 * The product's own database (G-DB): read-only, and never its password.
 *
 * Host, port, database and role, and where they came from — the protected file
 * first-run setup wrote, or configuration. Test asks through the service's own
 * pool; rotate has the product's role change its own password, and is offered
 * only when the product holds that password itself. A password from user
 * secrets or the environment lives where the product cannot write.
 */
function DatabaseCard() {
  const queryClient = useQueryClient()
  const [outcome, setOutcome] = useState<DatabaseActionView | null>(null)

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['database'],
    queryFn: api.database,
  })

  const test = useMutation({
    mutationFn: api.testDatabase,
    onSuccess: setOutcome,
    onError: (cause: unknown) =>
      setOutcome({ succeeded: false, detail: cause instanceof Error ? cause.message : String(cause) }),
  })

  const rotate = useMutation({
    mutationFn: api.rotateDatabasePassword,
    onSuccess: (result) => {
      setOutcome(result)
      void queryClient.invalidateQueries({ queryKey: ['database'] })
    },
    onError: (cause: unknown) =>
      setOutcome({ succeeded: false, detail: cause instanceof Error ? cause.message : String(cause) }),
  })

  if (isError) return <LoadFailure what="The database connection" error={error} />
  if (isPending) return <Loading what="the database connection" />

  const fromFile = data.source === 'ProtectedFile'

  return (
    <Card className="p-3">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">Database</span>
            <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
              {fromFile ? 'set up in the product' : 'from configuration'}
            </span>
            {data.requireTls && (
              <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                TLS required
              </span>
            )}
          </div>

          <div className="mt-1 text-sm text-muted-foreground">
            {data.username} at {data.host}:{data.port}/{data.database}
          </div>

          <Identifier>
            schema {data.schema}
            {data.passwordSetUtc !== null && ` · password set ${ago(data.passwordSetUtc)}`}
          </Identifier>

          {outcome !== null && (
            <div
              className={cn(
                'mt-2 rounded-md border px-3 py-2 text-sm',
                outcome.succeeded
                  ? cn(ramp('Healthy').surface, ramp('Healthy').border, ramp('Healthy').text)
                  : cn(ramp('Critical').surface, ramp('Critical').border, ramp('Critical').text),
              )}
            >
              {outcome.detail}
            </div>
          )}
        </div>

        <div className="flex shrink-0 gap-2">
          <button
            type="button"
            onClick={() => {
              setOutcome(null)
              test.mutate()
            }}
            disabled={test.isPending}
            className="rounded-md border border-border px-2 py-1 text-sm"
          >
            {test.isPending ? 'Testing…' : 'Test connection'}
          </button>
          <button
            type="button"
            onClick={() => {
              if (
                window.confirm(
                  'Give the product a new database password? The old one stops working at once; nothing else needs to change.',
                )
              ) {
                setOutcome(null)
                rotate.mutate()
              }
            }}
            disabled={!data.canRotate || rotate.isPending}
            title={data.rotateRefusal ?? undefined}
            className={cn('rounded-md border border-border px-2 py-1 text-sm', !data.canRotate && 'opacity-40')}
          >
            {rotate.isPending ? 'Rotating…' : 'Rotate password'}
          </button>
        </div>
      </div>
    </Card>
  )
}

function groupByKind(connections: ConnectionView[]): [string, ConnectionView[]][] {
  const byKind = new Map<string, ConnectionView[]>()

  for (const connection of connections) {
    const group = byKind.get(connection.kind)
    if (group) {
      group.push(connection)
    } else {
      byKind.set(connection.kind, [connection])
    }
  }

  // Known kinds first, in the order they are offered; anything else (a kind
  // saved before it was removed from the list, say) sorts after them rather
  // than disappearing.
  return [...byKind.entries()].sort(([a], [b]) => {
    const ia = KINDS.indexOf(a)
    const ib = KINDS.indexOf(b)
    if (ia === -1 && ib === -1) return a.localeCompare(b)
    if (ia === -1) return 1
    if (ib === -1) return -1
    return ia - ib
  })
}

function Row({ connection, onEdit }: { connection: ConnectionView; onEdit: () => void }) {
  const queryClient = useQueryClient()
  const [note, setNote] = useState<string | null>(null)

  const remove = useMutation({
    mutationFn: () => api.removeConnection(connection.instanceId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['connections'] }),
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  const configured = connection.origin === 'Configuration'

  return (
    <Card className="p-3">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{connection.instanceId}</span>

            {/*
              Not polled and not obviously broken are different things, and a
              connection that is merely disabled must not read as healthy.
            */}
            {!connection.isEnabled && (
              <StatusBadge status="Unknown">not polled</StatusBadge>
            )}

            {connection.passwordUnreadable ? (
              <StatusBadge status="Critical">password unreadable</StatusBadge>
            ) : !connection.hasPassword ? (
              <StatusBadge status="Warning">no password</StatusBadge>
            ) : null}

            {configured && (
              <span className="rounded-md border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
                from configuration
              </span>
            )}
          </div>

          <div className="mt-1 text-sm text-muted-foreground">
            {connection.username} at {connection.baseAddress}
          </div>

          {connection.acceptUntrustedCertificate && (
            <div className={cn('mt-1 text-xs', ramp('Warning').text)}>
              Accepting an untrusted certificate: traffic is encrypted, the server is not
              authenticated.
            </div>
          )}

          {connection.passwordUnreadable && (
            <div className={cn('mt-1 text-xs', ramp('Critical').text)}>
              A password is stored but cannot be decrypted — this happens when a database is
              restored without the key material beside it. Enter the password again.
            </div>
          )}

          <Identifier>
            {' '}
            {connection.kind} · page size {connection.pageSize}
            {connection.passwordSetUtc !== null &&
              ` · password set ${ago(connection.passwordSetUtc)}`}
            {connection.createdBy !== '' && ` · added by ${connection.createdBy}`}
          </Identifier>

          {note !== null && <div className={cn('mt-1 text-sm', ramp('Critical').text)}>{note}</div>}
        </div>

        <div className="flex shrink-0 gap-2">
          <button
            type="button"
            onClick={onEdit}
            disabled={configured}
            title={
              configured
                ? 'This connection comes from configuration. Change it where it is deployed.'
                : undefined
            }
            className={cn(
              'rounded-md border border-border px-2 py-1 text-sm',
              configured && 'opacity-40',
            )}
          >
            Edit
          </button>
          <button
            type="button"
            onClick={() => {
              if (
                window.confirm(
                  `Remove connection '${connection.instanceId}'? It stops being polled: its entities keep their last known state but show as stale, and its open alerts stay open and stale too — neither resolves on its own.`,
                )
              ) {
                remove.mutate()
              }
            }}
            disabled={configured || remove.isPending}
            className={cn(
              'rounded-md border border-border px-2 py-1 text-sm',
              configured && 'opacity-40',
            )}
          >
            Remove
          </button>
        </div>
      </div>
    </Card>
  )
}

function Editor({
  command,
  isNew,
  onChange,
  onDone,
}: {
  command: ConnectionCommand
  isNew: boolean
  onChange: (command: ConnectionCommand) => void
  onDone: () => void
}) {
  const queryClient = useQueryClient()
  const [probe, setProbe] = useState<ProbeView | null>(null)
  const [note, setNote] = useState<string | null>(null)

  const test = useMutation({
    mutationFn: () => api.testConnection(command),
    onSuccess: setProbe,
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  const save = useMutation({
    mutationFn: () =>
      isNew ? api.addConnection(command) : api.updateConnection(command.instanceId, command),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
      onDone()
    },
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  function set<K extends keyof ConnectionCommand>(key: K, value: ConnectionCommand[K]) {
    setProbe(null)
    setNote(null)
    onChange({ ...command, [key]: value })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate()
  }

  return (
    <Card className="p-4">
      <form onSubmit={submit} className="space-y-3">
        <h2 className="font-medium">
          {isNew ? `Add a ${kindLabel(command.kind)} connection` : `Edit ${command.instanceId}`}
        </h2>

        <Field
          label="Kind"
          hint={
            isNew
              ? 'Which collector reads this connection. Fixed once saved.'
              : 'Fixed. Set when the connection was added.'
          }
        >
          <select
            value={command.kind}
            onChange={(e) => {
              const kind = e.target.value
              onChange({
                ...command,
                kind,
                // Page size is vSphere-specific; carrying it across to a kind
                // that has no use for it would show a stale value nobody set.
                pageSize: blank(kind).pageSize,
              })
            }}
            disabled={!isNew}
            className="w-full rounded-md border border-border bg-page px-2 py-1 disabled:opacity-50"
          >
            {KINDS.map((kind) => (
              <option key={kind} value={kind}>
                {kindLabel(kind)}
              </option>
            ))}
          </select>
        </Field>

        <Field
          label="Name"
          hint={
            isNew
              ? 'Used to build every entity id from this connection, so it has to be stable. It cannot be changed later.'
              : 'Fixed. Changing it would orphan every entity built from the old name.'
          }
        >
          <input
            value={command.instanceId}
            onChange={(e) => set('instanceId', e.target.value)}
            disabled={!isNew}
            required
            className="w-full rounded-md border border-border bg-page px-2 py-1 disabled:opacity-50"
          />
        </Field>

        <Field label="Address" hint="Must be https. Over http the credentials are readable in transit.">
          <input
            value={command.baseAddress}
            onChange={(e) => set('baseAddress', e.target.value)}
            placeholder={KIND_ADDRESS_PLACEHOLDERS[command.kind] ?? 'https://host.example.local'}
            required
            className="w-full rounded-md border border-border bg-page px-2 py-1"
          />
        </Field>

        <Field
          label="Username"
          hint="A dedicated read-only service account, never an administrator. Every call the product makes is a read."
        >
          <input
            value={command.username}
            onChange={(e) => set('username', e.target.value)}
            placeholder="observatory@vsphere.local"
            autoComplete="off"
            required
            className="w-full rounded-md border border-border bg-page px-2 py-1"
          />
        </Field>

        <Field
          label="Password"
          hint={
            isNew
              ? 'Stored encrypted. Nothing in the product can read it back, including this screen.'
              : 'Leave blank to keep the stored password.'
          }
        >
          <input
            type="password"
            value={command.password}
            onChange={(e) => set('password', e.target.value)}
            autoComplete="new-password"
            className="w-full rounded-md border border-border bg-page px-2 py-1"
          />
        </Field>

        {command.kind === 'vsphere' && (
          <Field label="Page size" hint="Objects per page when listing inventory. vSphere-specific.">
            <input
              type="number"
              min={1}
              value={command.pageSize}
              onChange={(e) => set('pageSize', Number(e.target.value))}
              className="w-full rounded-md border border-border bg-page px-2 py-1"
            />
          </Field>
        )}

        <label className="flex items-start gap-2 text-sm">
          <input
            type="checkbox"
            checked={command.acceptUntrustedCertificate}
            onChange={(e) => set('acceptUntrustedCertificate', e.target.checked)}
            className="mt-1"
          />
          <span>
            Accept an untrusted certificate
            <span className="block text-xs text-muted-foreground">
              vCenter ships self-signed and many installations never replace it. Traffic stays
              encrypted, but the server is no longer authenticated — so this is a decision, not a
              convenience.
            </span>
          </span>
        </label>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={command.isEnabled}
            onChange={(e) => set('isEnabled', e.target.checked)}
          />
          <span>Poll this connection</span>
        </label>

        {probe !== null && (
          <div
            className={cn(
              'rounded-md border px-3 py-2 text-sm',
              probe.noCollector
                ? 'border-border text-muted-foreground'
                : probe.succeeded
                  ? cn(ramp('Healthy').surface, ramp('Healthy').border, ramp('Healthy').text)
                  : cn(ramp('Critical').surface, ramp('Critical').border, ramp('Critical').text),
            )}
          >
            {probe.detail}
            {probe.identified !== null && (
              <div className="mt-1 text-xs text-muted-foreground">{probe.identified}</div>
            )}
          </div>
        )}

        {note !== null && <div className={cn('text-sm', ramp('Critical').text)}>{note}</div>}

        <div className="flex flex-wrap gap-2">
          {/*
            Test before Save, in that order on the screen as well as in the
            sentence. Saving a credential to find out whether it works is the
            sequence that locks accounts out.
          */}
          <button
            type="button"
            onClick={() => test.mutate()}
            disabled={test.isPending}
            className="rounded-md border border-border px-3 py-1.5 text-sm"
          >
            {test.isPending ? 'Testing…' : 'Test'}
          </button>

          <button
            type="submit"
            disabled={save.isPending}
            className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
          >
            {save.isPending ? 'Saving…' : 'Save'}
          </button>

          <button
            type="button"
            onClick={onDone}
            className="rounded-md border border-border px-3 py-1.5 text-sm"
          >
            Cancel
          </button>
        </div>
      </form>
    </Card>
  )
}

function Field({
  label,
  hint,
  children,
}: {
  label: string
  hint: string
  children: React.ReactNode
}) {
  return (
    <label className="block text-sm">
      <span className="font-medium">{label}</span>
      {children}
      <span className="mt-0.5 block text-xs text-muted-foreground">{hint}</span>
    </label>
  )
}
