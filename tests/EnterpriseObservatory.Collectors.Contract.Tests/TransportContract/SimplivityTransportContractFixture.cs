using System.Text.Json.Nodes;
using EnterpriseObservatory.Collectors.Simplivity;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The wire-level contract for SimpliVity's REST transport
/// (<see cref="SimplivitySessionChannel"/>), scripted through
/// <see cref="FakeOmniStack"/>.
/// </summary>
/// <remarks>
/// REST creates no server-side view or collector, so "objects held" is
/// measured as the requests that could have created one — anything but a GET
/// or a token issue — and the token itself is the F3 session: kept across
/// cycles, given back at close.
/// </remarks>
public sealed class SimplivityTransportContractFixture : ITransportContractFixture
{
    private static readonly SimplivityInventoryContractFixture Sources = new();

    public async Task<bool> DisposingWithoutAnyCallIsSilentAsync()
    {
        var ovc = new FakeOmniStack(3, 0);
        var channel = ovc.Channel();

        var warning = await channel.RevokeAsync(CancellationToken.None);
        channel.Dispose();

        return warning is null && ovc.Requests.Count == 0;
    }

    public async Task<int> ReadHealthyInventoryEntityCountAsync(int targetCount)
    {
        var snapshot = await Sources.Source(new FakeOmniStack(targetCount, 0)).ReadAsync(CancellationToken.None);

        return snapshot.Annotations.Count;
    }

    public async Task<bool> CancelledReadLeavesNoServerObjectAsync()
    {
        var ovc = new FakeOmniStack(3, 0);
        using var cancel = new CancellationTokenSource();
        ovc.OnGet = cancel.Cancel;

        try
        {
            await Sources.Source(ovc).ReadAsync(cancel.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return ovc.ServerObjectsCreated == 0;
        }
    }

    public async Task<bool> SingleInvalidFieldFailsWholeReadAsync()
    {
        var ovc = new FakeOmniStack(3, 0)
        {
            // One host row that is not an object, among the rest.
            RawBody = new JsonObject
            {
                ["hosts"] = new JsonArray(new JsonObject { ["id"] = "a", ["state"] = "ALIVE" }, 42),
                ["count"] = 2,
            }.ToJsonString(),
        };

        return await Fails(() => Sources.Source(ovc).ReadAsync(CancellationToken.None));
    }

    public async Task<bool> MalformedReplyNeverBecomesEmptySuccessAsync()
    {
        var ovc = new FakeOmniStack(3, 0) { RawBody = "<html>503 Service Unavailable</html>" };

        return await Fails(() => Sources.Source(ovc).ReadAsync(CancellationToken.None));
    }

    public async Task<int> ReauthenticationsWhenTwoConcurrentCallsNoticeExpiredSessionAsync()
    {
        var ovc = new FakeOmniStack(3, 0);
        using var channel = ovc.Channel();
        (await channel.GetAsync("/api/hosts?limit=500&offset=0", CancellationToken.None)).Dispose();
        var before = ovc.TokenPosts;

        ovc.ExpireToken();
        var both = await Task.WhenAll(
            channel.GetAsync("/api/hosts?limit=500&offset=0", CancellationToken.None),
            channel.GetAsync("/api/virtual_machines?limit=500&offset=0", CancellationToken.None));

        foreach (var document in both)
        {
            document.Dispose();
        }

        return ovc.TokenPosts - before;
    }

    public async Task<int?> ViewsHeldAfterAFullCycleAsync(bool cancelDuringInventory)
    {
        var ovc = new FakeOmniStack(3, 0);
        using var cancel = new CancellationTokenSource();

        if (cancelDuringInventory)
        {
            ovc.OnGet = cancel.Cancel;
        }

        try
        {
            await Sources.Source(ovc).ReadAsync(cancel.Token);
        }
        catch (OperationCanceledException) when (cancelDuringInventory)
        {
        }

        return ovc.ServerObjectsCreated;
    }

    private static async Task<bool> Fails(Func<Task> read)
    {
        try
        {
            await read();
            return false;
        }
#pragma warning disable CA1031 // Justified: any failure is the pass condition.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }
}
