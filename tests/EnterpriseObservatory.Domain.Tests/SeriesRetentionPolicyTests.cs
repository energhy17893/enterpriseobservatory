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

    // --- reaching the data that is actually there -------------------------

    [Theory]
    // An hour wide, an hour old: raw is fine enough and still has it.
    [InlineData(1, 1, SeriesResolution.Raw)]
    // An hour wide, ten days old: raw is fine enough and no longer has it, so
    // the five-minute tier answers. This is the case that used to come back
    // empty while the data sat in the database.
    [InlineData(1, 24 * 10, SeriesResolution.FiveMinutes)]
    // Thirty days wide: too wide for anything but hourly, whatever its age.
    [InlineData(24 * 30, 24 * 30, SeriesResolution.OneHour)]
    // An hour wide, forty days old: past five-minute retention too.
    [InlineData(1, 24 * 40, SeriesResolution.OneHour)]
    public void A_window_is_answered_from_a_tier_that_is_both_fine_enough_and_still_kept(
        int rangeHours, int ageHours, SeriesResolution expected)
    {
        Assert.Equal(
            expected,
            SeriesRetentionPolicy.Default.RetainedResolutionFor(
                TimeSpan.FromHours(rangeHours), 720, TimeSpan.FromHours(ageHours)));
    }

    [Fact]
    public void Both_conditions_are_required_not_either()
    {
        var policy = SeriesRetentionPolicy.Default;

        // Fine enough but not kept: raw would answer an hour-wide window, and
        // ten days ago it has none.
        Assert.NotEqual(
            SeriesResolution.Raw,
            policy.RetainedResolutionFor(TimeSpan.FromHours(1), 720, TimeSpan.FromDays(10)));

        // Kept but not fine enough: the five-minute tier still holds ten-day-old
        // data, but a thirty-day window would need 8,640 points against a
        // budget of 720.
        Assert.NotEqual(
            SeriesResolution.FiveMinutes,
            policy.RetainedResolutionFor(TimeSpan.FromDays(30), 720, TimeSpan.FromDays(10)));
    }

    [Fact]
    public void A_window_older_than_every_tier_gets_the_coarsest_rather_than_an_error()
    {
        // It will come back empty, and that is the truth. Returning the
        // coarsest tier keeps the answer the same shape as every other answer,
        // so emptiness is data the caller can read rather than an exception it
        // has to catch.
        Assert.Equal(
            SeriesResolution.OneHour,
            SeriesRetentionPolicy.Default.RetainedResolutionFor(
                TimeSpan.FromHours(1), 720, TimeSpan.FromDays(500)));
    }

    [Fact]
    public void A_stretched_window_makes_an_older_range_reachable_again()
    {
        // The rule reads the policy rather than constants, so an installation
        // that buys back a longer window gets the reach that comes with it.
        var generous = SeriesRetentionPolicy.Default with { FiveMinutes = TimeSpan.FromDays(60) };

        Assert.Equal(
            SeriesResolution.FiveMinutes,
            generous.RetainedResolutionFor(TimeSpan.FromHours(1), 720, TimeSpan.FromDays(40)));

        Assert.Equal(
            SeriesResolution.OneHour,
            SeriesRetentionPolicy.Default.RetainedResolutionFor(
                TimeSpan.FromHours(1), 720, TimeSpan.FromDays(40)));
    }
}
