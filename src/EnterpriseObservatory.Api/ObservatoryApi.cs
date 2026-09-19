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

        var api = endpoints.MapGroup("/api");

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
                OperationsOptions options,
                AlertCommand command) =>
            Act(context, model, options, actor =>
                operations.Acknowledge(Fingerprint(command.Fingerprint), actor)))
            .WithName("AcknowledgeAlert");

        api.MapPost("/alerts/clear", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                OperationsOptions options,
                AlertCommand command) =>
            Act(context, model, options, actor =>
                operations.Clear(Fingerprint(command.Fingerprint), actor)))
            .WithName("ClearAlert");

        api.MapPost("/alerts/silence", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                OperationsOptions options,
                SilenceCommand command) =>
            Act(context, model, options, actor =>
                operations.Silence(Fingerprint(command.Fingerprint), actor, command.UntilUtc)))
            .WithName("SilenceAlert");

        // Anything under /api that matched no endpoint is a 404, not the SPA.
        // Without this the catch-all that serves the interface answers an
        // unmatched API request with a 200 carrying HTML, and the client's
        // symptom is a JSON parse error a long way from the cause.
        endpoints.MapFallback("/api/{**rest}", () => Results.NotFound())
            .WithName("ApiNotFound");

        return endpoints;
    }

    /// <summary>
    /// Runs an operator command, if the product is willing to attribute it.
    /// </summary>
    /// <remarks>
    /// The attribution check comes first, before the alert is even looked up.
    /// Refusing after doing the work would tell an unauthenticated caller
    /// whether an alert exists, and would be a longer road to the same no.
    /// </remarks>
    private static IResult Act(
        HttpContext context,
        ReadModel model,
        OperationsOptions options,
        Func<OperatorIdentity, AlertActionResult> command)
    {
        if (OperatorResolver.Resolve(context, options) is not { } actor)
        {
            return OperatorResolver.Refused();
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
