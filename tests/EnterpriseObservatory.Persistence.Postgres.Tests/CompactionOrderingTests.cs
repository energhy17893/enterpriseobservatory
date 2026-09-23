namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The order of a compaction sweep, without a database.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about compaction is checked against a real server, because
/// the SQL is where it can be wrong. The order is different: it is wrong in the
/// C#, it is wrong silently, and the damage is permanent — so it is checked
/// here, where it runs on every machine rather than only on one that has
/// PostgreSQL and a password for it. A guarantee that is only verified when
/// <c>EO_TEST_PG_PASSWORD</c> happens to be set is a guarantee nobody is
/// holding.
/// </para>
/// <para>
/// What these pin is the hazard Thanos names: the hourly tier folds from the
/// five-minute buckets, so a delete that runs after a failed fold removes the
/// next fold's source data. The fold then writes its watermark past a window it
/// never summarised, and the hole is never retried and never reported.
/// </para>
/// </remarks>
public class CompactionOrderingTests
{
    private static int Step(List<string> steps, string name)
    {
        steps.Add(name);
        return 0;
    }

    [Fact]
    public void A_sweep_folds_everything_before_it_deletes_anything()
    {
        // The plain statement of the order. If a delete moves ahead of a fold,
        // raw samples go before they have been summarised and the five-minute
        // tier is short a window that nothing will ever rebuild: the chart for
        // that hour is simply emptier than the estate was, forever.
        //
        // The list is exact, so it is also where a fifth step coming back is
        // noticed. There used to be one: it deleted series rows that had lost
        // their last sample, reclaiming some 300 bytes each while taking locks
        // on the table the metric cycle appends to — and sample.series_id
        // cascades, so the version of it that lost the race took the samples
        // just written with it, without saying so. ADR-0019.
        var steps = new List<string>();

        CompactionSequence.Run(
            () => Step(steps, "fold:5m"),
            () => Step(steps, "fold:1h"),
            () => Step(steps, "delete:samples"),
            () => Step(steps, "delete:buckets"));

        Assert.Equal(["fold:5m", "fold:1h", "delete:samples", "delete:buckets"], steps);
    }

    [Fact]
    public void A_five_minute_fold_that_throws_stops_the_sweep_before_any_delete()
    {
        // The reason a failed sweep costs a pass rather than data. Isolating the
        // steps so that "one failure does not stop the others" would let the
        // raw retention delete run on a database where the five-minute buckets
        // for that window were never written — and raw samples are the only
        // copy.
        var steps = new List<string>();

        Assert.Throws<TimeoutException>(() => CompactionSequence.Run(
            () => throw new TimeoutException("the fold exceeded the command timeout"),
            () => Step(steps, "fold:1h"),
            () => Step(steps, "delete:samples"),
            () => Step(steps, "delete:buckets")));

        Assert.Empty(steps);
    }

    [Fact]
    public void An_hourly_fold_that_throws_stops_the_sweep_before_any_delete()
    {
        // The same rule for the second fold, and the one that is easy to lose:
        // the five-minute buckets have already been written by the time it
        // runs, so the sweep looks like it succeeded. It did not. The hourly
        // tier folds FROM those buckets, and the bucket retention delete is
        // what removes them — run it now and the hour is built from source rows
        // that no longer exist, after the watermark has moved past them.
        var steps = new List<string>();
        var folded = 0;

        Assert.Throws<InvalidOperationException>(() => CompactionSequence.Run(
            () => ++folded,
            () => throw new InvalidOperationException("the hourly fold failed"),
            () => Step(steps, "delete:samples"),
            () => Step(steps, "delete:buckets")));

        Assert.Equal(1, folded);
        Assert.Empty(steps);
    }

    [Fact]
    public void A_sweep_reports_what_each_step_did()
    {
        // The report is what the worker logs and what the operator is shown, so
        // a step wired to the wrong field would have the product cheerfully
        // reporting deletions it never performed.
        var report = CompactionSequence.Run(
            () => 3, () => 4, () => 5, () => 6);

        Assert.Equal(7, report.BucketsWritten);
        Assert.Equal(5, report.SamplesDeleted);
        Assert.Equal(6, report.BucketsDeleted);
        Assert.True(report.DidSomething);
    }

    [Fact]
    public void A_sweep_times_the_deletes_and_not_the_folds()
    {
        // The duration decides whether sample needs an index on its age, so a
        // slow fold counted in it would argue for the wrong fix.
        var report = CompactionSequence.Run(
            () => { Thread.Sleep(1000); return 0; },
            () => 0,
            () => { Thread.Sleep(50); return 0; },
            () => 0);

        Assert.InRange(report.DeleteDuration, TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(900));
    }
}
