using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// F2's per-source request ceiling: a source's gate bounds how many requests
/// it has outstanding at once, independently of every other source's gate.
/// docs/proposals/f-invert-collector-authority.md §3.2, §8 decision 3.
/// </summary>
public class SourceRequestGateTests
{
    [Fact]
    public void The_default_limit_is_two()
    {
        Assert.Equal(2, SourceRequestGate.DefaultLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(100)]
    public void A_limit_outside_one_to_eight_is_refused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRequestGate(limit));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void A_limit_of_one_to_eight_is_accepted(int limit)
    {
        using var gate = new SourceRequestGate(limit);
        Assert.Equal(limit, gate.Limit);
    }

    [Fact]
    public async Task No_more_than_the_limit_are_in_flight_at_once()
    {
        using var gate = new SourceRequestGate(2);
        var inFlight = 0;
        var maxObserved = 0;
        var gateLock = new Lock();

        async Task OneRequestAsync()
        {
            using var permit = await gate.AcquireAsync(CancellationToken.None);

            lock (gateLock)
            {
                inFlight++;
                maxObserved = Math.Max(maxObserved, inFlight);
            }

            // Long enough that, with a limit honoured, several requests queue
            // up behind the two permits rather than all running at once.
            await Task.Delay(50).ConfigureAwait(false);

            lock (gateLock)
            {
                inFlight--;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => OneRequestAsync()));

        Assert.Equal(2, maxObserved);
    }

    [Fact]
    public async Task Releasing_a_permit_lets_the_next_request_through()
    {
        using var gate = new SourceRequestGate(1);

        var first = await gate.AcquireAsync(CancellationToken.None);

        var secondTask = gate.AcquireAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(secondTask.IsCompleted, "A second acquire must wait while the only permit is held.");

        first.Dispose();

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        second.Dispose();
    }

    [Fact]
    public async Task Two_sources_gates_are_independent()
    {
        // The whole point of F2: the limit is per source, never a shared,
        // global ceiling that one busy vCenter could spend on behalf of
        // another.
        using var gateA = new SourceRequestGate(1);
        using var gateB = new SourceRequestGate(1);

        var heldA = await gateA.AcquireAsync(CancellationToken.None);

        // B's single permit is still free even though A's is held.
        var heldB = await gateB.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        heldA.Dispose();
        heldB.Dispose();
    }
}
