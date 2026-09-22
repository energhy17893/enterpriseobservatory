using System.Globalization;
using System.Xml.Linq;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// Whether a host's real-time sample is complete the first time it is returned.
/// </summary>
/// <remarks>
/// The H3 live read starts each entity at its newest stored sample
/// (exclusive), so a slot is read once. That is only right if every series of
/// the entity already carries its value for the newest slot when the slot
/// first appears. This reads the same hosts repeatedly (maxSample 6, no
/// window) and, per sample time, counts the series whose point for that time
/// was absent (a series shorter than the sample list) or -1 when the time was
/// first seen and a real value on a later read. Read-only: every call is a
/// QueryPerf. Counter names only; no object names are printed.
/// </remarks>
internal static class LateSamples
{
    public static async Task<int> RunAsync(
        VsphereClient client,
        VsphereInventoryPayload payload,
        IReadOnlyList<VsphereCounter> catalog,
        int seconds,
        CancellationToken cancellationToken,
        bool fast = false)
    {
        var byKey = VsphereCounterIndex.ByKey(catalog);
        var byId = catalog.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var hosts = payload.Hosts.Select(h => h.MoRef).ToList();

        var available = new HashSet<string>(
            await client.GetAvailableCounterKeysAsync(
                hosts[0], VsphereEntityType.HostSystem, DateTimeOffset.UtcNow, cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var counters = VsphereCounters.Host
            .Where(k => byKey.ContainsKey(k) && available.Contains(k))
            .Select(k => byKey[k])
            .ToList();

        Console.WriteLine($"  hosts                    {hosts.Count}, counters {counters.Count}, for {seconds} s, every 10 s");

        // (host, time, series) -> state at first sight and whether it later had a value.
        var first = new Dictionary<(string Host, DateTimeOffset At, string Series), string>();
        var laterValid = new HashSet<(string, DateTimeOffset, string)>();
        var firstSeenAt = new Dictionary<(string, DateTimeOffset), DateTimeOffset>();

        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < until)
        {
          foreach (var one in fast ? hosts.Chunk(7).Select(b => b.ToList()) : hosts.Select(h => new List<string> { h }))
          {
            var (body, _) = await client.QueryPerfForMeasurementAsync(
                one, VsphereEntityType.HostSystem, counters, fast ? 3 : 6, null, cancellationToken);
            var readAt = fast ? await client.GetServerTimeAsync(cancellationToken) ?? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow;

            foreach (var entity in XDocument.Parse(body).Descendants().Where(e => e.Name.LocalName == "returnval"))
            {
                var host = Md5(Child(entity, "entity") ?? "?");
                var times = entity.Elements().Where(e => e.Name.LocalName == "sampleInfo")
                    .Select(s => DateTimeOffset.Parse(Child(s, "timestamp")!, CultureInfo.InvariantCulture))
                    .ToList();

                foreach (var at in times)
                {
                    firstSeenAt.TryAdd((host, at), readAt);
                }

                foreach (var series in entity.Elements().Where(e => e.Name.LocalName == "value"))
                {
                    var id = series.Elements().First(e => e.Name.LocalName == "id");
                    var counterId = int.Parse(Child(id, "counterId")!, CultureInfo.InvariantCulture);
                    var name = byId.TryGetValue(counterId, out var c) ? c.Key : counterId.ToString(CultureInfo.InvariantCulture);
                    var instance = Child(id, "instance") ?? string.Empty;
                    var key = $"{name}|{(instance.Length == 0 ? "agg" : "inst")}|{instance.GetHashCode(StringComparison.Ordinal):x8}";
                    var points = series.Elements().Where(e => e.Name.LocalName is "value" or "long")
                        .Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();

                    for (var slot = 0; slot < times.Count; slot++)
                    {
                        // Points line up with the times from the END when shorter? Unknown: record both.
                        var state = points.Count != times.Count
                            ? $"short({points.Count}/{times.Count})"
                            : points[slot] < 0 ? "neg" : "ok";
                        var k = (host, times[slot], key);
                        if (!first.ContainsKey(k))
                        {
                            first[k] = state;
                        }
                        else if (state == "ok")
                        {
                            laterValid.Add(k);
                        }
                    }
                }
            }

          }

            await Task.Delay(TimeSpan.FromSeconds(fast ? 0.5 : 10), cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine("  per sample time: series first seen not-ok, and of those later ok (by group)");
        foreach (var group in first.GroupBy(p => (p.Key.At, p.Key.Host)).OrderBy(g => g.Key.Host).ThenBy(g => g.Key.At))
        {
            var bad = group.Where(p => p.Value != "ok").ToList();
            var healed = bad.Where(p => laterValid.Contains(p.Key)).ToList();
            if (fast && bad.Count == 0)
            {
                continue;
            }

            var byGroup = string.Join(", ", healed
                .GroupBy(p => p.Key.Series.Split('|')[0].Split('.')[0] + "/" + p.Key.Series.Split('|')[1] + "/" + p.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}"));
            Console.WriteLine(FormattableString.Invariant(
                $"    {group.Key.Host} {group.Key.At:HH:mm:ss}Z  series {group.Count(),5}  first-not-ok {bad.Count,4}  later-ok {healed.Count,4}  {byGroup}"));
        }

        Console.WriteLine();
        Console.WriteLine("  delay between a sample time and the read that first returned it (s)");
        foreach (var group in firstSeenAt.GroupBy(p => p.Key.Item2).OrderBy(g => g.Key))
        {
            Console.WriteLine(FormattableString.Invariant(
                $"    {group.Key:HH:mm:ss}Z  {string.Join(" ", group.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).Select(p => p.Key.Item1[..4] + "=" + (p.Value - p.Key.Item2).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)))}"));
        }

        return 0;
    }

    /// <summary>
    /// The live read as the source makes it: per host, (mark, server now],
    /// the mark moved to the newest time returned. Prints every reply that
    /// returned a time, and what the parser made of it.
    /// </summary>
    public static async Task<int> FollowAsync(
        VsphereClient client,
        VsphereInventoryPayload payload,
        IReadOnlyList<VsphereCounter> catalog,
        int seconds,
        CancellationToken cancellationToken)
    {
        var byKey = VsphereCounterIndex.ByKey(catalog);
        var hosts = payload.Hosts.Select(h => h.MoRef).ToList();
        var available = new HashSet<string>(
            await client.GetAvailableCounterKeysAsync(
                hosts[0], VsphereEntityType.HostSystem, DateTimeOffset.UtcNow, cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var counters = VsphereCounters.Host
            .Where(k => byKey.ContainsKey(k) && available.Contains(k))
            .Select(k => byKey[k])
            .ToList();

        var marks = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < until)
        {
            var serverNow = await client.GetServerTimeAsync(cancellationToken) ?? DateTimeOffset.UtcNow;
            foreach (var batch in hosts.Chunk(7))
            {
                var targets = batch.Select(h => new PerfQueryTarget(
                    h,
                    marks.TryGetValue(h, out var m) && m > serverNow.AddMinutes(-2) ? m : serverNow.AddMinutes(-2),
                    serverNow)).ToList();
                var samples = await client.QueryPerfWindowsAsync(
                    targets, VsphereEntityType.HostSystem, counters, cancellationToken);

                foreach (var target in targets)
                {
                    var got = samples.Where(s => s.EntityMoRef == target.MoRef).ToList();
                    var line = got.Count == 0
                        ? "no returnval"
                        : string.Join(" | ", got.Select(s =>
                            string.Join(" ", s.Earlier.Select(e => FormattableString.Invariant($"{e.SampledAtUtc:HH:mm:ss}({e.Values.Count})"))) +
                            FormattableString.Invariant($" latest {s.SampledAtUtc:HH:mm:ss}({s.Values.Count})")));
                    var mark = marks.TryGetValue(target.MoRef, out var old) ? old : (DateTimeOffset?)null;
                    var newest = got.Select(s => s.SampledAtUtc).Where(t => t is not null).Max();
                    if (newest is { } n && (mark is null || n > mark))
                    {
                        marks[target.MoRef] = n;
                    }

                    if (newest is not null && newest != mark)
                    {
                        Console.WriteLine(FormattableString.Invariant(
                            $"    {Md5(target.MoRef)} server {serverNow:HH:mm:ss.f} ({target.StartExclusiveUtc:HH:mm:ss.f}, {target.EndInclusiveUtc:HH:mm:ss.f}]  {line}"));
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return 0;
    }

    // Not security: the same short fingerprint the store query uses to name a
    // host without printing it.
#pragma warning disable CA5351
    private static string Md5(string moRef) =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(moRef)))[..8];
#pragma warning restore CA5351

    private static string? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
