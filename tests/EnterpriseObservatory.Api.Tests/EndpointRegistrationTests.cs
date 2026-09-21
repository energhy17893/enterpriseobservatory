using EnterpriseObservatory.Api;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain.Compliance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// Every endpoint the product maps, registered together the way the host does.
/// </summary>
/// <remarks>
/// <para>
/// Written after the web interface went down on the live estate with 953 tests
/// green and CI passing. A copied line gave the coverage endpoint the name
/// "GetCollectors", which the collectors endpoint already had; ASP.NET Core
/// refuses duplicate endpoint names when it builds the matcher, and it builds
/// the matcher on the first request. So every request failed — not only the
/// new one — and nothing in the suite had ever mapped the endpoints at all.
/// </para>
/// <para>
/// This is the smallest guard for that class, not the composition-root smoke
/// suite the roadmap still owes. It proves the endpoints can coexist; it does
/// not prove the host wires authentication, authorization or the stores
/// correctly, and it is not a substitute for a WebApplicationFactory test that
/// makes a real request.
/// </para>
/// </remarks>
public class EndpointRegistrationTests
{
    private static IReadOnlyList<Endpoint> MapEverything()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();

        // The services the handlers take as parameters, registered so the
        // parameter binder knows they are services. Without them it infers
        // each one as a request body, a GET with a body is refused, and every
        // test here fails in setup -- which the first version of this file
        // did, and which read exactly like the duplicate-name failure it was
        // meant to catch. A red test that fails for the wrong reason proves
        // nothing.
        //
        // Registered with factories that throw, because the binder only asks
        // whether a type is a service and never builds one. No request is
        // made, so none is constructed; if a future change made one get
        // built here, the throw says so instead of silently wiring a fake.
        foreach (var type in new[]
        {
            // Mirrors the host's registrations in Program.cs. Kept as the whole
            // list rather than the handful the handlers happen to use today:
            // a handler that starts injecting something the host registers
            // must not fail here for a reason the host would not fail for.
            typeof(ReadModel),
            typeof(AccountService),
            typeof(AuthenticationService),
            typeof(AlertOperations),
            typeof(MaintenanceService),
            typeof(ComplianceService),
            typeof(ComplianceCatalogue),
            typeof(SourceConnectionCatalogue),
            typeof(MonitoringOptions),
            typeof(IConnectionProbe),
            typeof(ISourceCapabilityReader),
            typeof(ISourceConnectionStore),
            typeof(ISourceRegistry),
            typeof(IClock),
            typeof(IAlertNotifier),
            typeof(IAlertStateStore),
            typeof(ICollectorHealthStore),
            typeof(ICoverageStore),
            typeof(IEventStore),
            typeof(IEntityGraphStore),
            typeof(IObservationStore),
            typeof(IMaintenanceWindowStore),
            typeof(IComplianceStore),
            typeof(ISecretProtector),
            typeof(IUserAccountStore),
            typeof(ISmtpSettingsStore),
            typeof(IReportSubscriptionStore),
            typeof(IMailSender),
        })
        {
            builder.Services.AddSingleton(type, _ => throw new InvalidOperationException(
                $"{type.Name} was constructed by a registration test that makes no requests."));
        }

        var app = builder.Build();

        app.MapAuthenticationApi("setup-token-for-tests");
        app.MapAccountsApi();
        app.MapConnections();
        app.MapMaintenanceApi();
        app.MapComplianceApi();
        app.MapEmailApi();
        app.MapReportsApi();
        app.MapObservatoryApi();

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)];
    }

    [Fact]
    public void Every_endpoint_name_is_unique()
    {
        // The exact failure that took the interface down. Checked here rather
        // than discovered on the first request, because the first request is
        // an operator's.
        var names = MapEverything()
            .Select(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .OfType<string>()
            .ToList();

        var duplicates = names
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Endpoint names must be unique; duplicated: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Every_route_and_method_pair_is_mapped_once()
    {
        // The sibling mistake a copied line can make: two handlers on one
        // route, where the second silently shadows the first or the matcher
        // refuses the pair as ambiguous.
        var routes = MapEverything()
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => $"{m} {e.RoutePattern.RawText}"))
            .ToList();

        Assert.Equal(routes.Count, routes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_coverage_endpoint_is_mapped()
    {
        // A wiring check and nothing more. It does not build the matcher --
        // that happens on a real request, which needs a test host this suite
        // does not have -- so the two tests above are the ones that guard the
        // failure that took the interface down. This one only proves the
        // screen has something to call.
        var endpoints = MapEverything();

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.EndsWith("/coverage", StringComparison.Ordinal) == true);
    }
}
