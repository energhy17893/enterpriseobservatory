using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

namespace EnterpriseObservatory.RedfishProbe.Tests;

/// <summary>
/// Proves <see cref="StorageWalker"/> follows a collection's own
/// <c>Members[].@odata.id</c> rather than assuming a fixed id -- the gap a
/// live run hit: HPE iLO names a controller <c>Storage/DE00A000</c>, and the
/// fixed <c>Storage/1</c> path this probe used to GET came back 404.
/// </summary>
public sealed class StorageWalkerTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Redfish", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Follows_a_non_1_member_id_from_the_collection_rather_than_a_fixed_path()
    {
        // storage-collection.json's only member is Storage/DE00A000 -- not
        // Storage/1. A walker that guessed "1" would never resolve this.
        var collection = Load("storage-collection.json");
        var controller = Load("storage.json");
        var drive = Load("drive-1.json");

        Task<JsonElement?> Read(string path) => Task.FromResult<JsonElement?>(
            path == "/redfish/v1/Systems/1/Storage/DE00A000" ? controller
            : path.Contains("/Drives/", StringComparison.Ordinal) ? drive
            : null);

        var result = await StorageWalker.WalkAsync(collection, Read, CancellationToken.None);

        Assert.Single(result.Controllers);
        // storage.json declares 4 Drives[] links; drive-1.json answers all of them.
        Assert.Equal(4, result.Drives.Count);
        Assert.False(result.Capped);
    }

    [Fact]
    public async Task Stops_at_the_link_cap_and_reports_it_was_capped()
    {
        var members = Enumerable.Range(0, StorageWalker.MaxFollowedLinks + 10)
            .Select(i => $$"""{ "@odata.id": "/redfish/v1/Systems/1/Storage/C{{i}}" }""");
        var collection = JsonDocument.Parse(
            $$"""{ "Members": [{{string.Join(",", members)}}] }""").RootElement.Clone();

        var controller = JsonDocument.Parse("""{ "Drives": [] }""").RootElement.Clone();
        var followed = 0;

        Task<JsonElement?> Read(string path)
        {
            followed++;
            return Task.FromResult<JsonElement?>(controller);
        }

        var result = await StorageWalker.WalkAsync(collection, Read, CancellationToken.None);

        Assert.True(result.Capped);
        Assert.Equal(StorageWalker.MaxFollowedLinks, result.LinksFollowed);
        Assert.Equal(StorageWalker.MaxFollowedLinks, followed);
    }

    [Fact]
    public async Task Missing_collection_walks_to_nothing_without_throwing()
    {
        var result = await StorageWalker.WalkAsync(null, _ => Task.FromResult<JsonElement?>(null), CancellationToken.None);

        Assert.Empty(result.Controllers);
        Assert.Empty(result.Drives);
        Assert.False(result.Capped);
    }
}
