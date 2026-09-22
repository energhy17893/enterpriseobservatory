using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rung of the ladder the storage chain was built for.
/// </summary>
/// <remarks>
/// A volume slow from every host is the array or the fabric; the same volume
/// slow from one host is that host's path. Telling those apart is the product's
/// signature, and getting it wrong in the confident direction — naming a host
/// when the array is simply busy — is worse than staying quiet.
/// </remarks>
public class PeerOutliersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Latency = "datastore.totalReadLatency.average";

    private static Observation From(
        string host,
        double ms,
        string datastore = "vc-1:ds-prod",
        string counter = Latency,
        string unit = "millisecond",
        bool vantage = true) => new()
        {
            Entity = new EntityId(datastore),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = ms,
                Rollup = RollupType.Average,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = host,
                InstanceIsVantagePoint = vantage,
            },
        };

    /// <summary>Nine healthy hosts, so the tenth can be the interesting one.</summary>
    private static IEnumerable<Observation> Healthy(double ms = 0) =>
        Enumerable.Range(1, 9).Select(i => From($"esx0{i}", ms));

    [Fact]
    public void One_host_far_above_its_peers_is_named()
    {
        var alerts = PeerOutliers.Evaluate([.. Healthy(), From("esx10", 12)]);

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId("vc-1:ds-prod"), alert.Entity);
        Assert.Contains("esx10", alert.Description, StringComparison.Ordinal);
        Assert.Contains("not the array", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_host_being_slow_together_is_not_one_host_being_slow()
    {
        // The array under load, or the fabric. Naming a host here would send
        // somebody to check a cable while the real cause is shared -- the
        // single most expensive kind of wrong answer this rule can give.
        Assert.Empty(PeerOutliers.Evaluate([.. Healthy(11), From("esx10", 12)]));
    }

    [Fact]
    public void A_small_reading_is_not_an_outlier_however_large_the_multiple()
    {
        // Peers at zero make every reading an infinite multiple. On all-flash
        // anything under a millisecond truncates to zero, so this is the
        // normal state of the estate rather than an edge case.
        Assert.Empty(PeerOutliers.Evaluate([.. Healthy(0), From("esx10", 2)]));
    }

    [Fact]
    public void Two_vantage_points_cannot_say_which_of_them_is_wrong()
    {
        // A disagreement with no majority. Calling the higher one faulty is a
        // coin toss dressed as a diagnosis.
        Assert.Empty(PeerOutliers.Evaluate([From("esx01", 0), From("esx02", 40)]));
    }

    [Fact]
    public void Throughput_is_left_alone_even_when_one_host_dwarfs_the_rest()
    {
        // A host running more virtual machines does more I/O. That is not a
        // fault, and a rule that did not distinguish service time from demand
        // would report the busiest host in the estate as broken.
        Assert.Empty(PeerOutliers.Evaluate(
        [
            .. Enumerable.Range(1, 9).Select(i =>
                From($"esx0{i}", 100, counter: "datastore.numberReadAveraged.average", unit: "number")),
            From("esx10", 3762, counter: "datastore.numberReadAveraged.average", unit: "number"),
        ]));
    }

    [Fact]
    public void A_counter_whose_instance_is_a_device_is_not_compared_with_its_peers()
    {
        // A host's own LUNs are not vantage points onto one shared thing --
        // they are different things. Comparing them would report the busiest
        // LUN on every host in the estate.
        Assert.Empty(PeerOutliers.Evaluate(
        [
            .. Enumerable.Range(1, 9).Select(i => From($"naa.{i}", 0, vantage: false)),
            From("naa.10", 40, vantage: false),
        ]));
    }

    [Fact]
    public void Two_volumes_are_judged_separately()
    {
        // Each shared resource has its own set of vantage points, and mixing
        // them would compare one volume's hosts against another's.
        var alerts = PeerOutliers.Evaluate(
        [
            .. Healthy(), From("esx10", 12),
            .. Healthy(0).Select(o => o with
            {
                Entity = new EntityId("vc-1:ds-test"),
            }),
        ]);

        Assert.Equal(new EntityId("vc-1:ds-prod"), Assert.Single(alerts).Entity);
    }

    [Fact]
    public void The_fingerprint_stays_put_when_a_different_host_becomes_worst()
    {
        // Which host is worst can change between cycles while the volume stays
        // the sick one. A fingerprint that followed the host would raise a new
        // alert each time and lose the history of a fault running for hours.
        var first = Assert.Single(PeerOutliers.Evaluate([.. Healthy(), From("esx10", 12)]));
        var second = Assert.Single(PeerOutliers.Evaluate([.. Healthy(), From("esx02", 30)]));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Read_and_write_latency_are_judged_separately()
    {
        // They fail for different reasons -- read points at the array or its
        // cache, write at the write path -- and one alert covering both would
        // hide whichever arrived second.
        var alerts = PeerOutliers.Evaluate(
        [
            .. Healthy(), From("esx10", 12),
            .. Healthy(0).Select(o => o with
            {
                Value = o.Value with { CounterName = "datastore.totalWriteLatency.average" },
            }),
            From("esx10", 20, counter: "datastore.totalWriteLatency.average"),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Two_hosts_slow_together_is_not_one_host_slow()
    {
        // Written expecting an alert and corrected by the rule. Three hosts at
        // 0, 20 and 20: the worst is 20, its peers are 20 and 0, their median
        // is 10, and 20 is not four times 10. So nothing fires -- which is
        // right. Two of three hosts seeing the same latency is a shared cause,
        // and the difference between a shared cause and one bad path is the
        // entire question this rule exists to answer.
        Assert.Empty(PeerOutliers.Evaluate([From("a", 0), From("b", 20), From("c", 20)]));
    }

    [Fact]
    public void The_description_reports_what_the_peers_were_seeing()
    {
        // The number that makes the alert checkable. Without it the operator
        // is asked to believe "this host is unusual" with no way to judge how
        // unusual, and the first false positive teaches them to ignore it.
        var alert = Assert.Single(PeerOutliers.Evaluate([.. Healthy(1), From("esx10", 12)]));

        Assert.Contains("12 ms from 'esx10'", alert.Description, StringComparison.Ordinal);
        Assert.Contains("median of 1 ms", alert.Description, StringComparison.Ordinal);
        Assert.Contains("9 host(s)", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_absolute_floor_is_what_keeps_a_quiet_estate_quiet()
    {
        // Both gates pinned by policy rather than by a one-off mutation, so
        // the tests keep saying which one does what -- and this one had to be
        // narrowed twice, because at 2ms the ratio was doing the stopping and
        // the test proved nothing about the floor. Four milliseconds against
        // peers at zero clears the ratio exactly (4 is four times the floored
        // median of 1) and is stopped by the floor alone.
        List<Observation> readings = [.. Healthy(0), From("esx10", 4)];

        Assert.Empty(PeerOutliers.Evaluate(readings));

        Assert.Single(PeerOutliers.Evaluate(
            readings, PeerOutlierPolicy.Default with { MinimumMilliseconds = 0d }));
    }

    [Fact]
    public void The_peer_ratio_is_what_keeps_a_busy_estate_quiet()
    {
        // Everyone over the floor together: the array under load. It clears
        // the floor comfortably and is stopped only by the ratio, which is the
        // gate that separates a shared cause from one bad path.
        List<Observation> readings = [.. Healthy(11), From("esx10", 12)];

        Assert.Empty(PeerOutliers.Evaluate(readings));

        Assert.Single(PeerOutliers.Evaluate(
            readings, PeerOutlierPolicy.Default with { Multiple = 0d }));
    }

    [Fact]
    public void A_stricter_policy_is_obeyed()
    {
        // The numbers are defaults, not laws: an estate on spinning disk needs
        // a higher floor than one on flash.
        var strict = PeerOutlierPolicy.Default with { MinimumMilliseconds = 50d };

        Assert.Empty(PeerOutliers.Evaluate([.. Healthy(), From("esx10", 12)], strict));
    }

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(PeerOutliers.Evaluate([]));
        Assert.Empty(PeerOutliers.Judge([], null, T0));
    }

    // --- three values (ADR-0026) --------------------------------------------

    private static SubjectVerdict JudgeOne(IReadOnlyList<Observation> observations, PeerOutlierPolicy? policy = null) =>
        Assert.Single(PeerOutliers.Judge(observations, policy, T0));

    [Fact]
    public void An_outlier_is_present()
    {
        var present = Assert.IsType<ConditionPresent>(JudgeOne([.. Healthy(), From("esx10", 12)]));

        Assert.Equal(present.Covers[0], Assert.Single(present.Alerts).Fingerprint);
        Assert.Equal(new EntityId("vc-1:ds-prod"), present.Entity);
        Assert.Equal(T0, present.EvidenceAtUtc);
    }

    [Fact]
    public void A_healthy_volume_is_absent()
    {
        var absent = Assert.IsType<ConditionAbsent>(JudgeOne([.. Healthy(0), From("esx10", 2)]));

        Assert.Equal(T0, absent.EvidenceAtUtc);
    }

    [Fact]
    public void Fewer_than_three_vantage_points_is_not_judgeable()
    {
        var unknown = Assert.IsType<Unknown>(JudgeOne([From("esx01", 0), From("esx02", 40)]));

        Assert.Equal(UnknownReason.NotJudgeable, unknown.Reason);
        Assert.Contains("2 vantage point", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quiet_run_keeps_the_alert_open_across_the_rules_n_and_an_unknown_resets_the_count()
    {
        // Raise, confirm, then: one fresh absence (count 1), an unknown cycle
        // (must not count and must not resolve), then fresh absences again.
        // With N = 3 the alert must still be open after the unknown resets the
        // count back to zero.
        var rule = new PeerOutliersRule();
        List<Observation> raised = [.. Healthy(), From("esx10", 12)];
        List<Observation> absent = [.. Healthy(0), From("esx10", 2)];
        List<Observation> unknown = [From("esx01", 0), From("esx02", 40)];

        IReadOnlyList<AlertInstance> stored = [];

        List<Observation>[] cycles =
        [
            raised, raised, // Pending -> Active (Warning confirms on the 2nd hit)
            absent,         // 1/3
            unknown,        // resets to 0/3, must not resolve
            absent, absent, // 1/3, 2/3
        ];

        for (var minute = 0; minute < cycles.Length; minute++)
        {
            stored = Reconcile(rule, cycles[minute], stored, T0.AddMinutes(minute));
        }

        var alert = Assert.Single(stored);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
        Assert.Equal(2, alert.ConsecutiveAbsent);

        // One more fresh absence reaches N = 3 and resolves it.
        stored = Reconcile(rule, absent, stored, T0.AddMinutes(cycles.Length));
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
