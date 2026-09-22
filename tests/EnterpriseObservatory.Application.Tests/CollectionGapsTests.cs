using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The source-level gap record's arithmetic (handover 3, T0.4 second half).
/// </summary>
public class CollectionGapsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static CollectionGap Gap(DateTimeOffset from, DateTimeOffset to) => new()
    {
        Id = 1,
        SourceInstanceId = "vc-1",
        FromUtc = from,
        ToUtc = to,
        FilledToUtc = from,
        State = CollectionGapState.Open,
        OpenedAtUtc = T0,
    };

    [Fact]
    public void No_mark_means_nothing_to_record()
    {
        // A fresh installation has no history to have a hole in.
        Assert.Null(CollectionGaps.ToOpen(sourceMark: null, liveStart: T0));
    }

    [Fact]
    public void A_mark_the_live_read_reaches_is_not_a_gap()
    {
        Assert.Null(CollectionGaps.ToOpen(T0, liveStart: T0));
        Assert.Null(CollectionGaps.ToOpen(T0.AddSeconds(20), liveStart: T0));
    }

    [Fact]
    public void A_mark_behind_the_live_read_opens_exactly_the_part_it_does_not_cover()
    {
        var gap = CollectionGaps.ToOpen(T0.AddMinutes(-30), liveStart: T0.AddMinutes(-2));

        Assert.Equal((T0.AddMinutes(-30), T0.AddMinutes(-2)), gap);
    }

    [Fact]
    public void Slices_are_ten_minutes_from_where_the_fill_stopped_and_never_pass_the_gap()
    {
        var gap = Gap(T0.AddMinutes(-30), T0.AddMinutes(-2));

        Assert.Equal((T0.AddMinutes(-30), T0.AddMinutes(-20)), CollectionGaps.NextSlice(gap));

        var last = gap with { FilledToUtc = T0.AddMinutes(-10) };
        Assert.Equal((T0.AddMinutes(-10), T0.AddMinutes(-2)), CollectionGaps.NextSlice(last));
    }

    [Fact]
    public void Advancing_to_the_end_closes_the_gap_as_filled()
    {
        var gap = Gap(T0.AddMinutes(-30), T0.AddMinutes(-2));

        var partly = CollectionGaps.Advance(gap, T0.AddMinutes(-20), T0);
        Assert.Equal(CollectionGapState.Open, partly.State);
        Assert.Equal(T0.AddMinutes(-20), partly.FilledToUtc);
        Assert.Null(partly.ClosedAtUtc);

        var done = CollectionGaps.Advance(partly, T0.AddMinutes(-2), T0.AddSeconds(30));
        Assert.Equal(CollectionGapState.Filled, done.State);
        Assert.Equal(T0.AddSeconds(30), done.ClosedAtUtc);
        Assert.Null(done.LostBeforeUtc);
    }

    [Fact]
    public void Advancing_never_moves_back_and_never_past_the_end()
    {
        var gap = Gap(T0.AddMinutes(-30), T0.AddMinutes(-2)) with { FilledToUtc = T0.AddMinutes(-10) };

        Assert.Equal(T0.AddMinutes(-10), CollectionGaps.Advance(gap, T0.AddMinutes(-20), T0).FilledToUtc);
        Assert.Equal(T0.AddMinutes(-2), CollectionGaps.Advance(gap, T0.AddMinutes(5), T0).FilledToUtc);
    }

    [Fact]
    public void A_gap_wholly_beyond_retention_is_closed_unrecoverable_not_deleted()
    {
        var gap = Gap(T0.AddHours(-3), T0.AddHours(-2));

        var expired = CollectionGaps.Expire(gap, recoverableAfter: T0.AddHours(-1), T0);

        Assert.Equal(CollectionGapState.Unrecoverable, expired.State);
        Assert.Equal(T0.AddHours(-2), expired.LostBeforeUtc);
        Assert.Equal(T0.AddHours(-2), expired.FilledToUtc);
        Assert.Equal(T0, expired.ClosedAtUtc);
    }

    [Fact]
    public void A_gap_straddling_retention_records_the_lost_part_and_fills_the_rest()
    {
        var gap = Gap(T0.AddMinutes(-90), T0.AddMinutes(-2));

        var expired = CollectionGaps.Expire(gap, recoverableAfter: T0.AddMinutes(-59), T0);

        Assert.Equal(CollectionGapState.Open, expired.State);
        Assert.Equal(T0.AddMinutes(-59), expired.LostBeforeUtc);
        Assert.Equal(T0.AddMinutes(-59), expired.FilledToUtc);

        // Filled to the end, it still says part of it was never recovered.
        var closed = CollectionGaps.Advance(expired, T0.AddMinutes(-2), T0);
        Assert.Equal(CollectionGapState.Unrecoverable, closed.State);
        Assert.Equal(T0.AddMinutes(-59), closed.LostBeforeUtc);
    }

    [Fact]
    public void A_gap_inside_retention_is_left_alone()
    {
        var gap = Gap(T0.AddMinutes(-30), T0.AddMinutes(-2));

        Assert.Same(gap, CollectionGaps.Expire(gap, recoverableAfter: T0.AddMinutes(-59), T0));
    }

    [Fact]
    public void A_closed_gap_is_not_reopened_or_moved()
    {
        var closed = CollectionGaps.Advance(Gap(T0.AddMinutes(-30), T0.AddMinutes(-2)), T0.AddMinutes(-2), T0);

        Assert.Same(closed, CollectionGaps.Advance(closed, T0.AddMinutes(-1), T0.AddMinutes(1)));
        Assert.Same(closed, CollectionGaps.Expire(closed, T0, T0.AddHours(2)));
    }
}
