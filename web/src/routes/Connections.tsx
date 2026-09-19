import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading, StatusBadge } from '@/components/Primitives'
import { ago, cn } from '@/lib/ui'
import type { AuthStateView, ConnectionCommand, ConnectionView, ProbeView } from '@/api/types'

const BLANK: ConnectionCommand = {
  instanceId: '',
  kind: 'vsphere',
  baseAddress: '',
  username: '',
  password: '',
  acceptUntrustedCertificate: false,
  pageSize: 250,
  isEnabled: true,
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
            setEditing(BLANK)
          }}
          className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
        >
          Add a vCenter
        </button>
      </div>

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
        data.map((connection) => (
          <Row key={connection.instanceId} connection={connection} onEdit={() => edit(connection)} />
        ))
      )}
    </div>
  )
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
            <div className="mt-1 text-xs text-warning-on">
              Accepting an untrusted certificate: traffic is encrypted, the server is not
              authenticated.
            </div>
          )}

          {connection.passwordUnreadable && (
            <div className="mt-1 text-xs text-critical-on">
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

          {note !== null && <div className="mt-1 text-sm text-critical-on">{note}</div>}
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
            onClick={() => remove.mutate()}
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
        <h2 className="font-medium">{isNew ? 'Add a vCenter' : `Edit ${command.instanceId}`}</h2>

        <Field
          label="Name"
          hint={
            isNew
              ? 'Used to build every entity id from this vCenter, so it has to be stable. It cannot be changed later.'
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
            placeholder="https://vcenter.example.local"
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
              probe.succeeded ? 'border-healthy text-healthy-on' : 'border-critical text-critical-on',
            )}
          >
            {probe.detail}
            {probe.identified !== null && (
              <div className="mt-1 text-xs text-muted-foreground">{probe.identified}</div>
            )}
          </div>
        )}

        {note !== null && <div className="text-sm text-critical-on">{note}</div>}

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
