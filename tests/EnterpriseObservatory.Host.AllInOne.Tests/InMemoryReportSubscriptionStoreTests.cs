using EnterpriseObservatory.Application.Reporting;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The in-memory report subscription store's own guarantees, isolated from
/// the composition-root smoke suite's HTTP layer.
/// </summary>
public class InMemoryReportSubscriptionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);

    private static ReportSubscription Sample(string id = "sub-1") => new()
    {
        Id = id,
        Recipients = ["team@example.com"],
        Schedule = new ReportSchedule { Frequency = ReportFrequency.Daily, HourLocal = 7 },
        CreatedBy = "ertugrul",
        CreatedUtc = T0,
    };

    [Fact]
    public void Mark_dispatched_is_visible_on_the_same_store()
    {
        var store = new InMemoryReportSubscriptionStore();
        store.Add(Sample());

        store.MarkDispatched("sub-1", T0.AddDays(1));

        Assert.Equal(T0.AddDays(1), store.Find("sub-1")!.LastSentUtc);
    }

    [Fact]
    public void Update_keeps_the_stores_own_last_sent_utc_rather_than_the_callers_copy()
    {
        // The update race: a caller who read the record before a dispatch
        // claimed it must not be able to un-claim it by editing something
        // else and submitting its own (stale, null) LastSentUtc back.
        var store = new InMemoryReportSubscriptionStore();
        var original = Sample();
        store.Add(original);

        store.MarkDispatched("sub-1", T0.AddDays(1));

        var staleEdit = original with { Recipients = ["team@example.com", "second@example.com"] };
        Assert.True(store.Update(staleEdit));

        Assert.Equal(T0.AddDays(1), store.Find("sub-1")!.LastSentUtc);
    }
}
