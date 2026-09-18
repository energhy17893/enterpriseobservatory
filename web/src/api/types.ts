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
