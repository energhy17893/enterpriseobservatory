using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseObservatory.Api;

/// <summary>
/// <c>/health</c>: whether the product is still reading anything, for an
/// external monitor to page on.
/// </summary>
/// <remarks>
/// <para>
/// Package D (docs/feature-roadmap.md "D — Öz-izleme"), and the answer to the
/// gap docs/live-verification.md §9 measured: the service was twice dead for
/// hours on 21–22 September 2026 and nothing outside its own log said so. The
/// two "Collector unreachable" warnings landed in host.log only — this is
/// what should have paged instead.
/// </para>
/// <para>
/// Deliberately unauthenticated, unlike every other endpoint this product
/// exposes (see <see cref="ObservatoryApi.MapObservatoryApi"/>): the estate's
/// data is worth protecting, but "is this process able to page an operator"
/// has to be answered by something that does not itself need a login, or the
/// login service being down looks identical to the monitoring being down.
/// </para>
/// <para>
/// Reflects whether the product's cycles run, not process liveness: a process
/// that is up but has not attempted a read in an hour must not answer 200 just
/// because it can still accept a request.
/// </para>
/// <para>
/// And not the union of its sources either (Prometheus: a target's <c>up</c>
/// is not the server's own readiness). An unreachable customer vCenter is the
/// product working — it raises its own alert — so it shows in
/// <c>sources[]</c>, not in the status code. 503 means only this: some role
/// attempted nothing within <see cref="HealthOptions.UnhealthyAfter"/>, or the
/// store queue's last write failed. See <see cref="HealthAssessment"/>.
/// </para>
/// </remarks>
public static class HealthApi
{
    public static IEndpointRouteBuilder MapHealthApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", (
                ICollectorHealthStore collectors,
                ICollectionGapStore gaps,
                MonitoringOptions monitoring,
                HealthOptions healthOptions,
                IClock clock,
                HttpContext context) =>
            {
                // Optional: a host without the store queue has no write to fail.
                var storeQueue = context.RequestServices.GetService<IStoreQueueMetrics>();

                var report = HealthAssessment.Assess(
                    collectors.Current,
                    monitoring,
                    healthOptions,
                    gaps.CountsByState(),
                    clock.UtcNow,
                    storeQueue?.Snapshot().LastFailure);

                return report.Status == ServiceHealthStatus.Unhealthy
                    ? Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Json(report);
            })
            .AllowAnonymous()
            .WithName("GetHealth");

        return endpoints;
    }
}
