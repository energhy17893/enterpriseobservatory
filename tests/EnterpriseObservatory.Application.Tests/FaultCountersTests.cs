using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The first rule that judges a measurement rather than a collection failure.
/// </summary>
/// <remarks>
/// Weighted towards what it must <em>not</em> do. Alerting on something real is
/// the easy half; the expensive failures in this product have all been the
/// other kind — a number that looked like knowledge, or a silence that looked
/// like health.
/// </remarks>
public class FaultCountersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static Observation Sample(
        double raw,
        bool isFault = true,
        string instance = "vmhba0:C0:T0:L1",
        string counter = "storagePath.busResets.summation",
        string entity = "vc-1:host-1") => new()
        {
            Entity = new EntityId(entity),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = raw,
                Rollup = RollupType.Summation,
                Interval = TimeSpan.FromSeconds(20),
                Unit = "number",
                Instance = instance,
                IsFaultCount = isFault,
            },
        };

    [Fact]
    public void A_fault_counter_above_zero_raises_an_alert()
    {
        var alert = Assert.Single(FaultCounters.Evaluate([Sample(1)]));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
        Assert.Contains("vmhba0:C0:T0:L1", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_is_a_real_answer_and_raises_nothing()
    {
        // The whole reason these counters were chosen over the latency pair.
        // A summation of zero means it did not happen; a truncated average of
        // zero means nothing at all. See the counter map section 5b.
        Assert.Empty(FaultCounters.Evaluate([Sample(0)]));
    }

    [Fact]
    public void A_counter_that_is_a_level_rather_than_a_fault_is_left_alone()
    {
        // Latency at 3ms is not a fault, and this rule has no opinion about
        // where a level's line should be drawn. Without the flag it would
        // alert on every non-zero reading of everything.
        Assert.Empty(FaultCounters.Evaluate(
        [
            Sample(3, isFault: false, counter: "disk.deviceLatency.average"),
        ]));
    }

    [Fact]
    public void The_aggregate_across_devices_is_skipped()
    {
        // A per-device counter is stored twice: once per device and once as a
        // computed total. Alerting on both would raise a nameless second alert
        // beside every real one, saying the host has errors without saying
        // where -- which is the thing the per-device series were kept for.
        var alerts = FaultCounters.Evaluate(
        [
            Sample(2, instance: string.Empty),
            Sample(2, instance: "vmhba1:C0:T2:L5"),
        ]);

        Assert.Equal("vmhba1:C0:T2:L5", Assert.Single(alerts).Description.Split('\'')[3]);
    }

    [Fact]
    public void Each_device_gets_its_own_alert()
    {
        // One bad cable is one path out of thirty-two. An alert per host would
        // leave somebody to find which.
        var alerts = FaultCounters.Evaluate(
        [
            Sample(1, instance: "vmhba0:C0:T0:L1"),
            Sample(1, instance: "vmhba1:C0:T0:L1"),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_same_device_on_two_hosts_is_two_alerts()
    {
        // Runtime path names are host-local: vmhba0:C0:T0:L1 exists on every
        // host and means a different cable on each. A fingerprint that ignored
        // the entity would collapse ten hosts' faults into one.
        var alerts = FaultCounters.Evaluate(
        [
            Sample(1, entity: "vc-1:host-1"),
            Sample(1, entity: "vc-1:host-2"),
        ]);

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Two_different_fault_counters_on_one_device_are_two_alerts()
    {
        // A bus reset and an aborted command are different faults with
        // different causes, and collapsing them would hide whichever arrived
        // second.
        var alerts = FaultCounters.Evaluate(
        [
            Sample(1, counter: "storagePath.busResets.summation"),
            Sample(1, counter: "storagePath.commandsAborted.summation"),
        ]);

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_same_fault_twice_in_one_batch_is_one_alert()
    {
        // Defensive rather than expected. Reconciliation treats its input as
        // the whole truth and two identical fingerprints in one pass is a
        // shape it should never have to reason about.
        Assert.Single(FaultCounters.Evaluate([Sample(1), Sample(1)]));
    }

    [Fact]
    public void The_fingerprint_is_stable_across_cycles()
    {
        // It has to be, or every cycle raises a new alert and resolves the
        // last one -- which reads as a storm and loses the history of a fault
        // that has been going on for hours.
        Assert.Equal(
            Assert.Single(FaultCounters.Evaluate([Sample(1)])).Fingerprint,
            Assert.Single(FaultCounters.Evaluate([Sample(7)])).Fingerprint);
    }

    [Fact]
    public void The_reading_and_its_interval_are_both_in_the_description()
    {
        // A summation without its window cannot be read: three resets in
        // twenty seconds and three in five minutes are different situations.
        var description = Assert.Single(FaultCounters.Evaluate([Sample(3)])).Description;

        Assert.Contains("3", description, StringComparison.Ordinal);
        Assert.Contains("20 seconds", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(FaultCounters.Evaluate([]));
    }

    // --- three values (ADR-0026) -------------------------------------------

    [Fact]
    public void A_counter_that_arrived_and_read_zero_is_absent_and_one_that_did_not_arrive_gets_no_verdict()
    {
        // A missing sample used to resolve an open fault alert; now only a
        // zero that was read does.
        var raised = Assert.IsType<ConditionPresent>(Assert.Single(FaultCounters.Judge([Sample(2)], T0)));
        var fingerprint = Assert.Single(raised.Alerts).Fingerprint;

        var cleared = Assert.IsType<ConditionAbsent>(Assert.Single(FaultCounters.Judge([Sample(0)], T0)));
        Assert.Equal([fingerprint], cleared.Covers);

        var other = FaultCounters.Judge([Sample(0, instance: "vmhba1:C0:T0:L1")], T0);
        Assert.DoesNotContain(other, v => v.Covers.Contains(fingerprint));
    }

    [Fact]
    public void A_reading_above_zero_outweighs_a_zero_for_the_same_device()
    {
        var verdicts = FaultCounters.Judge([Sample(0), Sample(3)], T0);

        Assert.IsType<ConditionPresent>(Assert.Single(verdicts));
    }
}