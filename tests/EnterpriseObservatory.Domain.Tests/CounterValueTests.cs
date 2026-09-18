using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

public class CounterValueTests
{
    private static CounterValue Ready(double rawMs, TimeSpan interval) => new()
    {
        CounterName = "cpu.ready.summation",
        Raw = rawMs,
        Rollup = RollupType.Summation,
        Interval = interval,
        Unit = "millisecond",
    };

    [Fact]
    public void Cpu_ready_converts_to_a_percentage_of_its_interval()
    {
        // 2000 ms of ready time in a 20 s real-time sample is 10%.
        var value = Ready(2000, TimeSpan.FromSeconds(20));

        Assert.Equal(10d, value.AsPercentageOfInterval(), precision: 6);
    }

    [Fact]
    public void The_same_raw_number_means_a_different_percentage_over_a_different_interval()
    {
        // This is the whole reason the interval travels with the value. The
        // previous product performed this division in several places, so the
        // same reading could be reported as two different percentages.
        var raw = 2000d;

        var realtime = Ready(raw, TimeSpan.FromSeconds(20)).AsPercentageOfInterval();
        var historical = Ready(raw, TimeSpan.FromMinutes(5)).AsPercentageOfInterval();

        Assert.Equal(10d, realtime, precision: 6);
        Assert.Equal(0.666667d, historical, precision: 6);
    }

    [Fact]
    public void An_average_counter_cannot_be_read_as_a_percentage_of_its_interval()
    {
        var usage = new CounterValue
        {
            CounterName = "cpu.usage.average",
            Raw = 42,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
        };

        Assert.Throws<InvalidOperationException>(() => usage.AsPercentageOfInterval());
    }

    [Fact]
    public void A_summation_that_is_not_a_duration_cannot_be_read_as_a_percentage()
    {
        var bytes = new CounterValue
        {
            CounterName = "net.bytesRx.summation",
            Raw = 1024,
            Rollup = RollupType.Summation,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "kiloBytes",
        };

        Assert.Throws<InvalidOperationException>(() => bytes.AsPercentageOfInterval());
    }

    [Fact]
    public void A_non_positive_interval_is_rejected_rather_than_dividing_by_zero()
    {
        var broken = Ready(2000, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => broken.AsPercentageOfInterval());
    }
}
