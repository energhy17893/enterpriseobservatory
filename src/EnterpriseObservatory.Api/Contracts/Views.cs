using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Contracts;

/// <summary>
/// A page of results.
/// </summary>
/// <remarks>
/// Paged rather than complete because an estate has thousands of virtual
/// machines and the previous product rendered every row into the DOM. The total
/// is carried alongside so the client can say "1–50 of 2,184" rather than
/// discovering the end by running out.
/// </remarks>
public sealed record Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Total { get; init; }

    public required int Offset { get; init; }

    public required int Limit { get; init; }
}

/// <summary>
/// An alert as the inbox shows it.
/// </summary>
/// <remarks>
/// Projected from the one set of lifecycle instances. Every surface — the
/// inbox, an entity page, a vendor deep view — reads this same projection, so
/// an alert cannot appear acknowledged on one screen and open on another. That
/// was a real defect in the previous product and is what ADR-0007 exists to
/// prevent.
/// </remarks>
public sealed record AlertView
{
    public required string Fingerprint { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required AlertLifecycleState State { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>What kind of problem this is, e.g. Hardware or Configuration.</summary>
    public required string Category { get; init; }

    /// <summary>Which collector or subsystem reported it.</summary>
    /// <remarks>
    /// The filter axis vendor pages are built on. ADR-0007 keeps the vendor out
    /// of the top-level navigation and keeps it here, where it belongs: a way
    /// of narrowing one model rather than a separate model.
    /// </remarks>
    public required string Source { get; init; }

    /// <summary>The entity this is about, when it resolves to one.</summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// That entity's display name, resolved here rather than by the client.
    /// </summary>
    /// <remarks>
    /// A client that resolved names itself would need the whole graph to render
    /// a list of ten alerts, and would show a raw identifier whenever it did
    /// not have it.
    /// </remarks>
    public string? EntityName { get; init; }

    /// <summary>Whether the platform inferred this rather than observing it.</summary>
    public required bool IsDerived { get; init; }

    /// <summary>
    /// Set when a maintenance window is suppressing notification.
    /// </summary>
    /// <remarks>
    /// The alert is still shown. Maintenance stops the paging, not the
    /// reporting: hiding it would mean an operator working in a window cannot
    /// see what they are doing.
    /// </remarks>
    public string? SuppressedByWindowId { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>An entity as the explorer lists it (tier 1 of ADR-0007).</summary>
public sealed record EntityView
{
    public required string Id { get; init; }

    public required EntityKind Kind { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Health as it should be reported, not as last observed.
    /// </summary>
    /// <remarks>
    /// An entity we cannot currently see is Unknown whatever colour it was when
    /// we last saw it. See <see cref="Entity.EffectiveHealth"/> and product
    /// principle 1.
    /// </remarks>
    public required HealthState Health { get; init; }

    public required ObservationState ObservationState { get; init; }

    /// <summary>Which collector reported it.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Visible alerts currently scoped to this entity.</summary>
    public required int AlertCount { get; init; }
}

/// <summary>One edge, from the point of view of the entity being looked at.</summary>
public sealed record RelationshipView
{
    public required RelationshipKind Kind { get; init; }

    /// <summary>
    /// Whether this entity is the subject or the object of the relationship.
    /// </summary>
    /// <remarks>
    /// Direction is meaning, not presentation: "this VM runs on that host" and
    /// "this host runs that VM" are different sentences and the client must not
    /// have to guess which one it is holding.
    /// </remarks>
    public required bool IsOutgoing { get; init; }

    public required string OtherId { get; init; }

    public required string OtherName { get; init; }

    public required EntityKind OtherKind { get; init; }

    public required HealthState OtherHealth { get; init; }
}

/// <summary>An entity with everything its own page needs (tier 2 of ADR-0007).</summary>
public sealed record EntityDetailView
{
    public required EntityView Entity { get; init; }

    /// <summary>Evidence about which real-world thing this is.</summary>
    public required IReadOnlyList<IdentityMarkView> Marks { get; init; }

    public required IReadOnlyList<RelationshipView> Relationships { get; init; }

    /// <summary>
    /// The same instances the inbox shows, filtered to this entity.
    /// </summary>
    /// <remarks>
    /// Filtered, never recomputed. See ADR-0007 §1.
    /// </remarks>
    public required IReadOnlyList<AlertView> Alerts { get; init; }

    /// <summary>
    /// When a datastore fills at its current growth, or why that cannot be
    /// said. Null for every other kind of entity.
    /// </summary>
    public TimeToFullView? TimeToFull { get; init; }
}

/// <summary>
/// A datastore's fill date with the window it rests on, or a refusal.
/// </summary>
/// <remarks>
/// A refusal is an answer and is shown as one. Omitting it would leave an
/// operator unable to tell "not filling" from "not computed".
/// </remarks>
public sealed record TimeToFullView
{
    /// <summary><c>true</c> when there is a date; otherwise see <see cref="Reason"/>.</summary>
    public required bool IsForecast { get; init; }

    public DateTimeOffset? FullAtUtc { get; init; }

    public double? Days { get; init; }

    /// <summary>Growth in bytes per day, when a slope was computed.</summary>
    public double? GrowthBytesPerDay { get; init; }

    /// <summary>The span the estimate looked at; absent only when there were no points.</summary>
    public DateTimeOffset? WindowFromUtc { get; init; }

    public DateTimeOffset? WindowToUtc { get; init; }

    public required int PointsUsed { get; init; }

    /// <summary>Machine-readable refusal reason, e.g. <c>TooFewPoints</c>; null on a forecast.</summary>
    public string? Reason { get; init; }

    /// <summary>The sentence the product says, forecast or refusal.</summary>
    public required string Summary { get; init; }
}

public sealed record IdentityMarkView
{
    public required IdentityMarkKind Kind { get; init; }

    public required string Value { get; init; }

    public required string Source { get; init; }
}

/// <summary>How a collector is behaving.</summary>
public sealed record CollectorView
{
    public required string InstanceId { get; init; }

    /// <summary>Inventory or metrics. The two fail independently — see ADR-0009.</summary>
    public required string Role { get; init; }

    public required HealthState Health { get; init; }

    public required int ConsecutiveFailures { get; init; }

    public required bool IsBackingOff { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Why the last attempt failed outright, when it did.</summary>
    public string? LastFailureDetail { get; init; }

    /// <summary>
    /// What this collector reached but could not read.
    /// </summary>
    /// <remarks>
    /// All of them. This was one string carrying the first of them, which meant
    /// a collector with two unrelated problems reported one — and against a
    /// real estate the hidden one was an entire class of measurement missing.
    /// </remarks>
    public IReadOnlyList<PartialFailureView> PartialFailures { get; init; } = [];
}

/// <summary>One thing a collector could not read, and what it is.</summary>
public sealed record PartialFailureView
{
    public required string Kind { get; init; }

    /// <summary>The counter, device or endpoint. The actionable half.</summary>
    public required string Target { get; init; }

    public required string Detail { get; init; }
}

/// <summary>The triage summary: what is happening right now.</summary>
public sealed record OverviewView
{
    /// <summary>When this answer was computed, so the client can show its age.</summary>
    /// <remarks>
    /// Required by the wallboard rule in ADR-0007 §6: stale data must never be
    /// presented as fresh. A client that cannot reach the server keeps the last
    /// answer and says how old it is, rather than showing it as current.
    /// </remarks>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required int CriticalAlerts { get; init; }

    public required int WarningAlerts { get; init; }

    /// <summary>Visible alerts nobody has taken yet.</summary>
    public required int UnacknowledgedAlerts { get; init; }

    public required int SuppressedAlerts { get; init; }

    public required IReadOnlyDictionary<string, int> EntitiesByHealth { get; init; }

    public required int VanishedEntities { get; init; }

    /// <summary>
    /// Collectors that are currently failing.
    /// </summary>
    /// <remarks>
    /// On the overview rather than buried in a settings page, because it
    /// changes what every other number on this screen means: anything a failing
    /// collector covers is unknown, not healthy.
    /// </remarks>
    public required int FailingCollectors { get; init; }

    /// <summary>
    /// The oldest successful read across all collectors, or null if none has
    /// ever succeeded.
    /// </summary>
    /// <remarks>
    /// The honest answer to "how current is this screen": as current as the
    /// stalest thing on it.
    /// </remarks>
    public DateTimeOffset? OldestSuccessfulReadUtc { get; init; }
}

/// <summary>One counter available for an entity.</summary>
public sealed record SeriesOptionView
{
    public required string Counter { get; init; }

    /// <summary>The device, or empty for the aggregate across devices.</summary>
    public required string Instance { get; init; }
}

/// <summary>One point of a series.</summary>
/// <remarks>
/// Five numbers rather than one. A chart that draws only the average hides the
/// two minutes at 100% that the operator is looking for — see
/// <see cref="AggregatedSample"/>.
/// </remarks>
public sealed record SeriesPointView
{
    public required DateTimeOffset AtUtc { get; init; }

    public required double Min { get; init; }

    public required double Max { get; init; }

    public required double Average { get; init; }

    public required double Last { get; init; }

    public required int Count { get; init; }
}

/// <summary>A series, as a chart consumes it.</summary>
public sealed record SeriesView
{
    public required string EntityId { get; init; }

    public required string Counter { get; init; }

    public required string Instance { get; init; }

    /// <summary>
    /// Whether this counter has ever been recorded for this entity.
    /// </summary>
    /// <remarks>
    /// Distinct from having no points in the requested window. "We have never
    /// measured this" and "we measured it and there was nothing" are different
    /// answers and must not render the same way.
    /// </remarks>
    public required bool Exists { get; init; }

    /// <summary>
    /// The resolution actually used, which the interface is expected to show.
    /// </summary>
    /// <remarks>
    /// A chart that does not say it is drawing hourly averages reads as a live
    /// measurement. See ADR-0012.
    /// </remarks>
    public required SeriesResolution Resolution { get; init; }

    public required string Unit { get; init; }

    /// <summary>How the platform combined the samples; decides what may be read off a point.</summary>
    public required RollupType Rollup { get; init; }

    /// <summary>Whether points were dropped to keep the response bounded.</summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// The points, oldest first. Missing buckets are missing, never zero.
    /// </summary>
    public required IReadOnlyList<SeriesPointView> Points { get; init; }
}

/// <summary>An operator's command against one alert.</summary>
/// <remarks>
/// The fingerprint travels in the body rather than the path. It contains
/// separators and spaces, and putting an opaque identifier into a URL segment
/// is the mistake that made every entity page return the wrong thing — see
/// <see cref="EntityId.Separator"/>. These are commands, not resources, so a
/// body is also the more honest shape.
/// </remarks>
public sealed record AlertCommand
{
    public required string Fingerprint { get; init; }
}

/// <summary>A command to mute an alert until a deadline.</summary>
public sealed record SilenceCommand
{
    public required string Fingerprint { get; init; }

    /// <summary>
    /// When the silence ends and the alert returns on its own.
    /// </summary>
    /// <remarks>
    /// Required, with no indefinite option. Something switched off for ever is
    /// something nobody remembers to switch back on. See product principle 4.
    /// </remarks>
    public required DateTimeOffset UntilUtc { get; init; }
}

/// <summary>What an operator's command did.</summary>
public sealed record AlertActionView
{
    public required bool Applied { get; init; }

    /// <summary>The alert as it now stands, when the command applied.</summary>
    public AlertView? Alert { get; init; }

    /// <summary>Why not, when it did not.</summary>
    public string? Refusal { get; init; }

    /// <summary>
    /// Who this was recorded as, and whether the product could verify it.
    /// </summary>
    /// <remarks>
    /// Echoed back so the interface can show an operator what went into the
    /// audit trail under their name, rather than letting them assume.
    /// </remarks>
    public required string RecordedAs { get; init; }

    public required bool ActorVerified { get; init; }
}

/// <summary>An operator's command against several alerts at once.</summary>
public sealed record BulkAlertCommand
{
    public required IReadOnlyList<string> Fingerprints { get; init; }
}

/// <summary>A command to mute several alerts until one deadline.</summary>
public sealed record BulkSilenceCommand
{
    public required IReadOnlyList<string> Fingerprints { get; init; }

    public required DateTimeOffset UntilUtc { get; init; }
}

/// <summary>What a command against several alerts did.</summary>
public sealed record BulkActionView
{
    public required bool Applied { get; init; }

    public required int Requested { get; init; }

    /// <summary>
    /// How many were no longer there.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden. Alerts that resolved between the screen
    /// being drawn and the button being pressed are simply gone, and an
    /// operator who asked for twenty and changed eighteen should be told,
    /// not left to count.
    /// </remarks>
    public required int Missing { get; init; }

    public required IReadOnlyList<AlertView> Alerts { get; init; }

    public string? Refusal { get; init; }

    public required string RecordedAs { get; init; }
}

/// <summary>One step of the path that proves a group.</summary>
public sealed record CorrelationLinkView
{
    public required string FromId { get; init; }

    public required string FromName { get; init; }

    public required RelationshipKind Kind { get; init; }

    public required string ToId { get; init; }

    public required string ToName { get; init; }
}

/// <summary>Several alerts that are one thing going wrong.</summary>
public sealed record EventView
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required string RootId { get; init; }

    public required string RootName { get; init; }

    /// <summary>
    /// How many alerts and how many things, in the header.
    /// </summary>
    /// <remarks>
    /// The real counts. An operator will not accept a folded group without
    /// seeing what was folded into it -- ADR-0007 5.1.
    /// </remarks>
    public required int AlertCount { get; init; }

    public required int EntityCount { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Every alert in the group, worst first. Never a sample.</summary>
    public required IReadOnlyList<AlertView> Alerts { get; init; }

    /// <summary>Why these were grouped.</summary>
    public required IReadOnlyList<CorrelationLinkView> Explanation { get; init; }
}

/// <summary>Alerts that started together and may be one thing.</summary>
/// <remarks>
/// Deliberately not an <see cref="EventView"/>. The evidence is only that they
/// appeared together, and the interface offers it rather than folding it.
/// </remarks>
public sealed record SuggestionView
{
    public required DateTimeOffset WithinUtc { get; init; }

    public required int WindowSeconds { get; init; }

    public required IReadOnlyList<AlertView> Alerts { get; init; }
}

/// <summary>The inbox, grouped.</summary>
public sealed record EventBoardView
{
    public required IReadOnlyList<EventView> Events { get; init; }

    public required IReadOnlyList<SuggestionView> Suggestions { get; init; }

    /// <summary>Alerts belonging to no event, worst first.</summary>
    public required IReadOnlyList<AlertView> Ungrouped { get; init; }

    /// <summary>
    /// Every visible alert, so the two views can be checked against each other.
    /// </summary>
    /// <remarks>
    /// No alert may live only inside a group. Reporting the total lets the
    /// interface say "12 alerts in 3 events and 4 on their own" and lets
    /// anyone verify the arithmetic.
    /// </remarks>
    public required int TotalAlerts { get; init; }
}

/// <summary>
/// What one source managed to read, for the screen that asks whether the
/// monitoring is working.
/// </summary>
/// <remarks>
/// Beside the collectors rather than on a screen of its own, because it
/// answers the same question from the other side. Health says whether a source
/// answered; this says what was in the answer, and a perfectly healthy
/// collector can be blind to half of what the product reasons about without
/// anything appearing to go wrong.
/// </remarks>
public sealed record CoverageView
{
    public required string InstanceId { get; init; }

    /// <summary>
    /// When this was taken.
    /// </summary>
    /// <remarks>
    /// Carried rather than implied. Coverage survives a restart so the screen
    /// is not blank for a cycle — and a stale number presented without a
    /// timestamp is worse than a blank one, because it reads as current.
    /// </remarks>
    public required DateTimeOffset MeasuredAtUtc { get; init; }

    public IReadOnlyList<CoveragePropertyView> Properties { get; init; } = [];
}

/// <summary>One property's coverage on one object type.</summary>
public sealed record CoveragePropertyView
{
    public required string ObjectType { get; init; }

    public required string Property { get; init; }

    public required int Asked { get; init; }

    public required int Answered { get; init; }

    /// <summary>Nothing answered, although something was asked.</summary>
    /// <remarks>
    /// Computed here rather than in the browser so the definition lives in one
    /// place. A screen that decided this for itself would drift from the rule
    /// that raises the alert, and the two disagreeing is worse than either
    /// being wrong.
    /// </remarks>
    public bool IsBlind => Asked > 0 && Answered == 0;
}

// --- reports ------------------------------------------------------------
//
// M5.1's shape: every report is a filtered slice of one existing model, with
// a summary on top, and gets exported two ways — JSON for the SPA's own
// printable page, CSV for a spreadsheet. M5.2 and M5.3 are expected to add a
// sibling *ReportRow/*ReportView pair here and reuse Reports/CsvWriter.cs
// rather than inventing their own shape.

/// <summary>
/// One alert on the alert/finding report: everything M5.1 asks for, already
/// resolved to names rather than ids so neither the SPA nor the CSV has to
/// join anything.
/// </summary>
public sealed record AlertReportRow
{
    public required AlertSeverity Severity { get; init; }

    public required string Title { get; init; }

    /// <summary>The entity this is about, when it resolves to one.</summary>
    public string? EntityName { get; init; }

    public EntityKind? EntityKind { get; init; }

    public required string Category { get; init; }

    public required string Source { get; init; }

    public required AlertLifecycleState State { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Who acknowledged it and when, from the transition history. Null if nobody has.</summary>
    public string? AcknowledgedBy { get; init; }

    public DateTimeOffset? AcknowledgedAtUtc { get; init; }

    /// <summary>Who cleared it and when. Only set for an operator's own clear, not a condition going away.</summary>
    public string? ClearedBy { get; init; }

    public DateTimeOffset? ClearedAtUtc { get; init; }

    /// <summary>Whether the platform inferred this rather than observing it.</summary>
    public required bool IsDerived { get; init; }
}

/// <summary>Counts by severity and by state, for the summary at the top of the report.</summary>
public sealed record AlertReportSummary
{
    public required IReadOnlyDictionary<string, int> BySeverity { get; init; }

    public required IReadOnlyDictionary<string, int> ByState { get; init; }

    public required int Total { get; init; }
}

/// <summary>
/// The alert/finding report: open alerts plus whatever resolved within the
/// chosen window, for the printable page and the CSV export.
/// </summary>
public sealed record AlertReportView
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required DateTimeOffset FromUtc { get; init; }

    public required DateTimeOffset ToUtc { get; init; }

    public required AlertReportSummary Summary { get; init; }

    /// <summary>Worst first, then most recent — the same order the inbox uses.</summary>
    public required IReadOnlyList<AlertReportRow> Rows { get; init; }
}

// --- compliance report (M5.2) --------------------------------------------
//
// The auditor-facing sibling of the alert report: a header that states its
// own freshness, a summary per control, a detail per finding (including the
// exception or acceptance that covers it), removed exceptions as their own
// section, verdict history over a period, and the controls this product did
// not evaluate at all. Nothing here recomputes a verdict -- everything is
// read from ComplianceService and IComplianceStore, the same engine the
// compliance screen and its accept/except endpoints use, so the report can
// never disagree with the screen an operator worked from.

/// <summary>One finding on the compliance report, with the decision that covers it spelled out in full.</summary>
/// <remarks>
/// <see cref="ComplianceFindingView"/> (the compliance screen's row) carries
/// only the covering exception's id, because the screen looks it up in the
/// list it already has. A report stands alone -- printed, or opened a year
/// later -- so it carries the exception's owner, reason, expiry and who
/// recorded it, not a key into a table that may not be there anymore.
/// </remarks>
public sealed record ComplianceReportFindingRow
{
    public required string ControlId { get; init; }

    public required string ControlTitle { get; init; }

    public required string Priority { get; init; }

    public required string EntityId { get; init; }

    public required string EntityName { get; init; }

    public required FindingState State { get; init; }

    /// <summary>Why nothing was concluded; set only when <see cref="State"/> is NotEvaluated.</summary>
    public string? NotEvaluatedReason { get; init; }

    public string? Observed { get; init; }

    public required string Expected { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastEvaluatedUtc { get; init; }

    /// <summary>
    /// The host's source did not report in the cycle behind this verdict --
    /// called out per row, never silently rolled into a passing count.
    /// </summary>
    public required bool Stale { get; init; }

    public string? AcceptedBy { get; init; }

    public DateTimeOffset? AcceptedAtUtc { get; init; }

    public string? AcceptedReason { get; init; }

    public string? ExceptionId { get; init; }

    public string? ExceptionOwner { get; init; }

    public string? ExceptionReason { get; init; }

    public string? ExceptionCreatedBy { get; init; }

    public DateTimeOffset? ExceptionCreatedAtUtc { get; init; }

    public DateTimeOffset? ExceptionExpiresUtc { get; init; }
}

/// <summary>One verdict change from <c>compliance_transition</c>, as the report's change-history section shows it.</summary>
public sealed record ComplianceReportTransitionRow
{
    public required string ControlId { get; init; }

    /// <summary>
    /// The raw entity id the transition was recorded against. The finding it
    /// belonged to may since have left the evaluation -- its host retired, its
    /// control dropped from a new catalogue release -- so a name is not always
    /// resolvable; the detail section above, for entities still present, is
    /// where a name is shown.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>Null when the finding was new at this change.</summary>
    public ComplianceVerdict? From { get; init; }

    /// <summary>Null when the finding left the evaluation at this change.</summary>
    public ComplianceVerdict? To { get; init; }

    public string? Observed { get; init; }

    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// The compliance report: header, per-control summary, per-finding detail,
/// removed exceptions, verdict history over a period, and the controls not
/// evaluated at all.
/// </summary>
public sealed record ComplianceReportView
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required string CatalogueName { get; init; }

    public required string CatalogueRelease { get; init; }

    /// <summary>"All hosts", or what the caller scoped the report to.</summary>
    public required string Scope { get; init; }

    /// <summary>
    /// When the most recent evaluation behind this report ran; null when
    /// nothing has been evaluated yet. The single freshness date a printed
    /// page can show for the report as a whole.
    /// </summary>
    public DateTimeOffset? LastEvaluatedUtc { get; init; }

    /// <summary>How many findings in scope rest on a host whose source did not report in the last cycle.</summary>
    public required int StaleCount { get; init; }

    /// <summary>The hosts behind <see cref="StaleCount"/>, by name -- an auditor asks which ones, not only how many.</summary>
    public required IReadOnlyList<string> StaleEntityNames { get; init; }

    /// <summary>Every control the catalogue names, evaluated or not, with its counts.</summary>
    public required IReadOnlyList<ComplianceControlView> Controls { get; init; }

    public required FindingCountsView Totals { get; init; }

    public required IReadOnlyList<ComplianceReportFindingRow> Findings { get; init; }

    /// <summary>The exceptions standing now, in scope.</summary>
    public required IReadOnlyList<ComplianceExceptionView> Exceptions { get; init; }

    /// <summary>
    /// Exceptions somebody withdrew -- audit evidence in their own right, kept
    /// apart from the standing ones so a reader does not mistake one for the
    /// other.
    /// </summary>
    public required IReadOnlyList<ComplianceExceptionView> RemovedExceptions { get; init; }

    public required DateTimeOffset HistoryFromUtc { get; init; }

    public required DateTimeOffset HistoryToUtc { get; init; }

    /// <summary>Verdict changes in [<see cref="HistoryFromUtc"/>, <see cref="HistoryToUtc"/>], oldest first.</summary>
    public required IReadOnlyList<ComplianceReportTransitionRow> History { get; init; }
}

/// <summary>
/// One datastore on the capacity report: its latest reading and the same
/// fill-date answer <see cref="TimeToFullView"/> gives the datastore's own
/// page — never recomputed differently. See
/// EnterpriseObservatory.Application.Analysis.DatastoreTimeToFull.
/// </summary>
public sealed record CapacityReportRow
{
    public required string Name { get; init; }

    /// <summary>VMFS, NFS, vsan and so on, as vSphere words it. Null when not read.</summary>
    public string? DatastoreType { get; init; }

    public required string Source { get; init; }

    /// <summary>
    /// The latest reading of each, or null when this cycle never recorded one
    /// -- absent, never a zero standing in for "we did not look".
    /// </summary>
    public double? CapacityBytes { get; init; }

    public double? UsedBytes { get; init; }

    public double? FreeBytes { get; init; }

    /// <summary><see cref="UsedBytes"/> over <see cref="CapacityBytes"/>, in percent.</summary>
    public double? PercentUsed { get; init; }

    /// <summary>
    /// Used plus what has been promised to thin disks. Null when uncommitted
    /// space was never read -- not the same as a datastore with no thin disks.
    /// </summary>
    public double? ProvisionedBytes { get; init; }

    /// <summary><see cref="ProvisionedBytes"/> over <see cref="CapacityBytes"/>. Above 1 is over-committed.</summary>
    public double? OvercommitRatio { get; init; }

    /// <summary>The same estimate the datastore's own page and the filling rule use.</summary>
    public required TimeToFullView TimeToFull { get; init; }
}

/// <summary>Totals and counts across every datastore on the report.</summary>
public sealed record CapacityReportSummary
{
    public required int TotalDatastores { get; init; }

    public required double TotalCapacityBytes { get; init; }

    public required double TotalUsedBytes { get; init; }

    public required double TotalFreeBytes { get; init; }

    /// <summary>Filling inside 30 days, the product's warning threshold. See <c>DatastoreTimeToFullPolicy</c>.</summary>
    public required int FillingWithin30Days { get; init; }

    /// <summary>Filling inside 7 days, the product's critical threshold.</summary>
    public required int FillingWithin7Days { get; init; }

    /// <summary>Promised more than capacity: <see cref="CapacityReportRow.OvercommitRatio"/> above 1.</summary>
    public required int OvercommittedCount { get; init; }

    /// <summary>No fill-date estimate yet -- a refusal is counted here, never left blank.</summary>
    public required int NoEstimateCount { get; init; }

    /// <summary>
    /// Why, for every datastore counted in <see cref="NoEstimateCount"/>. Keyed
    /// by the machine-readable reason, e.g. <c>WindowTooShort</c> -- "less than
    /// a week of history" on a live estate whose capacity series only started
    /// recently is the expected, correct answer, not a bug.
    /// </summary>
    public required IReadOnlyDictionary<string, int> NoEstimateByReason { get; init; }
}

/// <summary>
/// The capacity report: every live datastore, worst first, for the printable
/// page and the CSV export. See <see cref="AlertReportView"/> for the shape
/// this copies.
/// </summary>
public sealed record CapacityReportView
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required CapacityReportSummary Summary { get; init; }

    /// <summary>Soonest fill date first, then highest percent used among the rest.</summary>
    public required IReadOnlyList<CapacityReportRow> Rows { get; init; }
}
