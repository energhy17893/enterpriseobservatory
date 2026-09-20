using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>
/// The retention windows themselves, and the arithmetic that justifies them.
/// </summary>
/// <remarks>
/// <para>
/// Nothing asserted these before. The compaction tests all construct a default
/// policy and jump five hundred days forward, so they pass at 90 days, at 400,
/// or at any number somebody types — which means ADR-0017 could be undone by a
/// one-character edit and the suite would stay green.
/// </para>
/// <para>
/// That matters here more than it usually would, because the decision has a
/// price attached. Four hundred days existed so that this December could be
/// compared with last December; ninety days ends that comparison, deliberately,
/// in exchange for 7.3 GB. A trade nobody can see is a trade that gets undone
/// by accident.
/// </para>
/// </remarks>
public class SeriesRetentionPolicyTests
{
    /// <summary>Samples per day per series, at the 30-second raw cadence.</summary>
    private const int RawPerDay = 2 * 60 * 24;

    private const int FiveMinutePerDay = 12 * 24;

    private const int HourlyPerDay = 24;

    /// <summary>Rows one series accumulates in a tier over its whole window.</summary>
    /// <remarks>
    /// The number that matters, and the one ADR-0012 did not compute. A tier's
    /// cost is its sample rate multiplied by how long it is kept, and it was
    /// the multiplication that overturned the original reasoning: a tenfold
    /// drop in rate against a two-hundredfold rise in retention is not a
    /// saving.
    /// </remarks>
    private static double RowsPerSeries(int perDay, TimeSpan window) =>
        perDay * window.TotalDays;

    [Fact]
    public void The_default_windows_are_what_was_decided()
    {
        var policy = SeriesRetentionPolicy.Default;

        Assert.Equal(TimeSpan.FromDays(2), policy.Raw);
        Assert.Equal(TimeSpan.FromDays(30), policy.FiveMinutes);
        Assert.Equal(TimeSpan.FromDays(90), policy.OneHour);
    }

    [Fact]
    public void A_window_can_still_be_configured_away_from_the_default()
    {
        // The decision is a default, not a law. An installation that wants
        // year-on-year comparison back sets Retention:HourlyDays and pays for
        // it; nothing in the code stops them, and ADR-0017 says so.
        var stretched = SeriesRetentionPolicy.Default with { OneHour = TimeSpan.FromDays(400) };

        Assert.Equal(TimeSpan.FromDays(400), stretched.For(SeriesResolution.OneHour));
        Assert.Equal(TimeSpan.FromDays(2), stretched.For(SeriesResolution.Raw));
    }

    [Fact]
    public void The_coarsest_tier_is_the_cheapest_which_is_the_point_of_ADR_0017()
    {
        var policy = SeriesRetentionPolicy.Default;

        var raw = RowsPerSeries(RawPerDay, policy.Raw);
        var fiveMinute = RowsPerSeries(FiveMinutePerDay, policy.FiveMinutes);
        var hourly = RowsPerSeries(HourlyPerDay, policy.OneHour);

        // The figures, so that a change to any window is visible as a change
        // to what it costs rather than as a number somebody edited.
        Assert.Equal(5_760d, raw);
        Assert.Equal(8_640d, fiveMinute);
        Assert.Equal(2_160d, hourly);

        // And the relationship those figures exist to produce, which is the
        // part that survives tuning. ADR-0012 asserted this and never checked
        // it; asserting only the numbers would pin the answer without pinning
        // the reason, and the reason is what a future edit will be arguing
        // with.
        Assert.True(
            hourly < fiveMinute && hourly < raw,
            $"The hourly tier is meant to be the cheapest of the three. It holds {hourly} rows " +
            $"per series against {fiveMinute} at five minutes and {raw} raw, which is the " +
            "situation ADR-0017 was written to end. Either the window grew or a finer tier " +
            "shrank; whichever it was, re-read that ADR before changing this test.");

        // The counterfactual, because the reason for the change is worth
        // keeping executable. At four hundred days the hourly tier was the
        // largest of the three, and "the long tail is nearly free" was the
        // sentence that put it there.
        var beforeAdr0017 = policy with { OneHour = TimeSpan.FromDays(400) };
        var stretched = RowsPerSeries(HourlyPerDay, beforeAdr0017.OneHour);

        Assert.Equal(9_600d, stretched);
        Assert.True(stretched > fiveMinute && stretched > raw);
    }
}
