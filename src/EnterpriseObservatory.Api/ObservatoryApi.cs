using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>
/// The interface's read surface.
/// </summary>
/// <remarks>
/// <para>
/// A backend-for-frontend rather than a generic REST API. ADR-0006 accepted
/// this layer as the cost of choosing a separate SPA over Blazor, so it should
/// earn that cost: each endpoint answers one screen's question completely, and
/// the client never joins data itself. An alert list that made the browser
/// fetch entity names one by one would be a worse product, not a purer API.
/// </para>
/// <para>
/// Mostly reads. The writes are the operator's four verbs against an alert, and
/// every one of them is attributed: a change nobody can be named for would
/// make the audit trail worse than empty, because it would look authoritative.
/// See <see cref="OperatorResolver"/> and ADR-0013.
/// </para>
/// </remarks>
public static class ObservatoryApi
{
    /// <summary>Maps every read endpoint under <c>/api</c>.</summary>
    public static IEndpointRouteBuilder MapObservatoryApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Everything here needs an account. Reads are not harmless in this
        // product: the estate's hostnames, addresses and serial numbers are
        // exactly what somebody would want before attacking it.
        var api = endpoints.MapGroup("/api").RequireAuthorization();

        api.MapGet("/overview", (ReadModel model) => model.Overview())
            .WithName("GetOverview");

        api.MapGet("/alerts", (
                ReadModel model,
                AlertSeverity? severity,
                AlertLifecycleState? state,
                string? category,
                string? source,
                string? search,
                int? offset,
                int? limit) =>
            model.Alerts(severity, state, category, source, search, offset ?? 0, limit ?? 50))
            .WithName("GetAlerts");

        api.MapGet("/entities", (
                ReadModel model,
                EntityKind? kind,
                HealthState? health,
                string? source,
                string? search,
                bool? includeVanished,
                int? offset,
                int? limit) =>
            model.Entities(
                kind, health, source, search, includeVanished ?? false, offset ?? 0, limit ?? 50))
            .WithName("GetEntities");

        api.MapGet("/entities/{id}", (ReadModel model, string id) =>
                model.Entity(id) is { } detail ? Results.Ok(detail) : Results.NotFound())
            .WithName("GetEntity");

        api.MapGet("/collectors", (ReadModel model) => model.Collectors())
            .WithName("GetCollectors");

        api.MapGet("/entities/{id}/series", (ReadModel model, string id) => model.SeriesFor(id))
            .WithName("GetEntitySeries");

        api.MapGet("/entities/{id}/series/{counter}", (
                ReadModel model,
                string id,
                string counter,
                string? instance,
                DateTimeOffset? from,
                DateTimeOffset? to,
                int? maxPoints) =>
            model.Series(id, counter, instance, from, to, maxPoints ?? 720))
            .WithName("GetSeries");

        // --- operator commands ---------------------------------------------
        //
        // POST rather than PATCH on a resource, and the fingerprint in the body
        // rather than the path. These are things an operator does, not fields
        // they edit; and an opaque identifier in a URL segment is the mistake
        // that made every entity page return the wrong thing.

        api.MapPost("/alerts/acknowledge", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                AlertCommand command) =>
            Act(context, model, actor =>
                operations.Acknowledge(Fingerprint(command.Fingerprint), actor)))
            .RequireAuthorization(Policies.Operator)
            .WithName("AcknowledgeAlert");

        api.MapPost("/alerts/clear", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                AlertCommand command) =>
            Act(context, model, actor =>
                operations.Clear(Fingerprint(command.Fingerprint), actor)))
            .RequireAuthorization(Policies.Operator)
            .WithName("ClearAlert");

        api.MapPost("/alerts/silence", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                SilenceCommand command) =>
            Act(context, model, actor =>
                operations.Silence(Fingerprint(command.Fingerprint), actor, command.UntilUtc)))
            .RequireAuthorization(Policies.Operator)
            .WithName("SilenceAlert");

        // Anything under /api that matched no endpoint is a 404, not the SPA.
        // Without this the catch-all that serves the interface answers an
        // unmatched API request with a 200 carrying HTML, and the client's
        // symptom is a JSON parse error a long way from the cause.
        endpoints.MapFallback("/api/{**rest}", () => Results.NotFound())
            .WithName("ApiNotFound");

        return endpoints;
    }

    /// <summary>The authorization policies the write endpoints require.</summary>
    /// <remarks>
    /// Named rather than repeated inline, so that adding a command cannot
    /// quietly add one that nobody has to be anybody to run.
    /// </remarks>
    public static class Policies
    {
        public const string Operator = nameof(Operator);

        public const string Administrator = nameof(Administrator);
    }

    /// <summary>
    /// Runs an operator command and reports what it did.
    /// </summary>
    /// <remarks>
    /// The identity is already established by the time this runs — an
    /// unauthenticated request never reaches a handler — so the only thing left
    /// is recording who it was.
    /// </remarks>
    private static IResult Act(
        HttpContext context,
        ReadModel model,
        Func<OperatorIdentity, AlertActionResult> command)
    {
        if (AuthenticationApi.OperatorFor(context) is not { } actor)
        {
            // Should be unreachable: the policy ran first. Kept because an
            // unattributed change is worse than a refused one, and a future
            // endpoint added without its policy would otherwise write "null"
            // into the audit trail.
            return Results.Unauthorized();
        }

        var result = command(actor);

        var view = new AlertActionView
        {
            Applied = result.Applied,
            Alert = result.Instance is { } instance ? model.Present(instance) : null,
            Refusal = result.Applied ? null : result.Refusal.ToString(),
            RecordedAs = actor.AuditName,
            ActorVerified = actor.IsVerified,
        };

        // A command against an alert that is no longer there is a 404 rather
        // than a failure: an operator acting on a screen thirty seconds old has
        // not made a mistake, and the interface should simply refresh.
        return result.Refusal == AlertActionRefusal.NotFound
            ? Results.NotFound(view)
            : result.Applied ? Results.Ok(view) : Results.BadRequest(view);
    }

    private static AlertFingerprint Fingerprint(string value) => AlertFingerprint.Restore(value);
}
