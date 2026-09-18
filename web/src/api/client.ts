import type {
  AlertView,
  CollectorView,
  EntityDetailView,
  EntityView,
  OverviewView,
  Page,
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

export interface AlertQuery extends Query {
  severity?: string
  state?: string
  category?: string
  source?: string
  search?: string
  offset?: number
  limit?: number
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
}
