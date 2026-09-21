using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
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

        // The grouped view of the same instances the flat list shows. Both are
        // always available: grouping that cannot be switched off is a way of
        // hiding things.
        api.MapGet("/events", (ReadModel model) => model.Events())
            .WithName("GetEvents");

        // What vCenter itself reported happening, as it said it. Not the
        // grouped alerts above, which share the word: those are this product's
        // conclusions, these are the source's record.
        api.MapGet("/vcenter-events", (IEventStore store, string? source, int? limit) =>
                EventFeed.Build(store, source, limit))
            .WithName("GetVcenterEvents");

        api.MapGet("/collectors", (ReadModel model) => model.Collectors())
            .WithName("GetCollectors");
        api.MapGet("/coverage", (ReadModel model) => model.Coverage())
            .WithName("GetCoverage");

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

        // --- reports ---------------------------------------------------------
        //
        // Read for any signed-in user, same as the alert list this is a
        // filtered slice of: an operator deciding whether to print a report
        // is not a more sensitive act than reading the inbox it comes from.

        api.MapGet("/reports/alerts", (
                ReadModel model,
                AlertSeverity? severity,
                AlertLifecycleState? state,
                string? category,
                string? source,
                DateTimeOffset? from,
                DateTimeOffset? to) =>
            model.AlertsReport(severity, state, category, source, from, to))
            .WithName("GetAlertsReport");

        api.MapGet("/reports/alerts.csv", (
                ReadModel model,
                AlertSeverity? severity,
                AlertLifecycleState? state,
                string? category,
                string? source,
                DateTimeOffset? from,
                DateTimeOffset? to) =>
        {
            var report = model.AlertsReport(severity, state, category, source, from, to);
            var csv = AlertsReportCsv.Write(report.Rows);
            var fileName = $"alerts-{report.GeneratedAtUtc:yyyyMMdd-HHmm}.csv";

            return Results.File(CsvWriter.ToUtf8WithBom(csv), "text/csv", fileName);
        })
            .WithName("GetAlertsReportCsv");

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

        // The same three verbs, applied to a selection. Twenty alerts from one
        // failed switch is one problem an operator is taking on, not twenty
        // decisions.

        api.MapPost("/alerts/acknowledge-many", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                BulkAlertCommand command) =>
            ActMany(context, model, actor =>
                operations.AcknowledgeMany(Fingerprints(command.Fingerprints), actor)))
            .RequireAuthorization(Policies.Operator)
            .WithName("AcknowledgeAlerts");

        api.MapPost("/alerts/clear-many", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                BulkAlertCommand command) =>
            ActMany(context, model, actor =>
                operations.ClearMany(Fingerprints(command.Fingerprints), actor)))
            .RequireAuthorization(Policies.Operator)
            .WithName("ClearAlerts");

        api.MapPost("/alerts/silence-many", (
                HttpContext context,
                ReadModel model,
                AlertOperations operations,
                BulkSilenceCommand command) =>
            ActMany(context, model, actor =>
                operations.SilenceMany(Fingerprints(command.Fingerprints), actor, command.UntilUtc)))
            .RequireAuthorization(Policies.Operator)
            .WithName("SilenceAlerts");

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

    /// <summary>Runs an operator command against several alerts.</summary>
    /// <remarks>
    /// A bulk action never partly fails: either the whole request is refused —
    /// a silence deadline in the past refuses all of it — or every alert still
    /// present is changed together. Alerts that have gone are counted and
    /// reported, which is a different thing from failing.
    /// </remarks>
    private static IResult ActMany(
        HttpContext context,
        ReadModel model,
        Func<OperatorIdentity, BulkActionResult> command)
    {
        if (AuthenticationApi.OperatorFor(context) is not { } actor)
        {
            return Results.Unauthorized();
        }

        var result = command(actor);

        var view = new BulkActionView
        {
            Applied = result.Applied,
            Requested = result.Requested,
            Missing = result.Missing,
            Alerts = [.. result.Changed.Select(model.Present)],
            Refusal = result.Applied ? null : result.Refusal.ToString(),
            RecordedAs = actor.AuditName,
        };

        return result.Applied ? Results.Ok(view) : Results.BadRequest(view);
    }

    /// <summary>The most alerts one command may touch.</summary>
    /// <remarks>
    /// A bound, because the whole batch is held under one lock while it is
    /// applied and a collection cycle waits behind it. A thousand at once would
    /// be somebody scripting against the API rather than an operator clearing a
    /// screen.
    /// </remarks>
    public const int MaxBulkSize = 200;

    private static IReadOnlyList<AlertFingerprint> Fingerprints(IReadOnlyList<string> values) =>
        [.. values.Take(MaxBulkSize).Select(AlertFingerprint.Restore)];

    private static AlertFingerprint Fingerprint(string value) => AlertFingerprint.Restore(value);
}
