using EnterpriseObservatory.Api.Projections;
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
/// Read-only throughout. Acknowledging and clearing alerts are writes and will
/// arrive with the commands that carry an operator's identity; until then, the
/// product shows and does not change.
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

        // Anything under /api that matched no endpoint is a 404, not the SPA.
        // Without this the catch-all that serves the interface answers an
        // unmatched API request with a 200 carrying HTML, and the client's
        // symptom is a JSON parse error a long way from the cause.
        endpoints.MapFallback("/api/{**rest}", () => Results.NotFound())
            .WithName("ApiNotFound");

        return endpoints;
    }
}
