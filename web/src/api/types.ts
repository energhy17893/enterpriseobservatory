/**
 * The API contract, as the interface sees it.
 *
 * Hand-written rather than generated, for now. It is small enough to keep in
 * step by reading, and a generator would be one more thing in the build before
 * there is enough surface to justify it. When the contract grows past what a
 * person can hold, this file should be generated from the OpenAPI document
 * rather than maintained.
 */

export type HealthState = 'Unknown' | 'Healthy' | 'Warning' | 'Critical'

export type AlertSeverity = 'Info' | 'Warning' | 'Critical'

export type AlertLifecycleState = 'Open' | 'Acknowledged' | 'Silenced' | 'Resolved'

export type ObservationState = 'Active' | 'InMaintenance' | 'Vanished'

export type EntityKind =
  | 'Unknown'
  | 'VCenter'
  | 'Cluster'
  | 'EsxiHost'
  | 'VirtualMachine'
  | 'Datastore'
  | 'ResourcePool'
  | 'PhysicalServer'
  | 'Bmc'
  | 'HardwareComponent'
  | 'HbaPort'
  | 'SanSwitch'
  | 'SanSwitchPort'
  | 'StorageArray'
  | 'ArrayPort'
  | 'Lun'
  | 'ManagementAppliance'
  | 'CollectorInstance'

/**
 * The closed vocabulary from ADR-0004. Adding a kind requires an ADR, which is
 * also what makes exhaustively handling them here safe.
 */
export type RelationshipKind =
  | 'PartOf'
  | 'RunsOn'
  | 'SameAs'
  | 'ConnectedTo'
  | 'BackedBy'
  | 'ManagedBy'

export interface Page<T> {
  items: T[]
  total: number
  offset: number
  limit: number
}

export interface AlertView {
  fingerprint: string
  severity: AlertSeverity
  state: AlertLifecycleState
  title: string
  description: string
  category: string
  source: string
  entityId: string | null
  entityName: string | null
  isDerived: boolean
  /** Set when a maintenance window is suppressing notification, not the alert. */
  suppressedByWindowId: string | null
  firstSeenUtc: string
  lastSeenUtc: string
}

export interface EntityView {
  id: string
  kind: EntityKind
  displayName: string
  /** Effective health: an entity we cannot see is Unknown, not its last colour. */
  health: HealthState
  observationState: ObservationState
  source: string
  lastSeenUtc: string
  alertCount: number
}

export interface RelationshipView {
  kind: RelationshipKind
  isOutgoing: boolean
  otherId: string
  otherName: string
  otherKind: EntityKind
  otherHealth: HealthState
}

export interface IdentityMarkView {
  kind: string
  value: string
  source: string
}

export interface EntityDetailView {
  entity: EntityView
  marks: IdentityMarkView[]
  relationships: RelationshipView[]
  alerts: AlertView[]
}

export interface CollectorView {
  instanceId: string
  role: 'Inventory' | 'Observation'
  health: HealthState
  consecutiveFailures: number
  isBackingOff: boolean
  lastSuccessUtc: string | null
  lastFailureDetail: string | null
  partialFailures: PartialFailureView[]
}

export interface OverviewView {
  generatedAtUtc: string
  criticalAlerts: number
  warningAlerts: number
  unacknowledgedAlerts: number
  suppressedAlerts: number
  entitiesByHealth: Record<HealthState, number>
  vanishedEntities: number
  failingCollectors: number
  /** The oldest successful collector read: how current this screen really is. */
  oldestSuccessfulReadUtc: string | null
}

export type SeriesResolution = 'Raw' | 'FiveMinutes' | 'OneHour'

export type RollupType =
  | 'Unknown'
  | 'Average'
  | 'Latest'
  | 'Summation'
  | 'Maximum'
  | 'Minimum'
  | 'None'

export interface SeriesOptionView {
  counter: string
  /** The device, or empty for the aggregate across devices. */
  instance: string
}

/**
 * One point. Five numbers rather than one, because drawing only the average
 * hides the two minutes at 100% the operator is looking for.
 */
export interface SeriesPointView {
  atUtc: string
  min: number
  max: number
  average: number
  last: number
  count: number
}

export interface SeriesView {
  entityId: string
  counter: string
  instance: string
  /** Whether this counter has ever been recorded — distinct from an empty window. */
  exists: boolean
  /** Always shown in the UI: an hourly average must not read as a live figure. */
  resolution: SeriesResolution
  unit: string
  rollup: RollupType
  truncated: boolean
  /** Oldest first. Missing buckets are missing, never zero. */
  points: SeriesPointView[]
}

/** What an operator's command did. */
export interface AlertActionView {
  applied: boolean
  alert: AlertView | null
  refusal: string | null
  /** Who it was recorded as — echoed back so nobody has to assume. */
  recordedAs: string
  actorVerified: boolean
}

export type Role = 'Viewer' | 'Operator' | 'Administrator'

/** Who the caller is, and what this installation still needs. */
export interface AuthStateView {
  signedIn: boolean
  username: string | null
  role: Role | null
  /** True only before anyone has claimed the installation. */
  needsSetup: boolean
  minimumPasswordLength: number
}

/** An account, as the interface lists it. No verifier, not even redacted. */
export interface AccountView {
  username: string
  role: Role
  createdUtc: string
  lastSignedInUtc: string | null
  lockedOut: boolean
}

/** Planned work. Suppresses notification, never observation. */
export interface MaintenanceWindowView {
  id: string
  title: string
  reason: string
  startUtc: string
  endUtc: string
  declaredBy: string
  declaredAtUtc: string
  /** Empty means the whole estate. */
  entities: string[]
  active: boolean
  scheduled: boolean
}

export interface DeclareWindowCommand {
  title: string
  reason: string
  startUtc: string
  endUtc: string
  entities: string[]
}

/** What a command against several alerts did. */
export interface BulkActionView {
  applied: boolean
  requested: number
  /** How many were no longer there — reported, not hidden. */
  missing: number
  alerts: AlertView[]
  refusal: string | null
  recordedAs: string
}

/** One step of the path that proves a group. */
export interface CorrelationLinkView {
  fromId: string
  fromName: string
  kind: RelationshipKind
  toId: string
  toName: string
}

/** Several alerts that are one thing going wrong. */
export interface EventView {
  id: string
  title: string
  severity: AlertSeverity
  rootId: string
  rootName: string
  /** The real counts, for the header. Never a sample. */
  alertCount: number
  entityCount: number
  firstSeenUtc: string
  lastSeenUtc: string
  alerts: AlertView[]
  /** Why these were grouped. */
  explanation: CorrelationLinkView[]
}

/**
 * Alerts that started together and may be one thing.
 *
 * Deliberately not an EventView: the evidence is only that they appeared
 * together, so the interface offers it rather than folding it.
 */
export interface SuggestionView {
  withinUtc: string
  windowSeconds: number
  alerts: AlertView[]
}

export interface EventBoardView {
  events: EventView[]
  suggestions: SuggestionView[]
  ungrouped: AlertView[]
  /** Every visible alert, so the two views can be checked against each other. */
  totalAlerts: number
}

/** How a connection was configured. A configured one cannot be edited here. */
export type ConnectionOrigin = 'Managed' | 'Configuration'

/**
 * A connection the product reads from.
 *
 * There is no password on this type and no endpoint that returns one. That is
 * deliberate rather than missing: a credential that can be read back out is one
 * that leaks through a screenshot or a support session.
 */
export interface ConnectionView {
  instanceId: string
  kind: string
  baseAddress: string
  username: string
  acceptUntrustedCertificate: boolean
  pageSize: number
  isEnabled: boolean
  origin: ConnectionOrigin
  hasPassword: boolean
  passwordUnreadable: boolean
  passwordSetUtc: string | null
  createdUtc: string
  createdBy: string
}

export interface ConnectionCommand {
  instanceId: string
  kind: string
  baseAddress: string
  username: string
  /** Empty when editing means "keep the stored password". */
  password: string
  acceptUntrustedCertificate: boolean
  pageSize: number
  isEnabled: boolean
}

export interface ProbeView {
  succeeded: boolean
  detail: string
  identified: string | null
  /** Shown differently: the next attempt may lock the account out. */
  credentialsRejected: boolean
}

/** One thing a collector reached but could not read. */
export interface PartialFailureView {
  kind: string
  /** The counter, device or endpoint. The half that makes it actionable. */
  target: string
  detail: string
}

/**
 * What one source managed to read.
 *
 * Beside the collectors rather than on a screen of its own, because it answers
 * the same question from the other side: health says whether a source
 * answered, this says what was in the answer. A perfectly healthy collector
 * can be blind to half of what the product reasons about without anything
 * appearing to go wrong.
 */
export interface CoverageView {
  instanceId: string
  /** Carried rather than implied: a stale number without a timestamp reads as current. */
  measuredAtUtc: string
  properties: CoveragePropertyView[]
}

export interface CoveragePropertyView {
  objectType: string
  property: string
  asked: number
  answered: number
  /** Computed on the server so the screen cannot drift from the rule that alerts. */
  isBlind: boolean
}
