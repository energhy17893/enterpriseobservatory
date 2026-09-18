using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Provenance the pipeline stamps rather than trusting a collector to set.
/// </summary>
/// <remarks>
/// Both fields decide behaviour and neither shows when it is missing.
/// <c>SourceInstanceId</c> decides whether an entity may be treated as
/// vanished; blank, nothing ever vanishes. The alert scope decides which
/// evaluation may resolve an alert; blank, the metric cycle clears every
/// inventory alert seconds after it is raised. Asking each collector author to
/// remember them would be asking to be caught out later.
/// </remarks>
public class CollectionAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private static CollectionPolicy Fast { get; } = CollectionPolicy.Default with { MaxRetries = 0 };

    [Fact]
    public async Task Every_entity_carries_the_id_of_the_source_that_reported_it()
    {
        var source = new AttributionInventorySource("vc-1")
        {
            Snapshot = () => new InventorySnapshot
            {
                SourceInstanceId = "vc-1",
                ReadAtUtc = T0,
                // Reported without provenance, as every collector does.
                Entities = [Host("host-1"), Host("host-2")],
            },
        };

        var result = await new InventoryCollectionPipeline(new FixedClock(T0))
            .RunAsync([source], [], Fast, CancellationToken.None);

        Assert.All(
            result.Snapshots.SelectMany(s => s.Entities),
            e => Assert.Equal("vc-1", e.SourceInstanceId));
    }

    [Fact]
    public async Task Alerts_a_collector_reported_belong_to_the_inventory_scope()
    {
        var source = new AttributionInventorySource("vc-1")
        {
            Snapshot = () => new InventorySnapshot
            {
                SourceInstanceId = "vc-1",
                ReadAtUtc = T0,
                Alerts = [Fault("vc-1")],
            },
        };

        var result = await new InventoryCollectionPipeline(new FixedClock(T0))
            .RunAsync([source], [], Fast, CancellationToken.None);

        Assert.All(
            result.Snapshots.SelectMany(s => s.Alerts),
            a => Assert.Equal(AlertScopes.Inventory, a.Scope));
    }

    [Fact]
    public async Task An_unreachable_inventory_source_reports_in_the_inventory_scope()
    {
        var source = new AttributionInventorySource("vc-1");

        var result = await new InventoryCollectionPipeline(new FixedClock(T0))
            .RunAsync([source], [], Fast, CancellationToken.None);

        Assert.All(result.CollectionAlerts, a => Assert.Equal(AlertScopes.Inventory, a.Scope));
    }

    [Fact]
    public async Task An_unreachable_metric_source_reports_in_the_observation_scope()
    {
        var source = new AttributionObservationSource("vc-1");

        var result = await new ObservationCollectionPipeline(new FixedClock(T0))
            .RunAsync([source], [], Fast, CancellationToken.None);

        Assert.NotEmpty(result.CollectionAlerts);
        Assert.All(result.CollectionAlerts, a => Assert.Equal(AlertScopes.Observation, a.Scope));
    }

    [Fact]
    public async Task The_two_roles_track_health_separately()
    {
        var inventory = new AttributionInventorySource("vc-1");
        var clock = new FixedClock(T0);

        var result = await new InventoryCollectionPipeline(clock)
            .RunAsync([inventory], [], Fast, CancellationToken.None);

        Assert.Equal(CollectorRole.Inventory, Assert.Single(result.Health).Role);

        var metrics = new AttributionObservationSource("vc-1");

        var metricResult = await new ObservationCollectionPipeline(clock)
            .RunAsync([metrics], [], Fast, CancellationToken.None);

        Assert.Equal(CollectorRole.Observation, Assert.Single(metricResult.Health).Role);
    }

    [Fact]
    public async Task Prior_health_from_the_other_role_is_not_picked_up()
    {
        // Otherwise a metric collector would inherit the inventory collector's
        // failure count and open its circuit breaker on somebody else's
        // problem.
        var metrics = new AttributionObservationSource("vc-1")
        {
            Batch = () => new ObservationBatch { SourceInstanceId = "vc-1", ReadAtUtc = T0 },
        };

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 9,
                LastSuccessUtc = T0.AddMinutes(-1),
            },
        };

        var result = await new ObservationCollectionPipeline(new FixedClock(T0))
            .RunAsync([metrics], prior, Fast, CancellationToken.None);

        Assert.Equal(1, metrics.Attempts);
        Assert.Equal(0, Assert.Single(result.Health).ConsecutiveFailures);
    }

    private static Entity Host(string name) => new()
    {
        Id = new EntityId(name),
        Kind = EntityKind.EsxiHost,
        DisplayName = name,
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
    };

    private static AlertDefinition Fault(string source) => new()
    {
        Fingerprint = AlertFingerprint.Create(source, "Fan failed", "Hardware", "fan-1", "fan"),
        Severity = AlertSeverity.Critical,
        Title = "Fan failed",
        Category = "Hardware",
        Source = source,
    };

    private sealed class AttributionInventorySource(string instanceId) : IInventorySource
    {
        public string InstanceId { get; } = instanceId;

        public Func<InventorySnapshot>? Snapshot { get; init; }

        public Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Snapshot is null
                ? throw new InvalidOperationException("unreachable")
                : Task.FromResult(Snapshot());
    }

    private sealed class AttributionObservationSource(string instanceId) : IObservationSource
    {
        public string InstanceId { get; } = instanceId;

        public Func<ObservationBatch>? Batch { get; init; }

        public int Attempts { get; private set; }

        public Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
        {
            Attempts++;

            return Batch is null
                ? throw new InvalidOperationException("unreachable")
                : Task.FromResult(Batch());
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
