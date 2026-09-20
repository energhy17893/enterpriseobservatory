using System.Globalization;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Checks the counters the product asks for against a real catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The test that was missing. Every other test in this project takes the
/// counter list as given and checks what happens to the values — which is
/// exactly the shape of assumption that let two counter names that do not
/// exist survive from the first commit to the first live run.
/// </para>
/// <para>
/// The failure had no symptom. No exception, no log line, no red screen: the
/// collector asked for <c>datastore.totalLatency.average</c>, vCenter said it
/// had never heard of it, and the product recorded that in a field it then
/// discarded. Forty-one datastores, twenty-nine of them shared SAN volumes,
/// simply had no measurements — on a product whose central diagnostic is
/// storage latency.
/// </para>
/// <para>
/// So this compares against data taken from a server rather than against
/// anybody's memory. It only checks groups the fixture actually contains, so
/// adding a group is a deliberate act of pasting in what a real vCenter
/// returned.
/// </para>
/// </remarks>
public class CounterNamesExistTests
{
    private static readonly IReadOnlyDictionary<string, (string Unit, int Level)> Catalogue = Load();

    /// <summary>The groups the fixture covers, and therefore the ones checked.</summary>
    private static readonly HashSet<string> CoveredGroups =
        [.. Catalogue.Keys.Select(GroupOf)];

    public static TheoryData<string> Requested()
    {
        var data = new TheoryData<string>();

        foreach (var counter in VsphereCounters.Host
            .Concat(VsphereCounters.VirtualMachine)
            .Concat(VsphereCounters.Datastore)
            .Distinct(StringComparer.Ordinal)
            .Where(c => CoveredGroups.Contains(GroupOf(c))))
        {
            data.Add(counter);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Requested))]
    public void A_counter_the_product_asks_for_exists_on_a_real_vcenter(string key)
    {
        Assert.True(
            Catalogue.ContainsKey(key),
            $"'{key}' is not in a real vSphere 8 catalogue. Nothing will fail when this is " +
            "wrong: the collector reports it and carries on, and every entity of that kind " +
            "goes unmeasured while the product looks healthy. Check the name against a live " +
            "server — /api/connections/{id}/counters answers this.");
    }

    [Fact]
    public void The_counter_that_started_this_is_still_absent()
    {
        // Pinned rather than assumed. If a future vSphere adds these, the
        // fixture changes and this test says so — which is the moment to
        // reconsider, not a moment to be surprised by.
        Assert.False(Catalogue.ContainsKey("datastore.totalLatency.average"));
        Assert.False(Catalogue.ContainsKey("virtualDisk.totalLatency.average"));
    }

    [Fact]
    public void Storage_latency_is_asked_for_as_read_and_write_separately()
    {
        // vSphere has no combined latency counter for either kind, and the two
        // halves say different things — read points at the array or its cache,
        // write at the write path. Collapsing them was never an option; the
        // product just did not know that.
        Assert.Contains("datastore.totalReadLatency.average", VsphereCounters.PerDatastore);
        Assert.Contains("datastore.totalWriteLatency.average", VsphereCounters.PerDatastore);
        Assert.Contains("virtualDisk.totalReadLatency.average", VsphereCounters.VirtualMachine);
        Assert.Contains("virtualDisk.totalWriteLatency.average", VsphereCounters.VirtualMachine);
    }

    [Fact]
    public void Datastore_counters_are_asked_of_the_host_because_that_is_where_they_live()
    {
        // The correction that took four attempts. Against a live vCenter the
        // Datastore object supplies no performance data at all, while every
        // HostSystem offers 24 datastore.* counters with the volume named in
        // the instance. Asking the datastore could never have worked, however
        // correct the counter name, the interval and the time range — and all
        // three were wrong first, which is what made the real cause so hard to
        // see.
        Assert.Empty(VsphereCounters.Datastore);

        Assert.All(
            VsphereCounters.PerDatastore,
            key => Assert.Contains(key, VsphereCounters.Host));
    }

    [Fact]
    public void The_path_errors_are_reachable_at_the_level_the_product_already_requires()
    {
        // The reason storage paths are collected at all rather than deferred
        // until someone raises a statistics level. A bus reset and an aborted
        // command are level 2, which the product already asks for, and they
        // are faults rather than thresholds — SCSI does not reset a bus
        // because the array is busy. An estate that never goes past level 2
        // still gets the one signal that names a bad cable.
        Assert.Equal(2, Catalogue["storagePath.busResets.summation"].Level);
        Assert.Equal(2, Catalogue["storagePath.commandsAborted.summation"].Level);

        // The latency pair is level 3, and the fixture keeps it so the reason
        // it is NOT collected stays legible. It was collected and dropped: it
        // cost 2,536 of 8,620 series to answer "which path is slow" on an
        // estate whose path latency cannot resolve below a millisecond in the
        // first place.
        Assert.Equal(3, Catalogue["storagePath.totalReadLatency.average"].Level);
        Assert.Equal(3, Catalogue["storagePath.totalWriteLatency.average"].Level);
    }

    [Fact]
    public void Storage_paths_are_collected_for_faults_and_not_for_latency()
    {
        // Pinned because it is a decision with a cost on each side, and the
        // next person to look will wonder why the obvious counter is absent.
        // Faults say "which path is broken", which is what the bottom of the
        // ladder is for; latency said "which path is slow", which this
        // platform cannot answer below a millisecond anyway.
        Assert.Equal(
            ["storagePath.busResets.summation", "storagePath.commandsAborted.summation"],
            VsphereCounters.PerStoragePath);

        Assert.All(
            VsphereCounters.PerStoragePath,
            key => Assert.Equal(RollupType.Summation, RollupOf(key)));

        // A summation of zero means it never happened. That is the property
        // that makes these worth keeping where the latency zeros were not:
        // a truncated average of zero says nothing at all.
        Assert.DoesNotContain(
            VsphereCounters.Host,
            k => k.StartsWith("storagePath.", StringComparison.Ordinal) &&
                 k.Contains("Latency", StringComparison.OrdinalIgnoreCase));
    }

    private static RollupType RollupOf(string key) =>
        VsphereCounter.ParseRollup(key.Split('.')[^1]);

    [Fact]
    public void Storage_paths_are_kept_one_by_one_because_averaging_them_is_the_whole_problem()
    {
        // A path is the finest grain vSphere offers and the only one that can
        // separate a bad cable from a slow array. Every coarser number
        // averages the broken path in with the working ones and reports
        // something mild — which is how a failing SFP goes unnoticed for weeks.
        Assert.All(
            VsphereCounters.PerStoragePath,
            key => Assert.True(VsphereCounters.KeepPerDevice(key), key));

        Assert.All(
            VsphereCounters.PerStoragePath,
            key => Assert.Contains(key, VsphereCounters.Host));

        // But they are about the host, not about some other entity. Only the
        // datastore counters name a different object in their instance.
        Assert.All(
            VsphereCounters.PerStoragePath,
            key => Assert.False(VsphereCounters.InstanceNamesAnEntity(key), key));
    }

    [Fact]
    public void A_counter_measured_on_one_entity_about_another_is_marked_as_such()
    {
        // The flag that stops the parser collapsing thirty volumes into one
        // number. Without it these would be aggregated like a host's HBAs, and
        // the worst volume's latency would be filed under the host with
        // nothing to say which volume it came from.
        Assert.All(
            VsphereCounters.PerDatastore,
            key => Assert.True(VsphereCounters.InstanceNamesAnEntity(key), key));

        Assert.False(VsphereCounters.InstanceNamesAnEntity("disk.deviceLatency.average"));
        Assert.False(VsphereCounters.InstanceNamesAnEntity("cpu.usage.average"));
        Assert.False(VsphereCounters.InstanceNamesAnEntity(null));
    }

    [Fact]
    public void Every_storage_latency_counter_is_stored_in_milliseconds()
    {
        // A unit mix-up is the same class of silent defect, and this caught a
        // live one: datastore.datastoreVMObservedLatency.latest is in
        // microseconds, three lines from the millisecond counters it exists to
        // be compared against. Comparing VM-observed latency with device
        // latency is how the product tells a queue problem from an array
        // problem — a comparison wrong by a thousand answers confidently and
        // incorrectly.
        //
        // Asserted on the unit the product stores rather than the one vSphere
        // sends, because the normaliser converts and carries the unit with the
        // value. Checking the wire unit would fail for a counter that is
        // handled correctly.
        foreach (var key in VsphereCounters.PerDatastore
            .Concat(VsphereCounters.VirtualMachine)
            .Concat(VsphereCounters.Host)
            .Where(k => k.Contains("Latency", StringComparison.OrdinalIgnoreCase))
            .Where(Catalogue.ContainsKey))
        {
            Assert.Equal(
                "millisecond",
                VsphereUnitNormalizer.NormalizedUnit(Catalogue[key].Unit));
        }
    }

    [Fact]
    public void A_microsecond_counter_is_converted_rather_than_stored_beside_milliseconds()
    {
        // The conversion itself, pinned. 3000 microseconds is 3 milliseconds,
        // and the unit has to move with the number or the next reader is owed
        // an explanation nobody will give them.
        Assert.Equal(3d, VsphereUnitNormalizer.Normalize(3000d, "microsecond"));
        Assert.Equal("millisecond", VsphereUnitNormalizer.NormalizedUnit("microsecond"));
        Assert.True(VsphereUnitNormalizer.IsScaled("microsecond"));

        // And a unit it does not recognise is left exactly alone. Rescaling on
        // a guess would be worse than the problem it solves.
        Assert.Equal(3000d, VsphereUnitNormalizer.Normalize(3000d, "kiloBytesPerSecond"));
        Assert.Equal("kiloBytesPerSecond", VsphereUnitNormalizer.NormalizedUnit("kiloBytesPerSecond"));
    }

    private static string GroupOf(string key) =>
        key.Split('.', 2) is [var group, ..] ? group : key;

    private static Dictionary<string, (string, int)> Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "vsphere8-counter-catalogue.tsv");

        var catalogue = new Dictionary<string, (string, int)>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('\t');

            if (parts.Length == 3)
            {
                catalogue[parts[0]] =
                    (parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture));
            }
        }

        return catalogue;
    }
}
