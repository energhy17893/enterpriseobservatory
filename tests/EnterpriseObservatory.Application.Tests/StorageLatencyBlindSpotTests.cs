using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rule that judges the product rather than the estate.
/// </summary>
/// <remarks>
/// <para>
/// Counter map §5b calls a zero worse than silence, because a zero looks like a
/// measurement. Every other rule here can be wrong about a volume; this one can
/// be wrong about whether the product is capable of seeing at all, and the
/// expensive direction is the confident one — telling an operator their
/// measurement is broken when it is working sends them to change a setting on a
/// healthy datastore and teaches them the alert is noise.
/// </para>
/// <para>
/// So most of what follows is the silence. Each negative test names the
/// situation it protects and what would reach an operator if the gate were
/// removed.
/// </para>
/// </remarks>
public class StorageLatencyBlindSpotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Read = "datastore.totalReadLatency.average";
    private const string Write = "datastore.totalWriteLatency.average";
    private const string Iops = "datastore.numberReadAveraged.average";
    private const string Sioc = "datastore.siocActiveTimePercentage.average";

    private const string Volume = "vc-1:ds-prod";

    private static Observation From(
        string host,
        double raw,
        string counter = Read,
        string unit = "millisecond",
        string datastore = Volume,
        bool vantage = true,
        RollupType rollup = RollupType.Average,
        bool fault = false) => new()
        {
            Entity = new EntityId(datastore),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = raw,
                Rollup = rollup,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = host,
                InstanceIsVantagePoint = vantage,
                IsFaultCount = fault,
            },
        };

    /// <summary>
    /// The estate as the probe found it: three hosts, latency truncated to zero,
    /// real I/O, SIOC never running.
    /// </summary>
    private static List<Observation> Blind(string datastore = Volume) =>
    [
        From("esx01", 0, datastore: datastore),
        From("esx02", 0, datastore: datastore),
        From("esx03", 0, datastore: datastore),
        From("esx01", 0, counter: Write, datastore: datastore),
        From("esx01", 1638, counter: Iops, unit: "number", datastore: datastore),
        From("esx01", 0, counter: Sioc, unit: "percent", datastore: datastore),
    ];

    [Fact]
    public void A_volume_carrying_load_whose_latency_reads_zero_is_a_blind_spot()
    {
        // The measured case, verbatim: 1638 operations a second and every
        // latency counter at zero. If this does not fire, the product ships
        // three storage rules that cannot speak on the estate it was written
        // against and says nothing about why.
        var alert = Assert.Single(StorageLatencyBlindSpot.Evaluate(Blind()));

        Assert.Equal(new EntityId(Volume), alert.Entity);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void The_alert_never_says_the_volume_is_slow()
    {
        // The product does not know that and must not imply it. This volume may
        // be the fastest in the estate; the finding is about the measurement.
        // An operator who reads this as a performance alert goes and opens a
        // ticket with the storage team over a healthy LUN.
        var alert = Assert.Single(StorageLatencyBlindSpot.Evaluate(Blind()));

        Assert.Contains("does not know", alert.Description, StringComparison.Ordinal);
        Assert.Contains("not a fast volume", alert.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("is slow", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alert_names_the_fix_and_what_it_buys()
    {
        // An alert that names no action is noise, and this one has a real fix.
        // Without the second half the operator has no reason to act: "we cannot
        // measure" is a shrug until it says which three rules go dark.
        var alert = Assert.Single(StorageLatencyBlindSpot.Evaluate(Blind()));

        Assert.Contains("Storage I/O Control", alert.Description, StringComparison.Ordinal);
        Assert.Contains("unable to fire", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_attributed_to_the_platform_rather_than_to_the_collector()
    {
        // No collector reported this; the product concluded it from an absence.
        // GuardedRule and CpuContention make the same choice, and the source is
        // part of the fingerprint, so getting it wrong once fixes it forever in
        // the wrong place.
        var alert = Assert.Single(StorageLatencyBlindSpot.Evaluate(Blind()));

        Assert.Equal("platform", alert.Source);
        Assert.Equal(GuardedRule.Category, alert.Category);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void An_idle_volume_reading_zero_is_an_honest_zero()
    {
        // The whole distinction the rule exists to make. Zero latency at zero
        // IOPS is the truth, not a truncation, and firing here would raise an
        // alert on every unused datastore in the estate -- the single cheapest
        // way to make this rule ignored.
        List<Observation> idle =
        [
            .. Blind().Where(o => o.Value.CounterName != Iops),
            From("esx01", 0, counter: Iops, unit: "number"),
        ];

        Assert.Empty(StorageLatencyBlindSpot.Evaluate(idle));

        // Neutralised, the same readings fire -- so the load gate, and not some
        // other gate, is what stopped them.
        Assert.Single(StorageLatencyBlindSpot.Evaluate(
            idle, StorageLatencyBlindSpotPolicy.Default with
            {
                MinimumOperationsPerSecond = -1d,
            }));
    }

    [Fact]
    public void A_volume_whose_latency_is_not_zero_is_being_measured_successfully()
    {
        // Nothing to say: the measurement works. This is also what keeps this
        // rule and the three storage rules from ever describing one situation
        // -- where there is a latency to judge, PeerOutliers and
        // SharedVolumeLatency judge it and this one is silent by construction.
        List<Observation> measured =
        [
            .. Blind().Where(o => o.Value.CounterName != Read),
            From("esx01", 0),
            From("esx02", 0),
            From("esx03", 7),
        ];

        Assert.Empty(StorageLatencyBlindSpot.Evaluate(measured));

        // Neutralised by declaring the platform incapable of reporting below
        // 1000 ms, which makes 7 ms a truncated zero. Non-zero on purpose: a
        // zero here would be no policy at all.
        Assert.Single(StorageLatencyBlindSpot.Evaluate(
            measured, StorageLatencyBlindSpotPolicy.Default with
            {
                MinimumMeasurableMilliseconds = 1000d,
            }));
    }

    [Fact]
    public void One_host_seeing_a_latency_clears_the_whole_volume()
    {
        // A volume is either measurable or it is not, and one host that can see
        // it proves the counters work. Judging per host would raise an alert
        // naming a datastore for a fault that is really one host's, which is
        // the mistake PeerOutliers exists to avoid in the other direction.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
        [
            .. Blind().Where(o => o.Value.CounterName != Read),
            From("esx01", 0),
            From("esx02", 0),
            From("esx03", 2),
        ]));
    }

    [Fact]
    public void Sioc_already_running_means_the_product_has_nothing_to_offer()
    {
        // The measurement channel exists, so the zeros are a different story
        // and the fix this alert names has already been applied. Firing here
        // tells an operator to enable something that is enabled.
        List<Observation> active =
        [
            .. Blind().Where(o => o.Value.CounterName != Sioc),
            From("esx01", 14, counter: Sioc, unit: "percent"),
        ];

        Assert.Empty(StorageLatencyBlindSpot.Evaluate(active));

        Assert.Single(StorageLatencyBlindSpot.Evaluate(
            active, StorageLatencyBlindSpotPolicy.Default with
            {
                MinimumActiveSiocPercentage = 1000d,
            }));
    }

    [Fact]
    public void One_host_seeing_sioc_active_clears_the_volume_for_all_of_them()
    {
        // SIOC is a property of the datastore, not of a host's view of it.
        // Reading the mean across hosts would let one throttling host be
        // averaged away by quiet ones and invent a blind spot on a volume that
        // has none.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
        [
            .. Blind(),
            From("esx02", 30, counter: Sioc, unit: "percent"),
        ]));
    }

    [Fact]
    public void A_single_reading_is_not_enough_to_call_the_measurement_broken()
    {
        // One number is a sample. The product sees one sample per series per
        // cycle and cannot accumulate evidence over time, so breadth is the
        // only currency it has -- the same currency PeerOutliers spends on its
        // three vantage points.
        List<Observation> thin =
        [
            From("esx01", 0),
            From("esx01", 1638, counter: Iops, unit: "number"),
            From("esx01", 0, counter: Sioc, unit: "percent"),
        ];

        Assert.Empty(StorageLatencyBlindSpot.Evaluate(thin));

        Assert.Single(StorageLatencyBlindSpot.Evaluate(
            thin, StorageLatencyBlindSpotPolicy.Default with
            {
                MinimumLatencyReadings = 1,
            }));
    }

    [Fact]
    public void A_volume_with_no_load_counters_at_all_is_not_judged()
    {
        // Not looking is not the same as looking and finding nothing. Without
        // the IOPS counters the product cannot tell a truncated zero from an
        // idle volume, and choosing one is the confident wrong answer this
        // codebase treats as worse than silence. Note this is a different
        // silence from the idle case above, and the two must not be collapsed:
        // that one has an answer, this one has no evidence.
        List<Observation> unread = [.. Blind().Where(o => o.Value.CounterName != Iops)];

        Assert.Empty(StorageLatencyBlindSpot.Evaluate(unread));

        // And still silent with the load floor taken away, which is the part
        // that has to be asserted separately. A rule that folded "no counter"
        // into "zero operations" would look identical here on the default
        // policy and would start firing on every unmeasured volume the moment
        // somebody lowered the floor -- a mutant that survives the test above
        // unless the absence is pinned on its own.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
            unread, StorageLatencyBlindSpotPolicy.Default with
            {
                MinimumOperationsPerSecond = -1d,
            }));
    }

    [Fact]
    public void A_volume_with_no_sioc_counter_collected_is_not_judged()
    {
        // The same argument for the evidence counter. §5b records that SIOC is
        // collected solely as the evidence for this rule; a collector that does
        // not report it leaves the product unable to say why the zeros are
        // there, and an alert that cannot name its cause cannot name its fix.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
            [.. Blind().Where(o => o.Value.CounterName != Sioc)]));
    }

    [Fact]
    public void A_hosts_own_devices_are_not_datastores_and_are_left_alone()
    {
        // The disk.* triple truncates the same way, and SIOC does nothing for
        // it. Firing here would name a problem whose fix does not exist. The
        // shape test -- a duration from a vantage point -- is what keeps this
        // rule on datastores without naming one.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
        [
            From("naa.1", 0, counter: "disk.deviceLatency.average",
                datastore: "vc-1:esx01", vantage: false),
            From("naa.2", 0, counter: "disk.kernelLatency.average",
                datastore: "vc-1:esx01", vantage: false),
            From("naa.3", 0, counter: "disk.queueLatency.average",
                datastore: "vc-1:esx01", vantage: false),
            From("naa.1", 500, counter: "disk.numberReadAveraged.average",
                unit: "number", datastore: "vc-1:esx01", vantage: false),
            From("naa.1", 0, counter: Sioc, unit: "percent",
                datastore: "vc-1:esx01", vantage: false),
        ]));
    }

    [Fact]
    public void A_duration_that_is_not_from_a_vantage_point_is_not_this_rules_business()
    {
        // The previous test is the realistic version and it is stopped by more
        // than one gate at once, so it cannot say which. This one is the same
        // claim isolated: everything else about the volume is exactly the
        // firing case, and only the latency readings are device-scoped rather
        // than observed from a host. If the shape test goes, the rule starts
        // recommending Storage I/O Control for a LUN's device latency, which
        // SIOC does nothing about.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
        [
            From("naa.1", 0, vantage: false),
            From("naa.2", 0, vantage: false),
            From("naa.3", 0, vantage: false),
            From("esx01", 1638, counter: Iops, unit: "number"),
            From("esx01", 0, counter: Sioc, unit: "percent"),
        ]));
    }

    [Fact]
    public void A_fault_count_is_not_load()
    {
        // Bus resets are not I/O. Counting them as demand would let a volume
        // that is doing nothing but failing look busy, and the alert would
        // claim a truncated zero where the honest reading is that nothing
        // succeeded.
        Assert.Empty(StorageLatencyBlindSpot.Evaluate(
        [
            .. Blind().Where(o => o.Value.CounterName != Iops),
            From("esx01", 40, counter: "storagePath.busResets.summation",
                unit: "number", rollup: RollupType.Average, fault: true),
        ]));
    }

    [Fact]
    public void Each_blind_volume_gets_its_own_alert()
    {
        // The answer to "one alert or forty-one". SIOC is enabled per
        // datastore, so the fix is per datastore: a single estate-wide alert
        // would be a progress bar that does not move until the last volume is
        // done, could not be acknowledged for the one volume where SIOC is off
        // on purpose, and would carry no entity for anything downstream to hang
        // it from.
        var alerts = StorageLatencyBlindSpot.Evaluate(
            [.. Blind("vc-1:ds-a"), .. Blind("vc-1:ds-b"), .. Blind("vc-1:ds-c")]);

        Assert.Equal(3, alerts.Count);
        Assert.Equal(3, alerts.Select(a => a.Fingerprint).Distinct().Count());
        Assert.Equal(3, alerts.Select(a => a.Entity).Distinct().Count());
    }

    [Fact]
    public void Every_volume_being_blind_reads_as_an_estate_default_not_a_volume_fault()
    {
        // The vROps lesson, honoured where it belongs: the proportion is a
        // field on the alert rather than a reason to collapse forty-one alerts
        // into one. Forty-one of forty-one is not forty-one misconfigured
        // volumes, it is an estate nobody configured, and an operator reading
        // one alert in isolation would go and fix exactly one volume.
        var alerts = StorageLatencyBlindSpot.Evaluate(
            [.. Blind("vc-1:ds-a"), .. Blind("vc-1:ds-b")]);

        Assert.All(alerts, a =>
            Assert.Contains("Every one of the 2 volumes", a.Description, StringComparison.Ordinal));
        Assert.All(alerts, a =>
            Assert.Contains("as policy", a.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void One_blind_volume_among_healthy_ones_points_at_that_volume()
    {
        // The other half of the same sentence. When the estate is mostly
        // measurable, the alert must not send the operator off to set a policy
        // -- this is one volume's switch, and the count is what says so.
        List<Observation> healthy =
        [
            .. Blind("vc-1:ds-ok").Where(o => o.Value.CounterName != Read),
            From("esx01", 6, datastore: "vc-1:ds-ok"),
            From("esx02", 6, datastore: "vc-1:ds-ok"),
            From("esx03", 6, datastore: "vc-1:ds-ok"),
        ];

        var alert = Assert.Single(
            StorageLatencyBlindSpot.Evaluate([.. Blind(), .. healthy]));

        Assert.Contains("1 of the 2 volume(s)", alert.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("as policy", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_denominator_counts_only_volumes_this_rule_could_consider()
    {
        // Entities with no latency counters at all -- hosts, clusters, virtual
        // machines -- are not volumes that were measured successfully. Counting
        // them would dilute "every volume in the estate" into a number that
        // never reaches the whole and would silence the policy sentence
        // forever. Here a host is present and the denominator stays at one.
        var alert = Assert.Single(StorageLatencyBlindSpot.Evaluate(
        [
            .. Blind(),
            From("esx01", 90, counter: "cpu.usage.average", unit: "percent",
                datastore: "vc-1:esx01", vantage: false),
        ]));

        Assert.Contains("1 of the 1 volume(s)", alert.Description, StringComparison.Ordinal);

        // And one volume of one is still not an estate-wide default. Written
        // expecting the policy sentence and corrected by the rule: "every
        // volume measured is blind" is a vacuous claim when one was measured,
        // and telling an operator to set a policy on that evidence is the
        // confident overreach the rest of this file is about.
        Assert.DoesNotContain("as policy", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fingerprint_does_not_move_when_a_different_host_reports_first()
    {
        // The finding is that every host agrees, so naming one in the identity
        // would invent a protagonist the alert is specifically denying -- and
        // the order observations arrive in is not stable between cycles, so the
        // alert would be raised anew each pass and lose its history.
        var first = Assert.Single(StorageLatencyBlindSpot.Evaluate(Blind()));

        // Reversed, which puts a different counter first. A fingerprint built
        // from the counter name would split one blind volume into one alert per
        // latency counter.
        var reversed = Assert.Single(
            StorageLatencyBlindSpot.Evaluate([.. Enumerable.Reverse(Blind())]));

        // And with a different host's reading first, which reversing does not
        // achieve -- both ends of this fixture are esx01, so the reversal alone
        // lets a fingerprint carrying the host slip through. That mutant
        // survived until this line existed.
        var readings = Blind();
        var moved = StorageLatencyBlindSpot.Evaluate(
            [readings[2], .. readings.Where((_, i) => i != 2)]);

        Assert.Equal(first.Fingerprint, reversed.Fingerprint);
        Assert.Equal(first.Fingerprint, Assert.Single(moved).Fingerprint);
    }

    /// <summary>
    /// A volume the product can read: the same shape, with real numbers on it.
    /// </summary>
    /// <remarks>
    /// SIOC is left inactive deliberately. These volumes are cleared by the
    /// latency gate alone, so an estate built from them exercises the
    /// denominator rather than the SIOC gate.
    /// </remarks>
    private static List<Observation> Measured(string datastore) =>
    [
        From("esx01", 6, datastore: datastore),
        From("esx02", 6, datastore: datastore),
        From("esx03", 6, datastore: datastore),
        From("esx01", 1638, counter: Iops, unit: "number", datastore: datastore),
        From("esx01", 0, counter: Sioc, unit: "percent", datastore: datastore),
    ];

    /// <summary>An estate of a given shape, named so the two halves stay apart.</summary>
    private static List<Observation> Estate(int blind, int measured)
    {
        List<Observation> all = [];

        for (var i = 0; i < blind; i++)
        {
            all.AddRange(Blind($"vc-1:ds-blind-{i:00}"));
        }

        for (var i = 0; i < measured; i++)
        {
            all.AddRange(Measured($"vc-1:ds-ok-{i:00}"));
        }

        return all;
    }

    [Fact]
    public void The_live_estate_of_twenty_five_blind_volumes_in_forty_one_raises_twenty_five()
    {
        // The estate as it actually is, after the rule shipped: 25 of 41
        // datastores have Storage I/O Control inactive, and the inbox went from
        // 9 alerts to 34. This is the case the rule was revised against, and it
        // is pinned here so that the shape it produces is a decision somebody
        // made rather than something nobody looked at.
        //
        // Twenty-five alerts, because SIOC is enabled per datastore and the
        // sixteen volumes that already have it must not be swept into a finding
        // that is not about them. If this ever becomes one alert, the sixteen
        // lose nothing -- but the twenty-five lose their EntityId, and every
        // operator decision recorded against them is destroyed the first time
        // the proportion moves. See the bulk-clear test below, which measures
        // exactly that.
        var alerts = StorageLatencyBlindSpot.Evaluate(Estate(blind: 25, measured: 16));

        Assert.Equal(25, alerts.Count);
        Assert.Equal(25, alerts.Select(a => a.Entity).Distinct().Count());
        Assert.All(alerts, a =>
            Assert.Contains("25 of the 41 volume(s)", a.Description, StringComparison.Ordinal));

        // A majority is not an estate-wide default. Twenty-five of forty-one
        // means sixteen volumes are configured, so "set it once, as policy" is
        // false on its face and would send the operator to change a setting on
        // sixteen volumes that already have it.
        Assert.All(alerts, a =>
            Assert.DoesNotContain("as policy", a.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_configured_volume_is_still_enough_to_deny_an_estate_wide_default()
    {
        // The boundary, and the reason it sits at "every" rather than at a
        // proportion. Forty of forty-one is as lopsided as an estate gets while
        // still containing a counter-example, and the counter-example is the
        // whole claim: one volume with Storage I/O Control running proves the
        // estate has no blanket default, so the sentence that tells an operator
        // to stop and set policy would be wrong.
        //
        // Moving this gate to a proportion -- half, or two thirds -- is the
        // change this test exists to make somebody argue for. It would put the
        // policy sentence on an estate that demonstrably has a policy already.
        var lopsided = StorageLatencyBlindSpot.Evaluate(Estate(blind: 40, measured: 1));

        Assert.Equal(40, lopsided.Count);
        Assert.All(lopsided, a =>
            Assert.Contains("40 of the 41 volume(s)", a.Description, StringComparison.Ordinal));
        Assert.All(lopsided, a =>
            Assert.DoesNotContain("as policy", a.Description, StringComparison.Ordinal));

        // And the one case where the claim is true, beside it, so that the
        // gate is pinned from both sides. Neither of these is a mutation of the
        // other: the difference is one configured volume.
        var complete = StorageLatencyBlindSpot.Evaluate(Estate(blind: 41, measured: 0));

        Assert.Equal(41, complete.Count);
        Assert.All(complete, a =>
            Assert.Contains("as policy", a.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void The_fingerprint_does_not_move_when_the_rest_of_the_estate_does()
    {
        // The property the whole per-datastore shape rests on, and the one
        // nothing pinned until now -- every other fingerprint test here judges
        // a single volume, where the proportion is one of one on every pass, so
        // a fingerprint built from the count would survive all of them.
        //
        // It must not carry the count. An operator's clear is filed against the
        // fingerprint, and the count changes every time anybody enables Storage
        // I/O Control anywhere in the estate. A fingerprint carrying it would
        // discard all twenty-five decisions the moment the twenty-sixth volume
        // was fixed, and raise them again as new alerts.
        var crowded = StorageLatencyBlindSpot.Evaluate(Estate(blind: 25, measured: 16))
            .Single(a => a.Entity == new EntityId("vc-1:ds-blind-00"));

        var sparse = StorageLatencyBlindSpot.Evaluate(Estate(blind: 2, measured: 39))
            .Single(a => a.Entity == new EntityId("vc-1:ds-blind-00"));

        Assert.Equal(crowded.Fingerprint, sparse.Fingerprint);

        // The description does move, and must: the count is the sentence that
        // tells the operator whether they are looking at one volume's switch or
        // at the estate. Identity stable, wording live.
        Assert.NotEqual(crowded.Description, sparse.Description);
    }

    [Fact]
    public void One_bulk_clear_settles_the_estate_and_survives_the_operator_making_progress()
    {
        // "We do not use Storage I/O Control here" is one decision, and this is
        // the measurement of what that decision costs: one bulk clear, and the
        // inbox is empty and stays empty.
        //
        // This is why the rule was left alone. The complaint that twenty-five
        // alerts take twenty-five actions to silence is answered a layer up --
        // AlertOperations.ClearMany, /alerts/clear-many, and the inbox's
        // select-all -- and an operator's clear is sticky, so re-observing the
        // same volumes next cycle does not reopen or re-notify.
        //
        // The second half is the part a proportion-driven rule could not do at
        // all. When the operator then enables SIOC on six volumes, the count
        // moves from 25 to 19 and every surviving alert's wording changes, but
        // the nineteen clears hold and the six fixed volumes retire quietly.
        // Acting on the alert is not punished. Were the shape to flip to a
        // single estate-wide alert at some proportion, crossing that line
        // retires every per-volume fingerprint and the clears go with them --
        // measured at 25 retired, 0 surviving, 25 re-notified on the way back.
        var observed = StorageLatencyBlindSpot.Evaluate(Estate(blind: 25, measured: 16));

        // Two cycles, because a warning confirms on the second hit.
        var first = Reconcile(observed, [], 0);
        var confirmed = Reconcile(observed, first.Instances, 1);

        Assert.Equal(25, confirmed.Visible.Count);

        // The decision, taken once over the whole selection.
        List<AlertInstance> cleared =
            [.. confirmed.Instances.Select(i => AlertLifecycle.Clear(i, "operator", T0.AddMinutes(2)))];

        var settled = Reconcile(observed, cleared, 3);

        Assert.Empty(settled.Visible);
        Assert.Empty(settled.ToNotify);
        Assert.Empty(settled.Retired);
        Assert.Equal(25, settled.Instances.Count(i => i.ClearedByOperator));

        // Now six of them get Storage I/O Control after all. The proportion
        // moves, the wording moves, the identities do not.
        var progressed = StorageLatencyBlindSpot.Evaluate(Estate(blind: 19, measured: 22));

        Assert.Equal(19, progressed.Count);
        Assert.All(progressed, a =>
            Assert.Contains("19 of the 41 volume(s)", a.Description, StringComparison.Ordinal));

        var after = Reconcile(progressed, settled.Instances, 4);

        Assert.Empty(after.Visible);
        Assert.Empty(after.ToNotify);
        Assert.Equal(19, after.Instances.Count(i => i.ClearedByOperator));

        // The six that were fixed are gone rather than lingering as cleared
        // rows: the fault disappeared, which is the one thing that ends a
        // sticky clear.
        Assert.Equal(6, after.Retired.Count);
    }

    private static AlertReconciliationResult Reconcile(
        IReadOnlyList<AlertDefinition> observed,
        IReadOnlyList<AlertInstance> stored,
        int minute) =>
        AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = "observation",
            Observed = observed,
            Stored = stored,
            NowUtc = T0.AddMinutes(minute),
        });

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(StorageLatencyBlindSpot.Evaluate([]));
    }
}
