import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { Card, Empty, Identifier, LoadFailure, Loading } from '@/components/Primitives'
import { ago, cn } from '@/lib/ui'
import type { AccountView, AuthStateView, Role } from '@/api/types'

const ROLES: Role[] = ['Viewer', 'Operator', 'Administrator']

const ROLE_MEANING: Record<Role, string> = {
  Viewer: 'Sees everything, changes nothing.',
  Operator: 'Also acknowledges, silences and clears alerts.',
  Administrator: 'Also manages accounts and configuration.',
}

/**
 * Accounts.
 *
 * Administrator-only, except for the one thing anybody may do: change their own
 * password. That exception is the point — a product where only an administrator
 * can rotate a credential is one where nobody rotates credentials.
 */
export function Accounts({ identity }: { identity: AuthStateView }) {
  const isAdministrator = identity.role === 'Administrator'

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold">Accounts</h1>

      <OwnPassword minimumLength={identity.minimumPasswordLength} />

      {isAdministrator ? (
        <Everyone minimumLength={identity.minimumPasswordLength} you={identity.username} />
      ) : (
        <Empty>
          Only an administrator can see or manage other accounts.
          <div className="mt-1">You can still change your own password above.</div>
        </Empty>
      )}
    </div>
  )
}

function OwnPassword({ minimumLength }: { minimumLength: number }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)

  const change = useMutation({
    mutationFn: () => api.changeOwnPassword(current, next),
    onSuccess: () => {
      setCurrent('')
      setNext('')
      setNote({ ok: true, text: 'Password changed.' })
    },
    onError: (cause: unknown) =>
      setNote({ ok: false, text: cause instanceof Error ? cause.message : String(cause) }),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    change.mutate()
  }

  return (
    <Card className="p-4">
      <h2 className="text-sm font-medium">Your password</h2>
      <form onSubmit={submit} className="mt-3 flex flex-wrap items-end gap-2">
        {/*
          The current password is asked for even though the session already
          proves identity. A session left open on an unlocked workstation is
          the common case and must not be enough to take the account over.
        */}
        <Input label="Current" value={current} onChange={setCurrent} type="password" />
        <Input label={`New (min ${minimumLength})`} value={next} onChange={setNext} type="password" />
        <Button busy={change.isPending}>Change</Button>
      </form>
      <Note note={note} />
    </Card>
  )
}

function Everyone({ minimumLength, you }: { minimumLength: number; you: string | null }) {
  const queryClient = useQueryClient()
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)

  const accounts = useQuery({ queryKey: ['accounts'], queryFn: api.accounts })

  const act = useMutation({
    mutationFn: (run: () => Promise<unknown>) => run(),
    onSuccess: () => {
      setNote(null)
      void queryClient.invalidateQueries({ queryKey: ['accounts'] })
    },
    onError: (cause: unknown) =>
      setNote({ ok: false, text: cause instanceof Error ? cause.message : String(cause) }),
  })

  if (accounts.isError) return <LoadFailure what="Accounts" error={accounts.error} />
  if (accounts.isPending) return <Loading what="accounts" />

  return (
    <div className="space-y-4">
      <NewAccount minimumLength={minimumLength} onDone={() => act.mutate(async () => {})} />

      <Card className="divide-y divide-border">
        {accounts.data.map((account) => (
          <Row
            key={account.username}
            account={account}
            isYou={account.username === you}
            minimumLength={minimumLength}
            onAct={(run) => act.mutate(run)}
            busy={act.isPending}
          />
        ))}
      </Card>

      <Note note={note} />
    </div>
  )
}

function Row({
  account,
  isYou,
  minimumLength,
  onAct,
  busy,
}: {
  account: AccountView
  isYou: boolean
  minimumLength: number
  onAct: (run: () => Promise<unknown>) => void
  busy: boolean
}) {
  const [resetting, setResetting] = useState(false)
  const [password, setPassword] = useState('')

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 p-3">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{account.username}</span>
          {isYou && <span className="text-xs text-muted-foreground">(you)</span>}
          {account.lockedOut && (
            <span className="rounded-md border border-status-warning-border bg-status-warning-surface px-1.5 py-0.5 text-xs text-status-warning-text">
              Locked out
            </span>
          )}
        </div>
        <Identifier>
          created {ago(account.createdUtc)} · last signed in {ago(account.lastSignedInUtc)}
        </Identifier>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <select
          value={account.role}
          disabled={busy}
          onChange={(event) =>
            onAct(() => api.changeRole(account.username, event.target.value as Role))
          }
          title={ROLE_MEANING[account.role]}
          className="rounded-md border border-border bg-page px-2 py-1 text-xs"
          aria-label={`Role for ${account.username}`}
        >
          {ROLES.map((role) => (
            <option key={role} value={role}>
              {role}
            </option>
          ))}
        </select>

        <Small onClick={() => setResetting((open) => !open)}>Reset password…</Small>

        {/*
          Removal is offered for everyone, including the last administrator.
          The server refuses that one and explains why — a button that is
          simply missing teaches nothing, and the explanation is the useful
          part.
        */}
        <Small onClick={() => onAct(() => api.removeAccount(account.username))}>Remove</Small>
      </div>

      {resetting && (
        <div className="flex w-full flex-wrap items-end gap-2 pt-1">
          <Input
            label={`New password for ${account.username} (min ${minimumLength})`}
            value={password}
            onChange={setPassword}
            type="password"
          />
          <Button
            busy={busy}
            onClick={() => {
              onAct(() => api.resetPassword(account.username, password))
              setPassword('')
              setResetting(false)
            }}
          >
            Set
          </Button>
          <span className="text-xs text-muted-foreground">
            This does not sign them out of sessions they already have.
          </span>
        </div>
      )}
    </div>
  )
}

function NewAccount({
  minimumLength,
  onDone,
}: {
  minimumLength: number
  onDone: () => void
}) {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<Role>('Operator')
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)

  const create = useMutation({
    mutationFn: () => api.createAccount(username, password, role),
    onSuccess: () => {
      setUsername('')
      setPassword('')
      setNote({ ok: true, text: 'Account created.' })
      void queryClient.invalidateQueries({ queryKey: ['accounts'] })
      onDone()
    },
    onError: (cause: unknown) =>
      setNote({ ok: false, text: cause instanceof Error ? cause.message : String(cause) }),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate()
  }

  return (
    <Card className="p-4">
      <h2 className="text-sm font-medium">Add an account</h2>
      <form onSubmit={submit} className="mt-3 flex flex-wrap items-end gap-2">
        <Input label="Username" value={username} onChange={setUsername} />
        <Input label={`Password (min ${minimumLength})`} value={password} onChange={setPassword} type="password" />
        <label className="block">
          <span className="mb-1 block text-xs text-muted-foreground">Role</span>
          <select
            value={role}
            onChange={(event) => setRole(event.target.value as Role)}
            className="rounded-md border border-border bg-page px-2 py-1.5 text-sm"
          >
            {ROLES.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>
        <Button busy={create.isPending}>Add</Button>
      </form>
      <div className="mt-2 text-xs text-muted-foreground">{ROLE_MEANING[role]}</div>
      <Note note={note} />
    </Card>
  )
}

function Input({
  label,
  value,
  onChange,
  type = 'text',
}: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-muted-foreground">{label}</span>
      <input
        type={type}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="rounded-md border border-border bg-page px-2 py-1.5 text-sm"
      />
    </label>
  )
}

function Button({
  busy,
  children,
  onClick,
}: {
  busy: boolean
  children: string
  onClick?: () => void
}) {
  return (
    <button
      type={onClick === undefined ? 'submit' : 'button'}
      onClick={onClick}
      disabled={busy}
      className={cn(
        'rounded-md px-3 py-1.5 text-sm font-medium',
        busy ? 'bg-border text-muted-foreground' : 'bg-primary text-primary-on',
      )}
    >
      {busy ? 'Working…' : children}
    </button>
  )
}

function Small({ children, onClick }: { children: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded-md border border-border px-2 py-1 text-xs hover:bg-page"
    >
      {children}
    </button>
  )
}

function Note({ note }: { note: { ok: boolean; text: string } | null }) {
  if (note === null) {
    return null
  }

  return (
    <div
      className={cn(
        'mt-2 rounded-md border px-2 py-1.5 text-xs',
        note.ok
          ? 'border-status-healthy-border bg-status-healthy-surface text-status-healthy-text'
          : 'border-status-critical-border bg-status-critical-surface text-status-critical-text',
      )}
    >
      {note.text}
    </div>
  )
}
