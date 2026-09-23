using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives today's vSphere inventory source (<see cref="VsphereInventorySource"/>)
/// through a fake <see cref="IVsphereInventoryApi"/> — the "one transport
/// handle" ADR-0025 describes handing to a collector.
/// </summary>
public sealed class VsphereInventoryContractFixture : IInventoryContractFixture
{
    public string InstanceId => "vc-contract";

    public IInventorySource CreateHealthy(int hostCount) =>
        new VsphereInventorySource(new FixedPayloadApi(InstanceId, Payload(hostCount, 0)), new TestClock());

    public IInventorySource CreateSlow() =>
        new VsphereInventorySource(new SlowApi(InstanceId), new TestClock());

    public IInventorySource CreateWithFailures(int hostCount, int failureCount) =>
        new VsphereInventorySource(
            new FixedPayloadApi(InstanceId, Payload(hostCount, failureCount)), new TestClock());

    public IReadOnlyList<string> AcceptedTargets(InventorySnapshot snapshot) =>
        [.. snapshot.Entities.Where(e => e.Kind == EntityKind.EsxiHost).Select(e => e.Id.Value)];

    private static VsphereInventoryPayload Payload(int hostCount, int failureCount) => new()
    {
        VCenterName = "vc-contract.corp.local",
        Hosts = [.. Enumerable.Range(1, hostCount).Select(i => new VsphereHost
        {
            MoRef = $"host-{i}",
            Name = $"esx{i:00}.corp.local",
        })],
        Failures = [.. Enumerable.Range(1, failureCount).Select(i => new VsphereReadFailure
        {
            Target = $"HostSystem host-fail-{i}: config.option",
            Detail = "unreadable",
        })],
    };

    /// <summary>A transport that always answers the same payload.</summary>
    private sealed class FixedPayloadApi(string instanceId, VsphereInventoryPayload payload) : IVsphereInventoryApi
    {
        public string InstanceId => instanceId;

        public Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(payload);
    }

    /// <summary>
    /// A transport that never answers within any reasonable policy timeout —
    /// an uncooperative vendor client that does not observe cancellation
    /// either, which is exactly the case <c>SourceRunner</c>'s hard timeout
    /// exists for (see its remarks on <c>AbandonedReadException</c>).
    /// </summary>
    private sealed class SlowApi(string instanceId) : IVsphereInventoryApi
    {
        public string InstanceId => instanceId;

        public async Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return new VsphereInventoryPayload { VCenterName = instanceId };
        }
    }
}
