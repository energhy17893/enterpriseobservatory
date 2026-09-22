namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The wire-level half of the collector contract suite (ADR-0025, roadmap
/// T2.1): every collector's transport implementation must pass this,
/// whatever vendor protocol it speaks underneath.
/// </summary>
/// <remarks>
/// A concrete collector supplies <typeparamref name="TFixture"/>, a fake
/// transport/test double that answers these scenarios without a real server.
/// A new vendor (Redfish, M6) proves it has not reintroduced one of the six
/// leaks ADR-0025 closed by making its own fixture pass this same suite —
/// nothing here is vSphere-specific.
/// </remarks>
public abstract class TransportContractTests<TFixture>
    where TFixture : ITransportContractFixture, new()
{
    // --- case 1: start/stop, including stop without ever starting ---------

    [Fact]
    public async Task Disposing_without_ever_reading_sends_nothing_and_throws_nothing()
    {
        var fixture = new TFixture();

        Assert.True(await fixture.DisposingWithoutAnyCallIsSilentAsync());
    }

    [Fact]
    public async Task A_healthy_read_reports_every_target_asked_for()
    {
        var fixture = new TFixture();

        Assert.Equal(5, await fixture.ReadHealthyInventoryEntityCountAsync(5));
    }

    // --- case 2: mid-cycle cancellation leaks no server-side object -------

    [Fact]
    public async Task A_read_cut_off_mid_cycle_leaves_no_server_side_object_behind()
    {
        var fixture = new TFixture();

        Assert.True(await fixture.CancelledReadLeavesNoServerObjectAsync());
    }

    // --- our case: a single invalid property path fails the whole read ----

    [Fact]
    public async Task A_single_invalid_property_path_fails_the_whole_inventory_read()
    {
        var fixture = new TFixture();

        Assert.True(await fixture.SingleInvalidFieldFailsWholeReadAsync());
    }

    // --- case 4: a malformed reply is never an empty Ok --------------------

    [Fact]
    public async Task A_malformed_reply_never_becomes_an_empty_success()
    {
        var fixture = new TFixture();

        Assert.True(await fixture.MalformedReplyNeverBecomesEmptySuccessAsync());
    }

    // --- our case: two concurrent calls notice one expired session --------

    [Fact]
    public async Task Two_calls_that_find_the_session_expired_reauthenticate_once()
    {
        var fixture = new TFixture();

        Assert.Equal(
            1, await fixture.ReauthenticationsWhenTwoConcurrentCallsNoticeExpiredSessionAsync());
    }

    // --- F4 (ADR-0025 §3): views_held is the proof, not just a test --------

    [Fact]
    public async Task No_views_are_held_after_a_full_inventory_metrics_and_event_cycle()
    {
        var fixture = new TFixture();

        Assert.Equal(0, await fixture.ViewsHeldAfterAFullCycleAsync(cancelDuringInventory: false));
    }

    [Fact]
    public async Task No_views_are_held_after_a_full_cycle_whose_inventory_read_was_cancelled_mid_cycle()
    {
        var fixture = new TFixture();

        Assert.Equal(0, await fixture.ViewsHeldAfterAFullCycleAsync(cancelDuringInventory: true));
    }
}
