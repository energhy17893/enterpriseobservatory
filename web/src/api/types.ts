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

/**
 * What `EntityView.health` rests on (K3/ADR-0018/ADR-0026): the entity's own
 * alerts, an Unknown-only alert set, the collector's rollup when there is no
 * alert and the source is reporting, or nothing observed at all.
 */
export type HealthBasis =
  | 'NoAlertsSourceReporting'
  | 'NoAlertsSourceSilent'
  | 'Alerts'
  | 'UnknownAlerts'
  | 'NotObserved'

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
  /** What `health` rests on -- alerts, Unknown alerts, the collector, or nothing seen. */
  healthBasis: HealthBasis
  /** Whether the alerts setting the colour have all lost fresh evidence; the colour stands. */
  healthIsStale: boolean
  /** Since when the colour has not been rechecked; null while fresh. */
  healthStaleSinceUtc: string | null
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
  /** Datastores only: a fill date with its window, or why there is none. */
  timeToFull?: TimeToFullView | null
  /** Clusters only: the vSphere HA configuration, or null when unread. */
  haScorecard?: HaScorecardView | null
  /**
   * Clusters only: if the largest host fails, do the survivors still hold the
   * running VMs' demand, and until when. Null for a cluster with fewer than
   * two live hosts, which N+1 cannot be asked of.
   */
  clusterFailover?: ClusterFailoverView | null
}

/**
 * A cluster's HA configuration, in the platform's own words. Every field is
 * null when that one setting was not reported — see roadmap M8.1.
 */
export interface HaScorecardView {
  enabled: boolean | null
  admissionControlEnabled: boolean | null
  admissionControlPolicyType: string | null
  hostMonitoring: string | null
  vmMonitoring: string | null
  apdResponse: string | null
  pdlResponse: string | null
  heartbeatDatastoreCount: number | null
  heartbeatDatastoreCandidatePolicy: string | null
  /** True when vCenter's own network-redundancy warning has been silenced. */
  redundantNetworkWarningSilenced: boolean | null
  /** This cluster's eo-cont.ha-* continuity findings in every state (ADR-0024). */
  findings: ContinuityFindingView[]
}

/** One continuity finding as a card or report shows it. */
export interface ContinuityFindingView {
  controlId: string
  title: string
  /** Citable basis of the expectation, or 'product policy'. */
  source: string
  subject: string
  subjectLabel: string | null
  state: FindingState
  stale: boolean
  expected: string
  observed: string | null
  reason: string | null
  acceptedBy: string | null
  acceptedReason: string | null
  lastEvaluatedUtc: string
}

/**
 * A cluster's N+1 answer for CPU and for memory, each in host-equivalents --
 * a share of one host's own capacity, because no collector this product runs
 * reads a host's CPU or memory capacity in MHz or bytes. "Largest host" is
 * approximated as any one host, one host-equivalent of capacity, which is
 * exact on a cluster built from identical hosts (VMware's own recommendation
 * for a predictable HA cluster) and an approximation otherwise.
 */
export interface ClusterFailoverView {
  hostCount: number
  cpu: ClusterFailoverResourceView
  memory: ClusterFailoverResourceView
}

export interface ClusterFailoverResourceView {
  /** Null when this cluster's current demand could not be read from at least one host -- unknown, not "yes". */
  holdsNow: boolean | null
  demandHosts: number | null
  availableAfterFailoverHosts: number
  /** The date the guarantee is lost, or why there is none. Null only alongside a null holdsNow. */
  date: TimeToFullView | null
}

export interface TimeToFullView {
  isForecast: boolean
  fullAtUtc: string | null
  days: number | null
  growthBytesPerDay: number | null
  windowFromUtc: string | null
  windowToUtc: string | null
  pointsUsed: number
  /** Machine-readable refusal reason, e.g. TooFewPoints; null on a forecast. */
  reason: string | null
  /** The sentence the server says, forecast or refusal. */
  summary: string
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

/** One role's most recent monitoring cycle (Package D). */
export interface CycleMetricsView {
  atUtc: string | null
  durationSeconds: number
  /** `alert_history` rows this cycle appended. */
  transitionsAppended: number
  /** Verdicts clamped to Unknown by the age rule (ADR-0026 §Z3) this cycle. */
  ageClampedToUnknown: number
}

/**
 * The runner's own self-monitoring numbers, for the overview's own-health
 * card. See `/api/metrics`.
 */
export interface SelfMetricsView {
  generatedAtUtc: string
  inventory: CycleMetricsView
  observation: CycleMetricsView
  unknownAlerts: number
  openGaps: number
  unrecoverableGaps: number
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
  /** True when there is no collector for this kind yet -- not an error. */
  noCollector: boolean
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

/** An object a vCenter event names. */
export interface EventObjectView {
  name: string
  /** The entity page it would be on; null when no id can be formed. */
  entityId: string | null
}

/**
 * One event, as vCenter reported it.
 *
 * `typeId` is what it really is; `eventClass` is only the class it arrived as.
 * Every `esx.problem.*` arrives as the one class `EventEx`, so anything that
 * groups or filters must use `typeId`.
 */
export interface SourceEventView {
  sourceInstanceId: string
  key: number
  createdAtUtc: string
  eventClass: string
  typeId: string
  /** info, warning, error or user — when the event carried one. */
  severity: string | null
  message: string
  userName: string | null
  datacenterName: string | null
  computeResource: EventObjectView | null
  host: EventObjectView | null
  virtualMachine: EventObjectView | null
  datastore: EventObjectView | null
}

/** Where one source's event stream stands. */
export interface EventStreamView {
  sourceInstanceId: string
  lastAttemptUtc: string | null
  lastSuccessUtc: string | null
  /** Why the last attempt could not read; null when it could. */
  lastFailure: string | null
  /** When a read last stopped short, leaving events unread. */
  lastGapUtc: string | null
}

/** Recent vCenter events, with the state of the streams they came from. */
export interface EventFeedView {
  /** Newest first. */
  events: SourceEventView[]
  streams: EventStreamView[]
  retentionDays: number
}
// --- compliance ---------------------------------------------------------------

/**
 * A finding's state. Not an alert state: a finding never resolves itself. It
 * passes when the setting is fixed, is accepted when somebody owns it (still
 * non-compliant), or is excepted until a date.
 */
export type FindingState = 'Failing' | 'Passing' | 'NotEvaluated' | 'Accepted' | 'Excepted'

export interface FindingCountsView {
  failing: number
  passing: number
  notEvaluated: number
  accepted: number
  excepted: number
  /** Of the above, how many rest on a host whose source did not report last cycle. */
  stale: number
}

export interface ComplianceControlView {
  controlId: string
  title: string
  component: string
  priority: string
  parameter: string
  installationDefault: string
  baselineValue: string
  assessment: string
  /** The catalogue the control comes from: 'Broadcom SCG' or 'eo-continuity'. Not the citation. */
  source: string
  /** What the expectation/threshold rests on, shown as "basis:"; empty for vendor-guide controls (see lib/basis). */
  citation: string
  /** False when this product cannot judge the control at all. */
  evaluated: boolean
  notEvaluatedReason: string | null
  counts: FindingCountsView
}

export interface ComplianceExceptionView {
  id: string
  controlId: string
  /** Null means every entity the control applies to. */
  entityId: string | null
  /** Null means every subject of the control (and entity, if given). */
  subject: string | null
  reason: string
  owner: string
  createdBy: string
  createdAtUtc: string
  expiresUtc: string
  expired: boolean
  /** Who withdrew it; null while it stands. */
  removedBy: string | null
  removedAtUtc: string | null
}

/** Who publishes a catalogue's controls. */
export type CatalogueOwner = 'Broadcom' | 'Product' | 'Cisa'

/** How this product came to hold a catalogue's controls. */
export type CatalogueKind = 'Passthrough' | 'Computed' | 'Feed'

/**
 * One catalogue's scorecard: the posture screen's row above the grouped
 * control sections (K3 §1.1). NotEvaluated is always shown, never folded
 * into `coverage` (ADR-0026) -- a zero counter is not hidden.
 */
export interface CatalogueScorecardView {
  /** Stable across editions of the same catalogue -- 'scg' or 'eo-continuity'. */
  id: string
  name: string
  owner: CatalogueOwner
  kind: CatalogueKind
  /** The specific version findings were judged against. */
  release: string
  /** Why this catalogue could not be loaded, when it could not be; counts are then all zero. */
  problem: string | null
  counts: FindingCountsView
  /** Passed + failing + accepted + excepted -- subjects this product reached a verdict on. */
  evaluableSubjects: number
  /** Every subject the catalogue's controls apply to, evaluated or not. */
  totalSubjects: number
  /** evaluableSubjects / totalSubjects; null when there are no subjects at all. */
  coverage: number | null
}

export interface ComplianceView {
  /** The vendor guide's source, found by ownership, not by list position; each control also carries its own. */
  source: string
  catalogueName: string
  catalogueRelease: string
  catalogueProblem: string | null
  /** Every loaded catalogue's scorecard, in registration order -- not "the vendor guide first". */
  catalogues: CatalogueScorecardView[]
  defaultControlsSkipped: number
  controls: ComplianceControlView[]
  totals: FindingCountsView
  exceptions: ComplianceExceptionView[]
  lastEvaluatedUtc: string | null
}

export interface ComplianceFindingView {
  controlId: string
  catalogueRelease: string
  entityId: string
  entityName: string
  /** What on the entity the finding is about; empty for the entity itself. */
  subject: string
  /** How the subject is shown; display only. */
  subjectLabel: string | null
  state: FindingState
  reason: string | null
  observed: string | null
  expected: string
  firstSeenUtc: string
  /** When the evidence was read: the host's last-seen time. */
  lastEvaluatedUtc: string
  /** The host's source did not report last cycle: the last verdict reached, not a current one. */
  stale: boolean
  acceptedBy: string | null
  acceptedAtUtc: string | null
  acceptedReason: string | null
  exceptionId: string | null
}

// --- reports --------------------------------------------------------------
//
// M5.1's shape: JSON for the SPA's own printable page, CSV (built server
// side, fetched as a plain download) for a spreadsheet. M5.2 and M5.3 are
// expected to add a sibling *ReportRow/*ReportView pair here.

export interface AlertReportRow {
  severity: AlertSeverity
  title: string
  entityName: string | null
  entityKind: EntityKind | null
  category: string
  source: string
  state: AlertLifecycleState
  firstSeenUtc: string
  lastSeenUtc: string
  acknowledgedBy: string | null
  acknowledgedAtUtc: string | null
  /** Only set for an operator's own clear, not a condition going away on its own. */
  clearedBy: string | null
  clearedAtUtc: string | null
  isDerived: boolean
}

export interface AlertReportSummary {
  bySeverity: Record<string, number>
  byState: Record<string, number>
  total: number
}

export interface AlertReportView {
  generatedAtUtc: string
  fromUtc: string
  toUtc: string
  summary: AlertReportSummary
  rows: AlertReportRow[]
}

// --- compliance report (M5.2) ----------------------------------------------
//
// The auditor-facing sibling of the alert report. Every finding carries the
// exception or acceptance that covers it in full -- a report stands alone,
// printed or read a year later, so a bare id into a list it does not carry
// would not do.

export interface ComplianceReportFindingRow {
  controlId: string
  controlTitle: string
  priority: string
  entityId: string
  entityName: string
  state: FindingState
  /** Set only when state is NotEvaluated. */
  notEvaluatedReason: string | null
  observed: string | null
  expected: string
  firstSeenUtc: string
  lastEvaluatedUtc: string
  stale: boolean
  acceptedBy: string | null
  acceptedAtUtc: string | null
  acceptedReason: string | null
  exceptionId: string | null
  exceptionOwner: string | null
  exceptionReason: string | null
  exceptionCreatedBy: string | null
  exceptionCreatedAtUtc: string | null
  exceptionExpiresUtc: string | null
}

export interface ComplianceReportTransitionRow {
  controlId: string
  /** The finding may since have left the evaluation, so this is not always resolvable to a name. */
  entityId: string
  /** Null when the finding was new at this change. */
  from: 'Failing' | 'Passing' | 'NotEvaluated' | null
  /** Null when the finding left the evaluation at this change. */
  to: 'Failing' | 'Passing' | 'NotEvaluated' | null
  observed: string | null
  atUtc: string
}

export interface ComplianceReportView {
  generatedAtUtc: string
  catalogueName: string
  catalogueRelease: string
  /** "All hosts", or what the caller scoped the report to. */
  scope: string
  lastEvaluatedUtc: string | null
  staleCount: number
  /** The hosts behind staleCount, by name. */
  staleEntityNames: string[]
  /** Every control the catalogue names, evaluated or not -- filter by !evaluated for the "not evaluated" section. */
  controls: ComplianceControlView[]
  totals: FindingCountsView
  findings: ComplianceReportFindingRow[]
  /** The exceptions standing now, in scope. */
  exceptions: ComplianceExceptionView[]
  /** Withdrawn exceptions -- audit evidence in their own right. */
  removedExceptions: ComplianceExceptionView[]
  historyFromUtc: string
  historyToUtc: string
  history: ComplianceReportTransitionRow[]
  /** Whether more transitions matched the scope and period than the store returned; history is a prefix, not the whole match. */
  historyTruncated: boolean
}

/** One datastore on the capacity report (M5.3): its latest reading and the same fill-date answer its own page shows. */
export interface CapacityReportRow {
  name: string
  /** VMFS, NFS, vsan and so on. Null when not read. */
  datastoreType: string | null
  source: string
  /** The latest reading of each, or null when never recorded -- never a zero standing in for "not looked". */
  capacityBytes: number | null
  usedBytes: number | null
  freeBytes: number | null
  percentUsed: number | null
  /** Used plus what has been promised to thin disks. Null when uncommitted space was never read. */
  provisionedBytes: number | null
  /** provisionedBytes over capacityBytes. Above 1 is over-committed. */
  overcommitRatio: number | null
  timeToFull: TimeToFullView
}

export interface CapacityReportSummary {
  totalDatastores: number
  totalCapacityBytes: number
  totalUsedBytes: number
  totalFreeBytes: number
  /** Filling inside 30 days, the product's warning threshold. */
  fillingWithin30Days: number
  /** Filling inside 7 days, the product's critical threshold. */
  fillingWithin7Days: number
  overcommittedCount: number
  /** No fill-date estimate yet -- a refusal is counted here, never left out. */
  noEstimateCount: number
  /** Why, keyed by the machine-readable reason, e.g. WindowTooShort. */
  noEstimateByReason: Record<string, number>
}

export interface CapacityReportView {
  generatedAtUtc: string
  summary: CapacityReportSummary
  rows: CapacityReportRow[]
}

// --- continuity report (M8.10) -------------------------------------------

/** Findings of one group counted by state; a stale finding also counts in stale. */
export interface ContinuityStateCounts {
  failing: number
  accepted: number
  excepted: number
  notEvaluated: number
  passing: number
  stale: number
}

/** Where a control's findings sit in the report, from the entity kind its check applies to. */
export type ContinuityReportScope = 'VCenter' | 'Cluster' | 'Entity'

/** One control of the eo-continuity catalogue as the report names it. */
export interface ContinuityControlInfo {
  controlId: string
  title: string
  /** What the expectation rests on; shown as "basis:", never hidden -- see lib/basis. */
  citation: string
  appliesTo: EntityKind
  scope: ContinuityReportScope
}

export interface ContinuityControlCounts {
  controlId: string
  counts: ContinuityStateCounts
}

/** One cluster's continuity posture, from the eo-continuity findings (ADR-0024). */
export interface ContinuityReportRow {
  clusterId: string
  clusterName: string
  source: string
  /** Whether this cluster's own inventory carried a dasConfig.* setting -- whether HA was actually read. */
  haSettingsCollected: boolean
  /** Every cluster-level control, in catalogue order. */
  controls: ContinuityControlCounts[]
  /** Findings of hosts, VMs and datastores under this cluster (containment edges). */
  contained: ContinuityStateCounts
  /** Entities under it with a failing, accepted or excepted finding: at most ten. */
  containedAffectedNames: string[]
  containedAffectedMore: number
  totals: ContinuityStateCounts
  hasFailing: boolean
}

/** One host/VM/datastore control, summarised on one row -- never one row per entity. */
export interface ContinuityControlRow {
  controlId: string
  title: string
  citation: string
  appliesTo: EntityKind
  counts: ContinuityStateCounts
  /** At most ten failing entities by name; the rest are counted in moreFailing. */
  failingNames: string[]
  moreFailing: number
}

export interface ContinuityAlarmView {
  title: string
  severity: AlertSeverity
  state: AlertLifecycleState
  isStale: boolean
  firstSeenUtc: string
}

/** One vCenter: its own controls (certificate) and the alarms raised on it. */
export interface ContinuityVCenterSection {
  vCenterId: string
  vCenterName: string
  source: string
  controls: ContinuityControlCounts[]
  findings: ContinuityFindingView[]
  alarms: ContinuityAlarmView[]
}

export interface ContinuityReportSummary {
  totalClusters: number
  /** False: the continuity checks have not run yet, so zeros mean "not looked at". */
  evaluated: boolean
  /** Per eo-cont.* control. */
  byControl: Record<string, ContinuityStateCounts>
  totals: ContinuityStateCounts
  clustersWithFailingCount: number
  clustersWithFailingNames: string[]
  /** False means the HA/DRS configuration has not been read on this estate yet. */
  haInputsCollected: boolean
  /** Set when the checks have not run or HA/DRS inputs were never read. */
  note: string | null
}

export interface ContinuityReportView {
  generatedAtUtc: string
  summary: ContinuityReportSummary
  /** Every catalogue control, in catalogue order, with where it sits. */
  controls: ContinuityControlInfo[]
  vCenters: ContinuityVCenterSection[]
  rows: ContinuityReportRow[]
  controlRows: ContinuityControlRow[]
}
export interface AddExceptionCommand {
  controlId: string
  /** Null for every entity the control applies to. */
  entityId: string | null
  /** Null or empty for every subject of the control (and entity, if given). */
  subject?: string | null
  reason: string
  owner: string
  expiresUtc: string
}

// --- scheduled email reports (M5.4) --------------------------------------------

export type SmtpTlsMode = 'None' | 'StartTls' | 'Implicit'

/**
 * The installation's SMTP settings.
 *
 * There is no password field here and no endpoint that returns one.
 * `passwordStatus` is the string 'set' or 'not set' — never the value.
 */
export interface SmtpSettingsView {
  host: string
  port: number
  tlsMode: SmtpTlsMode
  fromAddress: string
  username: string
  allowUnencrypted: boolean
  passwordStatus: 'set' | 'not set'
  passwordSetUtc: string | null
  isConfigured: boolean
}

export interface SmtpSettingsCommand {
  host: string
  port: number
  tlsMode: SmtpTlsMode
  fromAddress: string
  username: string
  /** Empty means "keep the stored password". */
  password: string
  allowUnencrypted: boolean
}

export interface TestEmailCommand extends SmtpSettingsCommand {
  to: string
}

export interface MailTestView {
  succeeded: boolean
  detail: string
}

export type ReportFrequency = 'Daily' | 'Weekly'

export type ReportKind = 'Alerts' | 'Compliance' | 'Capacity' | 'Continuity'

export interface ReportSubscriptionView {
  id: string
  recipients: string[]
  frequency: ReportFrequency
  dayOfWeek: string
  hourLocal: number
  timeZoneId: string
  kind: ReportKind
  isEnabled: boolean
  lastSentUtc: string | null
  lastError: string | null
  createdBy: string
  createdUtc: string
  lastModifiedBy: string | null
  lastModifiedUtc: string | null
}

export interface ReportSubscriptionCommand {
  recipients: string[]
  frequency: ReportFrequency
  dayOfWeek: string
  hourLocal: number
  timeZoneId: string
  kind: ReportKind
  isEnabled: boolean
}

// --- first-run setup and the Database card (G-DB) -----------------------

/** What the setup page starts from: Database:* from configuration, if any. */
export interface SetupStateView {
  setupRequired: boolean
  host: string
  port: number
  database: string
  username: string
  schema: string
  requireTls: boolean
  adminUsername: string
}

/**
 * What the setup form sends. `adminPassword` is used once, in that request,
 * and never stored; `password` is only for an existing database a DBA made.
 */
export interface SetupCommand {
  mode: 'create' | 'existing'
  host: string
  port: number
  database: string
  username: string
  schema: string
  requireTls: boolean
  adminUsername: string
  adminPassword: string
  password: string
}

export interface SetupResultView {
  succeeded: boolean
  detail: string
  restartRequired: boolean
  nextStep: string | null
}

/** The database connection, as the card shows it. There is no password field. */
export interface DatabaseView {
  host: string
  port: number
  database: string
  username: string
  schema: string
  requireTls: boolean
  source: 'ProtectedFile' | 'Configuration'
  canRotate: boolean
  rotateRefusal: string | null
  passwordSetUtc: string | null
}

export interface DatabaseActionView {
  succeeded: boolean
  detail: string
}
