using System.Globalization;
using System.Text;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The H3 regression of 22 September: a real-time slot that vCenter returns
/// before its values are in.
/// </summary>
/// <remarks>
/// <para>
/// Measured with the probe's late-samples mode against the live vCenter: a
/// host's newest slot can be listed in <c>sampleInfo</c> while every point of
/// some or all of its series is still -1, and a read a few seconds later
/// returns the same slot with real values. Whole hosts (cpu, mem, net, disk
/// and the datastore instances together) and datastore instances alone were
/// both seen, and every placeholder was filled on a later read.
/// </para>
/// <para>
/// The H3 live read starts at the entity's newest returned time, exclusive,
/// so the slot was never asked for again: the parser drops -1, the mark moved
/// past the slot anyway, and the 30-second cycle that met a host's :00 slot
/// in its first seconds lost it every minute. The old read asked for the
/// newest three samples each cycle and so read the slot again, filled.
/// </para>
/// <para>
/// These run the real parser over XML shaped like vCenter's, so the -1 is the
/// wire's, not a stand-in.
/// </para>
/// </remarks>
public class VsphereLateSampleTests
{
    private static readonly DateTimeOffset S0 = new(2026, 9, 22, 7, 1, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_slot_returned_before_its_values_is_read_again_once_they_are_in()
    {
        // The live pattern: the cycle meets the :00 slot a second after it is
        // listed, while the whole host is still -1.
        var fixture = new Fixture(placeholders: _ => true);

        var batches = new List<ObservationBatch>
        {
            await fixture.ReadAsync(S0.AddSeconds(1)),
            await fixture.ReadAsync(S0.AddSeconds(31)),
            await fixture.ReadAsync(S0.AddSeconds(61)),
        };

        var written = Written(batches);

        foreach (var slot in new[] { S0, S0.AddSeconds(20), S0.AddSeconds(40) })
        {
            Assert.Equal(
                ["cpu.usage.average|", "disk.deviceLatency.average|", "disk.deviceLatency.average|naa.1", "disk.deviceLatency.average|naa.2"],
                [.. written.Where(w => w.At == slot).Select(w => w.Series).Order(StringComparer.Ordinal)]);
        }

        AssertNothingWrittenTwice(written);
        Assert.DoesNotContain(batches.SelectMany(b => b.Failures), f => f.Target == VsphereObservationSource.GivenUpTarget);
    }

    [Fact]
    public async Task A_partly_filled_slot_writes_each_series_once_and_the_combined_value_whole()
    {
        // Only some series are late: one device and one CPU core.
        var fixture = new Fixture(placeholders: series => series is "naa.2" or "1");

        var batches = new List<ObservationBatch>
        {
            await fixture.ReadAsync(S0.AddSeconds(1)),
            await fixture.ReadAsync(S0.AddSeconds(31)),
        };

        var written = Written(batches);

        var atS0 = written.Where(w => w.At == S0).ToList();
        Assert.Equal(
            ["cpu.usage.average|", "disk.deviceLatency.average|", "disk.deviceLatency.average|naa.1", "disk.deviceLatency.average|naa.2"],
            [.. atS0.Select(w => w.Series).Order(StringComparer.Ordinal)]);

        // The worst device, over both devices: not a maximum taken while one
        // of them was still missing and then kept because it was first.
        Assert.Equal(
            Fixture.Latency(S0, "naa.2"),
            atS0.Single(w => w.Series == "disk.deviceLatency.average|").Raw);

        // The CPU aggregate came in the first read and was not written again.
        Assert.Contains(batches[0].Observations, o =>
            o.Value.CounterName == "cpu.usage.average" && o.SampledAtUtc == S0);

        AssertNothingWrittenTwice(written);
    }

    [Fact]
    public async Task A_complete_read_is_not_read_again()
    {
        // Nothing late: the window starts at the newest slot, as H3 intended.
        var fixture = new Fixture(placeholders: _ => false);

        await fixture.ReadAsync(S0.AddSeconds(1));
        await fixture.ReadAsync(S0.AddSeconds(31));

        Assert.Equal(S0, fixture.Api.Windows[^1].StartExclusiveUtc);
    }

    [Fact]
    public async Task A_slot_that_never_fills_is_given_up_at_the_lookback_and_never_written_twice()
    {
        // A value that stays unreadable (the -1.8e18 kind) must neither pin the
        // read forever nor make the collector write the rest of the slot again
        // every cycle.
        var fixture = new Fixture(placeholders: series => series == "naa.2", neverFills: S0);

        var batches = new List<ObservationBatch>();
        for (var i = 0; i < 10; i++)
        {
            batches.Add(await fixture.ReadAsync(S0.AddSeconds(1 + (30 * i))));
        }

        var written = Written(batches);
        AssertNothingWrittenTwice(written);

        // Given up: the last read starts after the stuck slot.
        Assert.True(fixture.Api.Windows[^1].StartExclusiveUtc >= S0);

        // And everything after it arrived.
        Assert.Contains(written, w => w.At == S0.AddSeconds(240) && w.Series == "disk.deviceLatency.average|naa.2");

        // The one value lost is counted as dropped, once, against the host,
        // saying why — a permanent loss is never silent.
        var givenUp = Assert.Single(
            batches.SelectMany(b => b.Failures),
            f => f.Target == VsphereObservationSource.GivenUpTarget);
        Assert.Equal(Id("host-1"), givenUp.Entity);
        Assert.StartsWith("1 sample(s) of host-1 dropped: 1 real-time slot(s) (07:01:00Z)", givenUp.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slot_given_up_is_reported_again_if_the_batch_that_reported_it_was_not_stored()
    {
        var fixture = new Fixture(placeholders: series => series == "naa.2", neverFills: S0);

        var before = new List<ObservationBatch>();
        for (var i = 0; i < 4; i++)
        {
            before.Add(await fixture.ReadAsync(S0.AddSeconds(1 + (30 * i))));
        }

        // S0 leaves the six-sample lookback at S0+120; the read at S0+121 says so.
        var notStored = await fixture.ReadAsync(S0.AddSeconds(121), store: false);
        var stored = await fixture.ReadAsync(S0.AddSeconds(151));
        var after = await fixture.ReadAsync(S0.AddSeconds(181));

        Assert.DoesNotContain(before.SelectMany(b => b.Failures), f => f.Target == VsphereObservationSource.GivenUpTarget);
        Assert.Single(notStored.Failures, f => f.Target == VsphereObservationSource.GivenUpTarget);
        Assert.Single(stored.Failures, f => f.Target == VsphereObservationSource.GivenUpTarget);
        Assert.DoesNotContain(after.Failures, f => f.Target == VsphereObservationSource.GivenUpTarget);
    }

    private static List<(string Series, DateTimeOffset At, double Raw)> Written(IEnumerable<ObservationBatch> batches) =>
    [
        .. batches
            .SelectMany(b => b.Observations.Concat(b.Backfill))
            .Where(o => o.Value.CounterName != CollectorSelfMetrics.ClockSkewCounter)
            .Select(o => ($"{o.Value.CounterName}|{o.Value.Instance}", o.SampledAtUtc, o.Value.Raw)),
    ];

    private static void AssertNothingWrittenTwice(List<(string Series, DateTimeOffset At, double Raw)> written)
    {
        var twice = written.GroupBy(w => (w.Series, w.At)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, $"Written twice: {string.Join(", ", twice)}");
    }

    private sealed class Fixture
    {
        public Fixture(Func<string, bool> placeholders, DateTimeOffset? neverFills = null)
        {
            Api = new PublishingVcenter(placeholders, neverFills);
            Gaps = new VsphereCollectionGapTests.InMemoryGaps(new() { [Id("host-1")] = S0.AddSeconds(-20) });
            Source = new VsphereObservationSource(Api, new Targets(), Clock, Gaps);
        }

        public PublishingVcenter Api { get; }

        public MovableClock Clock { get; } = new();

        public VsphereCollectionGapTests.InMemoryGaps Gaps { get; }

        public VsphereObservationSource Source { get; }

        public static double Latency(DateTimeOffset at, string device) =>
            (device == "naa.2" ? 7 : 3) + ((at - S0).TotalSeconds / 20);

        public async Task<ObservationBatch> ReadAsync(DateTimeOffset serverNow, bool store = true)
        {
            Api.ServerNow = serverNow;
            Clock.UtcNow = serverNow;

            var batch = await Source.ReadAsync(CancellationToken.None);
            if (store)
            {
                batch.Stored?.Invoke();
            }

            return batch;
        }
    }

    private static EntityId Id(string moRef) => EntityId.For("vc-1", moRef);

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Targets : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new() { Hosts = ["host-1"] };

        public EntityId? ResolveEntity(string moRef) => Id(moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => null;
    }

    /// <summary>
    /// A vCenter that lists a 20-second slot as soon as its time has passed
    /// and fills it three seconds later; until then the chosen series read -1.
    /// </summary>
    internal sealed class PublishingVcenter(Func<string, bool> placeholders, DateTimeOffset? neverFills) : IVsphereApi
    {
        private static readonly TimeSpan FillDelay = TimeSpan.FromSeconds(3);

        private static readonly VsphereCounter Cpu = new()
        {
            Id = 1, Group = "cpu", Name = "usage", Rollup = RollupType.Average, Unit = "percent", Level = 1,
        };

        private static readonly VsphereCounter Disk = new()
        {
            Id = 2, Group = "disk", Name = "deviceLatency", Rollup = RollupType.Average, Unit = "millisecond", Level = 2,
        };

        private static readonly (VsphereCounter Counter, string Instance)[] Series =
        [
            (Cpu, string.Empty), (Cpu, "0"), (Cpu, "1"), (Disk, "naa.1"), (Disk, "naa.2"),
        ];

        public string InstanceId => "vc-1";

        public DateTimeOffset ServerNow { get; set; }

        public List<PerfQueryTarget> Windows { get; } = [];

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>([Cpu, Disk]);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) => Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([Cpu.Key, Disk.Key]);

        public Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(ServerNow);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, DateTimeOffset nowUtc, CancellationToken ct) =>
            throw new InvalidOperationException("Every read gives a window now.");

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfWindowsAsync(
            IReadOnlyList<PerfQueryTarget> targets, VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters, CancellationToken cancellationToken)
        {
            var xml = new StringBuilder("<QueryPerfResponse xmlns=\"urn:vim25\">");

            foreach (var target in targets)
            {
                Windows.Add(target);

                var slots = new List<DateTimeOffset>();

                for (var t = S0.AddHours(-1); t <= target.EndInclusiveUtc && t <= ServerNow; t += TimeSpan.FromSeconds(20))
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

                foreach (var (counter, instance) in Series)
                {
                    xml.Append(CultureInfo.InvariantCulture,
                        $"<value><id><counterId>{counter.Id}</counterId><instance>{instance}</instance></id>");
                    foreach (var slot in slots)
                    {
                        var late = placeholders(instance) &&
                            (ServerNow < slot + FillDelay || slot == neverFills);
                        var point = late
                            ? -1
                            : counter == Cpu ? 2500 : Latency(slot, instance);
                        xml.Append(CultureInfo.InvariantCulture, $"<value>{point}</value>");
                    }

                    xml.Append("</value>");
                }

                xml.Append("</returnval>");
            }

            xml.Append("</QueryPerfResponse>");

            var byId = new Dictionary<int, VsphereCounter> { [Cpu.Id] = Cpu, [Disk.Id] = Disk };
            return Task.FromResult(PerfResponseParser.ParseSamples(xml.ToString(), byId, TimeSpan.FromSeconds(20)));
        }

        private static double Latency(DateTimeOffset slot, string device) => Fixture.Latency(slot, device);
    }
}
