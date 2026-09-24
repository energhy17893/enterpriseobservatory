import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, LoadFailure, Loading } from '@/components/Primitives'
import { cn, ramp } from '@/lib/ui'
import type { AuthStateView, SmtpSettingsCommand, MailTestView, SmtpTlsMode } from '@/api/types'

const TLS_MODES: { value: SmtpTlsMode; label: string; hint: string }[] = [
  { value: 'StartTls', label: 'STARTTLS', hint: 'Connects in the clear, then upgrades. What most relays expect on 587.' },
  { value: 'Implicit', label: 'Implicit TLS', hint: 'TLS from the first byte, on its own port — typically 465.' },
  { value: 'None', label: 'None', hint: 'Plain text. Credentials travel unencrypted; refused unless allowed below.' },
]

/**
 * The relay this installation sends scheduled reports through (roadmap M5.4).
 *
 * The password is write-only, the same rule Connections follows: nothing in
 * the product can read it back out, including this screen, so the field is
 * always blank and "leave it blank to keep the stored one" is the standing
 * rule on every save.
 */
export function Email({ identity }: { identity: AuthStateView }) {
  const queryClient = useQueryClient()
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['smtp-settings'],
    queryFn: api.smtpSettings,
  })

  const [form, setForm] = useState<SmtpSettingsCommand | null>(null)
  const [testTo, setTestTo] = useState('')
  const [test, setTest] = useState<MailTestView | null>(null)
  const [note, setNote] = useState<string | null>(null)

  if (identity.role !== 'Administrator') {
    return (
      <div className="space-y-4">
        <h1 className="text-xl font-semibold">Email</h1>
        <Empty>
          Only an administrator can see the mail relay settings.
          <div className="mt-1">They name the account this installation authenticates as.</div>
        </Empty>
      </div>
    )
  }

  if (isError) return <LoadFailure what="Email settings" error={error} />
  if (isPending) return <Loading what="email settings" />

  const command: SmtpSettingsCommand = form ?? {
    host: data.host,
    port: data.port,
    tlsMode: data.tlsMode,
    fromAddress: data.fromAddress,
    username: data.username,
    password: '',
    allowUnencrypted: data.allowUnencrypted,
  }

  function set<K extends keyof SmtpSettingsCommand>(key: K, value: SmtpSettingsCommand[K]) {
    setTest(null)
    setNote(null)
    setForm({ ...command, [key]: value })
  }

  const testMutation = useMutation({
    mutationFn: () => api.testSmtpSettings({ ...command, to: testTo }),
    onSuccess: setTest,
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  const save = useMutation({
    mutationFn: () => api.updateSmtpSettings(command),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['smtp-settings'] })
      setForm(null)
    },
    onError: (cause: unknown) => setNote(cause instanceof Error ? cause.message : String(cause)),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate()
  }

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Email</h1>

      <Card className="p-4">
        <form onSubmit={submit} className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Relay host">
              <input
                value={command.host}
                onChange={(e) => set('host', e.target.value)}
                placeholder="smtp.example.com"
                required
                className="w-full rounded-md border border-border bg-page px-2 py-1"
              />
            </Field>

            <Field label="Port">
              <input
                type="number"
                value={command.port}
                onChange={(e) => set('port', Number(e.target.value))}
                min={1}
                max={65535}
                required
                className="w-full rounded-md border border-border bg-page px-2 py-1"
              />
            </Field>
          </div>

          <Field label="From address">
            <input
              type="email"
              value={command.fromAddress}
              onChange={(e) => set('fromAddress', e.target.value)}
              placeholder="observatory@example.com"
              required
              className="w-full rounded-md border border-border bg-page px-2 py-1"
            />
          </Field>

          <Field label="Username" hint="Leave blank for a relay that does not require authentication.">
            <input
              value={command.username}
              onChange={(e) => set('username', e.target.value)}
              autoComplete="off"
              className="w-full rounded-md border border-border bg-page px-2 py-1"
            />
          </Field>

          <Field
            label="Password"
            hint={
              data.passwordStatus === 'set'
                ? 'Leave blank to keep the stored password.'
                : 'Stored encrypted. Nothing in the product can read it back, including this screen.'
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

          <div className="space-y-1.5">
            <span className="text-sm font-medium">TLS</span>
            {TLS_MODES.map((mode) => (
              <label key={mode.value} className="flex items-start gap-2 text-sm">
                <input
                  type="radio"
                  name="tlsMode"
                  checked={command.tlsMode === mode.value}
                  onChange={() => set('tlsMode', mode.value)}
                  className="mt-1"
                />
                <span>
                  {mode.label}
                  <span className="block text-xs text-muted-foreground">{mode.hint}</span>
                </span>
              </label>
            ))}
          </div>

          {command.tlsMode === 'None' && (
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                checked={command.allowUnencrypted}
                onChange={(e) => set('allowUnencrypted', e.target.checked)}
                className="mt-1"
              />
              <span>
                Send credentials over this unencrypted connection anyway
                <span className={cn('block text-xs', ramp('Warning').text)}>
                  Refused unless checked. Only correct for a relay reachable solely on a closed
                  management network.
                </span>
              </span>
            </label>
          )}

          <Field label="Send a test to" hint="Tries these settings without saving them.">
            <div className="flex gap-2">
              <input
                type="email"
                value={testTo}
                onChange={(e) => setTestTo(e.target.value)}
                placeholder="you@example.com"
                className="w-full rounded-md border border-border bg-page px-2 py-1"
              />
              <button
                type="button"
                onClick={() => testMutation.mutate()}
                disabled={testMutation.isPending || testTo === ''}
                className="shrink-0 rounded-md border border-border px-3 py-1.5 text-sm"
              >
                {testMutation.isPending ? 'Sending…' : 'Test'}
              </button>
            </div>
          </Field>

          {test !== null && (
            <div
              className={cn(
                'rounded-md border px-3 py-2 text-sm',
                test.succeeded
                  ? cn(ramp('Healthy').surface, ramp('Healthy').border, ramp('Healthy').text)
                  : cn(ramp('Critical').surface, ramp('Critical').border, ramp('Critical').text),
              )}
            >
              {test.detail}
            </div>
          )}

          {note !== null && <div className={cn('text-sm', ramp('Critical').text)}>{note}</div>}

          <div className="flex flex-wrap items-center gap-2">
            <button
              type="submit"
              disabled={save.isPending}
              className="rounded-md border border-border bg-primary px-3 py-1.5 text-sm text-primary-on"
            >
              {save.isPending ? 'Saving…' : 'Save'}
            </button>
            <span className="text-xs text-muted-foreground">
              Password {data.passwordStatus}
              {data.passwordSetUtc !== null && ` · set ${new Date(data.passwordSetUtc).toLocaleString()}`}
            </span>
          </div>
        </form>
      </Card>
    </div>
  )
}

function Field({
  label,
  hint,
  children,
}: {
  label: string
  hint?: string
  children: React.ReactNode
}) {
  return (
    <label className="block text-sm">
      <span className="font-medium">{label}</span>
      {children}
      {hint !== undefined && <span className="mt-0.5 block text-xs text-muted-foreground">{hint}</span>}
    </label>
  )
}
