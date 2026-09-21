import type {
  AlertView,
  CollectorView,
  CoverageView,
  EntityDetailView,
  EntityView,
  OverviewView,
  Page,
  SeriesOptionView,
  SeriesView,
} from './types'
import type {
  AccountView,
  ConnectionCommand,
  ConnectionView,
  ProbeView,
  AlertActionView,
  AlertReportView,
  AuthStateView,
  CapacityReportView,
  BulkActionView,
  DeclareWindowCommand,
  EventBoardView,
  EventFeedView,
  MaintenanceWindowView,
  Role,
  AddExceptionCommand,
  ComplianceExceptionView,
  ComplianceFindingView,
  ComplianceView,
  FindingState,
} from './types'

/**
 * A failed request, carrying enough to say something true about it.
 *
 * The interface must be able to tell "the server said no" from "the server is
 * not there", because they mean different things to an operator and the second
 * one means everything on screen is now stale.
 */
export class ApiError extends Error {
  /** The HTTP status, or null when the request never got an answer at all. */
  readonly status: number | null

  constructor(message: string, status: number | null, options?: ErrorOptions) {
    super(message, options)
    this.name = 'ApiError'
    this.status = status
  }
}

type QueryValue = string | number | boolean | null | undefined
type Query = Record<string, QueryValue>

async function get<T>(path: string, query: Query = {}): Promise<T> {
  const url = new URL(path, window.location.origin)

  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') {
      url.searchParams.set(key, String(value))
    }
  }

  let response: Response

  try {
    // Same origin, so the session cookie travels without any token handling in
    // the client. See ADR-0006.
    response = await fetch(url, { headers: { Accept: 'application/json' } })
  } catch (cause) {
    throw new ApiError('The server could not be reached.', null, { cause })
  }

  if (!response.ok) {
    throw new ApiError(`The server answered ${response.status}.`, response.status)
  }

  return (await response.json()) as T
}

async function post<T>(path: string, body: unknown): Promise<T> {
  let response: Response

  try {
    response = await fetch(new URL(path, window.location.origin), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
    })
  } catch (cause) {
    throw new ApiError('The server could not be reached.', null, { cause })
  }

  // A refusal carries a body explaining itself — "not signed in", "that alert
  // is gone" — and the caller needs it more than it needs an exception.
  if ([400, 403, 404, 409].includes(response.status)) {
    const detail = await response.json().catch(() => null)
    throw new ApiError(messageFor(response.status, detail), response.status)
  }

  if (response.status === 401 || response.status === 429) {
    const detail = await response.json().catch(() => null)
    throw new ApiError(messageFor(response.status, detail), response.status)
  }

  if (!response.ok) {
    throw new ApiError(`The server answered ${response.status}.`, response.status)
  }

  // 204, from signing out.
  return (response.status === 204 ? null : await response.json()) as T
}

/** PUT and DELETE, which only the connection screens need. */
async function send<T>(method: 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
  let response: Response

  try {
    response = await fetch(new URL(path, window.location.origin), {
      method,
      headers: body === undefined
        ? { Accept: 'application/json' }
        : { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch (cause) {
    throw new ApiError('The server could not be reached.', null, { cause })
  }

  if (!response.ok) {
    const detail = await response.json().catch(() => null)
    throw new ApiError(messageFor(response.status, detail), response.status)
  }

  return (response.status === 204 ? null : await response.json()) as T
}

function messageFor(status: number, detail: unknown): string {
  if (detail !== null && typeof detail === 'object') {
    const body = detail as { detail?: string; refusal?: string }

    if (typeof body.detail === 'string') return body.detail
    if (body.refusal === 'NotFound') return 'That alert is no longer firing.'
    if (body.refusal === 'DeadlineInThePast') return 'A silence has to end in the future.'
  }

  if (status === 403) return 'Your account is not allowed to do that.'
  if (status === 401) return 'You are not signed in.'

  return `The server answered ${status}.`
}

export interface AlertQuery extends Query {
  severity?: string
  state?: string
  category?: string
  source?: string
  search?: string
  offset?: number
  limit?: number
}

export interface AlertReportQuery extends Query {
  severity?: string
  state?: string
  category?: string
  source?: string
  from?: string
  to?: string
}

export interface SeriesQuery extends Query {
  instance?: string
  from?: string
  to?: string
  maxPoints?: number
}

export interface EntityQuery extends Query {
  kind?: string
  health?: string
  source?: string
  search?: string
  includeVanished?: boolean
  offset?: number
  limit?: number
}

export const api = {
  overview: () => get<OverviewView>('/api/overview'),
  alerts: (query: AlertQuery = {}) => get<Page<AlertView>>('/api/alerts', query),
  entities: (query: EntityQuery = {}) => get<Page<EntityView>>('/api/entities', query),
  entity: (id: string) => get<EntityDetailView>(`/api/entities/${encodeURIComponent(id)}`),
  collectors: () => get<CollectorView[]>('/api/collectors'),
  coverage: () => get<CoverageView[]>('/api/coverage'),
  seriesFor: (entityId: string) =>
    get<SeriesOptionView[]>(`/api/entities/${encodeURIComponent(entityId)}/series`),
  authState: () => get<AuthStateView>('/api/auth/state'),
  signIn: (username: string, password: string) =>
    post<AuthStateView>('/api/auth/signin', { username, password }),
  signOut: () => post<null>('/api/auth/signout', {}),
  bootstrap: (token: string, username: string, password: string) =>
    post<AuthStateView>('/api/auth/bootstrap', { token, username, password }),
  events: () => get<EventBoardView>('/api/events'),
  vcenterEvents: (source?: string) =>
    get<EventFeedView>('/api/vcenter-events', { source, limit: 200 }),
  maintenanceWindows: () => get<MaintenanceWindowView[]>('/api/maintenance'),
  declareMaintenanceWindow: (command: DeclareWindowCommand) =>
    post<MaintenanceWindowView>('/api/maintenance/declare', command),
  endMaintenanceWindow: (id: string) =>
    post<MaintenanceWindowView>('/api/maintenance/end', { id }),
  accounts: () => get<AccountView[]>('/api/accounts'),
  createAccount: (username: string, password: string, role: Role) =>
    post<AccountView>('/api/accounts/create', { username, password, role }),
  changeOwnPassword: (currentPassword: string, newPassword: string) =>
    post<AccountView>('/api/accounts/password', { currentPassword, newPassword }),
  resetPassword: (username: string, newPassword: string) =>
    post<AccountView>('/api/accounts/reset-password', { username, newPassword }),
  changeRole: (username: string, role: Role) =>
    post<AccountView>('/api/accounts/role', { username, role }),
  removeAccount: (username: string) =>
    post<AccountView>('/api/accounts/remove', { username }),
  acknowledgeMany: (fingerprints: string[]) =>
    post<BulkActionView>('/api/alerts/acknowledge-many', { fingerprints }),
  clearMany: (fingerprints: string[]) =>
    post<BulkActionView>('/api/alerts/clear-many', { fingerprints }),
  silenceMany: (fingerprints: string[], untilUtc: string) =>
    post<BulkActionView>('/api/alerts/silence-many', { fingerprints, untilUtc }),
  acknowledge: (fingerprint: string) =>
    post<AlertActionView>('/api/alerts/acknowledge', { fingerprint }),
  clear: (fingerprint: string) => post<AlertActionView>('/api/alerts/clear', { fingerprint }),
  silence: (fingerprint: string, untilUtc: string) =>
    post<AlertActionView>('/api/alerts/silence', { fingerprint, untilUtc }),
  connections: () => get<ConnectionView[]>('/api/connections'),
  addConnection: (command: ConnectionCommand) =>
    post<ConnectionView>('/api/connections', command),
  updateConnection: (instanceId: string, command: ConnectionCommand) =>
    send<ConnectionView>('PUT', `/api/connections/${encodeURIComponent(instanceId)}`, command),
  removeConnection: (instanceId: string) =>
    send<ConnectionView>('DELETE', `/api/connections/${encodeURIComponent(instanceId)}`),

  // Takes the form's contents, not the stored connection: the whole point is
  // finding out whether a password works before saving it.
  testConnection: (command: ConnectionCommand) =>
    post<ProbeView>('/api/connections/test', command),
  compliance: () => get<ComplianceView>('/api/compliance'),
  complianceFindings: (query: { control?: string; entity?: string; state?: FindingState } = {}) =>
    get<ComplianceFindingView[]>('/api/compliance/findings', query),
  acceptFinding: (controlId: string, entityId: string, reason: string) =>
    post<ComplianceFindingView>('/api/compliance/accept', { controlId, entityId, reason }),
  addComplianceException: (command: AddExceptionCommand) =>
    post<ComplianceExceptionView>('/api/compliance/exceptions', command),
  removeComplianceException: (id: string) =>
    post<ComplianceExceptionView>('/api/compliance/exceptions/remove', { id }),
  series: (entityId: string, counter: string, query: SeriesQuery = {}) =>
    get<SeriesView>(
      `/api/entities/${encodeURIComponent(entityId)}/series/${encodeURIComponent(counter)}`,
      query,
    ),
  alertsReport: (query: AlertReportQuery = {}) => get<AlertReportView>('/api/reports/alerts', query),

  // A URL, not a fetch: the CSV is a download the browser handles itself, not
  // JSON this client parses. Built the same way `get` builds its URL so the
  // two never drift apart on which params they accept.
  alertsReportCsvUrl: (query: AlertReportQuery = {}) => {
    const url = new URL('/api/reports/alerts.csv', window.location.origin)
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== '') {
        url.searchParams.set(key, String(value))
      }
    }
    return url.toString()
  },

  capacityReport: () => get<CapacityReportView>('/api/reports/capacity'),
  capacityReportCsvUrl: () => new URL('/api/reports/capacity.csv', window.location.origin).toString(),
}
