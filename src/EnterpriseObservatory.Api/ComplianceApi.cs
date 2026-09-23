using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>How many findings of a control are in each state.</summary>
public sealed record FindingCountsView
{
    public int Failing { get; init; }

    public int Passing { get; init; }

    public int NotEvaluated { get; init; }

    public int Accepted { get; init; }

    public int Excepted { get; init; }

    /// <summary>
    /// How many of the findings above rest on a host whose source did not
    /// report in the last cycle. Not a state of its own: a stale failing
    /// finding is still counted as failing, and also here.
    /// </summary>
    public int Stale { get; init; }
}

/// <summary>One catalogue control, as the compliance screen groups by it.</summary>
public sealed record ComplianceControlView
{
    public required string ControlId { get; init; }

    public required string Title { get; init; }

    public required string Component { get; init; }

    public required string Priority { get; init; }

    public required string Parameter { get; init; }

    public required string InstallationDefault { get; init; }

    public required string BaselineValue { get; init; }

    public required string Assessment { get; init; }

    /// <summary>
    /// The catalogue the control comes from, as the screen names it:
    /// <see cref="ComplianceSources.Scg"/> or <see cref="ComplianceSources.Continuity"/>.
    /// Not the citation -- see <see cref="Citation"/>.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// What the control's expectation or threshold rests on
    /// (<c>ComplianceControl.Source</c>), shown as "basis:"; empty for a
    /// vendor guide's control, shown then as "no citation — product policy".
    /// </summary>
    public string Citation { get; init; } = string.Empty;

    /// <summary>Whether this product judges the control at all.</summary>
    public required bool Evaluated { get; init; }

    /// <summary>Why not, when it does not. Never counted as passing.</summary>
    public string? NotEvaluatedReason { get; init; }

    /// <summary>The catalogue the control comes from: the vendor guide, or the product's own.</summary>
    public string CatalogueName { get; init; } = string.Empty;

    public required FindingCountsView Counts { get; init; }
}

/// <summary>An exception to a control, as the interface shows it.</summary>
public sealed record ComplianceExceptionView
{
    public required string Id { get; init; }

    public required string ControlId { get; init; }

    /// <summary>Null means every entity the control applies to.</summary>
    public string? EntityId { get; init; }

    /// <summary>Null means every subject.</summary>
    public string? Subject { get; init; }

    public required string Reason { get; init; }

    public required string Owner { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    public required bool Expired { get; init; }

    /// <summary>Who withdrew it; null while it stands.</summary>
    public string? RemovedBy { get; init; }

    public DateTimeOffset? RemovedAtUtc { get; init; }
}

/// <summary>
/// One catalogue's scorecard: the five-way finding count and coverage, for
/// the posture screen's row above the grouped control sections (K3 §1.1,
/// ADR-0026 — NotEvaluated is always shown, never folded into a percentage).
/// </summary>
/// <remarks>
/// Built independently for every entry of <see cref="ComplianceService.Descriptors"/>
/// — there is no "the" catalogue here, only a list, so a screen showing two
/// (or, with M9, more) catalogues never has to guess which one is first.
/// </remarks>
public sealed record CatalogueScorecardView
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CatalogueOwner Owner { get; init; }

    public required CatalogueKind Kind { get; init; }

    public required string Release { get; init; }

    /// <summary>Why this catalogue could not be loaded, when it could not be; counts are then all zero.</summary>
    public string? Problem { get; init; }

    public required FindingCountsView Counts { get; init; }

    /// <summary>Passed + failing + accepted + excepted — subjects this product reached a verdict on.</summary>
    public required int EvaluableSubjects { get; init; }

    /// <summary>Every subject the catalogue's controls apply to, evaluated or not.</summary>
    public required int TotalSubjects { get; init; }

    /// <summary>
    /// <see cref="EvaluableSubjects"/> over <see cref="TotalSubjects"/>; null
    /// when there are no subjects at all (an unloaded or empty catalogue) —
    /// never a manufactured 0% or 100% for "nothing to judge".
    /// </summary>
    public double? Coverage { get; init; }

    /// <summary>This catalogue's verdict deltas over the last seven days (P2).</summary>
    public required CatalogueLast7DaysView Last7Days { get; init; }
}

/// <summary>
/// One catalogue's verdict deltas over the posture scorecard's trailing
/// window (P2) — a count of <c>compliance_transition</c> rows, derived from
/// the append-only log rather than a second, independently-maintained total
/// (ADR-0026).
/// </summary>
public sealed record CatalogueLast7DaysView
{
    /// <summary>Subjects that started failing in the window.</summary>
    public required int FailingIn { get; init; }

    /// <summary>Subjects that stopped failing in the window (passed, or left the evaluation).</summary>
    public required int FailingOut { get; init; }

    /// <summary>Subjects that became not-evaluated in the window.</summary>
    public required int NotEvaluatedIn { get; init; }
}

/// <summary>The whole compliance screen's summary.</summary>
public sealed record ComplianceView
{
    /// <summary>
    /// The source of the vendor guide, found by ownership
    /// (<see cref="CatalogueOwner.Broadcom"/>), never by list position; each
    /// control carries its own catalogue too. Kept for the header text
    /// existing callers already read — <see cref="Catalogues"/> is the
    /// list a screen should read to show more than one.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    public required string CatalogueName { get; init; }

    public required string CatalogueRelease { get; init; }

    /// <summary>Why no vendor guide is loaded, when none is.</summary>
    public string? CatalogueProblem { get; init; }

    /// <summary>Every loaded catalogue's scorecard, in registration order — not "the vendor guide first".</summary>
    public required IReadOnlyList<CatalogueScorecardView> Catalogues { get; init; }

    public required int DefaultControlsSkipped { get; init; }

    public required IReadOnlyList<ComplianceControlView> Controls { get; init; }

    public required FindingCountsView Totals { get; init; }

    /// <summary>The exceptions that stand, expired or not; withdrawn ones are listed separately.</summary>
    public required IReadOnlyList<ComplianceExceptionView> Exceptions { get; init; }

    /// <summary>When findings were last evaluated, or null when never.</summary>
    public DateTimeOffset? LastEvaluatedUtc { get; init; }
}

/// <summary>One finding, as the drill-down shows it.</summary>
public sealed record ComplianceFindingView
{
    public required string ControlId { get; init; }

    public required string CatalogueRelease { get; init; }

    public required string EntityId { get; init; }

    public required string EntityName { get; init; }

    /// <summary>What on the entity the finding is about; empty for the entity itself.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>How the subject is shown; display only.</summary>
    public string? SubjectLabel { get; init; }

    public required FindingState State { get; init; }

    public string? Reason { get; init; }

    public string? Observed { get; init; }

    public required string Expected { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>When the evidence was read: the host's last-seen time.</summary>
    public required DateTimeOffset LastEvaluatedUtc { get; init; }

    /// <summary>
    /// The host's source did not report in the last cycle: this is the last
    /// verdict that could be reached, not a current one.
    /// </summary>
    public required bool Stale { get; init; }

    public string? AcceptedBy { get; init; }

    public DateTimeOffset? AcceptedAtUtc { get; init; }

    public string? AcceptedReason { get; init; }

    /// <summary>The exception covering it now, if any.</summary>
    public string? ExceptionId { get; init; }
}

public sealed record AcceptFindingCommand
{
    public required string ControlId { get; init; }

    public required string EntityId { get; init; }

    /// <summary>The finding's subject; empty (the default) for a finding about the entity itself.</summary>
    public string Subject { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed record AddExceptionCommand
{
    public required string ControlId { get; init; }

    /// <summary>Null or empty for every entity the control applies to.</summary>
    public string? EntityId { get; init; }

    /// <summary>Null or empty for every subject.</summary>
    public string? Subject { get; init; }

    public required string Reason { get; init; }

    public required string Owner { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }
}

public sealed record RemoveExceptionCommand
{
    public required string Id { get; init; }
}

/// <summary>
/// The compliance screen: findings grouped by control, and what people decide about them.
/// </summary>
/// <remarks>
/// Reads for anyone signed in, writes for operators — the same line the alert
/// verbs draw. Separate from the alert endpoints on purpose: a finding is not
/// an alert and does not belong in the inbox (product-architecture §2).
/// </remarks>
public static class ComplianceApi
{
    public static IEndpointRouteBuilder MapComplianceApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var compliance = endpoints.MapGroup("/api/compliance").RequireAuthorization();

        compliance.MapGet("/", (ComplianceService service) => Summary(service))
            .WithName("GetCompliance");

        compliance.MapGet("/findings", (
                ComplianceService service,
                FindingState? state,
                string? control,
                string? entity) => Findings(service, state, control, entity))
            .WithName("GetComplianceFindings");

        compliance.MapPost("/accept", (
                HttpContext context,
                ComplianceService service,
                AcceptFindingCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                return Respond(
                    service,
                    service.Accept(
                        command.ControlId,
                        new EntityId(command.EntityId),
                        command.Subject ?? string.Empty,
                        command.Reason,
                        actor));
            })
            .RequireAuthorization(ObservatoryApi.Policies.Operator)
            .WithName("AcceptComplianceFinding");

        compliance.MapPost("/exceptions", (
                HttpContext context,
                ComplianceService service,
                AddExceptionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                return Respond(service, service.AddException(
                    command.ControlId,
                    string.IsNullOrWhiteSpace(command.EntityId) ? null : new EntityId(command.EntityId),
                    command.Subject,
                    command.Reason,
                    command.Owner,
                    command.ExpiresUtc,
                    actor));
            })
            .RequireAuthorization(ObservatoryApi.Policies.Operator)
            .WithName("AddComplianceException");

        compliance.MapGet("/exceptions", (ComplianceService service, bool? includeRemoved) =>
                ExceptionList(service, includeRemoved ?? false))
            .WithName("GetComplianceExceptions");

        compliance.MapPost("/exceptions/remove", (
                HttpContext context,
                ComplianceService service,
                RemoveExceptionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                return Respond(service, service.RemoveException(command.Id, actor));
            })
            .RequireAuthorization(ObservatoryApi.Policies.Operator)
            .WithName("RemoveComplianceException");

        // --- reports ---------------------------------------------------------
        //
        // The auditor-facing report, M5.2. Read for any signed-in user, same
        // line as the screen above: printing or exporting a finding is not a
        // more sensitive act than reading it. A sibling of the alert report
        // under the same /api/reports prefix (ObservatoryApi.MapObservatoryApi),
        // mapped here instead because everything it reads comes from
        // ComplianceService, which this file already has.

        var reports = endpoints.MapGroup("/api/reports").RequireAuthorization();

        reports.MapGet("/compliance", (
                ComplianceService service,
                string? catalogue,
                string? control,
                string? entity,
                DateTimeOffset? from,
                DateTimeOffset? to) =>
            Report(service, catalogue, control, entity, from, to) is { } report
                ? Results.Ok(report)
                : Results.NotFound())
            .WithName("GetComplianceReport");

        reports.MapGet("/compliance.csv", (
                ComplianceService service,
                string? catalogue,
                string? control,
                string? entity,
                DateTimeOffset? from,
                DateTimeOffset? to,
                string? section) =>
        {
            var report = Report(service, catalogue, control, entity, from, to);

            if (report is null)
            {
                return Results.NotFound();
            }

            // "history" is the one other section this report has; anything
            // else -- including nothing -- is the findings detail, the report
            // an auditor reaches for first.
            var isHistory = string.Equals(section, "history", StringComparison.OrdinalIgnoreCase);

            var csv = isHistory
                ? ComplianceHistoryReportCsv.Write(report.History, report.HistoryTruncated)
                : ComplianceFindingsReportCsv.Write(report.Findings);

            var name = isHistory ? "compliance-history" : "compliance";
            var fileName = $"{name}-{report.GeneratedAtUtc:yyyyMMdd-HHmm}.csv";

            return Results.File(CsvWriter.ToUtf8WithBom(csv), "text/csv", fileName);
        })
            .WithName("GetComplianceReportCsv");

        return endpoints;
    }

    /// <summary>How far back the change-history section reaches when the caller does not say.</summary>
    /// <remarks>
    /// Thirty days: long enough to show a month's worth of remediation
    /// without printing a year of <c>compliance_transition</c> on a page
    /// meant to be read, not archived -- the store's own, wider retention
    /// (thirteen months) is for a caller who asks for it with an explicit
    /// <c>from</c>.
    /// </remarks>
    public static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Resolves a catalogue by its registry id (<see cref="CatalogueDescriptor.Id"/>);
    /// the vendor guide when <paramref name="catalogueId"/> is null (the
    /// report's old, single-catalogue URL keeps working), found by
    /// ownership, never by list position. Null when the id names no loaded
    /// catalogue.
    /// </summary>
    private static ComplianceCatalogue? ResolveCatalogue(ComplianceService service, string? catalogueId) =>
        catalogueId is null
            ? CatalogueDescriptor.VendorGuide(service.Catalogues)
            : service.Catalogues.FirstOrDefault(
                c => string.Equals(CatalogueDescriptor.Of(c).Id, catalogueId, StringComparison.Ordinal));

    /// <summary>
    /// Builds the compliance report for one catalogue: everything an auditor
    /// asks for, in one read. Null when <paramref name="catalogueId"/> names
    /// no loaded catalogue.
    /// </summary>
    /// <remarks>
    /// Every field comes from <see cref="ComplianceService"/> the same way
    /// <see cref="Summary"/> and <see cref="Findings"/> above read it --
    /// nothing here re-derives a verdict or re-decides what counts as stale.
    /// A report that disagreed with the screen an operator worked from would
    /// be worse than no report.
    /// </remarks>
    public static ComplianceReportView? Report(
        ComplianceService service,
        string? catalogueId,
        string? control,
        string? entity,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        ArgumentNullException.ThrowIfNull(service);

        var catalogue = ResolveCatalogue(service, catalogueId);

        if (catalogue is null)
        {
            return null;
        }

        var now = service.Now;
        var to = toUtc ?? now;
        var from = fromUtc ?? to - DefaultHistoryWindow;

        var exceptions = service.Exceptions();
        var standing = exceptions.Where(e => e.RemovedAtUtc is null).ToList();
        var removedExceptions = exceptions.Where(e => e.RemovedAtUtc is not null).ToList();

        var findings = service.Findings()
            .Where(f => string.Equals(f.CatalogueRelease, catalogue.Release, StringComparison.Ordinal))
            .Where(f => control is null || string.Equals(f.ControlId, control, StringComparison.Ordinal))
            .Where(f => entity is null || string.Equals(f.Entity.Value, entity, StringComparison.Ordinal))
            .ToList();

        var controlsById = service.Controls()
            .Where(b => string.Equals(b.CatalogueRelease, catalogue.Release, StringComparison.Ordinal))
            .GroupBy(b => b.Control.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Control, StringComparer.Ordinal);

        var byControl = findings
            .GroupBy(f => f.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Count(g, exceptions, now), StringComparer.Ordinal);

        var boundControls = service.Controls()
            .Where(bound => string.Equals(bound.CatalogueRelease, catalogue.Release, StringComparison.Ordinal))
            .Where(bound => control is null ||
                string.Equals(bound.Control.ControlId, control, StringComparison.Ordinal));

        var staleEntityNames = findings
            .Where(f => f.Stale)
            .Select(f => f.EntityName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Release, control, entity, the end of the window and the row cap are
        // all the store's to apply in SQL now -- see IComplianceStore.TransitionsSince
        // -- so this handler only shapes what came back into report rows.
        var historyPage = service.TransitionsSince(
            from, to, catalogue, control, string.IsNullOrEmpty(entity) ? null : new EntityId(entity));

        var history = historyPage.Transitions
            .Select(t => new ComplianceReportTransitionRow
            {
                ControlId = t.ControlId,
                EntityId = t.Entity.Value,
                Subject = t.Subject,
                SubjectLabel = t.SubjectLabel,
                AcceptedBy = t.AcceptedBy,
                AcceptedReason = t.AcceptedReason,
                From = t.From,
                To = t.To,
                Observed = t.Observed,
                AtUtc = t.AtUtc,
            })
            .ToList();

        return new ComplianceReportView
        {
            GeneratedAtUtc = now,
            CatalogueName = catalogue.Name,
            CatalogueRelease = catalogue.Release,
            Scope = DescribeScope(control, entity),
            LastEvaluatedUtc = findings.Count == 0 ? null : findings.Max(f => f.LastEvaluatedUtc),
            StaleCount = findings.Count(f => f.Stale),
            StaleEntityNames = staleEntityNames,
            Controls =
            [
                .. boundControls.Select(bound => new ComplianceControlView
                {
                    ControlId = bound.Control.ControlId,
                    Title = bound.Control.Title,
                    Component = bound.Control.Component,
                    Priority = bound.Control.Priority,
                    Parameter = bound.Control.Parameter,
                    InstallationDefault = bound.Control.InstallationDefault,
                    BaselineValue = bound.Control.BaselineValue,
                    Assessment = bound.Control.Assessment,
                    Source = ComplianceSources.Of(bound.CatalogueName),
                    Citation = bound.Control.Source,
                    Evaluated = bound.IsEvaluated,
                    NotEvaluatedReason = bound.NotEvaluatedReason,
                    CatalogueName = bound.CatalogueName,
                    Counts = byControl.TryGetValue(bound.Control.ControlId, out var counts)
                        ? counts
                        : new FindingCountsView(),
                }),
            ],
            Totals = Count(findings, exceptions, now),
            Findings =
            [
                .. findings
                    .Select(f => ToReportRow(f, exceptions, now, controlsById))
                    .OrderBy(r => r.State)
                    .ThenBy(r => r.ControlId, StringComparer.Ordinal)
                    .ThenBy(r => r.EntityName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Subject, StringComparer.Ordinal),
            ],
            Exceptions =
            [
                .. standing
                    .Where(e => control is null || string.Equals(e.ControlId, control, StringComparison.Ordinal))
                    .Where(e => entity is null || e.Entity is null ||
                        string.Equals(e.Entity?.Value, entity, StringComparison.Ordinal))
                    .OrderBy(e => e.ExpiresUtc)
                    .Select(e => ToView(e, now)),
            ],
            RemovedExceptions =
            [
                .. removedExceptions
                    .Where(e => control is null || string.Equals(e.ControlId, control, StringComparison.Ordinal))
                    .OrderByDescending(e => e.RemovedAtUtc)
                    .Select(e => ToView(e, now)),
            ],
            HistoryFromUtc = from,
            HistoryToUtc = to,
            History = history,
            HistoryTruncated = historyPage.Truncated,
        };
    }

    private static string DescribeScope(string? control, string? entity)
    {
        if (control is null && entity is null)
        {
            return "All hosts";
        }

        var parts = new List<string>();

        if (control is not null)
        {
            parts.Add($"control {control}");
        }

        if (entity is not null)
        {
            parts.Add($"entity {entity}");
        }

        return string.Join(", ", parts);
    }

    private static ComplianceReportFindingRow ToReportRow(
        ComplianceFinding finding,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now,
        Dictionary<string, ComplianceControl> controlsById)
    {
        var state = finding.StateAt(exceptions, now);

        var covering = finding.Verdict == ComplianceVerdict.Failing
            ? finding.CoveringException(exceptions, now)
            : null;

        controlsById.TryGetValue(finding.ControlId, out var control);

        return new ComplianceReportFindingRow
        {
            ControlId = finding.ControlId,
            ControlTitle = control?.Title ?? finding.ControlId,
            Priority = control?.Priority ?? string.Empty,
            EntityId = finding.Entity.Value,
            EntityName = finding.EntityName,
            Subject = finding.Subject,
            SubjectLabel = finding.SubjectLabel,
            State = state,
            NotEvaluatedReason = state == FindingState.NotEvaluated ? finding.Reason : null,
            Observed = finding.Observed,
            Expected = finding.Expected,
            FirstSeenUtc = finding.FirstSeenUtc,
            LastEvaluatedUtc = finding.LastEvaluatedUtc,
            Stale = finding.Stale,
            AcceptedBy = finding.Acceptance?.By,
            AcceptedAtUtc = finding.Acceptance?.AtUtc,
            AcceptedReason = finding.Acceptance?.Reason,
            ExceptionId = covering?.Id,
            ExceptionOwner = covering?.Owner,
            ExceptionReason = covering?.Reason,
            ExceptionCreatedBy = covering?.CreatedBy,
            ExceptionCreatedAtUtc = covering?.CreatedAtUtc,
            ExceptionExpiresUtc = covering?.ExpiresUtc,
        };
    }

    /// <summary>Every control with its counts, plus the exceptions in the record.</summary>
    public static ComplianceView Summary(ComplianceService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var now = service.Now;
        var exceptions = service.Exceptions();
        var findings = service.Findings();

        var byControl = findings
            .GroupBy(f => f.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Count(g, exceptions, now), StringComparer.Ordinal);

        // The vendor guide, found by who owns it, never by "the first
        // catalogue" -- P1 removes that assumption. Null only when no
        // catalogue owned by Broadcom is loaded, which cannot happen in
        // production (ComplianceService always registers the vendor guide);
        // a test with no Broadcom-owned catalogue then gets an honestly
        // empty header rather than a name borrowed from whichever catalogue
        // happened to load first.
        var vendorCatalogue = CatalogueDescriptor.VendorGuide(service.Catalogues);
        var sevenDaysAgo = now - SevenDayWindow;

        return new ComplianceView
        {
            Source = vendorCatalogue is null ? string.Empty : ComplianceSources.Of(vendorCatalogue.Name),
            CatalogueName = vendorCatalogue?.Name ?? string.Empty,
            CatalogueRelease = vendorCatalogue?.Release ?? string.Empty,
            CatalogueProblem = vendorCatalogue?.Problem,
            Catalogues = [.. service.Catalogues.Select(c => Scorecard(service, c, findings, exceptions, now, sevenDaysAgo))],
            DefaultControlsSkipped = service.Catalogues.Sum(c => c.DefaultControlsSkipped),
            Controls =
            [
                .. service.Controls().Select(bound => new ComplianceControlView
                {
                    ControlId = bound.Control.ControlId,
                    Title = bound.Control.Title,
                    Component = bound.Control.Component,
                    Priority = bound.Control.Priority,
                    Parameter = bound.Control.Parameter,
                    InstallationDefault = bound.Control.InstallationDefault,
                    BaselineValue = bound.Control.BaselineValue,
                    Assessment = bound.Control.Assessment,
                    Source = ComplianceSources.Of(bound.CatalogueName),
                    Citation = bound.Control.Source,
                    Evaluated = bound.IsEvaluated,
                    NotEvaluatedReason = bound.NotEvaluatedReason,
                    CatalogueName = bound.CatalogueName,
                    Counts = byControl.TryGetValue(bound.Control.ControlId, out var counts)
                        ? counts
                        : new FindingCountsView(),
                }),
            ],
            Totals = Count(findings, exceptions, now),
            Exceptions =
            [
                .. exceptions
                    .Where(e => e.RemovedAtUtc is null)
                    .OrderBy(e => e.ExpiresUtc)
                    .Select(e => ToView(e, now)),
            ],
            LastEvaluatedUtc = findings.Count == 0 ? null : findings.Max(f => f.LastEvaluatedUtc),
        };
    }

    /// <summary>Findings, filtered, in the order an operator works through them.</summary>
    public static IReadOnlyList<ComplianceFindingView> Findings(
        ComplianceService service, FindingState? state, string? control, string? entity)
    {
        ArgumentNullException.ThrowIfNull(service);

        var now = service.Now;
        var exceptions = service.Exceptions();

        return
        [
            .. service.Findings()
                .Where(f => control is null || string.Equals(f.ControlId, control, StringComparison.Ordinal))
                .Where(f => entity is null || string.Equals(f.Entity.Value, entity, StringComparison.Ordinal))
                .Select(f => ToView(f, exceptions, now))
                .Where(f => state is null || f.State == state)
                .OrderBy(f => f.State)
                .ThenBy(f => f.ControlId, StringComparer.Ordinal)
                .ThenBy(f => f.EntityName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Subject, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The exceptions on the record: the standing ones, and the withdrawn ones when asked.
    /// </summary>
    /// <remarks>
    /// A withdrawn exception covers nothing, but it is kept and listed here
    /// with who withdrew it: an audit asks what was excepted at a date, not
    /// only what is excepted now.
    /// </remarks>
    public static IReadOnlyList<ComplianceExceptionView> ExceptionList(
        ComplianceService service, bool includeRemoved)
    {
        ArgumentNullException.ThrowIfNull(service);

        var now = service.Now;

        return
        [
            .. service.Exceptions()
                .Where(e => includeRemoved || e.RemovedAtUtc is null)
                .OrderBy(e => e.RemovedAtUtc is null ? 0 : 1)
                .ThenBy(e => e.ExpiresUtc)
                .Select(e => ToView(e, now)),
        ];
    }

    /// <summary>How far back the posture scorecard's "last 7 days" delta looks.</summary>
    public static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private static readonly CatalogueLast7DaysView NoDelta = new()
    {
        FailingIn = 0,
        FailingOut = 0,
        NotEvaluatedIn = 0,
    };

    /// <summary>
    /// One catalogue's scorecard, counted only over its own findings —
    /// <see cref="ComplianceFinding.CatalogueRelease"/> is the identity's own
    /// field, so no catalogue's row can pick up another's rows.
    /// </summary>
    private static CatalogueScorecardView Scorecard(
        ComplianceService service,
        ComplianceCatalogue catalogue,
        IReadOnlyList<ComplianceFinding> allFindings,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now,
        DateTimeOffset sevenDaysAgo)
    {
        var descriptor = CatalogueDescriptor.Of(catalogue);

        if (catalogue.Problem is not null)
        {
            return new CatalogueScorecardView
            {
                Id = descriptor.Id,
                Name = descriptor.Name,
                Owner = descriptor.Owner,
                Kind = descriptor.Kind,
                Release = descriptor.Release,
                Problem = catalogue.Problem,
                Counts = new FindingCountsView(),
                EvaluableSubjects = 0,
                TotalSubjects = 0,
                Coverage = null,
                Last7Days = NoDelta,
            };
        }

        var own = allFindings.Where(f => string.Equals(f.CatalogueRelease, catalogue.Release, StringComparison.Ordinal));
        var counts = Count(own, exceptions, now);

        // Coverage is a subject-count fraction, not a state count: a subject
        // that is Failing-and-Accepted is one evaluable subject, not two, and
        // NotEvaluated never contributes to the numerator (ADR-0026).
        var evaluable = counts.Passing + counts.Failing + counts.Accepted + counts.Excepted;
        var total = evaluable + counts.NotEvaluated;

        var delta = service.DeltaSince(catalogue, sevenDaysAgo);

        return new CatalogueScorecardView
        {
            Id = descriptor.Id,
            Name = descriptor.Name,
            Owner = descriptor.Owner,
            Kind = descriptor.Kind,
            Release = descriptor.Release,
            Problem = null,
            Counts = counts,
            EvaluableSubjects = evaluable,
            TotalSubjects = total,
            Coverage = total == 0 ? null : (double)evaluable / total,
            Last7Days = new CatalogueLast7DaysView
            {
                FailingIn = delta.FailingIn,
                FailingOut = delta.FailingOut,
                NotEvaluatedIn = delta.NotEvaluatedIn,
            },
        };
    }

    private static FindingCountsView Count(
        IEnumerable<ComplianceFinding> findings,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        var all = findings.ToList();
        var states = all.Select(f => f.StateAt(exceptions, now)).ToList();

        return new FindingCountsView
        {
            Failing = states.Count(s => s == FindingState.Failing),
            Passing = states.Count(s => s == FindingState.Passing),
            NotEvaluated = states.Count(s => s == FindingState.NotEvaluated),
            Accepted = states.Count(s => s == FindingState.Accepted),
            Excepted = states.Count(s => s == FindingState.Excepted),
            Stale = all.Count(f => f.Stale),
        };
    }

    private static ComplianceFindingView ToView(
        ComplianceFinding finding, IReadOnlyList<ComplianceWaiver> exceptions, DateTimeOffset now) => new()
        {
            ControlId = finding.ControlId,
            CatalogueRelease = finding.CatalogueRelease,
            EntityId = finding.Entity.Value,
            EntityName = finding.EntityName,
            Subject = finding.Subject,
            SubjectLabel = finding.SubjectLabel,
            State = finding.StateAt(exceptions, now),
            Reason = finding.Reason,
            Observed = finding.Observed,
            Expected = finding.Expected,
            FirstSeenUtc = finding.FirstSeenUtc,
            LastEvaluatedUtc = finding.LastEvaluatedUtc,
            Stale = finding.Stale,
            AcceptedBy = finding.Acceptance?.By,
            AcceptedAtUtc = finding.Acceptance?.AtUtc,
            AcceptedReason = finding.Acceptance?.Reason,
            ExceptionId = finding.Verdict == ComplianceVerdict.Failing
                ? finding.CoveringException(exceptions, now)?.Id
                : null,
        };

    private static ComplianceExceptionView ToView(ComplianceWaiver exception, DateTimeOffset now) => new()
    {
        Id = exception.Id,
        ControlId = exception.ControlId,
        EntityId = exception.Entity?.Value,
        Subject = exception.Subject,
        Reason = exception.Reason,
        Owner = exception.Owner,
        CreatedBy = exception.CreatedBy,
        CreatedAtUtc = exception.CreatedAtUtc,
        ExpiresUtc = exception.ExpiresUtc,
        Expired = exception.IsExpiredAt(now),
        RemovedBy = exception.RemovedBy,
        RemovedAtUtc = exception.RemovedAtUtc,
    };

    private static IResult Respond(ComplianceService service, ComplianceResult result)
    {
        if (result.Applied)
        {
            var now = service.Now;

            return result.Finding is { } finding
                ? Results.Ok(ToView(finding, service.Exceptions(), now))
                : Results.Ok(ToView(result.Exception!, now));
        }

        return Results.Problem(
            title: "Not applied",
            detail: Explain(result.Failure),
            statusCode: result.Failure switch
            {
                ComplianceFailure.NotFound => StatusCodes.Status404NotFound,
                ComplianceFailure.AlreadyAccepted => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            });
    }

    private static string Explain(ComplianceFailure failure) => failure switch
    {
        ComplianceFailure.NotFound => "There is no such finding or exception.",
        ComplianceFailure.NotFailing =>
            "Only a failing finding can be accepted. A passing one needs nothing, and one that was " +
            "not evaluated has no verdict to own.",
        ComplianceFailure.UnknownControl => "That control is not in the loaded catalogue.",
        ComplianceFailure.BadExpiry =>
            "An exception has to end in the future and within " +
            $"{ComplianceService.MaximumExceptionDuration.TotalDays:0} days. One with no end is one " +
            "nobody remembers.",
        ComplianceFailure.MissingDetail =>
            "An exception needs a reason and an owner — somebody has to be able to explain it and " +
            "answer for it when it comes up for renewal.",
        ComplianceFailure.TooLong =>
            $"A reason can be at most {ComplianceService.MaximumReasonLength} characters and an owner " +
            $"at most {ComplianceService.MaximumOwnerLength}. Cite the change ticket rather than " +
            "copying it.",
        ComplianceFailure.AlreadyAccepted =>
            "Somebody has already accepted this finding. Their acceptance stays on the record until " +
            "the finding passes; it is not overwritten.",
        _ => "The change was not applied.",
    };
}
