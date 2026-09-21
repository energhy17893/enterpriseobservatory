using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

public sealed record ReportSubscriptionView
{
    public required string Id { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    public required string Frequency { get; init; }

    public required string DayOfWeek { get; init; }

    public required int HourLocal { get; init; }

    public required string TimeZoneId { get; init; }

    public required string Kind { get; init; }

    public required bool IsEnabled { get; init; }

    public DateTimeOffset? LastSentUtc { get; init; }

    public string? LastError { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public string? LastModifiedBy { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed record ReportSubscriptionCommand
{
    public required IReadOnlyList<string> Recipients { get; init; }

    public string Frequency { get; init; } = nameof(ReportFrequency.Daily);

    public string DayOfWeek { get; init; } = nameof(System.DayOfWeek.Monday);

    public int HourLocal { get; init; } = 7;

    public string TimeZoneId { get; init; } = "UTC";

    public string Kind { get; init; } = nameof(ReportKind.Alerts);

    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Standing subscriptions to scheduled email reports (roadmap M5.4).
/// </summary>
/// <remarks>
/// Operator-or-Administrator, unlike <see cref="EmailApi"/>'s Administrator-only
/// SMTP settings. Declaring "I want the weekly alert digest in my inbox" is an
/// operational act, the same shape <see cref="MaintenanceApi"/> makes for
/// declaring a maintenance window — the person who wants the report is not
/// necessarily the person who is trusted to reconfigure the relay every
/// subscriber sends through.
/// </remarks>
public static class ReportsApi
{
    public static IEndpointRouteBuilder MapReportsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var reports = endpoints
            .MapGroup("/api/reports/subscriptions")
            .RequireAuthorization(ObservatoryApi.Policies.Operator);

        reports.MapGet("/", (IReportSubscriptionStore store) =>
                store.All.OrderBy(s => s.CreatedUtc).Select(ToView))
            .WithName("GetReportSubscriptions");

        reports.MapPost("/", (
                HttpContext context, IReportSubscriptionStore store, ReportSubscriptionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                if (Parse(Guid.NewGuid().ToString("n"), command, actor.Name, DateTimeOffset.UtcNow)
                    is not { } subscription)
                {
                    return Problem(["The frequency, day of week or kind was not recognised."]);
                }

                var problems = subscription.Validate();

                if (problems.Count > 0)
                {
                    return Problem(problems);
                }

                return store.Add(subscription)
                    ? Results.Ok(ToView(subscription))
                    : Problem(["A subscription with that id already exists."], StatusCodes.Status409Conflict);
            })
            .WithName("AddReportSubscription");

        reports.MapPut("/{id}", (
                HttpContext context, IReportSubscriptionStore store, string id, ReportSubscriptionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                if (store.Find(id) is not { } existing)
                {
                    return Results.NotFound();
                }

                if (!MayChange(existing, actor, context))
                {
                    return Forbidden();
                }

                if (Parse(id, command, existing.CreatedBy, existing.CreatedUtc) is not { } subscription)
                {
                    return Problem(["The frequency, day of week or kind was not recognised."]);
                }

                // Who touched it and when, for the audit trail; the previous
                // editor's stamp is not worth keeping once this one lands.
                // Sending status (LastSentUtc/LastError) is deliberately left
                // off here -- IReportSubscriptionStore.Update keeps whatever
                // it currently holds, under its own lock, because a copy read
                // before this handler ran cannot know whether a dispatch
                // claimed the subscription in between.
                subscription = subscription with
                {
                    LastModifiedBy = actor.AuditName,
                    LastModifiedUtc = DateTimeOffset.UtcNow,
                };

                var problems = subscription.Validate();

                if (problems.Count > 0)
                {
                    return Problem(problems);
                }

                if (!store.Update(subscription))
                {
                    return Results.NotFound();
                }

                // Read back rather than echoing the local copy: it still
                // carries whatever LastSentUtc/LastError this handler
                // started with, and the store -- not this request -- decided
                // what those two fields actually ended up holding.
                return Results.Ok(ToView(store.Find(id)!));
            })
            .WithName("UpdateReportSubscription");

        reports.MapDelete("/{id}", (HttpContext context, IReportSubscriptionStore store, string id) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                if (store.Find(id) is not { } existing)
                {
                    return Results.NotFound();
                }

                if (!MayChange(existing, actor, context))
                {
                    return Forbidden();
                }

                return store.Remove(id) ? Results.NoContent() : Results.NotFound();
            })
            .WithName("RemoveReportSubscription");

        return endpoints;
    }

    /// <summary>
    /// Whether <paramref name="actor"/> may edit or delete <paramref name="existing"/>.
    /// </summary>
    /// <remarks>
    /// The creator, or an Administrator: the same line
    /// <see cref="EmailApi"/>'s SMTP settings draw between "wants the
    /// report" and "is trusted to redirect where every subscriber's mail
    /// goes" -- an Operator who did not create a subscription can otherwise
    /// repoint someone else's standing report at an address of their
    /// choosing, unnoticed, because nothing recorded who changed it.
    /// </remarks>
    private static bool MayChange(ReportSubscription existing, OperatorIdentity actor, HttpContext context) =>
        string.Equals(existing.CreatedBy, actor.Name, StringComparison.Ordinal) ||
        AuthenticationApi.RoleOf(context.User) == Role.Administrator;

    private static IResult Forbidden() => Results.Problem(
        title: "Not applied",
        detail: "Only the subscription's creator or an Administrator may change or remove it.",
        statusCode: StatusCodes.Status403Forbidden);

    private static ReportSubscription? Parse(
        string id, ReportSubscriptionCommand command, string createdBy, DateTimeOffset createdUtc)
    {
        // Enum.TryParse alone accepts a numeric string outside the named
        // range -- "7" parses as a DayOfWeek nothing names -- so IsDefined
        // is checked too; TryParse only supplies the type conversion.
        if (!Enum.TryParse<ReportFrequency>(command.Frequency, out var frequency) ||
            !Enum.IsDefined(frequency) ||
            !Enum.TryParse<DayOfWeek>(command.DayOfWeek, out var dayOfWeek) ||
            !Enum.IsDefined(dayOfWeek) ||
            !Enum.TryParse<ReportKind>(command.Kind, out var kind) ||
            !Enum.IsDefined(kind))
        {
            return null;
        }

        return new ReportSubscription
        {
            Id = id,
            Recipients = [.. command.Recipients.Select(r => r.Trim())],
            Schedule = new ReportSchedule
            {
                Frequency = frequency,
                DayOfWeek = dayOfWeek,
                HourLocal = command.HourLocal,
                TimeZoneId = command.TimeZoneId?.Trim() is { Length: > 0 } zone ? zone : "UTC",
            },
            Kind = kind,
            IsEnabled = command.IsEnabled,
            CreatedBy = createdBy,
            CreatedUtc = createdUtc,
        };
    }

    private static IResult Problem(IReadOnlyList<string> problems, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Problem(title: "Not applied", detail: string.Join(" ", problems), statusCode: statusCode);

    private static ReportSubscriptionView ToView(ReportSubscription subscription) => new()
    {
        Id = subscription.Id,
        Recipients = subscription.Recipients,
        Frequency = subscription.Schedule.Frequency.ToString(),
        DayOfWeek = subscription.Schedule.DayOfWeek.ToString(),
        HourLocal = subscription.Schedule.HourLocal,
        TimeZoneId = subscription.Schedule.TimeZoneId,
        Kind = subscription.Kind.ToString(),
        IsEnabled = subscription.IsEnabled,
        LastSentUtc = subscription.LastSentUtc,
        LastError = subscription.LastError,
        CreatedBy = subscription.CreatedBy,
        CreatedUtc = subscription.CreatedUtc,
        LastModifiedBy = subscription.LastModifiedBy,
        LastModifiedUtc = subscription.LastModifiedUtc,
    };
}
