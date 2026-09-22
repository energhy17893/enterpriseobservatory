using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The half of the rung nothing covered.
/// </summary>
/// <remarks>
/// <para>
/// PeerOutliers finds a volume slow from one host and not the rest, which is
/// that host's path. Its own tests record the case it deliberately stays quiet
/// about — every host elevated together — and call naming a host there the
/// most expensive wrong answer it can give. It was right to stay quiet and
/// wrong to leave the situation unsaid: every host elevated together is the
/// array or the fabric, and that is this rule.
/// </para>
/// <para>
/// The two halves must never both fire for one volume, and the tests that pin
/// that run both rules over the same readings rather than trusting a comment.
/// </para>
/// </remarks>
public class SharedVolumeLatencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Latency = "datastore.totalReadLatency.average";
    private const string Reads = "datastore.numberReadAveraged.average";

    private const string Volume = "vc-1:ds-prod";

    private static Observation From(
        string host,
        double ms,
        string datastore = Volume,
        string counter = Latency,
        string unit = "millisecond",
        RollupType rollup = RollupType.Average,
        bool vantage = true,
        bool fault = false) => new()
        {
            Entity = new EntityId(datastore),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = ms,
                Rollup = rollup,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = host,
                InstanceIsVantagePoint = vantage,
                IsFaultCount = fault,
            },
        };

    /// <summary>The volume's demand, as one host reports it.</summary>
    private static Observation Load(double operations, string datastore = Volume) =>
        From("esx01", operations, datastore, Reads, unit: "number");

    /// <summary>Every host that mounts the volume, all seeing the same thing.</summary>
    private static IEnumerable<Observation> AllHosts(double ms, int count = 5) =>
        Enumerable.Range(1, count).Select(i => From($"esx0{i}", ms));

    /// <summary>A volume elevated everywhere, carrying enough load to mean it.</summary>
    private static List<Observation> Unanimous(double ms = 8, int hosts = 5) =>
        [.. AllHosts(ms, hosts), Load(100)];

    /// <summary>
    /// Four other volumes carrying an ordinary load, so the estate has a
    /// median to be measured against rather than only the volume under test.
    /// </summary>
    private static IEnumerable<Observation> Neighbours(double operations = 50) =>
        Enumerable.Range(1, 4).Select(i => Load(operations, $"vc-1:ds-other{i}"));

    [Fact]
    public void A_volume_slow_from_every_host_that_mounts_it_names_the_array()
    {
        // The sentence the product could not say. Ten hosts with ten different
        // adapters, ten different cables and ten different switch ports agree
        // about how long this volume takes, and the only thing they share is
        // what is underneath it.
        var alert = Assert.Single(SharedVolumeLatency.Evaluate(Unanimous()));

        Assert.Equal(new EntityId(Volume), alert.Entity);
        Assert.Equal("Slow from every host", alert.Title);
        Assert.Contains("not one host's path", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_with_one_host_standing_out_is_left_to_the_peer_rule()
    {
        // The overlap case, and the reason this test runs both rules. Every
        // host is over the floor, so the unanimity gate is satisfied and this
        // rule would happily fire -- but one host is four times its peers,
        // which is the other rule's finding and a different fix. Exactly one
        // of the two may speak, and if that ever stops being true an operator
        // is told to check a cable and an array over one fault.
        List<Observation> readings =
        [
            From("esx01", 5), From("esx02", 5), From("esx03", 5), From("esx04", 5),
            From("esx05", 60), Load(100),
        ];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));
        Assert.Single(PeerOutliers.Evaluate(readings));
    }

    [Fact]
    public void The_case_the_peer_rule_stays_quiet_about_is_exactly_the_case_this_one_answers()
    {
        // The complement, proved from the other side. PeerOutliers is silent
        // here by design; before this rule existed the product was silent
        // altogether, which is the gap that made the estate's storage chain a
        // diagnostic ladder with a missing rung.
        var readings = Unanimous();

        Assert.Empty(PeerOutliers.Evaluate(readings));
        Assert.Single(SharedVolumeLatency.Evaluate(readings));
    }

    [Fact]
    public void One_quiet_host_is_enough_to_clear_the_array()
    {
        // The case for the defence, and it only takes one witness. A host
        // getting its I/O served promptly is direct evidence that the volume
        // can serve I/O promptly, so whatever is wrong with the other four is
        // not underneath all five. This is the gate that makes the title
        // honest rather than merely loud.
        List<Observation> readings =
        [
            From("esx01", 0), From("esx02", 20), From("esx03", 20), From("esx04", 20),
            From("esx05", 20), Load(100),
        ];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));

        Assert.Single(SharedVolumeLatency.Evaluate(
            readings, SharedVolumePolicy.Default with { MinimumElevatedShare = 0.8d }));
    }

    [Fact]
    public void A_volume_mounted_by_one_host_cannot_accuse_the_array()
    {
        // "Slow from every host that mounts it" is a true sentence about one
        // host and it implies something false. With a single vantage point the
        // reading is equally well explained by that host's cable, and this
        // rule would be borrowing the authority of a consensus that does not
        // exist.
        Assert.Empty(SharedVolumeLatency.Evaluate([From("esx01", 40), Load(100)]));
    }

    [Fact]
    public void Two_hosts_agreeing_is_the_smallest_agreement_there_is_and_is_not_enough()
    {
        // Mirrors PeerOutliers refusing to judge two vantage points. Two hosts
        // can share a switch, a fabric segment or a firmware level; calling
        // their agreement an estate-wide consensus is the same coin toss in
        // the opposite direction.
        List<Observation> readings = [From("esx01", 20), From("esx02", 20), Load(100)];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));

        Assert.Single(SharedVolumeLatency.Evaluate(
            readings, SharedVolumePolicy.Default with { MinimumMountingHosts = 2 }));
    }

    [Fact]
    public void An_idle_volumes_latency_average_is_the_fate_of_a_few_requests()
    {
        // Slow versus busy, at the bottom end. Five operations a second over a
        // twenty-second interval is a hundred requests, and an average over a
        // hundred requests is dominated by whichever one was unlucky. It looks
        // identical to a volume in trouble and there is nothing in the number
        // that tells them apart, so nothing is said.
        List<Observation> readings = [.. AllHosts(20), Load(5)];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));

        Assert.Single(SharedVolumeLatency.Evaluate(
            readings, SharedVolumePolicy.Default with { MinimumOperationsPerSecond = 0d }));
    }

    [Fact]
    public void A_volume_carrying_several_times_the_estates_load_is_not_accused_of_being_slow()
    {
        // The backup window. High latency at high load may be a volume doing
        // its job, and this product would rather withhold a true-looking alert
        // than deliver one that sends the storage team after a scheduled job.
        // The comparison is against what the estate's other volumes are
        // carrying right now, because no vendor publishes an IOPS line and any
        // absolute number would be invented.
        List<Observation> readings =
        [
            .. AllHosts(20), Load(5000),
            Load(50, "vc-1:ds-a"), Load(50, "vc-1:ds-b"),
            Load(50, "vc-1:ds-c"), Load(50, "vc-1:ds-d"),
        ];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));

        Assert.Single(SharedVolumeLatency.Evaluate(
            readings, SharedVolumePolicy.Default with { BusyMultipleOfEstateMedian = 1000d }));
    }

    [Fact]
    public void An_estate_doing_nothing_cannot_elect_one_volume_busy()
    {
        // The busy line is a multiple of the estate median, and on a quiet
        // estate that median is zero. Without a floor under it every volume
        // with any load at all would be ruled busy and the rule would go
        // permanently silent -- the failure mode the counter map warns about,
        // where a ratio against zero decides everything.
        Assert.Single(SharedVolumeLatency.Evaluate(
        [
            .. AllHosts(20), Load(30),
            Load(0, "vc-1:ds-a"), Load(0, "vc-1:ds-b"), Load(0, "vc-1:ds-c"),
        ]));
    }

    [Fact]
    public void A_volume_whose_load_could_not_be_read_is_reported_with_the_load_unknown()
    {
        // The ladder's third answer. The rule does not depend on a demand
        // counter existing -- a collector that has none must still get the
        // latency finding -- but printing an unmeasured thing as a measured
        // one is the exact mistake the counter map was written about.
        var alert = Assert.Single(SharedVolumeLatency.Evaluate([.. AllHosts(20)]));

        Assert.Contains("could not be read", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Latency_below_the_floor_is_not_slow_however_unanimous_the_hosts_are()
    {
        // The estate's defining hazard. Sub-millisecond service times truncate
        // to whole milliseconds, so on all-flash every host agrees at zero or
        // one all day long -- perfect unanimity about a volume that is fine.
        // Without the floor this rule would fire on every shared volume in the
        // estate, forever.
        List<Observation> readings = [.. AllHosts(1), Load(100)];

        Assert.Empty(SharedVolumeLatency.Evaluate(readings));

        Assert.Single(SharedVolumeLatency.Evaluate(
            readings,
            SharedVolumePolicy.Default with
            {
                Peers = PeerOutlierPolicy.Default with { MinimumMilliseconds = 0d },
            }));
    }

    [Fact]
    public void The_two_halves_of_the_rung_move_together_when_the_floor_is_raised()
    {
        // The floor is held once and read by both rules on purpose. An estate
        // on spinning disk lives above five milliseconds all day; raising the
        // floor for one rule and not the other would open a band where both
        // fire, or one where neither does, and it would open it silently.
        var raised = PeerOutlierPolicy.Default with { MinimumMilliseconds = 50d };

        Assert.Empty(SharedVolumeLatency.Evaluate(
            Unanimous(20), SharedVolumePolicy.Default with { Peers = raised }));

        Assert.Empty(PeerOutliers.Evaluate(
            [.. AllHosts(0), From("esx09", 20)], raised));
    }

    [Fact]
    public void Throughput_is_left_alone_however_unanimous_it_is()
    {
        // Demand is read to qualify the latency and is never judged as
        // latency. Every host asking a busy volume for a lot of I/O is not a
        // fault, and a rule that could not tell service time from demand would
        // report the estate's most useful volume as its sickest.
        Assert.Empty(SharedVolumeLatency.Evaluate(
        [
            .. Enumerable.Range(1, 5).Select(i =>
                From($"esx0{i}", 3762, counter: Reads, unit: "number")),
        ]));
    }

    [Fact]
    public void A_counter_whose_instance_is_a_device_is_not_treated_as_a_host()
    {
        // A host's own LUNs are different things, not several views of one
        // thing. Reading them as vantage points would announce that every LUN
        // on every host agrees, which is a consensus about nothing.
        Assert.Empty(SharedVolumeLatency.Evaluate(
            [.. Enumerable.Range(1, 5).Select(i => From($"naa.{i}", 20, vantage: false))]));
    }

    [Fact]
    public void A_fault_counter_is_not_mistaken_for_demand()
    {
        // Bus resets are counted in the same unit as operations a second and
        // mean something entirely different. Counting them as load would let a
        // burst of resets push a volume over the busy line and silence the
        // very alert the resets are evidence for -- the counter doing the
        // silencing would be the counter proving the fault. The rollup usually
        // separates them, but the collector's own declaration is the thing
        // that must be trusted: a counter it calls a fault is never a rate,
        // whatever shape it arrives in.
        List<Observation> readings =
        [
            .. Unanimous(), .. Neighbours(),
            From("esx01", 900_000, counter: "datastore.busResets.summation",
                unit: "number", fault: true),
        ];

        Assert.Single(SharedVolumeLatency.Evaluate(readings));
    }

    [Fact]
    public void A_summed_counter_is_not_mistaken_for_a_rate()
    {
        // A summation says "this many, during this interval"; an average of a
        // rate says "this many per second". Reading one as the other is the
        // silent wrong answer CounterValue was built to make impossible, and
        // at a three-hundred-second interval it is wrong by a factor of three
        // hundred -- easily enough to rule a struggling volume busy.
        List<Observation> readings =
        [
            .. Unanimous(), .. Neighbours(),
            From("esx01", 900_000, counter: "datastore.read.summation",
                unit: "number", rollup: RollupType.Summation),
        ];

        Assert.Single(SharedVolumeLatency.Evaluate(readings));
    }

    [Fact]
    public void A_volumes_load_is_what_all_its_hosts_ask_of_it_together()
    {
        // The array does not know which host sent what. Three operations a
        // second from each of five hosts is fifteen reaching the volume, which
        // is enough to mean something; reading any one host's share alone
        // would rule the volume idle and withhold the finding. The same
        // mistake in the other direction under-counts a volume being hammered
        // from every host at once, which is the case most worth seeing.
        Assert.Single(SharedVolumeLatency.Evaluate(
        [
            .. AllHosts(20),
            .. Enumerable.Range(1, 5).Select(i =>
                From($"esx0{i}", 3, counter: Reads, unit: "number")),
        ]));
    }

    [Fact]
    public void Read_and_write_latency_are_judged_separately()
    {
        // They fail for different reasons -- read points at the array or its
        // cache, write at the write path -- and one alert covering both would
        // hide whichever arrived second. Matches PeerOutliers, so the two
        // halves of the rung stay comparable at the same granularity.
        var alerts = SharedVolumeLatency.Evaluate(
        [
            .. AllHosts(20),
            .. AllHosts(20).Select(o => o with
            {
                Value = o.Value with { CounterName = "datastore.totalWriteLatency.average" },
            }),
            Load(100),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Two_volumes_are_judged_separately()
    {
        // Each shared resource has its own set of hosts and its own load.
        // Mixing them would weigh one volume's consensus with another's.
        var alerts = SharedVolumeLatency.Evaluate(
        [
            .. Unanimous(),
            .. AllHosts(0).Select(o => o with { Entity = new EntityId("vc-1:ds-test") }),
            Load(100, "vc-1:ds-test"),
        ]);

        Assert.Equal(new EntityId(Volume), Assert.Single(alerts).Entity);
    }

    [Fact]
    public void The_fingerprint_does_not_follow_whichever_host_happens_to_be_highest()
    {
        // The finding is that the hosts agree, so naming one of them would
        // invent a protagonist the alert is specifically denying -- and the
        // highest of a tight cluster changes every cycle, which would raise a
        // new alert each time and lose the history of a fault running for
        // hours.
        var first = Assert.Single(SharedVolumeLatency.Evaluate(
            [From("esx01", 22), From("esx02", 20), From("esx03", 20), Load(100)]));

        var second = Assert.Single(SharedVolumeLatency.Evaluate(
            [From("esx01", 20), From("esx02", 21), From("esx03", 20), Load(100)]));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void The_two_rules_never_share_a_fingerprint_for_one_volume()
    {
        // Belt and braces on the exclusion. Even if a future change let both
        // fire, they must not collide into one alert whose description
        // depended on collector ordering -- the reconciler keeps the worse
        // severity and would silently discard one diagnosis.
        var mine = Assert.Single(SharedVolumeLatency.Evaluate(Unanimous()));
        var theirs = Assert.Single(PeerOutliers.Evaluate([.. AllHosts(0), From("esx09", 40)]));

        Assert.NotEqual(mine.Fingerprint, theirs.Fingerprint);
    }

    [Fact]
    public void The_description_reports_the_spread_and_the_load_that_justify_it()
    {
        // The numbers that make the alert checkable. An operator asked to
        // believe "the array is slow" with nothing to verify will believe it
        // once, and after the first false positive will never believe it
        // again.
        var alert = Assert.Single(SharedVolumeLatency.Evaluate(
            [From("esx01", 22), From("esx02", 20), From("esx03", 18), Load(100)]));

        Assert.Contains("median of 20 ms", alert.Description, StringComparison.Ordinal);
        Assert.Contains("from 18 to 22 ms", alert.Description, StringComparison.Ordinal);
        Assert.Contains("3 host(s)", alert.Description, StringComparison.Ordinal);
        Assert.Contains("100 operations a second", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(SharedVolumeLatency.Evaluate([]));
        Assert.Empty(SharedVolumeLatency.Judge([], null, T0));
    }

    // --- three values (ADR-0026) --------------------------------------------

    private static SubjectVerdict JudgeOne(IReadOnlyList<Observation> observations, SharedVolumePolicy? policy = null) =>
        Assert.Single(SharedVolumeLatency.Judge(observations, policy, T0));

    [Fact]
    public void A_volume_slow_everywhere_is_present()
    {
        var present = Assert.IsType<ConditionPresent>(JudgeOne(Unanimous()));

        Assert.Equal(new EntityId(Volume), present.Entity);
        Assert.Equal(T0, present.EvidenceAtUtc);
    }

    [Fact]
    public void One_host_standing_out_is_absent_rather_than_silent()
    {
        // Left to PeerOutliers: a genuine absence for this rule, not "we could
        // not tell".
        var absent = Assert.IsType<ConditionAbsent>(JudgeOne(
            [From("esx01", 5), From("esx02", 5), From("esx03", 5), From("esx04", 5), From("esx05", 60), Load(100)]));

        Assert.Equal(T0, absent.EvidenceAtUtc);
    }

    [Fact]
    public void Fewer_than_three_mounting_hosts_is_not_judgeable()
    {
        var unknown = Assert.IsType<Unknown>(JudgeOne([From("esx01", 40), Load(100)]));

        Assert.Equal(UnknownReason.NotJudgeable, unknown.Reason);
        Assert.Contains("1 host(s)", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_idle_volume_is_not_judgeable_rather_than_absent()
    {
        var unknown = Assert.IsType<Unknown>(JudgeOne([.. AllHosts(20), Load(5)]));

        Assert.Equal(UnknownReason.NotJudgeable, unknown.Reason);
        Assert.Contains("below the", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_busy_volume_is_not_judgeable_rather_than_absent()
    {
        List<Observation> readings =
        [
            .. AllHosts(20), Load(5000),
            Load(50, "vc-1:ds-a"), Load(50, "vc-1:ds-b"),
            Load(50, "vc-1:ds-c"), Load(50, "vc-1:ds-d"),
        ];

        var unknown = Assert.IsType<Unknown>(JudgeOne(readings));

        Assert.Equal(UnknownReason.NotJudgeable, unknown.Reason);
        Assert.Contains(StorageNoisyNeighbour.RuleId, unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quiet_run_keeps_the_alert_open_across_the_rules_n_and_an_unknown_resets_the_count()
    {
        var rule = new SharedVolumeLatencyRule();
        List<Observation> raised = Unanimous();
        List<Observation> absent =
        [From("esx01", 5), From("esx02", 5), From("esx03", 5), From("esx04", 5), From("esx05", 60), Load(100)];
        List<Observation> unknown = [From("esx01", 40), Load(100)];

        IReadOnlyList<AlertInstance> stored = [];
        List<List<Observation>> cycles = [raised, raised, absent, unknown, absent, absent];

        for (var minute = 0; minute < cycles.Count; minute++)
        {
            stored = Reconcile(rule, cycles[minute], stored, T0.AddMinutes(minute));
        }

        var alert = Assert.Single(stored);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
        Assert.Equal(2, alert.ConsecutiveAbsent);

        stored = Reconcile(rule, absent, stored, T0.AddMinutes(cycles.Count));
        Assert.DoesNotContain(stored, i => i.State == AlertLifecycleState.Open);
    }

    private static IReadOnlyList<AlertInstance> Reconcile<TRule>(
        TRule rule, IReadOnlyList<Observation> observations, IReadOnlyList<AlertInstance> stored, DateTimeOffset nowUtc)
        where TRule : IAnalysisRule
    {
        var context = new RuleContext
        {
            Observations = observations,
            ReadGraph = () => EntityGraph.Empty,
            NowUtc = nowUtc,
            Options = MonitoringOptions.Default,
            Series = new NoSeries(),
            Events = new NoEvents(),
        };

        return AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = "observation",
            Stored = stored,
            NowUtc = nowUtc,
            Evaluations = [new RuleEvaluation(rule.RuleId, rule.Resolution, rule.Evaluate(context))],
            Sources = new EvidenceSources { Reporting = ["vc-1"], OwnerOf = _ => "vc-1" },
            RawRetention = TimeSpan.FromDays(2),
        }).Instances;
    }

    private sealed class NoSeries : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query) => new() { Key = query.Key, Resolution = SeriesResolution.Raw };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }

    private sealed class NoEvents : EnterpriseObservatory.Application.Collection.IEventReader
    {
        public IReadOnlyList<EnterpriseObservatory.Application.Collection.SourceEvent> OfTypes(
            IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }
}
