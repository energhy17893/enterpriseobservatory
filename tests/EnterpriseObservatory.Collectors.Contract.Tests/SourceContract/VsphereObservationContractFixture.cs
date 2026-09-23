using System.Globalization;
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

    public ILateValueScenario CreateWithLateValues() => new LateValueScenario(InstanceId);

    /// <summary>
    /// A host whose 20-second slot is listed as soon as its time has passed
    /// and filled three seconds later: until then vCenter's own CPU aggregate
    /// and one of two devices read -1, on the wire, through the real parser.
    /// </summary>
    private sealed class LateValueScenario : ILateValueScenario
    {
        private static readonly DateTimeOffset S0 = new(2026, 9, 22, 7, 1, 0, TimeSpan.Zero);

        private readonly LateApi _api;
        private readonly TestClock _clock = new();

        /// <summary>The runner's slot: the state the source learns lives here (F5).</summary>
        private readonly ObservationSourceSlot _slot;

        public LateValueScenario(string instanceId)
        {
            _api = new LateApi(instanceId);
            Source = new VsphereObservationSource(
                _api, new FixedTargets(new VsphereSampleTargets { Hosts = ["host-1"] }), _clock);
            _slot = new ObservationSourceSlot(instanceId);
        }

        public IObservationSource Source { get; }

        public DateTimeOffset LateSampleAt => S0;

        public IReadOnlyCollection<string> SeriesPerSample { get; } =
        [
            "cpu.usage.average|",
            "disk.deviceLatency.average|",
            "disk.deviceLatency.average|naa.1",
            "disk.deviceLatency.average|naa.2",
        ];

        public (string Series, double Value) Combined => ("disk.deviceLatency.average|", LateApi.Latency(S0, "naa.2"));

        public Task<ObservationBatch> ReadCycleAsync(int cycle)
        {
            // One second after the slot is listed, then every thirty: the
            // phase the live collector met the :00 slot in.
            _api.ServerNow = S0.AddSeconds(1 + (30 * cycle));
            _clock.UtcNow = _api.ServerNow;
            return _slot.ReadAsync(Source, CancellationToken.None);
        }

        public void Accept(ObservationBatch batch) => _slot.Accept(batch);
    }

    private sealed class LateApi(string instanceId) : IVsphereApi, IVsphereChannelSelfMetrics
    {
        // F6: this fixture's late-value scenario steps ServerNow by hand to
        // drive the fill window, so it must keep offering GetServerTimeAsync
        // through the self-metrics channel interface — VsphereObservationSource
        // no longer calls IVsphereApi for it at all.
        public int SessionsHeld => 1;

        private static readonly TimeSpan FillDelay = TimeSpan.FromSeconds(3);

        private static readonly VsphereCounter Cpu = new()
        {
            Id = 1, Group = "cpu", Name = "usage", Rollup = RollupType.Average, Unit = "percent", Level = 1,
        };

        private static readonly VsphereCounter Disk = new()
        {
            Id = 2, Group = "disk", Name = "deviceLatency", Rollup = RollupType.Average, Unit = "millisecond", Level = 2,
        };

        private static readonly (VsphereCounter Counter, string Instance, bool Late)[] Series =
        [
            (Cpu, string.Empty, true), (Cpu, "0", false), (Cpu, "1", false),
            (Disk, "naa.1", false), (Disk, "naa.2", true),
        ];

        public string InstanceId => instanceId;

        public DateTimeOffset ServerNow { get; set; }

        public static double Latency(DateTimeOffset at, string device) =>
            (device == "naa.2" ? 7 : 3) + (at.ToUnixTimeSeconds() % 600 / 20);

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>([Cpu, Disk]);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([Cpu.Key, Disk.Key]);

        public Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(ServerNow);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The live read gives a window.");

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
            IReadOnlyList<PerfQueryTarget> targets, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, CancellationToken cancellationToken)
        {
            var xml = new System.Text.StringBuilder("<QueryPerfResponse xmlns=\"urn:vim25\">");

            foreach (var target in targets)
            {
                var slots = new List<DateTimeOffset>();
                for (var t = ServerNow.AddMinutes(-10).AddTicks(-(ServerNow.UtcTicks % TimeSpan.FromSeconds(20).Ticks));
                     t <= target.EndInclusiveUtc && t <= ServerNow;
                     t += TimeSpan.FromSeconds(20))
                {
                    if (t > target.StartExclusiveUtc)
                    {
                        slots.Add(t);
                    }
                }

                xml.Append(CultureInfo.InvariantCulture, $"<returnval><entity type=\"HostSystem\">{target.MoRef}</entity>");
                foreach (var slot in slots)
                {
                    xml.Append(CultureInfo.InvariantCulture,
                        $"<sampleInfo><timestamp>{slot.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</timestamp><interval>20</interval></sampleInfo>");
                }

                foreach (var (counter, instance, late) in Series)
                {
                    xml.Append(CultureInfo.InvariantCulture,
                        $"<value><id><counterId>{counter.Id}</counterId><instance>{instance}</instance></id>");
                    foreach (var slot in slots)
                    {
                        var point = late && ServerNow < slot + FillDelay
                            ? -1
                            : counter == Cpu ? 2500 : Latency(slot, instance);
                        xml.Append(CultureInfo.InvariantCulture, $"<value>{point}</value>");
                    }

                    xml.Append("</value>");
                }

                xml.Append("</returnval>");
            }

            xml.Append("</QueryPerfResponse>");

            return Task.FromResult(PerfResponseParser.ParseSamples(
                xml.ToString(),
                new Dictionary<int, VsphereCounter> { [Cpu.Id] = Cpu, [Disk.Id] = Disk },
                TimeSpan.FromSeconds(20)));
        }
    }

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
