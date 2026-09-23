using System.Globalization;
using System.Text;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;
using Xunit.Abstractions;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Runs alone: <see cref="GC.GetTotalMemory(bool)"/> is process-wide, and a
/// test allocating beside a measurement is measured with it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurement
{
    public const string Name = "Memory measurement";
}

/// <summary>
/// How many bytes one row costs while it waits in the store queue (F5,
/// ADR-0025 §6) — the number the queue's default byte budget is derived from.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the real queued type, produced the real way: a vim25
/// <c>QueryPerf</c> reply goes through <see cref="PerfResponseParser"/> and
/// <see cref="VsphereObservationSource"/>, so string sharing is what
/// production has — one counter name and one instance string per series,
/// shared by that series' slots, one entity id per entity. Retained bytes are
/// read with <see cref="GC.GetTotalMemory(bool)"/> after a full collection,
/// while the rows are held the way the queue holds them.
/// </para>
/// <para>
/// Two shapes: one slot per series per read (every string paid for by one
/// row, the dearest case) and two (the live read's steady state alternates
/// between one and two 20 s slots per 30 s cycle). The budget is sized on the
/// dearer one. Run with <c>--logger "console;verbosity=detailed"</c> to see the
/// figures; the assertion only keeps the measured number inside the band the
/// default budget was chosen from (see <see cref="StoreQueueLimits"/>), so a
/// change to the row's shape that moves it is noticed.
/// </para>
/// </remarks>
[Collection(MemoryMeasurement.Name)]
public class QueuedRowSizeMeasurementTests(ITestOutputHelper output)
{
    private const int Hosts = 20;
    private const int VirtualMachines = 150;

    [Fact]
    public async Task Measure_bytes_per_queued_row()
    {
        var oneSlot = await MeasureAsync(slots: 1);
        var twoSlots = await MeasureAsync(slots: 2);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"one slot per series: {oneSlot.Rows} rows, {oneSlot.BytesPerRow:0.0} bytes/row"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"two slots per series: {twoSlots.Rows} rows, {twoSlots.BytesPerRow:0.0} bytes/row"));

        // The accounting constant is the dearer shape, rounded up; a row that
        // grew past it would make the queue under-count its own memory.
        Assert.InRange(oneSlot.BytesPerRow, 100, StoreQueueLimits.MeasuredBytesPerRow);
        Assert.InRange(twoSlots.BytesPerRow, 100, StoreQueueLimits.MeasuredBytesPerRow);
    }

    private static async Task<(int Rows, double BytesPerRow)> MeasureAsync(int slots)
    {
        var api = new XmlVcenter(slots);
        var source = new VsphereObservationSource(api, new Targets(), new Clock());

        // Warm: the catalogue, the probe and the parser's statics are paid
        // for once and are not rows.
        await source.ReadAsync(CancellationToken.None);

        var held = new List<IReadOnlyList<Observation>>();
        var rows = 0;

        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var read = 0; read < 5; read++)
        {
            api.ServerNow = api.ServerNow.AddMinutes(1);
            var batch = await source.ReadAsync(CancellationToken.None);

            // What the queue holds: the batch's rows, nothing else of the read.
            IReadOnlyList<Observation> queued = [.. batch.Observations, .. batch.Backfill];
            held.Add(queued);
            rows += queued.Count;
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(held);

        return (rows, (after - before) / (double)rows);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Targets : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new()
        {
            Hosts = [.. Enumerable.Range(1, Hosts).Select(i => $"host-{1000 + i}")],
            VirtualMachines = [.. Enumerable.Range(1, VirtualMachines).Select(i => $"vm-{2000 + i}")],
        };

        // A new string per call, as the graph-backed provider builds one.
        public EntityId? ResolveEntity(string moRef) => EntityId.For("vcenter-prod-01", moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => $"esx-{moRef}.corp.example";
    }

    /// <summary>A vCenter that answers every live query with a vim25 reply of real counter keys.</summary>
    private sealed class XmlVcenter(int slots) : IVsphereApi
    {
        private readonly IReadOnlyList<VsphereCounter> _catalog =
        [
            .. VsphereCounters.Host.Concat(VsphereCounters.VirtualMachine)
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
                        Unit = "millisecond",
                        Level = 1,
                    };
                }),
        ];

        public string InstanceId => "vcenter-prod-01";

        public DateTimeOffset ServerNow { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_catalog);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(-1);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _catalog.Select(c => c.Key)]);

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
            var xml = new StringBuilder("<QueryPerfResponse xmlns=\"urn:vim25\">");

            foreach (var target in targets)
            {
                var type = entityType == VsphereEntityType.HostSystem ? "HostSystem" : "VirtualMachine";
                xml.Append(CultureInfo.InvariantCulture, $"<returnval><entity type=\"{type}\">{target.MoRef}</entity>");

                for (var s = slots - 1; s >= 0; s--)
                {
                    var at = ServerNow.AddSeconds(-20 * s);
                    xml.Append(CultureInfo.InvariantCulture,
                        $"<sampleInfo><timestamp>{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</timestamp><interval>20</interval></sampleInfo>");
                }

                foreach (var counter in counters)
                {
                    foreach (var instance in InstancesOf(counter))
                    {
                        xml.Append(CultureInfo.InvariantCulture,
                            $"<value><id><counterId>{counter.Id}</counterId><instance>{instance}</instance></id>");
                        for (var s = 0; s < slots; s++)
                        {
                            xml.Append(CultureInfo.InvariantCulture, $"<value>{17 + s + counter.Id}</value>");
                        }

                        xml.Append("</value>");
                    }
                }

                xml.Append("</returnval>");
            }

            xml.Append("</QueryPerfResponse>");

            return Task.FromResult(PerfResponseParser.ParseSamples(
                xml.ToString(),
                _catalog.ToDictionary(c => c.Id),
                TimeSpan.FromSeconds(20)));
        }

        /// <summary>The instance mix a real host or VM reports: the aggregate, cores, devices, NICs.</summary>
        private static IEnumerable<string> InstancesOf(VsphereCounter counter) => counter.Group switch
        {
            "cpu" => ["", "0", "1", "2", "3"],
            "disk" or "virtualDisk" => ["naa.600508b1001c7e3a9b2f4d5e6f708192", "naa.600508b1001c7e3a9b2f4d5e6f708193"],
            "net" => ["", "vmnic0", "vmnic1"],
            _ => [""],
        };
    }
}
