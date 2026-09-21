using EnterpriseObservatory.Application.Reporting;
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

        reports.MapPut("/{id}", (IReportSubscriptionStore store, string id, ReportSubscriptionCommand command) =>
            {
                if (store.Find(id) is not { } existing)
                {
                    return Results.NotFound();
                }

                if (Parse(id, command, existing.CreatedBy, existing.CreatedUtc) is not { } subscription)
                {
                    return Problem(["The frequency, day of week or kind was not recognised."]);
                }

                // Sending status is the store's to keep, not the form's to
                // overwrite: an edit does not un-fail a subscription and must
                // not erase the last attempt's timestamp.
                subscription = subscription with
                {
                    LastSentUtc = existing.LastSentUtc,
                    LastError = existing.LastError,
                };

                var problems = subscription.Validate();

                if (problems.Count > 0)
                {
                    return Problem(problems);
                }

                return store.Update(subscription) ? Results.Ok(ToView(subscription)) : Results.NotFound();
            })
            .WithName("UpdateReportSubscription");

        reports.MapDelete("/{id}", (IReportSubscriptionStore store, string id) =>
                store.Remove(id) ? Results.NoContent() : Results.NotFound())
            .WithName("RemoveReportSubscription");

        return endpoints;
    }

    private static ReportSubscription? Parse(
        string id, ReportSubscriptionCommand command, string createdBy, DateTimeOffset createdUtc)
    {
        if (!Enum.TryParse<ReportFrequency>(command.Frequency, out var frequency) ||
            !Enum.TryParse<DayOfWeek>(command.DayOfWeek, out var dayOfWeek) ||
            !Enum.TryParse<ReportKind>(command.Kind, out var kind))
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
    };
}
