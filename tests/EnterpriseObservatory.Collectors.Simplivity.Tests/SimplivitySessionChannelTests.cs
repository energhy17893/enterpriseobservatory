using System.Net;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Simplivity.Tests;

/// <summary>
/// The token lifecycle (F3 pattern): one token across cycles, a 401 replaced
/// once, revoked only at close, and a refused revoke is a warning.
/// </summary>
public class SimplivitySessionChannelTests
{
    [Fact]
    public async Task One_token_serves_every_read_across_cycles()
    {
        var ovc = FakeOvc.FromFixtures();
        using var channel = ovc.Channel();

        for (var cycle = 0; cycle < 5; cycle++)
        {
            using var _ = await channel.GetAsync("/api/hosts?limit=500&offset=0", CancellationToken.None);
        }

        Assert.Equal(1, ovc.TokenPosts);
        Assert.Equal(1, channel.SessionsHeld);
    }

    [Fact]
    public async Task A_401_replaces_the_token_once_and_asks_again()
    {
        var ovc = FakeOvc.FromFixtures();
        using var channel = ovc.Channel();
        using (await channel.GetAsync("/api/hosts", CancellationToken.None))
        {
        }

        ovc.ExpireToken();
        using var hosts = await channel.GetAsync("/api/hosts", CancellationToken.None);

        Assert.Equal(2, ovc.TokenPosts);
        Assert.Equal(3, hosts.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Two_reads_that_find_the_token_dead_replace_it_once()
    {
        var ovc = FakeOvc.FromFixtures();
        using var channel = ovc.Channel();
        using (await channel.GetAsync("/api/hosts", CancellationToken.None))
        {
        }

        ovc.ExpireToken();
        var both = await Task.WhenAll(
            channel.GetAsync("/api/hosts", CancellationToken.None),
            channel.GetAsync("/api/virtual_machines", CancellationToken.None));

        foreach (var document in both)
        {
            document.Dispose();
        }

        Assert.Equal(2, ovc.TokenPosts);
        Assert.Equal(2, channel.TokensIssued);
    }

    [Fact]
    public async Task A_401_on_a_token_just_issued_is_not_retried_again()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.RefuseAllTokens = true;
        using var channel = ovc.Channel();

        var thrown = await Assert.ThrowsAsync<SimplivityApiException>(
            () => channel.GetAsync("/api/hosts", CancellationToken.None));

        Assert.Equal(CollectionFailureKind.AuthenticationRejected, thrown.Kind);
        Assert.Equal(2, ovc.TokenPosts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_rejected_password_is_not_worth_retrying(HttpStatusCode status)
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.TokenStatus = status;
        using var channel = ovc.Channel();

        var thrown = await Assert.ThrowsAsync<SimplivityApiException>(
            () => channel.GetAsync("/api/hosts", CancellationToken.None));

        Assert.False(CollectionFailures.IsWorthRetrying(thrown.Kind));
        Assert.Equal(1, ovc.TokenPosts);
    }

    [Fact]
    public async Task A_revoke_answered_401_is_a_warning_not_an_error()
    {
        var ovc = FakeOvc.FromFixtures();
        using var channel = ovc.Channel();
        using (await channel.GetAsync("/api/hosts", CancellationToken.None))
        {
        }

        var warning = await channel.RevokeAsync(CancellationToken.None);

        Assert.NotNull(warning);
        Assert.Contains("401", warning, StringComparison.Ordinal);
        Assert.Equal(0, channel.SessionsHeld);
        Assert.Contains("POST /api/oauth/revoke", ovc.Requests);
    }

    [Fact]
    public async Task A_revoke_the_server_accepts_reports_nothing()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.RevokeStatus = HttpStatusCode.OK;
        using var channel = ovc.Channel();
        using (await channel.GetAsync("/api/hosts", CancellationToken.None))
        {
        }

        Assert.Null(await channel.RevokeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_is_sent_to_revoke_a_token_never_taken()
    {
        var ovc = FakeOvc.FromFixtures();
        using var channel = ovc.Channel();

        Assert.Null(await channel.RevokeAsync(CancellationToken.None));
        Assert.Empty(ovc.Requests);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_a_failure_not_an_empty_success()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.RawBody = "<html>maintenance</html>";
        using var channel = ovc.Channel();

        var thrown = await Assert.ThrowsAsync<SimplivityApiException>(
            () => channel.GetAsync("/api/hosts", CancellationToken.None));

        Assert.Equal(CollectionFailureKind.ProtocolError, thrown.Kind);
    }
}
