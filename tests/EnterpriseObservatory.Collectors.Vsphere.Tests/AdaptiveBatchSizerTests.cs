using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class AdaptiveBatchSizerTests
{
    [Fact]
    public void The_batch_is_derived_from_the_server_limit_and_the_counter_count()
    {
        // 256 * 0.8 / 8 = 25
        var sizer = new AdaptiveBatchSizer(serverMaxQueryMetrics: 256, counterCount: 8);

        Assert.Equal(25, sizer.Current);
    }

    [Fact]
    public void Asking_for_more_counters_shrinks_the_batch()
    {
        var few = new AdaptiveBatchSizer(256, counterCount: 2);
        var many = new AdaptiveBatchSizer(256, counterCount: 16);

        Assert.True(few.Current > many.Current);
    }

    [Fact]
    public void The_previous_products_fixed_batch_would_have_exceeded_the_default_limit()
    {
        // 32 entities x 8 counters = 256 metrics, at the default limit with no
        // headroom. The origin of that 32 is not recorded anywhere; under the
        // stricter reading of maxQueryMetrics those queries were being refused
        // in the field and coming back empty.
        var sizer = new AdaptiveBatchSizer(AdaptiveBatchSizer.DefaultMaxQueryMetrics, counterCount: 8);

        Assert.True(sizer.Current < 32);
    }

    [Fact]
    public void An_unknown_server_limit_falls_back_to_the_documented_default()
    {
        var unknown = new AdaptiveBatchSizer(serverMaxQueryMetrics: null, counterCount: 8);
        var explicitDefault = new AdaptiveBatchSizer(AdaptiveBatchSizer.DefaultMaxQueryMetrics, 8);

        Assert.Equal(explicitDefault.Current, unknown.Current);
    }

    [Fact]
    public void A_disabled_limit_is_treated_as_generous_rather_than_unlimited()
    {
        // -1 disables the cap, but an unbounded query is still a way to make
        // vCenter unresponsive for everyone else using it.
        var disabled = new AdaptiveBatchSizer(serverMaxQueryMetrics: -1, counterCount: 8);

        Assert.True(disabled.Current > new AdaptiveBatchSizer(256, 8).Current);
        Assert.True(disabled.Current < 10_000);
    }

    [Fact]
    public void A_refusal_halves_the_batch()
    {
        var sizer = new AdaptiveBatchSizer(256, counterCount: 8);
        var before = sizer.Current;

        Assert.True(sizer.Reduce());

        Assert.Equal(before / 2, sizer.Current);
        Assert.True(sizer.WasReduced);
    }

    [Fact]
    public void Reducing_stops_at_one_entity()
    {
        // Below one there is nothing left to give: if a single-entity query is
        // still refused, size is not the problem and pretending otherwise would
        // loop forever.
        var sizer = new AdaptiveBatchSizer(8, counterCount: 8);

        while (sizer.Reduce())
        {
            // keep halving
        }

        Assert.Equal(1, sizer.Current);
        Assert.False(sizer.Reduce());
    }

    [Fact]
    public void The_batch_is_never_zero_even_with_absurdly_many_counters()
    {
        var sizer = new AdaptiveBatchSizer(serverMaxQueryMetrics: 4, counterCount: 500);

        Assert.Equal(1, sizer.Current);
    }

    [Fact]
    public void A_counter_count_of_zero_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveBatchSizer(256, 0));
    }

    [Theory]
    [InlineData("Request processing is restricted by administrator.")]
    [InlineData("The number of metrics exceeds the maximum allowed.")]
    [InlineData("Too many metrics requested")]
    public void A_query_size_refusal_is_recognised(string message)
    {
        // vCenter reports this as a generic fault whose message is the only
        // distinguishing feature, so matching on text is unavoidable.
        Assert.True(AdaptiveBatchSizer.IsQuerySizeRefusal(message));
    }

    [Theory]
    [InlineData("Cannot complete login due to an incorrect user name or password.")]
    [InlineData("The object has already been deleted or has not been completely created.")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_failures_are_not_mistaken_for_a_size_problem(string? message)
    {
        // Halving the batch in response to an authentication failure would loop
        // pointlessly and hide the real cause.
        Assert.False(AdaptiveBatchSizer.IsQuerySizeRefusal(message));
    }

    [Theory]
    [InlineData("'config.vpxd.stats.maxQueryMetrics' is invalid or exceeds the maximum number of characters permitted.")]
    [InlineData("'some.other.option' is invalid")]
    public void An_invalid_name_is_not_a_size_problem_however_it_is_worded(string message)
    {
        // A live vCenter 8 returned the first of these for an option that was
        // never set. An earlier version of this matcher accepted "exceeds the
        // maximum" and a bare mention of maxQueryMetrics, and read it as a
        // performance-query size refusal — which would send the collector
        // shrinking batches against a problem that has nothing to do with size.
        //
        // These cases were previously asserted the other way round. The test
        // was codifying the bug.
        Assert.False(AdaptiveBatchSizer.IsQuerySizeRefusal(message));
    }
}
