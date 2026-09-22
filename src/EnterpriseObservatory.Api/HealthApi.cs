using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
/// Reflects freshness, not process liveness: a process that is up but has not
/// read anything in an hour must not answer 200 just because it can still
/// accept a request.
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
                IClock clock) =>
            {
                var report = HealthAssessment.Assess(
                    collectors.Current,
                    monitoring,
                    healthOptions,
                    gaps.CountsByState(),
                    clock.UtcNow);

                return report.Status == ServiceHealthStatus.Unhealthy
                    ? Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Json(report);
            })
            .AllowAnonymous()
            .WithName("GetHealth");

        return endpoints;
    }
}
