using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

internal sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeInventorySource(string instanceId) : IInventorySource
{
    public string InstanceId { get; } = instanceId;

    /// <summary>What the next read returns, or null to throw.</summary>
    public Func<InventorySnapshot>? Behaviour { get; set; }

    public int Attempts { get; private set; }

    public Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        Attempts++;

        return Behaviour is null
            ? throw new InvalidOperationException("unreachable")
            : Task.FromResult(Behaviour());
    }
}

internal sealed class FakeObservationSource(string instanceId) : IObservationSource
{
    public string InstanceId { get; } = instanceId;

    public Func<ObservationBatch>? Behaviour { get; set; }

    public int Attempts { get; private set; }

    public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken)
    {
        Attempts++;

        return Behaviour is null
            ? throw new InvalidOperationException("unreachable")
            : Task.FromResult(Behaviour());
    }
}

/// <summary>An inventory-only source's observation stand-in (IRoleNotApplicable, ADR-0026).</summary>
internal sealed class FakeRoleNotApplicableSource(string instanceId) : IObservationSource, IRoleNotApplicable
{
    public string InstanceId { get; } = instanceId;

    public string Reason => $"'{InstanceId}' is not being polled: inventory only.";

    public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);
}

internal sealed class RecordingNotifier : IAlertNotifier
{
    public List<AlertInstance> Dispatched { get; } = [];

    public Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken)
    {
        Dispatched.AddRange(pending);
        return Task.CompletedTask;
    }
}

internal sealed class FailingNotifier : IAlertNotifier
{
    public int Calls { get; private set; }

    public Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("the pager is down");
    }
}

/// <summary>A store whose disk is full, for the cycle that must survive it.</summary>
internal sealed class FailingObservationStore : IObservationStore
{
    public void Append(IReadOnlyList<Observation> observations) =>
        throw new IOException("the disk is full");

    public SeriesResult Query(SeriesQuery query) =>
        new() { Key = query.Key, Resolution = SeriesResolution.Raw, Exists = false };

    public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

    public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) => new();
}
