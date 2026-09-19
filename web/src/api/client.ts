import type {
  AlertView,
  CollectorView,
  EntityDetailView,
  EntityView,
  OverviewView,
  Page,
  SeriesOptionView,
  SeriesView,
} from './types'
import type { AlertActionView } from './types'

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
  if (response.status === 403 || response.status === 404 || response.status === 400) {
    const detail = await response.json().catch(() => null)
    throw new ApiError(messageFor(response.status, detail), response.status)
  }

  if (!response.ok) {
    throw new ApiError(`The server answered ${response.status}.`, response.status)
  }

  return (await response.json()) as T
}

function messageFor(status: number, detail: unknown): string {
  if (detail !== null && typeof detail === 'object') {
    const body = detail as { detail?: string; refusal?: string }

    if (typeof body.detail === 'string') return body.detail
    if (body.refusal === 'NotFound') return 'That alert is no longer firing.'
    if (body.refusal === 'DeadlineInThePast') return 'A silence has to end in the future.'
  }

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
  seriesFor: (entityId: string) =>
    get<SeriesOptionView[]>(`/api/entities/${encodeURIComponent(entityId)}/series`),
  acknowledge: (fingerprint: string) =>
    post<AlertActionView>('/api/alerts/acknowledge', { fingerprint }),
  clear: (fingerprint: string) => post<AlertActionView>('/api/alerts/clear', { fingerprint }),
  silence: (fingerprint: string, untilUtc: string) =>
    post<AlertActionView>('/api/alerts/silence', { fingerprint, untilUtc }),
  series: (entityId: string, counter: string, query: SeriesQuery = {}) =>
    get<SeriesView>(
      `/api/entities/${encodeURIComponent(entityId)}/series/${encodeURIComponent(counter)}`,
      query,
    ),
}
