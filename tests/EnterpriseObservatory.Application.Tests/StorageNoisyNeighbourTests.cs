using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Naming the machines that load a slow volume, and refusing to when the
/// evidence says the array is at fault instead.
/// </summary>
/// <remarks>
/// Every silence here is tested with the fixture that fires changed in exactly
/// one respect, so a test that passes proves the gate it names rather than
/// proving the fixture was quiet to begin with.
/// </remarks>
public class StorageNoisyNeighbourTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Volume = "vc-1:ds-prod";
    private const string Reads = "virtualDisk.numberReadAveraged.average";
    private const string Writes = "virtualDisk.numberWriteAveraged.average";

    private static readonly string[] Hosts = ["esx01", "esx02", "esx03"];

    // --- fixture ------------------------------------------------------------

    private static Observation VolumeReading(
        string host, string counter, double value, string unit, string volume = Volume) => new()
    {
        Entity = new EntityId(volume),
        Source = "vc-1",
        SampledAtUtc = T0,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = value,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = unit,
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    private static IEnumerable<Observation> Latency(double ms, string volume = Volume) =>
        Hosts.Select(h => VolumeReading(h, "datastore.totalReadLatency.average", ms, "millisecond", volume));

    private static IEnumerable<Observation> Load(double perHost, string volume = Volume) =>
        Hosts.Select(h => VolumeReading(h, "datastore.numberReadAveraged.average", perHost, "number", volume));

    private static Observation Machine(string vm, double ops, string counter = Reads, string instance = "") => new()
    {
        Entity = new EntityId(vm),
        Source = "vc-1",
        SampledAtUtc = T0,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = ops,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "number",
            Instance = instance,
        },
    };

    private static Entity Node(string id, EntityKind kind, ObservationState state = ObservationState.Active) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id.Split(':')[^1],
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        ObservationState = state,
    };

    private static Relationship StoredOn(string vm, string volume) => new()
    {
        From = new EntityId(vm),
        To = new EntityId(volume),
        Kind = RelationshipKind.BackedBy,
        ObservedAtUtc = T0,
    };

    /// <summary>One loud machine and four quiet ones, all stored only on the volume.</summary>
    private static readonly string[] Quiet = ["vc-1:vm-2", "vc-1:vm-3", "vc-1:vm-4", "vc-1:vm-5"];

    private const string Loud = "vc-1:vm-1";

    private static EntityGraph Graph(
        IEnumerable<string>? residents = null,
        IEnumerable<Relationship>? extraEdges = null,
        IEnumerable<Entity>? extraNodes = null)
    {
        var vms = (residents ?? [Loud, .. Quiet]).ToList();

        var entities = new List<Entity>
        {
            Node(Volume, EntityKind.Datastore),
            Node("vc-1:ds-other", EntityKind.Datastore),
        };

        entities.AddRange(vms.Select(v => Node(v, EntityKind.VirtualMachine)));
        entities.AddRange(extraNodes ?? []);

        return new EntityGraph
        {
            Entities = entities.DistinctBy(e => e.Id).ToDictionary(e => e.Id),
            Relationships = [.. vms.Select(v => StoredOn(v, Volume)), .. extraEdges ?? []],
        };
    }

    /// <summary>
    /// Slow from every host, load four times its usual level, and one machine
    /// at twenty times its neighbours.
    /// </summary>
    private static List<Observation> Firing(double ms = 9, double loadPerHost = 400, double loud = 1000) =>
    [
        .. Latency(ms),
        .. Load(loadPerHost),
        Machine(Loud, loud),
        .. Quiet.Select(v => Machine(v, 50)),
    ];

    /// <summary>A history in which every load series usually runs at this rate.</summary>
    private static Func<SeriesKey, double?> Usual(double perSeries) => _ => perSeries;

    private static readonly Func<SeriesKey, double?> NoHistory = _ => null;

    private static IReadOnlyList<Domain.Alerts.AlertDefinition> Run(
        List<Observation> observations,
        EntityGraph? graph = null,
        Func<SeriesKey, double?>? history = null,
        StorageNoisyNeighbourPolicy? policy = null) =>
        StorageNoisyNeighbour.Evaluate(observations, graph ?? Graph(), history ?? Usual(100), policy);

    // --- fires ----------------------------------------------------------------

    [Fact]
    public void A_slow_volume_whose_load_rose_names_the_machine_carrying_it()
    {
        var alert = Assert.Single(Run(Firing()));

        Assert.Equal(new EntityId(Volume), alert.Entity);
        Assert.Equal(StorageNoisyNeighbour.Category, alert.Category);
        Assert.Contains("'vm-1'", alert.Description, StringComparison.Ordinal);
        Assert.Contains("1000 operations a second", alert.Description, StringComparison.Ordinal);

        // The quiet ones are not accused.
        Assert.DoesNotContain("'vm-2'", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_and_write_are_one_machines_load_together()
    {
        // 150 reads is under four times neighbours at 50; 150 reads and 150
        // writes is not. Judging the two halves separately would miss a
        // machine split evenly between them.
        Assert.Empty(Run(Firing(loud: 150)));

        var observations = Firing(loud: 150);
        observations.Add(Machine(Loud, 150, Writes));

        var alert = Assert.Single(Run(observations));

        Assert.Contains("300 operations a second", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_loud_machines_are_both_named()
    {
        // Each is compared with the median of everyone else, which still
        // includes the other loud one. With four quiet neighbours that median
        // stays quiet, so both stand out.
        var graph = Graph([Loud, "vc-1:vm-6", .. Quiet]);
        var observations = Firing();
        observations.Add(Machine("vc-1:vm-6", 800));

        var alert = Assert.Single(Run(observations, graph));

        Assert.Contains("'vm-1'", alert.Description, StringComparison.Ordinal);
        Assert.Contains("'vm-6'", alert.Description, StringComparison.Ordinal);
    }

    // --- the volume must be slow --------------------------------------------

    [Fact]
    public void A_volume_reading_zero_latency_is_not_slow_and_nothing_is_named()
    {
        // The estate this was written against: latency truncated to zero on
        // every volume. The rule is silent there by design, not by accident,
        // and nothing is worked around to make it speak.
        Assert.Empty(Run(Firing(ms: 0)));
    }

    [Fact]
    public void A_volume_slow_from_one_host_only_is_that_hosts_path_not_a_workload()
    {
        List<Observation> observations =
        [
            VolumeReading("esx01", "datastore.totalReadLatency.average", 30, "millisecond"),
            VolumeReading("esx02", "datastore.totalReadLatency.average", 0, "millisecond"),
            VolumeReading("esx03", "datastore.totalReadLatency.average", 0, "millisecond"),
            .. Firing().Where(o => o.Value.Unit != "millisecond"),
        ];

        Assert.Empty(Run(observations));
    }

    [Fact]
    public void Slow_means_the_floor_the_peer_rule_was_given()
    {
        // Nine milliseconds fires at the default floor of five. Raise the
        // shared floor above it and this rule must follow.
        var strict = StorageNoisyNeighbourPolicy.Default with
        {
            Peers = PeerOutlierPolicy.Default with { MinimumMilliseconds = 10 },
        };

        Assert.Empty(Run(Firing(), policy: strict));
        Assert.Single(Run(Firing(ms: 10.0001), policy: strict));
    }

    // --- the suppressor -------------------------------------------------------

    [Fact]
    public void Load_that_did_not_rise_means_the_array_degraded_and_no_machine_is_blamed()
    {
        // Slow at 400 a host, when 400 a host is what it always carries. The
        // loud machine is loud every day; the volume is slow today. Naming it
        // would send someone to throttle a workload while the controller fails.
        Assert.Empty(Run(Firing(), history: Usual(400)));
    }

    [Fact]
    public void The_rise_is_measured_against_the_rise_multiple()
    {
        // 400 against a usual 267 is 1.498x -- just under half again.
        Assert.Empty(Run(Firing(), history: Usual(267)));
        Assert.Single(Run(Firing(), history: Usual(266)));
    }

    [Fact]
    public void Unknown_history_is_not_evidence_that_load_rose()
    {
        Assert.Empty(Run(Firing(), history: NoHistory));
    }

    [Fact]
    public void One_host_without_history_makes_the_whole_baseline_unknown()
    {
        // Summing only the known series would understate "usual" -- a host
        // added this morning has none -- and an understated baseline is
        // exactly what makes load look as though it rose.
        Func<SeriesKey, double?> partial = k => k.Instance == "esx03" ? null : 100;

        Assert.Empty(Run(Firing(), history: partial));
    }

    [Fact]
    public void History_is_not_read_for_a_volume_that_failed_a_cheaper_gate()
    {
        // The store is a database round trip. On a healthy estate no volume
        // reaches the suppressor and the rule costs nothing.
        Func<SeriesKey, double?> exploding = _ => throw new InvalidOperationException("read");

        Assert.Empty(Run(Firing(ms: 0), history: exploding));
    }

    // --- the machine must stand out ----------------------------------------

    [Fact]
    public void A_machine_below_the_floor_is_nobodys_problem_however_it_compares()
    {
        // 99 against neighbours at 5 is twenty times them and still not load
        // that could slow a shared array.
        var observations = Firing(loud: 99)
            .Select(o => Quiet.Contains(o.Entity.Value) ? Machine(o.Entity.Value, 5) : o)
            .ToList();

        Assert.Empty(Run(observations));

        observations = [.. observations.Where(o => o.Entity.Value != Loud), Machine(Loud, 100.0001)];
        Assert.Single(Run(observations));
    }

    [Fact]
    public void A_machine_under_the_multiple_of_its_neighbours_is_not_named()
    {
        // 199.99 is just under four times a median of 50.
        Assert.Empty(Run(Firing(loud: 199.99)));
        Assert.Single(Run(Firing(loud: 200)));
    }

    [Fact]
    public void Too_few_neighbours_have_no_median_worth_comparing_with()
    {
        // The loud machine and two others: a median of two is a disagreement.
        var graph = Graph([Loud, "vc-1:vm-2", "vc-1:vm-3"]);

        Assert.Empty(Run(Firing(), graph));

        graph = Graph([Loud, "vc-1:vm-2", "vc-1:vm-3", "vc-1:vm-4"]);
        Assert.Single(Run(Firing(), graph));
    }

    [Fact]
    public void An_unmeasured_machine_is_not_a_quiet_neighbour()
    {
        // Powered off, reporting nothing. Counted at zero it would supply the
        // third neighbour the rule needs and drag the median down.
        var graph = Graph([Loud, "vc-1:vm-2", "vc-1:vm-3", "vc-1:vm-off"]);
        List<Observation> observations =
        [
            .. Firing().Where(o => o.Entity.Value is not ("vc-1:vm-4" or "vc-1:vm-5")),
        ];

        Assert.Empty(Run(observations, graph));
    }

    // --- attribution ------------------------------------------------------------

    [Fact]
    public void A_machine_on_two_volumes_is_neither_named_nor_counted_and_the_alert_says_so()
    {
        // Its total cannot be split between volumes, so it is not evidence
        // about either. It must not be named, even though it is the loudest.
        const string Spanning = "vc-1:vm-9";

        var graph = Graph(
            extraEdges: [StoredOn(Spanning, Volume), StoredOn(Spanning, "vc-1:ds-other")],
            extraNodes: [Node(Spanning, EntityKind.VirtualMachine)]);

        var observations = Firing();
        observations.Add(Machine(Spanning, 5000));

        var alert = Assert.Single(Run(observations, graph));

        Assert.DoesNotContain("'vm-9'", alert.Description, StringComparison.Ordinal);
        Assert.Contains("1 machine(s) with disks on this and another volume", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_on_two_volumes_cannot_be_the_only_culprit()
    {
        const string Spanning = "vc-1:vm-9";

        var graph = Graph(
            residents: Quiet,
            extraEdges: [StoredOn(Spanning, Volume), StoredOn(Spanning, "vc-1:ds-other")],
            extraNodes: [Node(Spanning, EntityKind.VirtualMachine)]);

        List<Observation> observations =
        [
            .. Latency(9),
            .. Load(400),
            Machine(Spanning, 5000),
            .. Quiet.Select(v => Machine(v, 50)),
        ];

        Assert.Empty(Run(observations, graph));
    }

    [Fact]
    public void A_vanished_machine_is_not_judged()
    {
        var graph = Graph(
            residents: Quiet,
            extraEdges: [StoredOn(Loud, Volume)],
            extraNodes: [Node(Loud, EntityKind.VirtualMachine, ObservationState.Vanished)]);

        Assert.Empty(Run(Firing(), graph));
    }

    [Fact]
    public void Per_disk_readings_are_not_added_to_the_machines_total()
    {
        // The collector sends one summed figure. If per-disk series ever
        // arrive beside it, adding them in would count every request twice.
        var observations = Firing(loud: 150);
        observations.Add(Machine(Loud, 150, instance: "scsi0:0"));

        Assert.Empty(Run(observations));
    }

    [Fact]
    public void The_fingerprint_is_the_volume_so_a_changing_culprit_is_one_incident()
    {
        var first = Assert.Single(Run(Firing()));

        var graph = Graph([Loud, "vc-1:vm-6", .. Quiet]);
        List<Observation> swapped =
        [
            .. Firing(loud: 50),
            Machine("vc-1:vm-6", 1000),
        ];

        var second = Assert.Single(Run(swapped, graph));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Contains("'vm-6'", second.Description, StringComparison.Ordinal);
    }

    // --- history ------------------------------------------------------------------

    [Fact]
    public void The_usual_level_is_the_mean_of_the_last_day_leaving_out_the_last_hour()
    {
        var key = new SeriesKey(new EntityId(Volume), "datastore.numberReadAveraged.average", "esx01");
        var store = new RecordingStore(
            new AggregatedSample { StartUtc = T0.AddHours(-20), Min = 0, Max = 0, Sum = 300, Count = 3, Last = 0 },
            new AggregatedSample { StartUtc = T0.AddHours(-10), Min = 0, Max = 0, Sum = 100, Count = 1, Last = 0 });

        var usual = StorageNoisyNeighbour.TypicalRateFrom(store, T0)(key);

        // Weighted by samples, not by buckets: 400 over 4, not (100 + 100) / 2.
        Assert.Equal(100d, usual);

        var asked = Assert.Single(store.Queries);
        Assert.Equal(key, asked.Key);
        Assert.Equal(T0.AddDays(-1), asked.FromUtc);
        Assert.Equal(T0.AddHours(-1), asked.ToUtc);
    }

    [Fact]
    public void A_series_with_nothing_in_the_window_has_no_usual_level()
    {
        var key = new SeriesKey(new EntityId(Volume), "datastore.numberReadAveraged.average", "esx01");

        Assert.Null(StorageNoisyNeighbour.TypicalRateFrom(new RecordingStore(), T0)(key));
        Assert.Null(StorageNoisyNeighbour.TypicalRateFrom(new RecordingStore(exists: false), T0)(key));
    }

    // --- three values (ADR-0026) --------------------------------------------

    private static SubjectVerdict JudgeOne(
        List<Observation> observations,
        EntityGraph? graph = null,
        Func<SeriesKey, double?>? history = null,
        StorageNoisyNeighbourPolicy? policy = null) =>
        Assert.Single(StorageNoisyNeighbour.Judge(observations, graph ?? Graph(), history ?? Usual(100), policy, T0));

    [Fact]
    public void A_slow_volume_whose_load_rose_is_present()
    {
        var present = Assert.IsType<ConditionPresent>(JudgeOne(Firing()));

        Assert.Equal(new EntityId(Volume), present.Entity);
        Assert.Equal(T0, present.EvidenceAtUtc);
    }

    [Fact]
    public void A_machine_under_the_multiple_of_its_neighbours_is_absent()
    {
        var absent = Assert.IsType<ConditionAbsent>(JudgeOne(Firing(loud: 199.99)));

        Assert.Equal(T0, absent.EvidenceAtUtc);
    }

    [Fact]
    public void Too_few_measured_residents_is_not_judgeable()
    {
        var graph = Graph([Loud, "vc-1:vm-2", "vc-1:vm-3"]);

        var unknown = Assert.IsType<Unknown>(JudgeOne(Firing(), graph));

        Assert.Equal(UnknownReason.NotJudgeable, unknown.Reason);
    }

    [Fact]
    public void A_missing_load_counter_is_not_collected()
    {
        List<Observation> withoutLoad = [.. Latency(9), Machine(Loud, 1000), .. Quiet.Select(v => Machine(v, 50))];

        var unknown = Assert.IsType<Unknown>(JudgeOne(withoutLoad));

        Assert.Equal(UnknownReason.InputNotCollected, unknown.Reason);
    }

    [Fact]
    public void Unknown_history_is_insufficient_series_rather_than_absent()
    {
        var unknown = Assert.IsType<Unknown>(JudgeOne(Firing(), history: NoHistory));

        Assert.Equal(UnknownReason.InsufficientSeries, unknown.Reason);
    }

    [Fact]
    public void A_baseline_read_that_throws_fails_only_this_volume()
    {
        Func<SeriesKey, double?> exploding = _ => throw new InvalidOperationException("read");

        var unknown = Assert.IsType<Unknown>(JudgeOne(Firing(), history: exploding));

        Assert.Equal(UnknownReason.RuleFailed, unknown.Reason);
        Assert.Contains("threw", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quiet_run_keeps_the_alert_open_across_the_rules_n_and_an_unknown_resets_the_count()
    {
        var rule = new StorageNoisyNeighbourRule();
        var graph = Graph();
        var raised = Firing();
        var absent = Firing(loud: 199.99);
        var unknown = Firing(); // history explodes this cycle: RuleFailed
        Func<SeriesKey, double?> history = Usual(100);
        Func<SeriesKey, double?> exploding = _ => throw new InvalidOperationException("read");

        IReadOnlyList<AlertInstance> stored = [];
        (List<Observation> Observations, Func<SeriesKey, double?> History)[] cycles =
        [
            (raised, history), (raised, history),
            (absent, history),
            (unknown, exploding),
            (absent, history), (absent, history),
        ];

        for (var minute = 0; minute < cycles.Length; minute++)
        {
            stored = Reconcile(rule, cycles[minute].Observations, graph, cycles[minute].History, stored, T0.AddMinutes(minute));
        }

        var alert = Assert.Single(stored);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
        Assert.Equal(2, alert.ConsecutiveAbsent);

        stored = Reconcile(rule, absent, graph, history, stored, T0.AddMinutes(cycles.Length));
        Assert.DoesNotContain(stored, i => i.State == AlertLifecycleState.Open);
    }

    private static IReadOnlyList<AlertInstance> Reconcile<TRule>(
        TRule rule,
        List<Observation> observations,
        EntityGraph graph,
        Func<SeriesKey, double?> history,
        IReadOnlyList<AlertInstance> stored,
        DateTimeOffset nowUtc)
        where TRule : IAnalysisRule
    {
        var context = new RuleContext
        {
            Observations = observations,
            ReadGraph = () => graph,
            NowUtc = nowUtc,
            Options = MonitoringOptions.Default,
            Series = new StubSeries(history),
            Events = new NoEvents(),
        };

        return AlertReconciler.Reconcile(
            new AlertReconciliationRequest
            {
                Scope = "observation",
                Stored = stored,
                NowUtc = nowUtc,
                Evaluations = [new RuleEvaluation(rule.RuleId, rule.Resolution, rule.Evaluate(context))],
                Sources = new EvidenceSources { Reporting = ["vc-1"], OwnerOf = _ => "vc-1" },
                RawRetention = TimeSpan.FromDays(2),
            }).Instances;
    }

    private sealed class StubSeries(Func<SeriesKey, double?> history) : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query)
        {
            var value = history(query.Key);

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = query.Resolution ?? SeriesResolution.FiveMinutes,
                Points = value is { } v ? [new AggregatedSample { StartUtc = query.FromUtc, Min = v, Max = v, Sum = v, Count = 1, Last = v }] : [],
                Exists = value is not null,
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    private sealed class NoEvents : EnterpriseObservatory.Application.Collection.IEventReader
    {
        public IReadOnlyList<EnterpriseObservatory.Application.Collection.SourceEvent> OfTypes(
            IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }

    private sealed class RecordingStore(params AggregatedSample[] points) : IObservationStore
    {
        private readonly bool _exists = true;

        public RecordingStore(bool exists)
            : this()
        {
            _exists = exists;
        }

        public List<SeriesQuery> Queries { get; } = [];

        public void Append(IReadOnlyList<Observation> observations) => throw new NotSupportedException();

        public SeriesResult Query(SeriesQuery query)
        {
            Queries.Add(query);

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = query.Resolution ?? SeriesResolution.Raw,
                Points = _exists ? points : [],
                Exists = _exists,
            };
        }

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

        public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) =>
            throw new NotSupportedException();
    }
}
