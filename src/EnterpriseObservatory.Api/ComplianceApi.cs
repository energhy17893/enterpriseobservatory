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

    /// <summary>Whether this product judges the control at all.</summary>
    public required bool Evaluated { get; init; }

    /// <summary>Why not, when it does not. Never counted as passing.</summary>
    public string? NotEvaluatedReason { get; init; }

    public required FindingCountsView Counts { get; init; }
}

/// <summary>An exception to a control, as the interface shows it.</summary>
public sealed record ComplianceExceptionView
{
    public required string Id { get; init; }

    public required string ControlId { get; init; }

    /// <summary>Null means every entity the control applies to.</summary>
    public string? EntityId { get; init; }

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

/// <summary>The whole compliance screen's summary.</summary>
public sealed record ComplianceView
{
    public required string CatalogueName { get; init; }

    public required string CatalogueRelease { get; init; }

    /// <summary>Why no catalogue is loaded, when none is.</summary>
    public string? CatalogueProblem { get; init; }

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

    public string Reason { get; init; } = string.Empty;
}

public sealed record AddExceptionCommand
{
    public required string ControlId { get; init; }

    /// <summary>Null or empty for every entity the control applies to.</summary>
    public string? EntityId { get; init; }

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
                    service.Accept(command.ControlId, new EntityId(command.EntityId), command.Reason, actor));
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

        return endpoints;
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

        var catalogue = service.Catalogue;

        return new ComplianceView
        {
            CatalogueName = catalogue.Name,
            CatalogueRelease = catalogue.Release,
            CatalogueProblem = catalogue.Problem,
            DefaultControlsSkipped = catalogue.DefaultControlsSkipped,
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
                    Evaluated = bound.IsEvaluated,
                    NotEvaluatedReason = bound.NotEvaluatedReason,
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
                .ThenBy(f => f.EntityName, StringComparer.OrdinalIgnoreCase),
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
