using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// Walks a Redfish <c>Systems/1/Storage</c> collection the way DMTF's own
/// collection pattern requires: <c>Members[].@odata.id</c> is the only
/// reliable path to a storage controller, and each controller's
/// <c>Drives[].@odata.id</c> is the only reliable path to a drive. Fixed ids
/// (<c>Storage/1</c>) do not hold on HPE iLO, which names controllers
/// <c>Storage/DE00A000</c>-style.
/// </summary>
/// <remarks>
/// Read-only: <paramref name="read"/> is whatever GET the caller already
/// has -- a live <c>RedfishClient</c> call in production, a dictionary
/// lookup over bundled JSON in <c>--dry</c> and in tests. The walk itself
/// does not know or care which.
/// </remarks>
internal static class StorageWalker
{
    /// <summary>
    /// A generous but finite number of links this probe will follow in one
    /// walk -- a misbehaving or very large array should not turn a probe run
    /// into an unbounded crawl.
    /// </summary>
    public const int MaxFollowedLinks = 64;

    public sealed record Result(
        IReadOnlyList<JsonElement> Controllers,
        IReadOnlyList<JsonElement> Drives,
        int LinksFollowed,
        bool Capped);

    public static async Task<Result> WalkAsync(
        JsonElement? collection,
        Func<string, Task<JsonElement?>> read,
        CancellationToken cancellationToken)
    {
        var controllers = new List<JsonElement>();
        var drives = new List<JsonElement>();
        var linksFollowed = 0;
        var capped = false;

        if (collection is { } coll &&
            coll.TryGetProperty("Members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linksFollowed >= MaxFollowedLinks)
                {
                    capped = true;
                    break;
                }

                if (!TryId(member, out var controllerPath))
                {
                    continue;
                }

                linksFollowed++;

                if (await read(controllerPath) is not { } controller)
                {
                    continue;
                }

                controllers.Add(controller);

                if (!controller.TryGetProperty("Drives", out var driveLinks) ||
                    driveLinks.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var driveLink in driveLinks.EnumerateArray())
                {
                    if (linksFollowed >= MaxFollowedLinks)
                    {
                        capped = true;
                        break;
                    }

                    if (!TryId(driveLink, out var drivePath))
                    {
                        continue;
                    }

                    linksFollowed++;

                    if (await read(drivePath) is { } drive)
                    {
                        drives.Add(drive);
                    }
                }
            }
        }

        return new Result(controllers, drives, linksFollowed, capped);
    }

    private static bool TryId(JsonElement link, out string path)
    {
        path = link.ValueKind == JsonValueKind.Object &&
               link.TryGetProperty("@odata.id", out var id) &&
               id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;

        return path.Length > 0;
    }
}
