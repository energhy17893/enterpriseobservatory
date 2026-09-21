using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Dropped packets as a share of real traffic, never as "any drop is bad".
/// </summary>
/// <remarks>
/// At the defaults a verdict needs at least one percent dropped and at least
/// one hundred packets a second in that direction — two thousand in a
/// twenty-second window. Most tests sit on one side of exactly one of those
/// lines so that each gate is defended by a test of its own.
/// </remarks>
public class DroppedPacketsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Vm = "vc-1:vm-7";

    private static Observation Count(
        string counter,
        double raw,
        string entity = Vm,
        string instance = "",
        RollupType rollup = RollupType.Summation,
        int seconds = 20) => new()
        {
            Entity = new EntityId(entity),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = raw,
                Rollup = rollup,
                Interval = TimeSpan.FromSeconds(seconds),
                Unit = "number",
                Instance = instance,
            },
        };

    private static Observation[] Received(double packets, double dropped, string entity = Vm, int seconds = 20) =>
    [
        Count("net.packetsRx.summation", packets, entity, seconds: seconds),
        Count("net.droppedRx.summation", dropped, entity, seconds: seconds),
    ];

    private static Observation[] Transmitted(double packets, double dropped, string entity = Vm) =>
    [
        Count("net.packetsTx.summation", packets, entity),
        Count("net.droppedTx.summation", dropped, entity),
    ];

    // --- it speaks ---------------------------------------------------------

    [Fact]
    public void A_high_drop_ratio_under_real_traffic_raises_a_receive_alert()
    {
        // 500 of 10 500 is 4.8%, at 525 packets a second.
        var alert = Assert.Single(DroppedPackets.Evaluate(Received(10_000, 500)));

        Assert.Equal("Dropping received packets", alert.Title);
        Assert.Equal(new EntityId(Vm), alert.Entity);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(DroppedPackets.Category, alert.Category);
        Assert.True(alert.IsDerived);
        Assert.Contains("4.76%", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Transmit_is_judged_on_its_own_and_says_so()
    {
        // Receive is clean and busy; transmit is dropping. The two point at
        // different work, so they must not blur into one verdict.
        var alert = Assert.Single(DroppedPackets.Evaluate(
            [.. Received(10_000, 0), .. Transmitted(10_000, 500)]));

        Assert.Equal("Dropping transmitted packets", alert.Title);
    }

    [Fact]
    public void A_host_is_judged_exactly_as_a_machine_is()
    {
        var alert = Assert.Single(DroppedPackets.Evaluate(Received(10_000, 500, "vc-1:host-1")));

        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
    }

    [Fact]
    public void Exactly_the_drop_line_is_enough()
    {
        // 100 of 10 000 is exactly 1%.
        Assert.Single(DroppedPackets.Evaluate(Received(9_900, 100)));
    }

    [Fact]
    public void Exactly_the_traffic_floor_is_enough()
    {
        // 2 000 packets in 20 seconds is exactly 100 a second.
        Assert.Single(DroppedPackets.Evaluate(Received(1_980, 20)));
    }

    // --- the ratio gate ----------------------------------------------------

    [Fact]
    public void A_low_drop_ratio_on_a_busy_link_is_silent()
    {
        // The whole reason these are not faults: a busy link drops the odd
        // frame. 50 of 10 050 is half a percent.
        Assert.Empty(DroppedPackets.Evaluate(Received(10_000, 50)));
    }

    [Fact]
    public void Just_under_the_drop_line_is_silent()
    {
        // 99 of 10 000 is 0.99%.
        Assert.Empty(DroppedPackets.Evaluate(Received(9_901, 99)));
    }

    [Fact]
    public void The_drop_line_is_policy()
    {
        var strict = new DroppedPacketsPolicy { DropPercent = 10d };

        Assert.Empty(DroppedPackets.Evaluate(Received(10_000, 500), strict));
    }

    // --- the traffic floor -------------------------------------------------

    [Fact]
    public void An_idle_nic_that_drops_one_of_three_is_silent()
    {
        // 33% of three packets is noise, not a network problem.
        Assert.Empty(DroppedPackets.Evaluate(Received(2, 1)));
    }

    [Fact]
    public void Just_under_the_traffic_floor_is_silent()
    {
        // 1 999 packets in 20 seconds, at a ratio well over the line.
        Assert.Empty(DroppedPackets.Evaluate(Received(1_899, 100)));
    }

    [Fact]
    public void The_floor_is_a_rate_so_a_longer_window_needs_more_packets()
    {
        // The same counts that fire over twenty seconds are 35 a second over
        // three hundred.
        Assert.Empty(DroppedPackets.Evaluate(Received(10_000, 500, seconds: 300)));
    }

    [Fact]
    public void The_traffic_floor_is_policy()
    {
        var quiet = new DroppedPacketsPolicy { MinimumPacketsPerSecond = 1_000d };

        Assert.Empty(DroppedPackets.Evaluate(Received(10_000, 500), quiet));
    }

    [Fact]
    public void Dropped_packets_count_as_traffic()
    {
        // 1 900 passed and 100 dropped is 2 000 offered: at the floor. Counting
        // only what passed would put it at 95 a second and silence a link
        // dropping five percent.
        Assert.Single(DroppedPackets.Evaluate(Received(1_900, 100)));
    }

    [Fact]
    public void A_direction_that_dropped_everything_is_reported_rather_than_divided_by_zero()
    {
        var alert = Assert.Single(DroppedPackets.Evaluate(Received(0, 5_000)));

        Assert.Contains("100%", alert.Description, StringComparison.Ordinal);
    }

    // --- not looking is not finding nothing --------------------------------

    [Fact]
    public void Drops_without_a_packet_count_reach_no_verdict()
    {
        // No denominator, no ratio. Falling back to "any drop" is exactly the
        // FaultCounters behaviour these counters were kept out of.
        Assert.Empty(DroppedPackets.Evaluate([Count("net.droppedRx.summation", 5_000)]));
    }

    [Fact]
    public void Another_entitys_packets_are_not_this_ones_denominator()
    {
        Assert.Empty(DroppedPackets.Evaluate(
        [
            Count("net.packetsRx.summation", 10_000, "vc-1:vm-other"),
            Count("net.droppedRx.summation", 500),
        ]));
    }

    [Fact]
    public void A_per_nic_series_is_not_read()
    {
        Assert.Empty(DroppedPackets.Evaluate(
        [
            Count("net.packetsRx.summation", 10_000, instance: "4000"),
            Count("net.droppedRx.summation", 500, instance: "4000"),
        ]));
    }

    [Fact]
    public void A_count_that_is_not_a_summation_is_not_read()
    {
        Assert.Empty(DroppedPackets.Evaluate(
        [
            Count("net.packetsRx.summation", 10_000, rollup: RollupType.Average),
            Count("net.droppedRx.summation", 500),
        ]));
    }

    [Fact]
    public void A_count_with_no_window_is_skipped_rather_than_thrown_over()
    {
        Assert.Empty(DroppedPackets.Evaluate(
        [
            Count("net.packetsRx.summation", 10_000, seconds: 0),
            Count("net.droppedRx.summation", 500, seconds: 0),
        ]));
    }

    [Fact]
    public void A_negative_packet_count_is_not_traffic()
    {
        // -1 000 passed and 3 000 dropped would otherwise be 2 000 offered at
        // 150% dropped.
        Assert.Empty(DroppedPackets.Evaluate(Received(-1_000, 3_000)));
    }

    [Fact]
    public void Two_samples_for_one_entity_keep_the_larger_whatever_the_order()
    {
        Assert.Single(DroppedPackets.Evaluate(
        [
            Count("net.packetsRx.summation", 10_000),
            Count("net.packetsRx.summation", 100),
            Count("net.droppedRx.summation", 500),
        ]));
    }

    // --- identity ----------------------------------------------------------

    [Fact]
    public void The_fingerprint_follows_the_entity_and_direction_not_the_ratio()
    {
        var first = Assert.Single(DroppedPackets.Evaluate(Received(10_000, 500))).Fingerprint;
        var worse = Assert.Single(DroppedPackets.Evaluate(Received(10_000, 3_000))).Fingerprint;
        var tx = Assert.Single(DroppedPackets.Evaluate(Transmitted(10_000, 500))).Fingerprint;
        var other = Assert.Single(DroppedPackets.Evaluate(Received(10_000, 500, "vc-1:vm-8"))).Fingerprint;

        Assert.Equal(first, worse);
        Assert.NotEqual(first, tx);
        Assert.NotEqual(first, other);
    }
}
