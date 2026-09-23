using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// <c>--backup-paging</c>: does offset paging over <c>/api/backups</c> lose a
/// VM's newest PROTECTED backup between two back-to-back reads?
/// </summary>
/// <remarks>
/// Measures the backup-freshness flap (24 September 2026): the collector
/// (SimplivityInventorySource.ReadAllAsync) pages <c>limit=500&amp;offset=</c>
/// until <c>count</c>, and Newest() keeps the newest PROTECTED row per VM from
/// whatever rows arrived. Reads the collector's exact query twice, then the
/// two candidate fixes, and compares each read's newest-per-VM with the
/// newest any read saw that was created before that read began. Counts only:
/// no VM name or id is printed. GETs only.
/// </remarks>
internal static class BackupPaging
{
    private const int Limit = 500;

    private sealed record Read(
        string Label,
        DateTimeOffset StartedUtc,
        TimeSpan Elapsed,
        int Pages,
        int Rows,
        int DistinctIds,
        IReadOnlyList<int?> Counts,
        int OrderBreaks,
        int BoundaryTies,
        Dictionary<string, DateTimeOffset> Newest,
        List<(string Vm, DateTimeOffset At)> Protected,
        bool Complete);

    public static async Task<int> RunAsync(Func<string, Task<JsonElement?>> get)
    {
        var reads = new List<Read>
        {
            await ReadAsync(get, "A collector   ", "/api/backups?show_optional_fields=true"),
            await ReadAsync(get, "B collector   ", "/api/backups?show_optional_fields=true"),
            await ReadAsync(get, "C sort asc    ", "/api/backups?show_optional_fields=true&sort=created_at&order=ascending"),
            await ReadAsync(get, "D PROTECTED   ", "/api/backups?show_optional_fields=true&state=PROTECTED"),
            await ReadAsync(get, "E asc+PROTECT ", "/api/backups?show_optional_fields=true&state=PROTECTED&sort=created_at&order=ascending"),
        };

        Console.WriteLine("=== --backup-paging: per read ===");
        Console.WriteLine("  read            pages  rows  distinct  count(first..last)   order-breaks  boundary-ties  VMs  secs  complete");

        foreach (var r in reads)
        {
            var counts = r.Counts.Count == 0
                ? "-"
                : $"{Show(r.Counts[0])}..{Show(r.Counts[^1])}" + (r.Counts.Distinct().Count() > 1 ? " (changed)" : string.Empty);
            Console.WriteLine(
                $"  {r.Label} {r.Pages,6} {r.Rows,6} {r.DistinctIds,9}  {counts,-20} {r.OrderBreaks,13} {r.BoundaryTies,14} " +
                $"{r.Newest.Count,5} {r.Elapsed.TotalSeconds,5:0.0}  {(r.Complete ? "yes" : "NO")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== newest PROTECTED per VM vs the newest any read saw, created before this read began ===");
        Console.WriteLine("  read            VMs-missing  VMs-older  (of VMs with any PROTECTED backup)");

        foreach (var r in reads)
        {
            var truth = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach (var (vm, at) in reads.SelectMany(x => x.Protected).Where(p => p.At < r.StartedUtc))
            {
                if (!truth.TryGetValue(vm, out var seen) || at > seen)
                {
                    truth[vm] = at;
                }
            }

            var missing = truth.Keys.Count(vm => !r.Newest.ContainsKey(vm));
            var older = truth.Count(t => r.Newest.TryGetValue(t.Key, out var mine) && mine < t.Value);
            Console.WriteLine($"  {r.Label} {missing,12} {older,10}  ({truth.Count})");
        }

        var (a, b) = (reads[0], reads[1]);
        var differ = a.Newest.Keys.Union(b.Newest.Keys, StringComparer.Ordinal)
            .Count(vm => !a.Newest.TryGetValue(vm, out var x) || !b.Newest.TryGetValue(vm, out var y) || x != y);
        var newer = b.Protected.Count(p => p.At >= a.StartedUtc);
        Console.WriteLine();
        Console.WriteLine($"  A vs B: {differ} VMs' newest differs; {newer} PROTECTED rows in B created after A began");

        Console.WriteLine();
        Console.WriteLine("=== largest page served (offset=0) ===");

        foreach (var limit in new[] { 1000, 2000, 5000 })
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await get($"/api/backups?show_optional_fields=true&limit={limit}&offset=0");
            var rows = page is { } p && p.TryGetProperty("backups", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.GetArrayLength().ToString(CultureInfo.InvariantCulture)
                : "not read";
            Console.WriteLine($"  limit={limit,-5} rows {rows,-9} {stopwatch.Elapsed.TotalSeconds:0.0} s");
        }

        return 0;
    }

    private static string Show(int? count) => count?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary>The collector's paging, exactly: limit 500, offset += rows, stop at count or a short page.</summary>
    private static async Task<Read> ReadAsync(Func<string, Task<JsonElement?>> get, string label, string path)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var rows = new List<JsonElement>();
        var counts = new List<int?>();
        var pageStarts = new List<int>();
        var complete = true;

        for (var offset = 0; ;)
        {
            if (await get($"{path}&limit={Limit}&offset={offset}") is not { } root ||
                !root.TryGetProperty("backups", out var page) || page.ValueKind != JsonValueKind.Array)
            {
                complete = false;
                break;
            }

            pageStarts.Add(rows.Count);
            rows.AddRange(page.EnumerateArray().Select(e => e.Clone()));
            var n = page.GetArrayLength();
            offset += n;

            int? count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var total) ? total : null;
            counts.Add(count);

            if (n == 0 || (count is { } t ? offset >= t : n < Limit))
            {
                break;
            }
        }

        stopwatch.Stop();

        var created = rows.Select(r => At(r, "created_at")).ToList();

        // Adjacent rows out of either direction's order: 0 in a sorted read.
        var descending = created.Zip(created.Skip(1)).Count(p => p.First is { } x && p.Second is { } y && y > x);
        var ascending = created.Zip(created.Skip(1)).Count(p => p.First is { } x && p.Second is { } y && y < x);

        // Rows sharing created_at across a page boundary: an order that ties
        // there is not stable between two queries, so the boundary may cut
        // the tie group differently each read.
        var ties = pageStarts.Skip(1)
            .Where(i => i > 0 && i < created.Count && created[i] is { } at && created[i - 1] == at)
            .Sum(i => created.Count(x => x == created[i]));

        var protectedRows = new List<(string, DateTimeOffset)>();
        var newest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (Text(row, "state") != "PROTECTED" || Text(row, "virtual_machine_id") is not { } vm ||
                At(row, "created_at") is not { } at)
            {
                continue;
            }

            protectedRows.Add((vm, at));

            if (!newest.TryGetValue(vm, out var seen) || at > seen)
            {
                newest[vm] = at;
            }
        }

        return new Read(
            label,
            started,
            stopwatch.Elapsed,
            pageStarts.Count,
            rows.Count,
            rows.Select(r => Text(r, "id")).Distinct(StringComparer.Ordinal).Count(),
            counts,
            Math.Min(descending, ascending),
            ties,
            newest,
            protectedRows,
            complete);
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? At(JsonElement row, string name) =>
        DateTimeOffset.TryParse(Text(row, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
}
