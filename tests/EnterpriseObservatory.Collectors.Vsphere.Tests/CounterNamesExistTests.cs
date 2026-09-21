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

    public static TheoryData<string, bool> FaultClassification()
    {
        var data = new TheoryData<string, bool>();

        foreach (var key in VsphereCounters.PerStoragePath)
        {
            data.Add(key, true);
        }

        // The same SCSI errors counted against the LUN rather than the route
        // to it. Same argument, same answer: the array being busy does not
        // reset a bus, abort a command or refuse a reservation.
        foreach (var key in VsphereCounters.PerDeviceScsiFaults)
        {
            data.Add(key, true);
        }

        // Levels, not faults: each of these is high or low, and a high one is
        // a threshold question rather than an error that happened.
        foreach (var key in new[]
        {
            "cpu.usage.average",
            "cpu.ready.summation",

            // A summation whose zeros are real, and still not a fault -- the
            // same shape as the dropped-packet counters and for a sharper
            // reason. A CPU limit is a configuration somebody chose. On a VM
            // deliberately capped for licensing or for a test rig it bites
            // every cycle, for ever, entirely as intended. Non-zero here is an
            // explanation of waiting, not an error that occurred, and marking
            // it would open a standing alert on every correctly configured
            // machine in the estate.
            "cpu.maxlimited.summation",

            "disk.deviceLatency.average",
            "datastore.totalReadLatency.average",
            "mem.vmmemctl.average",

            // A summation whose zeros are real, like the fault counters, and
            // still not a fault. A dropped frame has benign causes a bus reset
            // does not, and the rule this flag feeds carries no threshold and
            // no memory — so one dropped packet anywhere on a busy uplink
            // would open a warning, every cycle, on a healthy estate.
            "net.droppedRx.summation",
            "net.droppedTx.summation",

            // A published enum, not a count. Zero here is "high" — the
            // healthy state — so the flag's promise that zero means nothing
            // happened is inverted, and the rule's own guard (skip the
            // aggregate instance) would discard it anyway.
            "mem.state.latest",

            // Rates. A host swapping 4 MB/s is a level to draw a line
            // through, not an error that occurred.
            "mem.swapinRate.average",
            "mem.compressionRate.average",
            "mem.active.average",
        })
        {
            data.Add(key, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FaultClassification))]
    public void A_counter_is_classified_as_a_fault_only_when_any_non_zero_reading_is_one(
        string key, bool isFault)
    {
        // The classifier the whole fault path rests on, and it was reachable
        // from no test at all. Both directions matter and they fail
        // differently: a fault counter not marked makes a bus reset
        // unreportable, indistinguishable from the 670,514 zeros a clean
        // fabric produces; a level counter marked as a fault turns every
        // busy host into a reported error and the signal is lost in it.
        Assert.Equal(isFault, VsphereCounters.IsFaultCounter(key));
    }

    [Fact]
    public void An_unknown_counter_is_not_a_fault_counter()
    {
        // Absence is not a fault. Defaulting the other way would make a
        // counter nobody has classified yet report an error on its first
        // non-zero reading.
        Assert.False(VsphereCounters.IsFaultCounter("storagePath.totalReadLatency.average"));
        Assert.False(VsphereCounters.IsFaultCounter(null));
        Assert.False(VsphereCounters.IsFaultCounter(string.Empty));
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

    [Fact]
    public void The_scsi_faults_counted_per_device_are_kept_per_device_or_they_name_nothing()
    {
        // The entire reason these are collected beside the path faults. A
        // path's runtime name carries no LUN identity, so a reset on a path is
        // attributable to a host and an HBA and stops there; a device fault
        // carries naa.*, and the map's §5c chain turns that into a datastore
        // and the VMs on it. Collapse them to a host summary and that is
        // exactly the half that is lost — the alert becomes "this host had a
        // reservation conflict" with thirty LUNs to choose from.
        Assert.All(
            VsphereCounters.PerDeviceScsiFaults,
            key => Assert.True(VsphereCounters.KeepPerDevice(key), key));

        Assert.All(
            VsphereCounters.PerDeviceScsiFaults,
            key => Assert.Contains(key, VsphereCounters.Host));

        // And they are about this host's devices, not about another entity.
        // Marking them otherwise would stop the parser computing the host
        // summary at all.
        Assert.All(
            VsphereCounters.PerDeviceScsiFaults,
            key => Assert.False(VsphereCounters.InstanceNamesAnEntity(key), key));

        // Summations, every one. The property the whole fault argument rests
        // on: a summation of zero says "it did not happen", where a truncated
        // average of zero says nothing at all (§5b). An average sneaking into
        // this list would be a fault alert fired on a rounding artefact.
        Assert.All(
            VsphereCounters.PerDeviceScsiFaults,
            key => Assert.Equal(RollupType.Summation, RollupOf(key)));
    }

    [Fact]
    public void The_reservation_conflict_counter_has_no_substitute_and_is_pinned_as_such()
    {
        // Pinned because it is the one counter in this batch that would be
        // dropped first by anybody counting series and last by anybody who
        // has debugged the problem. busResets and commandsAborted duplicate
        // the path counters at a different grain; a reservation conflict is
        // collected nowhere else. It is one host being refused a lock another
        // host holds — no latency counter explains it, and it stalls every
        // host contending for the volume at the same moment.
        Assert.Contains("disk.scsiReservationConflicts.summation", VsphereCounters.PerDeviceScsiFaults);
        Assert.True(VsphereCounters.IsFaultCounter("disk.scsiReservationConflicts.summation"));

        Assert.DoesNotContain(
            VsphereCounters.PerStoragePath,
            k => k.Contains("scsiReservation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dropped_packets_are_collected_on_both_objects_and_summarised_rather_than_kept_per_nic()
    {
        // This product collected no network data of any kind, which made a
        // dropped-packet storm indistinguishable from an application problem:
        // every storage and CPU number clean while the operator walks down
        // the storage ladder. Host side says the uplink is dropping, VM side
        // says which guest lost the frames, and either alone leaves the other
        // question open.
        foreach (var key in new[] { "net.droppedRx.summation", "net.droppedTx.summation" })
        {
            Assert.Contains(key, VsphereCounters.Host);
            Assert.Contains(key, VsphereCounters.VirtualMachine);
            Assert.Equal(RollupType.Summation, RollupOf(key));

            // Deliberately summarised. Sixteen NICs a host, two counters,
            // ten hosts is roughly 320 series to answer "which uplink" — a
            // second question, bought later if anything is ever seen to drop.
            // If this flips, the cost has to be argued in the counter map
            // rather than arriving as a surprise in the series count.
            Assert.False(VsphereCounters.KeepPerDevice(key), key);
        }
    }

    [Fact]
    public void Memory_pressure_is_asked_for_as_rates_beside_the_level_they_cannot_replace()
    {
        // mem.swapused is a level and stays high for weeks after one event,
        // so an alert on it fires long after there is anything to do and
        // becomes an alert nobody believes. The rates separate "swapped once
        // in March" from "swapping now". Compression is the rung below swap:
        // a host compressing is under pressure and coping, a host swapping in
        // has already lost — and without both rungs the product can only say
        // "bad", never "getting worse".
        string[] rates =
        [
            "mem.swapinRate.average",
            "mem.swapoutRate.average",
            "mem.compressionRate.average",
            "mem.decompressionRate.average",
        ];

        foreach (var key in rates)
        {
            Assert.Contains(key, VsphereCounters.Host);
        }

        // The level is kept, not replaced: it still answers how much is
        // parked out of memory, which no rate can.
        Assert.Contains("mem.swapused.average", VsphereCounters.Host);

        // And ballooning is not in this group on purpose. Dynatrace's
        // detection uses these four and explicitly not vmmemctl or mem.usage,
        // because a balloon is the mechanism working and high usage on a
        // consolidated host is normal. Putting either here would rebuild the
        // false alarm the group exists to avoid.
        Assert.DoesNotContain("mem.vmmemctl.average", rates);
        Assert.DoesNotContain("mem.usage.average", rates);
    }

    [Fact]
    public void Ballooning_is_never_asked_about_a_virtual_machine_without_the_counter_that_qualifies_it()
    {
        // The pairing is the finding, so it is pinned as a pairing. Datadog
        // says alert on any positive vmmemctl; Dynatrace ignores ballooning
        // entirely; both are right about half of it. A balloon taking pages
        // from a VM whose active memory is far below its granted memory is
        // the mechanism working, and on a healthy consolidated estate that is
        // most VMs — so vmmemctl alone ships a standing false alarm. Remove
        // mem.active and that is what the product goes back to.
        Assert.Contains("mem.vmmemctl.average", VsphereCounters.VirtualMachine);
        Assert.Contains("mem.active.average", VsphereCounters.VirtualMachine);
    }

    [Fact]
    public void The_host_memory_state_enum_is_collected_but_not_forced_into_the_fault_flag()
    {
        // The one counter here whose meaning is published rather than
        // invented — high / soft / hard / low, straight from the vendor —
        // which matters because Broadcom publishes almost no thresholds and
        // every other memory rule this product writes will be somebody's
        // guess.
        //
        // It does not fit IsFaultCount, and it fails twice rather than once,
        // which is why it is not shaded into it: zero means "high", the
        // healthy state, so the flag's promise that zero means nothing
        // happened is exactly inverted; and the rule that consumes the flag
        // skips aggregate instances, which this counter always is. Marking it
        // would produce a flag that is wrong AND never read.
        Assert.Contains("mem.state.latest", VsphereCounters.Host);
        Assert.False(VsphereCounters.IsFaultCounter("mem.state.latest"));
        Assert.Equal(RollupType.Latest, RollupOf("mem.state.latest"));

        // Nor is a new flag declared here yet. What it needs is a breakpoint —
        // "degraded at or above 2" — which is a value, not a bool, and it has
        // no rule to travel to. See the counter map §5d.
        Assert.False(VsphereCounters.InstanceNamesAnEntity("mem.state.latest"));
        Assert.False(VsphereCounters.KeepPerDevice("mem.state.latest"));
    }

    [Fact]
    public void The_cpu_limit_counter_is_asked_of_the_machine_and_summarised_rather_than_kept_per_vcpu()
    {
        // cpu.ready alone cannot say why a machine was ready to run and did
        // not, so the contention rule blamed the host for waiting a configured
        // limit had caused -- an operator sent to a host that turns out fine,
        // while the forgotten limit stays invisible. This counter is the only
        // cheap way to tell the two apart, and it only accumulates when a
        // ceiling actually held a vCPU back.
        Assert.Contains("cpu.maxlimited.summation", VsphereCounters.VirtualMachine);
        Assert.Equal(RollupType.Summation, RollupOf("cpu.maxlimited.summation"));

        // A limit is a property of the machine, so it is never asked of a
        // host. Asking there would collect a number with nothing to attribute
        // it to.
        Assert.DoesNotContain("cpu.maxlimited.summation", VsphereCounters.Host);

        // Summarised, like every other cpu counter and for the reason
        // KeepPerDevice gives: vSphere reports this per vCPU as well, and a
        // per-vCPU series is not a small virtual machine. Keeping them would
        // let the rule compare one guest's busiest core against another guest.
        Assert.False(VsphereCounters.KeepPerDevice("cpu.maxlimited.summation"));
        Assert.False(VsphereCounters.InstanceNamesAnEntity("cpu.maxlimited.summation"));
    }

    [Fact]
    public void Every_counter_no_catalogue_in_this_repository_can_check_is_listed_rather_than_assumed()
    {
        // The honest half of the guard above. The fixture covers datastore,
        // storagePath and virtualDisk only, so the theory that checks counter
        // names against a real vCenter silently skips every cpu, mem, disk
        // and net counter the product asks for — including all nine added on
        // 2026-09-20, whose names and statistics levels came from vendor
        // documentation and have NOT been put to a server.
        //
        // A skipped check that looks like a passing one is the shape of
        // defect this whole file exists to stop, so the skipped set is
        // written down. Two things then fail loudly instead of quietly:
        // adding a counter in an unchecked group without noticing, and
        // pasting a real catalogue in for one of these groups without going
        // back to confirm the names it now covers.
        var unverifiable = VsphereCounters.Host
            .Concat(VsphereCounters.VirtualMachine)
            .Concat(VsphereCounters.Datastore)
            .Distinct(StringComparer.Ordinal)
            .Where(c => !CoveredGroups.Contains(GroupOf(c)))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "cpu.costop.summation",

                // Added 2026-09-20 with the CPU limit verdict, and it lands
                // here rather than under the theory above for the same reason
                // as the rest of this list: the fixture holds no cpu group, so
                // its name and its statistics level came from vendor
                // documentation and have not been put to a live server. That
                // matters more for this counter than for most. The rule now
                // SUPPRESSES a host verdict on the strength of it, so a name
                // that is wrong does not merely lose a signal -- it silently
                // restores the false positive it was added to close, and the
                // product looks healthy while doing it. See the counter map
                // §5h.
                "cpu.maxlimited.summation",

                "cpu.ready.summation",
                "cpu.usage.average",
                "disk.busResets.summation",
                "disk.commandsAborted.summation",
                "disk.deviceLatency.average",
                "disk.kernelLatency.average",
                "disk.maxTotalLatency.latest",
                "disk.queueLatency.average",
                "disk.scsiReservationConflicts.summation",
                "mem.active.average",
                "mem.compressionRate.average",
                "mem.decompressionRate.average",
                "mem.state.latest",
                "mem.swapinRate.average",
                "mem.swapoutRate.average",
                "mem.swapused.average",
                "mem.usage.average",
                "mem.vmmemctl.average",
                "net.droppedRx.summation",
                "net.droppedTx.summation",

                // Added with the dropped-packet rule as its denominator. Named
                // from vendor documentation, not put to a live server; a wrong
                // name here does not fail loudly, it silences that rule.
                "net.packetsRx.summation",
                "net.packetsTx.summation",
            ],
            unverifiable);
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
