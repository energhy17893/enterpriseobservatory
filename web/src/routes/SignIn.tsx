import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card } from '@/components/Primitives'
import { cn } from '@/lib/ui'
import type { AuthStateView } from '@/api/types'

/**
 * The gate.
 *
 * Two screens in one, because they are the same moment from the product's point
 * of view: either this installation has been claimed and you sign in, or it has
 * not and somebody claims it. Shipping a default administrator password would
 * make the second screen unnecessary and the installation everybody's.
 */
export function SignIn({ state }: { state: AuthStateView }) {
  return state.needsSetup ? <FirstRun state={state} /> : <Credentials />
}

function Credentials() {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const signIn = useMutation({
    mutationFn: () => api.signIn(username, password),
    onSuccess: () => {
      setError(null)
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : String(cause)),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    signIn.mutate()
  }

  return (
    <Frame title="Sign in">
      <form onSubmit={submit} className="space-y-3">
        <Field label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <Field
          label="Password"
          value={password}
          onChange={setPassword}
          type="password"
          autoComplete="current-password"
        />
        <Submit busy={signIn.isPending}>Sign in</Submit>
        <Failure message={error} />
      </form>
    </Frame>
  )
}

function FirstRun({ state }: { state: AuthStateView }) {
  const queryClient = useQueryClient()
  const [token, setToken] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const bootstrap = useMutation({
    mutationFn: () => api.bootstrap(token, username, password),
    onSuccess: () => {
      setError(null)
      void queryClient.invalidateQueries()
    },
    onError: (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : String(cause)),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    bootstrap.mutate()
  }

  const tooShort = password.length > 0 && password.length < state.minimumPasswordLength

  return (
    <Frame title="Claim this installation">
      <p className="mb-4 text-sm text-muted-foreground">
        Nobody has an account here yet. The service printed a one-time setup token to its log when
        it started; paste it below to create the first administrator. Restarting the service issues
        a new token.
      </p>

      <form onSubmit={submit} className="space-y-3">
        <Field label="Setup token" value={token} onChange={setToken} mono />
        <Field label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <Field
          label="Password"
          value={password}
          onChange={setPassword}
          type="password"
          autoComplete="new-password"
        />

        {/*
          A length floor and nothing else. Composition rules push people towards
          Password1! and are advised against by NIST and the NCSC; length is
          what actually costs an attacker anything.
        */}
        <div className={cn('text-xs', tooShort ? 'text-status-warning-text' : 'text-muted-foreground')}>
          At least {state.minimumPasswordLength} characters. Length is the only rule — a long
          phrase beats a short one with a symbol in it.
        </div>

        <Submit busy={bootstrap.isPending}>Create administrator</Submit>
        <Failure message={error} />
      </form>
    </Frame>
  )
}

function Frame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <Card className="w-full max-w-sm p-6">
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
  mono,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  autoComplete?: string
  mono?: boolean
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-muted-foreground">{label}</span>
      <input
        type={type}
        value={value}
        autoComplete={autoComplete}
        onChange={(event) => onChange(event.target.value)}
        className={cn(
          'w-full rounded-md border border-border bg-page px-2 py-1.5 text-sm',
          mono === true && 'font-mono text-xs',
        )}
      />
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
