using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereObservationSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class Targets(params string[] hosts) : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; private set; } = new() { Hosts = hosts };

        /// <summary>Unknown refs resolve to null, as an unmapped entity would.</summary>
        public HashSet<string> Resolvable { get; } = [.. hosts];

        /// <summary>Also sample these virtual machines, resolvable like the hosts.</summary>
        /// <remarks>
        /// A second entity type is what makes a per-type failure visible at
        /// all: with one type, "the type failed" and "the source failed" are
        /// the same observation and no test can tell them apart.
        /// </remarks>
        public Targets AndVirtualMachines(params string[] virtualMachines)
        {
            Current = Current with { VirtualMachines = virtualMachines };

            foreach (var moRef in virtualMachines)
            {
                Resolvable.Add(moRef);
            }

            return this;
        }

        /// <summary>Volume identifier to the datastore that owns it.</summary>
        public Dictionary<string, string> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Managed object reference to display name.</summary>
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public EntityId? ResolveEntity(string moRef) =>
            Resolvable.Contains(moRef) ? new EntityId(moRef) : null;

        public EntityId? ResolveVolume(string volumeIdentifier) =>
            Volumes.TryGetValue(volumeIdentifier, out var datastore) ? new EntityId(datastore) : null;

        public string? DisplayNameOf(string moRef) => Names.GetValueOrDefault(moRef);
    }

    private sealed class FakeApi : IVsphereApi
    {
        public string InstanceId => "vc-1";

        public int? MaxQueryMetrics { get; init; } = 256;

        public List<VsphereCounter> Catalog { get; init; } = [.. AllWantedCounters()];

        /// <summary>A type whose performance query faults, and with what.</summary>
        /// <remarks>
        /// Posed as an exception rather than a flag so a test can choose which
        /// fault: the whole point of the guard is that two vim25 faults are
        /// treated differently from each other, and a boolean could not ask
        /// that question.
        /// </remarks>
        public (VsphereEntityType Type, Exception Error)? FaultOn { get; init; }

        /// <summary>What the platform is actually collecting. Defaults to everything.</summary>
        public HashSet<string>? Available { get; init; }

        /// <summary>A server that answers the query but has nothing to give.</summary>
        /// <remarks>
        /// The case that separates "the probe was wrong" from "the probe was
        /// right": both return a query, only one returns data.
        /// </remarks>
        public bool EmptySamples { get; init; }

        /// <summary>Batch sizes the server refuses, to exercise adaptive sizing.</summary>
        public int RefuseBatchesLargerThan { get; init; } = int.MaxValue;

        public List<int> ObservedBatchSizes { get; } = [];

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>(Catalog);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) =>
            Task.FromResult(MaxQueryMetrics);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Available ?? [.. Catalog.Select(c => c.Key)]]);

        /// <summary>The window each query asked for, or null for real-time.</summary>
        /// <remarks>
        /// Recorded because its absence was the defect: a historical query
        /// with no range comes back empty, and empty is not an error.
        /// </remarks>
        public List<(DateTimeOffset Now, VsphereEntityType Type)> ObservedQueries { get; } = [];

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs,
            VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters,
            DateTimeOffset nowUtc,
            CancellationToken ct)
        {
            ObservedBatchSizes.Add(entityMoRefs.Count);
            ObservedQueries.Add((nowUtc, entityType));

            if (FaultOn is { } fault && fault.Type == entityType)
            {
                throw fault.Error;
            }

            if (entityMoRefs.Count > RefuseBatchesLargerThan)
            {
                throw new VsphereQuerySizeRefusedException("Request processing is restricted by administrator.");
            }

            if (EmptySamples)
            {
                return Task.FromResult<IReadOnlyList<PerfEntitySamples>>([]);
            }

            return Task.FromResult<IReadOnlyList<PerfEntitySamples>>(
            [
                .. entityMoRefs.Select(moRef => new PerfEntitySamples
                {
                    EntityMoRef = moRef,
                    Values = [.. counters.SelectMany(c => Series(c))],
                    SampledAtUtc = SampledAtUtc,
                    Earlier = SampledAtUtc is { } latest
                        ?
                        [
                            new PerfSampleSet
                            {
                                SampledAtUtc = latest.AddSeconds(-20),
                                Values = [.. counters.SelectMany(c => Series(c))],
                            },
                        ]
                        : [],
                }),
            ]);
        }

        /// <summary>
        /// One value per counter, or one per volume for the datastore ones.
        /// </summary>
        /// <remarks>
        /// A host reports datastore counters once per volume it can see, which
        /// is the shape the re-attribution exists to handle. A fake that
        /// returned a single instance-less value would let every one of those
        /// tests pass without exercising anything.
        /// </remarks>
        private IEnumerable<CounterValue> Series(VsphereCounter counter)
        {
            var instances = VsphereCounters.InstanceNamesAnEntity(counter.Key) && Volumes.Count > 0
                ? Volumes
                : [string.Empty];

            foreach (var instance in instances)
            {
                yield return new CounterValue
                {
                    CounterName = counter.Key,
                    Raw = 42,
                    Rollup = counter.Rollup,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = counter.Unit,
                    Instance = instance,
                };
            }
        }

        /// <summary>
        /// When vCenter says it took the latest sample. With it, the reply also
        /// carries the sample before, as a real one does.
        /// </summary>
        public DateTimeOffset? SampledAtUtc { get; init; }

        /// <summary>Volume identifiers this host reports datastore counters for.</summary>
        public List<string> Volumes { get; init; } = [];

        /// <summary>
        /// Every counter the product asks for, of either type.
        /// </summary>
        /// <remarks>
        /// Both lists, because a vCenter offering host counters offers the
        /// virtual-machine ones too. A catalogue holding only the host's would
        /// make every VM query fail as "this vCenter has no such counter", so a
        /// test about a faulting VM query would never reach the query.
        /// Deduplicated because <c>mem.vmmemctl.average</c> is on both lists
        /// and a real catalogue names a counter once.
        /// </remarks>
        private static IEnumerable<VsphereCounter> AllWantedCounters() =>
            VsphereCounters.Host
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
                    Unit = parts[0] switch
                    {
                        "cpu" => "percent",
                        "datastore" when key.Contains("Observed", StringComparison.Ordinal) => "microsecond",
                        "datastore" when key.Contains("number", StringComparison.Ordinal) => "number",
                        _ => "millisecond",
                    },
                    Level = key.StartsWith("disk.", StringComparison.Ordinal) ? 2 : 1,
                };
            });
    }

    private static VsphereObservationSource Source(
        FakeApi api, IVsphereSampleTargetProvider targets, IClock? clock = null) =>
        new(api, targets, clock ?? new FixedClock());

    /// <summary>A vCenter that admits to nothing, whatever it actually holds.</summary>
    private static FakeApi SilentProbe() => new() { Available = [] };

    [Fact]
    public async Task A_sample_is_filed_under_the_time_vcenter_took_it_not_when_we_asked()
    {
        // Stamped with the local clock, a five-minute-old datastore sample
        // claimed to be current, and the same sample read twice became two
        // rows a few seconds apart — which for a summation is counting twice.
        var takenAt = new DateTimeOffset(2026, 9, 21, 11, 59, 40, TimeSpan.Zero);
        var batch = await Source(new FakeApi { SampledAtUtc = takenAt }, new Targets("host-1"))
            .ReadAsync(CancellationToken.None);

        Assert.NotEmpty(batch.Observations);
        Assert.All(batch.Observations, o => Assert.Equal(takenAt, o.SampledAtUtc));
    }

    [Fact]
    public async Task Earlier_samples_are_kept_for_the_store_and_kept_away_from_the_rules()
    {
        var takenAt = new DateTimeOffset(2026, 9, 21, 11, 59, 40, TimeSpan.Zero);
        var batch = await Source(new FakeApi { SampledAtUtc = takenAt }, new Targets("host-1"))
            .ReadAsync(CancellationToken.None);

        // One value per series is still what a rule sees.
        Assert.Equal(
            batch.Observations.Count,
            batch.Observations.Select(o => (o.Entity, o.Value.CounterName, o.Value.Instance)).Distinct().Count());

        Assert.Equal(batch.Observations.Count, batch.Backfill.Count);
        Assert.All(batch.Backfill, o => Assert.Equal(takenAt.AddSeconds(-20), o.SampledAtUtc));
        Assert.All(batch.Backfill, o => Assert.Equal(new EntityId("host-1"), o.Entity));
    }

    [Fact]
    public async Task A_reply_without_sample_times_falls_back_to_the_local_clock_and_keeps_nothing_earlier()
    {
        var clock = new FixedClock();
        var batch = await Source(new FakeApi(), new Targets("host-1"), clock)
            .ReadAsync(CancellationToken.None);

        Assert.All(batch.Observations, o => Assert.Equal(clock.UtcNow, o.SampledAtUtc));
        Assert.Empty(batch.Backfill);
    }

    [Fact]
    public async Task Samples_are_attributed_to_the_entity_they_belong_to()
    {
        var api = new FakeApi();
        var batch = await Source(api, new Targets("host-1", "host-2"))
            .ReadAsync(CancellationToken.None);

        Assert.Equal("vc-1", batch.SourceInstanceId);
        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));
        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-2"));
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task A_sample_that_cannot_be_attributed_is_dropped_rather_than_guessed_at()
    {
        // An unattributed number is worse than no number: it looks like
        // knowledge.
        var targets = new Targets("host-1", "host-2");
        targets.Resolvable.Remove("host-2");

        var batch = await Source(new FakeApi(), targets).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));
        Assert.DoesNotContain(batch.Observations, o => o.Entity == new EntityId("host-2"));
    }

    [Fact]
    public async Task A_counter_the_platform_is_not_collecting_is_reported_not_silently_missing()
    {
        // The vSphere statistics level case. At level 1 the disk latency triad
        // is absent, and those three are what let the product say which layer
        // is slow rather than merely that something is.
        //
        // The three device SCSI fault counters ride the same path, which is
        // the point of checking them here: they are level 2 like the triad,
        // so an estate that has not raised its statistics level loses them
        // too — and losing a fault counter silently is worse than losing a
        // latency one, because its zeros are supposed to be trustworthy. One
        // failure per counter, named, is what stops "no resets reported" from
        // being read as "no resets happened".
        var api = new FakeApi
        {
            Available = [.. VsphereCounters.Host.Where(k => !k.StartsWith("disk.", StringComparison.Ordinal))],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var insufficient = batch.Failures
            .Where(f => f.Kind == CollectionFailureKind.InsufficientDetailLevel)
            .ToList();

        Assert.Equal(7, insufficient.Count);
        Assert.Contains(insufficient, f => f.Target == "disk.deviceLatency.average");
        Assert.Contains(insufficient, f => f.Target == "disk.queueLatency.average");
        Assert.Contains(insufficient, f => f.Target == "disk.kernelLatency.average");
        Assert.Contains(insufficient, f => f.Target == "disk.busResets.summation");
        Assert.Contains(insufficient, f => f.Target == "disk.commandsAborted.summation");
        Assert.Contains(insufficient, f => f.Target == "disk.scsiReservationConflicts.summation");
    }

    [Fact]
    public async Task The_counters_that_are_available_are_still_collected()
    {
        // Partial success: losing the disk triad must not cost the CPU and
        // memory readings too.
        var api = new FakeApi
        {
            Available = [.. VsphereCounters.Host.Where(k => !k.StartsWith("disk.", StringComparison.Ordinal))],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Value.CounterName == "cpu.usage.average");
        Assert.DoesNotContain(batch.Observations, o => o.Value.CounterName.StartsWith("disk.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_counter_this_vcenter_does_not_define_is_a_protocol_difference_not_a_config_problem()
    {
        var api = new FakeApi
        {
            Catalog = [.. new FakeApi().Catalog.Where(c => c.Key != "mem.swapused.average")],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var failure = Assert.Single(batch.Failures, f => f.Target == "mem.swapused.average");
        Assert.Equal(CollectionFailureKind.ProtocolError, failure.Kind);
    }

    [Fact]
    public async Task The_batch_size_respects_the_servers_limit()
    {
        // 256 * 0.8 / (counters per host) entities per query, so the ceiling
        // moves down as counters are added. Asserted as a ceiling rather than
        // a number for exactly that reason.
        var api = new FakeApi();
        var hosts = Enumerable.Range(0, 60).Select(i => $"host-{i}").ToArray();

        await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.All(api.ObservedBatchSizes, size => Assert.True(size <= 25));
        Assert.Equal(60, api.ObservedBatchSizes.Sum());
    }

    [Fact]
    public async Task A_refused_query_is_retried_smaller_rather_than_abandoned()
    {
        // The server told us what it will accept; believing it is cheaper than
        // guessing, and a fixed guess is how the previous product's charts came
        // back empty.
        var api = new FakeApi { RefuseBatchesLargerThan = 5 };
        var hosts = Enumerable.Range(0, 20).Select(i => $"host-{i}").ToArray();

        var batch = await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.Equal(20, batch.Observations.Select(o => o.Entity).Distinct().Count());
        Assert.Contains(api.ObservedBatchSizes, size => size > 5);  // the refused attempt
        Assert.Contains(api.ObservedBatchSizes, size => size <= 5); // the accepted one
    }

    [Fact]
    public async Task Having_had_to_shrink_the_query_is_reported()
    {
        // Silently succeeding more slowly hides a tuning problem an operator
        // could fix.
        var api = new FakeApi { RefuseBatchesLargerThan = 5 };
        var hosts = Enumerable.Range(0, 20).Select(i => $"host-{i}").ToArray();

        var batch = await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Failures, f => f.Detail.Contains("reduced to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_query_refused_even_for_one_entity_stops_rather_than_looping()
    {
        var api = new FakeApi { RefuseBatchesLargerThan = 0 };

        var batch = await Source(api, new Targets("host-1", "host-2")).ReadAsync(CancellationToken.None);

        Assert.Empty(batch.Observations);
        Assert.Contains(batch.Failures, f =>
            f.Detail.Contains("even for a single entity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_server_limit_falls_back_rather_than_failing()
    {
        // Null is a legitimate answer: the setting may be absent on older
        // versions or the account may not be allowed to read it.
        var api = new FakeApi { MaxQueryMetrics = null };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.NotEmpty(batch.Observations);
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task Nothing_to_sample_is_not_an_error()
    {
        var batch = await Source(new FakeApi(), new Targets()).ReadAsync(CancellationToken.None);

        Assert.Empty(batch.Observations);
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task A_source_that_admits_to_nothing_is_still_asked_once()
    {
        // The first cycle queries regardless. Trusting the probe from the
        // outset is what made forty-one datastores read as unmeasured, and a
        // product that never asks cannot discover it was wrong to stop.
        var api = SilentProbe();

        await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.Single(api.ObservedQueries);
    }

    [Fact]
    public async Task And_then_left_alone_until_the_hour_is_up()
    {
        // Asking every thirty seconds for an answer that will not change is a
        // monitoring tool making work for the system it monitors.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddMinutes(30);
        await source.ReadAsync(CancellationToken.None);

        Assert.Single(api.ObservedQueries);
    }

    [Fact]
    public async Task And_asked_again_once_it_is()
    {
        // The skip expires. A statistics level raised this morning starts
        // producing data the same day rather than at the next restart.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddHours(2);
        await source.ReadAsync(CancellationToken.None);

        Assert.Equal(2, api.ObservedQueries.Count);
    }

    [Fact]
    public async Task A_probe_the_data_contradicts_stops_being_consulted()
    {
        // The point of re-checking. If the query returns data for a type the
        // probe dismissed, the probe is wrong here — and measuring once an
        // hour while the data was there all along would be its own defect.
        var api = SilentProbe();
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddSeconds(30);
        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddSeconds(60);
        var third = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(3, api.ObservedQueries.Count);
        Assert.NotEmpty(third.Observations);
        Assert.Empty(third.Failures);
    }

    [Fact]
    public async Task A_source_that_admits_to_nothing_says_so_in_one_sentence()
    {
        // Not one complaint per counter. Against a live vCenter that produced
        // two messages each claiming a counter "requires statistics level 1",
        // which is not a thing that can be true — level 1 is the floor.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var failure = Assert.Single(batch.Failures);

        Assert.Equal(CollectionFailureKind.NotConfigured, failure.Kind);
        Assert.Equal("HostSystem", failure.Target);
        Assert.DoesNotContain("level 1", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- one entity type failing, and the rest of the read surviving ------

    /// <summary>A vCenter that faults on the virtual-machine query only.</summary>
    private static FakeApi FaultingOnVirtualMachines(Exception error) =>
        new() { FaultOn = (VsphereEntityType.VirtualMachine, error) };

    /// <summary>One host and one VM, so the two types can be told apart.</summary>
    private static Targets HostAndVirtualMachine() =>
        new Targets("host-1").AndVirtualMachines("vm-1");

    private static VsphereApiException Fault(VsphereFaultKind kind, string message) =>
        new(kind, message);

    [Fact]
    public async Task One_entity_types_failure_does_not_discard_another_types_samples()
    {
        // The deployment this was found in, and it is a common one: the
        // monitoring account may read some entity types and not others. The
        // host samples are gathered first and were then thrown away when the
        // next type's query faulted, so a vCenter that was answering perfectly
        // well produced no metrics at all — every cycle, indefinitely, while
        // the data was already in hand.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.NoPermission, "Permission to perform this operation was denied."));

        var batch = await Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));
    }

    [Fact]
    public async Task The_type_that_could_not_be_read_is_named_rather_than_the_whole_vcenter()
    {
        // What the operator is sent to do. "Virtual machine metrics are not
        // permitted" ends at a role assignment; "collector unreachable" — which
        // is what the runner reports for an escaped exception — sends somebody
        // to the network team about a vCenter that is answering every other
        // query, and they find nothing, because nothing is wrong there.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.NoPermission, "Permission to perform this operation was denied."));

        var batch = await Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None);

        // Since T1.4 a query fault is narrowed to the entities it is about;
        // the report still names the type, and still sends the operator to a role.
        var failure = Assert.Single(batch.Failures, f => f.Target.StartsWith("VirtualMachine", StringComparison.Ordinal));

        Assert.Equal(CollectionFailureKind.AuthorizationDenied, failure.Kind);
        Assert.Contains("denied", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_type_that_could_not_be_read_contributes_no_samples_at_all()
    {
        // In vSphere the normal shape of failure is silence, and a fabricated
        // zero is worse than a gap because zero reads as a measurement: a VM
        // with no cpu.ready sample is unknown, and one reading zero is a VM
        // with no CPU contention. Nothing downstream can tell those apart
        // afterwards, so the difference has to be preserved here.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.NoPermission, "Permission to perform this operation was denied."));

        var batch = await Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None);

        Assert.DoesNotContain(batch.Observations, o => o.Entity == new EntityId("vm-1"));
    }

    [Fact]
    public async Task A_fault_on_one_managed_object_costs_that_type_and_not_the_cycle()
    {
        // An object deleted between being inventoried and being read. Common on
        // a busy estate and nobody's fault, so it must not escalate: throwing
        // would have a routine VM deletion mark the whole vCenter Unknown and
        // trigger two pointless re-reads of the types that had just worked.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.ManagedObjectNotFound, "The object has already been deleted."));

        var batch = await Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));

        // Narrower since T1.4: the fault is about one object, so the report
        // names that object rather than the whole type.
        var gone = Assert.Single(batch.Failures, f => f.Target.StartsWith("VirtualMachine", StringComparison.Ordinal));
        Assert.Equal(CollectionFailureKind.ProtocolError, gone.Kind);
        Assert.Contains("vm-1", gone.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_login_is_not_demoted_to_one_types_problem()
    {
        // Credentials belong to the session, not to the type being asked about:
        // if this login is rejected the next type's query is rejected too, and
        // reporting one failure per type would say three things about one
        // problem and name none of them. Worse, the runner would never see it —
        // it counts a returned batch as a success — so the one-strike rule that
        // exists to stop repeated rejected logins would never fire, and the
        // product would spend the account's way into an SSO lockout while
        // showing a message promising it does not.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.InvalidLogin, "Cannot complete login due to an incorrect user name or password."));

        var thrown = await Assert.ThrowsAsync<VsphereApiException>(() =>
            Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None));

        Assert.Equal(VsphereFaultKind.InvalidLogin, thrown.Kind);
    }

    [Fact]
    public async Task An_expired_session_is_not_demoted_to_one_types_problem()
    {
        // The session token is gone, so every remaining query in this call
        // would fault for the same reason. The fix is to start the read again
        // on a fresh connection, which only the runner can do — holding the
        // failure here as a per-type note would leave the collector looking
        // reachable while it quietly stopped collecting anything.
        var api = FaultingOnVirtualMachines(
            Fault(VsphereFaultKind.NotAuthenticated, "The session is not authenticated."));

        var thrown = await Assert.ThrowsAsync<VsphereApiException>(() =>
            Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None));

        Assert.Equal(VsphereFaultKind.NotAuthenticated, thrown.Kind);
    }

    [Fact]
    public async Task A_failure_with_no_fault_behind_it_is_not_demoted_either()
    {
        // vCenter answers a query it dislikes with a SOAP fault, so a transport
        // exception means it did not answer at all — the socket, the TLS
        // handshake or the host itself. There is nothing to say about one
        // entity type in particular, and saying it anyway would have the
        // product report "virtual machine metrics unavailable" while the truth
        // is that the vCenter is gone and every other number on screen is
        // equally stale.
        var api = FaultingOnVirtualMachines(
            new HttpRequestException("The SSL connection could not be established."));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Source(api, HostAndVirtualMachine()).ReadAsync(CancellationToken.None));
    }

    // --- datastore counters, measured on hosts ----------------------------

    /// <summary>A host that sees two volumes, and targets that know them.</summary>
    private static (FakeApi Api, Targets Targets) HostWithVolumes()
    {
        var api = new FakeApi { Volumes = ["vol-aaa", "vol-bbb"] };
        var targets = new Targets("host-1");

        targets.Volumes["vol-aaa"] = "ds-prod";
        targets.Volumes["vol-bbb"] = "ds-test";
        targets.Names["host-1"] = "esx01.corp.local";

        return (api, targets);
    }

    private static IEnumerable<Observation> DatastoreLatency(ObservationBatch batch) =>
        batch.Observations.Where(o =>
            o.Value.CounterName == "datastore.totalReadLatency.average");

    [Fact]
    public async Task A_datastore_counter_is_filed_under_the_datastore_not_the_host_that_reported_it()
    {
        // The correction this whole path exists for. vSphere keeps datastore
        // counters on HostSystem, so the sample arrives under a host; filing
        // it there would put storage latency on the wrong page and leave every
        // datastore in the estate unmeasured, which is what happened.
        var (api, targets) = HostWithVolumes();

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        var latency = DatastoreLatency(batch).ToList();

        Assert.Equal(2, latency.Count);
        Assert.Contains(latency, o => o.Entity == new EntityId("ds-prod"));
        Assert.Contains(latency, o => o.Entity == new EntityId("ds-test"));
        Assert.DoesNotContain(latency, o => o.Entity == new EntityId("host-1"));
    }

    [Fact]
    public async Task The_instance_becomes_the_host_that_measured_it()
    {
        // One datastore, one series per host. This is the distinction the
        // product is for: a volume slow from every host is the array or the
        // fabric, and one slow from a single host is that host's HBA, cable
        // or path. Collapsing to a single number per datastore would throw
        // away the only evidence that separates them.
        var (api, targets) = HostWithVolumes();

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        Assert.All(DatastoreLatency(batch), o =>
            Assert.Equal("esx01.corp.local", o.Value.Instance));
    }

    [Fact]
    public async Task A_reattributed_series_says_that_its_instance_is_a_vantage_point()
    {
        // The flag the peer comparison is built on, set in exactly one place
        // in the product and until now fabricated by hand in every test that
        // read it. Without it "slow from one host only" — the rung of the
        // ladder the whole storage chain was built for — never fires again on
        // a real estate, because the rule sees no series it is allowed to
        // compare and a broken rule looks exactly like a healthy fabric.
        var (api, targets) = HostWithVolumes();

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        var latency = DatastoreLatency(batch).ToList();

        Assert.Equal(2, latency.Count);
        Assert.All(latency, o =>
            Assert.True(o.Value.InstanceIsVantagePoint, o.Entity.Value));
    }

    [Fact]
    public async Task A_host_counter_that_was_not_reattributed_is_not_a_vantage_point()
    {
        // The other direction, and the one that kills a flag set everywhere.
        // A host's own LUNs and paths are devices belonging to it, not places
        // it was observed from; marking them comparable would have the rule
        // compare thirty-two LUNs of one host against each other and call the
        // slowest of them a per-host storage problem.
        var (api, targets) = HostWithVolumes();

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        var hostCounters = batch.Observations
            .Where(o => o.Entity == new EntityId("host-1"))
            .ToList();

        Assert.NotEmpty(hostCounters);
        Assert.All(hostCounters, o =>
            Assert.False(o.Value.InstanceIsVantagePoint, o.Value.CounterName));
    }

    [Fact]
    public async Task A_volume_that_is_not_inventoried_is_dropped_rather_than_guessed_at()
    {
        // A datastore mounted on a host but not collected, or added since the
        // last inventory cycle. Attaching its latency to whichever datastore
        // happened to be nearby would be a number that looks like knowledge.
        var api = new FakeApi { Volumes = ["vol-aaa", "vol-unknown"] };
        var targets = new Targets("host-1");
        targets.Volumes["vol-aaa"] = "ds-prod";
        targets.Names["host-1"] = "esx01.corp.local";

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        var latency = Assert.Single(DatastoreLatency(batch));
        Assert.Equal(new EntityId("ds-prod"), latency.Entity);
    }

    [Fact]
    public async Task A_host_counter_is_still_a_host_counter()
    {
        // The re-attribution must be narrow. CPU and disk latency are about
        // the host and stay there; a rule that moved everything with an
        // instance would empty the host pages.
        var (api, targets) = HostWithVolumes();

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o =>
            o.Value.CounterName == "cpu.usage.average" && o.Entity == new EntityId("host-1"));
        Assert.Contains(batch.Observations, o =>
            o.Value.CounterName == "disk.deviceLatency.average" && o.Entity == new EntityId("host-1"));
    }

    [Fact]
    public async Task Without_a_display_name_the_series_is_still_labelled_rather_than_left_blank()
    {
        // An entity the graph has not caught up with. An unlabelled series
        // would silently merge with another host's, averaging two hosts'
        // storage paths into one line.
        var api = new FakeApi { Volumes = ["vol-aaa"] };
        var targets = new Targets("host-1");
        targets.Volumes["vol-aaa"] = "ds-prod";

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        Assert.Equal("host-1", Assert.Single(DatastoreLatency(batch)).Value.Instance);
    }

    [Fact]
    public async Task Two_hosts_seeing_one_datastore_produce_two_series_not_one()
    {
        // The shape a shared SAN volume actually has, and the reason the
        // instance had to change rather than be cleared.
        var api = new FakeApi { Volumes = ["vol-aaa"] };
        var targets = new Targets("host-1", "host-2");
        targets.Volumes["vol-aaa"] = "ds-prod";
        targets.Names["host-1"] = "esx01";
        targets.Names["host-2"] = "esx02";

        var batch = await Source(api, targets).ReadAsync(CancellationToken.None);

        var latency = DatastoreLatency(batch).ToList();

        Assert.Equal(2, latency.Count);
        Assert.All(latency, o => Assert.Equal(new EntityId("ds-prod"), o.Entity));
        Assert.Equal(["esx01", "esx02"], latency.Select(o => o.Value.Instance).Order());
    }

    // --- telling the operator what cannot be measured ---------------------

    /// <summary>
    /// A source whose samples the test dictates, so the SIOC condition can be
    /// posed directly rather than assembled out of counter plumbing.
    /// </summary>
    private sealed class DictatedApi(params CounterValue[] values) : IVsphereApi
    {
        public string InstanceId => "vc-1";

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>(
            [
                .. values.Select((v, i) => new VsphereCounter
                {
                    Id = i + 1,
                    Group = v.CounterName.Split('.')[0],
                    Name = v.CounterName.Split('.')[1],
                    Rollup = v.Rollup,
                    Unit = v.Unit,
                    Level = 1,
                }),
            ]);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) => Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string moRef, VsphereEntityType type, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. values.Select(v => v.CounterName)]);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> moRefs, VsphereEntityType type,
            IReadOnlyList<VsphereCounter> counters, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PerfEntitySamples>>(
                [.. moRefs.Select(m => new PerfEntitySamples { EntityMoRef = m, Values = values })]);
    }

    private static CounterValue Reading(string counter, double raw, string instance = "vol-a") => new()
    {
        CounterName = counter,
        Raw = raw,
        Rollup = RollupType.Average,
        Interval = TimeSpan.FromSeconds(20),
        Unit = "number",
        Instance = instance,
    };

    private const string Sioc = "datastore.siocActiveTimePercentage.average";
    private const string ReadIops = "datastore.numberReadAveraged.average";

    /// <summary>
    /// The unmeasurable-latency report, if this batch produced one.
    /// </summary>
    /// <remarks>
    /// Matched on kind as well as target. The dictated catalogue below holds
    /// only the counters a test names, so the source also — correctly —
    /// reports every other wanted counter as absent, and one of those absences
    /// is about this same counter. Two different findings about one counter is
    /// the right behaviour; conflating them in a test is not.
    /// </remarks>
    private static IEnumerable<CollectionFailure> Unmeasurable(
        IReadOnlyList<CollectionFailure> failures) =>
        failures.Where(f =>
            f.Kind == CollectionFailureKind.NotConfigured &&
            f.Target == "datastore.datastoreVMObservedLatency.latest");

    private static async Task<IReadOnlyList<CollectionFailure>> Failures(params CounterValue[] values)
    {
        var targets = new Targets("host-1");
        targets.Volumes["vol-a"] = "ds-prod";
        targets.Volumes["vol-b"] = "ds-test";

        var batch = await new VsphereObservationSource(
            new DictatedApi(values), targets, new FixedClock())
            .ReadAsync(CancellationToken.None);

        return batch.Failures;
    }

    [Fact]
    public async Task Sioc_being_off_while_volumes_are_busy_is_reported_as_unmeasurable()
    {
        // The product saying what it cannot see. A latency chart of zeroes
        // reads as a fast array; here it means nothing was measured, and an
        // operator who is not told will eliminate storage on the strength of
        // it.
        var failures = await Failures(Reading(Sioc, 0), Reading(ReadIops, 400));

        var failure = Assert.Single(Unmeasurable(failures));

        Assert.Contains("not measured", failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Storage I/O Control", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idle_estate_is_not_told_to_enable_anything()
    {
        // SIOC reads zero on an idle datastore too. Advising somebody to turn
        // on a feature for a volume nobody uses is how advice gets ignored.
        var failures = await Failures(Reading(Sioc, 0), Reading(ReadIops, 0));

        Assert.Empty(Unmeasurable(failures));
    }

    [Fact]
    public async Task An_estate_with_sioc_running_is_left_alone()
    {
        var failures = await Failures(Reading(Sioc, 12), Reading(ReadIops, 400));

        Assert.Empty(Unmeasurable(failures));
    }

    [Fact]
    public async Task One_report_for_the_estate_rather_than_one_per_volume()
    {
        // Forty-one identical messages would be a wall, and the fix is a
        // single decision about the platform rather than one per volume.
        var failures = await Failures(
            Reading(Sioc, 0, "vol-a"),
            Reading(ReadIops, 400, "vol-a"),
            Reading(Sioc, 0, "vol-b"),
            Reading(ReadIops, 900, "vol-b"));

        Assert.Single(Unmeasurable(failures));
    }

    [Fact]
    public async Task A_vcenter_that_never_reported_sioc_at_all_is_not_second_guessed()
    {
        // The counter absent entirely is a different problem with its own
        // report; claiming SIOC is off because nothing said otherwise would be
        // inventing a finding out of a silence.
        var failures = await Failures(Reading(ReadIops, 400));

        Assert.Empty(Unmeasurable(failures));
    }
}
