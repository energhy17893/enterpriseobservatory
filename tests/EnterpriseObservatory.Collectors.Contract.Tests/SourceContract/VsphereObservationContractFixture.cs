using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives today's vSphere observation source (<see cref="VsphereObservationSource"/>)
/// through a fake <see cref="IVsphereApi"/> — the "one transport handle"
/// ADR-0025 describes handing to a collector.
/// </summary>
public sealed class VsphereObservationContractFixture : IObservationContractFixture
{
    public string InstanceId => "vc-contract";

    public IObservationSource CreateHealthy(int targetCount) => new VsphereObservationSource(
        new FakeApi(InstanceId),
        new FixedTargets(new VsphereSampleTargets
        {
            Hosts = [.. Enumerable.Range(1, targetCount).Select(i => $"host-{i}")],
        }),
        new TestClock());

    public IObservationSource CreateSlow() => new VsphereObservationSource(
        new FakeApi(InstanceId) { Slow = true },
        new FixedTargets(new VsphereSampleTargets { Hosts = ["host-1"] }),
        new TestClock());

    public IObservationSource CreateWithOneFailingEntityType() => new VsphereObservationSource(
        new FakeApi(InstanceId) { FaultOnType = VsphereEntityType.VirtualMachine },
        new FixedTargets(new VsphereSampleTargets
        {
            Hosts = ["host-1"],
            VirtualMachines = ["vm-1"],
        }),
        new TestClock());

    /// <summary>A target provider whose targets and resolution never change.</summary>
    private sealed class FixedTargets(VsphereSampleTargets targets) : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current => targets;

        public EntityId? ResolveEntity(string moRef) => new EntityId(moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => moRef;
    }

    /// <summary>
    /// A vCenter offering every counter the product wants — a real catalogue
    /// omitting any of them is a separate, already-covered case
    /// (<c>VsphereObservationSourceTests</c>) — and, on request, faulting one
    /// entity type's query outright.
    /// </summary>
    private sealed class FakeApi(string instanceId) : IVsphereApi
    {
        /// <summary>
        /// Every counter the product asks for, of either type. Anything not
        /// in this catalogue becomes its own "no such counter" failure, which
        /// would swamp the one failure a contract case is asking about.
        /// </summary>
        private static readonly IReadOnlyList<VsphereCounter> Catalog =
        [
            .. VsphereCounters.Host
                .Concat(VsphereCounters.VirtualMachine)
                .Distinct(StringComparer.Ordinal)
                .Select((key, index) =>
                {
                    var parts = key.Split('.');
                    return new VsphereCounter
                    {
                        Id = index + 1,
                        Group = parts[0],
                        Name = parts[1],
                        Rollup = VsphereCounter.ParseRollup(parts[2]),
                        Unit = "number",
                        Level = 1,
                    };
                }),
        ];

        public string InstanceId => instanceId;

        /// <summary>The entity type whose query throws, or null for none.</summary>
        public VsphereEntityType? FaultOnType { get; init; }

        /// <summary>
        /// True for a transport that never answers within any reasonable
        /// policy timeout — an uncooperative vendor client that does not
        /// observe cancellation either.
        /// </summary>
        public bool Slow { get; init; }

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Catalog);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. Catalog.Select(c => c.Key)]);

        public async Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs,
            VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            if (Slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }

            if (FaultOnType == entityType)
            {
                throw new VsphereApiException(
                    VsphereFaultKind.Other, $"{entityType} performance query failed.");
            }

            return [.. entityMoRefs.Select(moRef => new PerfEntitySamples
            {
                EntityMoRef = moRef,
                Values = [.. counters.Select(c => new CounterValue
                {
                    CounterName = c.Key,
                    Raw = 42,
                    Rollup = c.Rollup,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = c.Unit,
                    Instance = string.Empty,
                })],
            })];
        }
    }
}
