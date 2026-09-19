using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>A maintenance window, as the interface shows it.</summary>
public sealed record MaintenanceWindowView
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Who declared it. See ADR-0013 on why this is recorded.</summary>
    public required string DeclaredBy { get; init; }

    public required DateTimeOffset DeclaredAtUtc { get; init; }

    /// <summary>Empty means the whole estate.</summary>
    public required IReadOnlyList<string> Entities { get; init; }

    /// <summary>Whether it is suppressing anything right now.</summary>
    public required bool Active { get; init; }

    /// <summary>Whether it has not started yet.</summary>
    public required bool Scheduled { get; init; }
}

public sealed record DeclareWindowCommand
{
    public required string Title { get; init; }

    public string Reason { get; init; } = string.Empty;

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// The entities it covers. Empty means the whole estate.
    /// </summary>
    /// <remarks>
    /// Estate-wide is expressible because a datacentre power test really is
    /// that. It is also blunt — everything goes quiet, including the failure
    /// the test was meant to reveal — so the interface says so.
    /// </remarks>
    public IReadOnlyList<string> Entities { get; init; } = [];
}

public sealed record EndWindowCommand
{
    public required string Id { get; init; }
}

/// <summary>
/// Declaring planned work.
/// </summary>
/// <remarks>
/// A window suppresses notification and never observation: the alert is still
/// raised and still visible, carrying the id of the window that silenced it.
/// Hiding it would make the window useless afterwards as an account of what
/// actually broke during the work.
/// </remarks>
public static class MaintenanceApi
{
    public static IEndpointRouteBuilder MapMaintenanceApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var windows = endpoints.MapGroup("/api/maintenance").RequireAuthorization();

        windows.MapGet("/", (MaintenanceService service) =>
                service.All()
                    .OrderByDescending(w => w.StartUtc)
                    .Select(w => ToView(w, DateTimeOffset.UtcNow)))
            .WithName("GetMaintenanceWindows");

        // Declaring one is an operational act, not an administrative one: the
        // person doing the work is the person who knows when it starts.
        windows.MapPost("/declare", (
                HttpContext context,
                MaintenanceService service,
                DeclareWindowCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                return Respond(service.Declare(
                    command.Title,
                    command.Reason,
                    command.StartUtc,
                    command.EndUtc,
                    [.. command.Entities.Select(id => new EntityId(id))],
                    actor));
            })
            .RequireAuthorization(ObservatoryApi.Policies.Operator)
            .WithName("DeclareMaintenanceWindow");

        windows.MapPost("/end", (MaintenanceService service, EndWindowCommand command) =>
                Respond(service.End(command.Id)))
            .RequireAuthorization(ObservatoryApi.Policies.Operator)
            .WithName("EndMaintenanceWindow");

        return endpoints;
    }

    private static IResult Respond(MaintenanceResult result) =>
        result.Applied
            ? Results.Ok(ToView(result.Window!, DateTimeOffset.UtcNow))
            : Results.Problem(
                title: "Not applied",
                detail: Explain(result.Failure),
                statusCode: result.Failure == MaintenanceFailure.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest);

    private static MaintenanceWindowView ToView(
        Domain.Alerts.MaintenanceWindow window, DateTimeOffset nowUtc) => new()
        {
            Id = window.Id,
            Title = window.Title,
            Reason = window.Reason,
            StartUtc = window.StartUtc,
            EndUtc = window.EndUtc,
            DeclaredBy = window.DeclaredBy,
            DeclaredAtUtc = window.DeclaredAtUtc,
            Entities = [.. window.Entities.Select(e => e.Value)],
            Active = window.IsActiveAt(nowUtc),
            Scheduled = window.IsScheduled(nowUtc),
        };

    private static string Explain(MaintenanceFailure failure) => failure switch
    {
        MaintenanceFailure.NotFound => "There is no window with that id.",
        MaintenanceFailure.BadSchedule =>
            "A window has to end after it starts, and after now. One that has already ended " +
            "suppresses nothing.",
        MaintenanceFailure.TooLong =>
            $"A window may run for at most {MaintenanceService.MaximumDuration.TotalDays:0} days. " +
            "Beyond that it is not planned work, it is switching the product off — and somebody " +
            "should have to say so again.",
        _ => "The window was not declared.",
    };
}
