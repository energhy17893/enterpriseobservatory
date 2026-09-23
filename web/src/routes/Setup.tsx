import { useEffect, useState, type FormEvent } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card } from '@/components/Primitives'
import { cn } from '@/lib/ui'
import type { SetupCommand, SetupResultView, SetupStateView } from '@/api/types'

/**
 * First-run setup (G-DB): connect the product to PostgreSQL, once.
 *
 * The service shows this only while it has no database at all, and only to a
 * browser on the server itself — setup mode listens on loopback and nowhere
 * else, because this form takes a database administrator's credential and
 * there is no account to sign in with yet. See ADR-0010's addendum.
 *
 * Two paths, because enterprises split: some let the product create its own
 * role and database from a one-time administrator credential; in others a DBA
 * has made them already and will never hand over CREATEROLE.
 */
export function Setup() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['setup'],
    queryFn: api.setupState,
    retry: false,
  })

  if (isPending) {
    return <Frame title="Setup">Loading…</Frame>
  }

  if (isError) {
    return (
      <Frame title="Setup">
        <Failure
          message={
            (error instanceof Error ? error.message : String(error)) +
            ' Setup only answers a browser on the server itself.'
          }
        />
      </Frame>
    )
  }

  return <Form defaults={data} />
}

function Form({ defaults }: { defaults: SetupStateView }) {
  const [command, setCommand] = useState<SetupCommand>({
    mode: 'create',
    host: defaults.host,
    port: defaults.port,
    database: defaults.database,
    username: defaults.username,
    schema: defaults.schema,
    requireTls: defaults.requireTls,
    adminUsername: defaults.adminUsername,
    adminPassword: '',
    password: '',
  })
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<SetupResultView | null>(null)

  const run = useMutation({
    mutationFn: () => api.runSetup(command),
    onSuccess: (outcome) => {
      setError(null)
      // The administrator's password is gone from this page as soon as the
      // server has used it.
      setCommand((current) => ({ ...current, adminPassword: '', password: '' }))
      setResult(outcome)
    },
    onError: (cause: unknown) => setError(cause instanceof Error ? cause.message : String(cause)),
  })

  function set<K extends keyof SetupCommand>(key: K, value: SetupCommand[K]) {
    setError(null)
    setCommand((current) => ({ ...current, [key]: value }))
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    run.mutate()
  }

  if (result !== null) {
    return <Done result={result} />
  }

  const creating = command.mode === 'create'

  return (
    <Frame title="Connect to PostgreSQL">
      <p className="mb-4 text-sm text-muted-foreground">
        This installation has no database yet. This page is only reachable from this machine, and
        disappears once the database is set up.
      </p>

      <form onSubmit={submit} className="space-y-3">
        <fieldset className="flex gap-4 text-sm">
          <label className="flex items-center gap-1.5">
            <input
              type="radio"
              checked={creating}
              onChange={() => set('mode', 'create')}
            />
            Create the database and role
          </label>
          <label className="flex items-center gap-1.5">
            <input
              type="radio"
              checked={!creating}
              onChange={() => set('mode', 'existing')}
            />
            Use an existing database
          </label>
        </fieldset>

        <div className="grid grid-cols-3 gap-2">
          <div className="col-span-2">
            <Field label="Host" value={command.host} onChange={(v) => set('host', v)} />
          </div>
          <Field
            label="Port"
            value={String(command.port)}
            onChange={(v) => set('port', Number(v))}
            type="number"
          />
        </div>

        <Field label="Database" value={command.database} onChange={(v) => set('database', v)} />
        <Field
          label="Product role"
          value={command.username}
          onChange={(v) => set('username', v)}
          hint={
            creating
              ? 'Created for the product: never a superuser, owns its database, password generated and never shown.'
              : 'Made by your DBA. Must not be SUPERUSER, CREATEROLE or CREATEDB, and must be able to create tables in the schema.'
          }
        />

        {!creating && (
          <Field
            label="Product role's password"
            value={command.password}
            onChange={(v) => set('password', v)}
            type="password"
            autoComplete="new-password"
          />
        )}

        {creating && (
          <div className="space-y-3 rounded-md border border-border p-3">
            <div className="text-xs text-muted-foreground">
              A one-time administrator, used for this request only — never stored and never logged.
              It needs CREATEDB and CREATEROLE; it does not need to be a superuser.
            </div>
            <Field
              label="Administrator"
              value={command.adminUsername}
              onChange={(v) => set('adminUsername', v)}
              autoComplete="off"
            />
            <Field
              label="Administrator's password"
              value={command.adminPassword}
              onChange={(v) => set('adminPassword', v)}
              type="password"
              autoComplete="off"
            />
          </div>
        )}

        <Field label="Schema" value={command.schema} onChange={(v) => set('schema', v)} />

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={command.requireTls}
            onChange={(e) => set('requireTls', e.target.checked)}
          />
          Require TLS (when the database is on another machine)
        </label>

        <Submit busy={run.isPending}>
          {creating ? 'Create and connect' : 'Connect'}
        </Submit>
        <Failure message={error} />
      </form>
    </Frame>
  )
}

/**
 * After success the service switches to normal mode by itself. This waits for
 * it to answer, then reloads — and says what to do if it never does, because
 * a page that silently waits looks exactly like one that hung.
 */
function Done({ result }: { result: SetupResultView }) {
  const [answering, setAnswering] = useState(false)

  useEffect(() => {
    if (result.restartRequired) return

    const timer = window.setInterval(() => {
      fetch('/api/auth/state', { headers: { Accept: 'application/json' } })
        .then((response) => {
          if (response.ok) {
            setAnswering(true)
            window.location.assign('/')
          }
        })
        .catch(() => {
          // Still switching over; the next tick asks again.
        })
    }, 2000)

    return () => window.clearInterval(timer)
  }, [result.restartRequired])

  return (
    <Frame title="Database connected">
      <div className="space-y-3 text-sm">
        <div className="rounded-md border border-healthy px-3 py-2 text-healthy-on">{result.detail}</div>
        {result.nextStep !== null && <p className="text-muted-foreground">{result.nextStep}</p>}
        {result.restartRequired ? (
          <p className="font-medium">Restart the service to finish.</p>
        ) : (
          <p className="text-xs text-muted-foreground">
            {answering ? 'The service is answering; reloading…' : 'Waiting for the service to switch over…'}
          </p>
        )}
      </div>
    </Frame>
  )
}

function Frame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <Card className="w-full max-w-md p-6">
        <div className="mb-1 font-semibold">Enterprise Observatory</div>
        <h1 className="mb-4 text-lg">{title}</h1>
        {children}
      </Card>
    </div>
  )
}

function Field({
  label,
  value,
  onChange,
  type = 'text',
  autoComplete,
  hint,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  autoComplete?: string
  hint?: string
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-muted-foreground">{label}</span>
      <input
        type={type}
        value={value}
        autoComplete={autoComplete}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded-md border border-border bg-page px-2 py-1.5 text-sm"
      />
      {hint !== undefined && <span className="mt-0.5 block text-xs text-muted-foreground">{hint}</span>}
    </label>
  )
}

function Submit({ busy, children }: { busy: boolean; children: string }) {
  return (
    <button
      type="submit"
      disabled={busy}
      className={cn(
        'w-full rounded-md px-3 py-1.5 text-sm font-medium',
        busy ? 'bg-border text-muted-foreground' : 'bg-primary text-primary-on',
      )}
    >
      {busy ? 'Working…' : children}
    </button>
  )
}

function Failure({ message }: { message: string | null }) {
  if (message === null) {
    return null
  }

  return (
    <div className="rounded-md border border-status-critical-border bg-status-critical-surface px-2 py-1.5 text-xs text-status-critical-text">
      {message}
    </div>
  )
}
