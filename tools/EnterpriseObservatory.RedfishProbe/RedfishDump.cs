using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// <c>--dump &lt;dir&gt;</c>: the raw JSON of exactly the endpoints the M6.1
/// collector reads, masked, one file per GET, so the collector's fixtures
/// are recorded responses rather than shapes written from memory (#135
/// recorded shapes only).
/// </summary>
/// <remarks>
/// Every property name, every non-identifying value and every JSON type is
/// kept exactly as received; only identifying string values change (see
/// <see cref="DumpMasker"/>). An <c>index.json</c> records each path, HTTP
/// status and elapsed time.
/// </remarks>
internal static class RedfishDump
{
    public const int MaxFollowedLinks = 64;

    public static async Task<int> RunAsync(
        RedfishClient client, string directory, string connectionHost, CancellationToken cancellationToken)
    {
        var raw = new List<(string Path, JsonNode? Body, int? Status, double Ms, string? Error)>();
        var followed = 0;

        async Task<JsonNode?> GetAsync(string path)
        {
            var read = await client.GetAsync(path, cancellationToken);
            var body = read.Ok ? JsonNode.Parse(read.Document!.RootElement.GetRawText()) : null;
            raw.Add((path, body, read.StatusCode, read.Elapsed.TotalMilliseconds, read.Error));
            Console.WriteLine($"  {path,-72} {(read.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? read.Error ?? "no response")}  {read.Elapsed.TotalMilliseconds:F0} ms");
            return body;
        }

        // Follows Members[].@odata.id only where a member is a bare link --
        // an inline member (iLO 5's IML) is already in the collection body.
        async Task<List<JsonNode>> MembersAsync(JsonNode? collection)
        {
            var result = new List<JsonNode>();
            foreach (var member in (collection?["Members"] as JsonArray) ?? [])
            {
                if (member is JsonObject { Count: 1 } link && link["@odata.id"]?.GetValue<string>() is { Length: > 0 } path &&
                    followed++ < MaxFollowedLinks && await GetAsync(path) is { } body)
                {
                    result.Add(body);
                }
            }

            return result;
        }

        // Per cycle (§10.7).
        await GetAsync("/redfish/v1/Systems/1");
        await GetAsync("/redfish/v1/Chassis/1/Power");
        await GetAsync("/redfish/v1/Chassis/1/Thermal");

        // Deep walk.
        foreach (var controller in await MembersAsync(await GetAsync("/redfish/v1/Systems/1/Storage")))
        {
            foreach (var drive in (controller["Drives"] as JsonArray) ?? [])
            {
                if (drive?["@odata.id"]?.GetValue<string>() is { Length: > 0 } path && followed++ < MaxFollowedLinks)
                {
                    await GetAsync(path);
                }
            }
        }

        await MembersAsync(await GetAsync("/redfish/v1/Systems/1/Memory"));
        await MembersAsync(await GetAsync("/redfish/v1/UpdateService/FirmwareInventory"));

        // IML: a small page, then the watermark query the collector will
        // issue, so whether iLO 5 honours $filter is recorded, not assumed.
        const string iml = "/redfish/v1/Systems/1/LogServices/IML/Entries";
        var page = await GetAsync(iml + "?$top=5");
        await MembersAsync(page);
        var watermark = ((page?["Members"] as JsonArray) ?? [])
            .Select(m => m?["Created"]?.GetValue<string>())
            .Where(c => c is not null)
            .Max(StringComparer.Ordinal);
        if (watermark is not null)
        {
            await GetAsync($"{iml}?$filter={Uri.EscapeDataString($"Created gt '{watermark}'")}");
        }

        if (followed > MaxFollowedLinks)
        {
            Console.WriteLine($"  link walk capped at {MaxFollowedLinks}");
        }

        var masker = new DumpMasker();
        masker.AddHost(connectionHost);
        foreach (var entry in raw)
        {
            masker.Collect(entry.Body);
        }

        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var index = new JsonArray();

        foreach (var entry in raw)
        {
            var file = FileNameOf(masker.MaskText(entry.Path));
            if (entry.Body is { } body)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, file), masker.Apply(body).ToJsonString(options) + "\n", cancellationToken);
            }

            index.Add(new JsonObject
            {
                ["path"] = masker.MaskText(entry.Path),
                ["file"] = entry.Body is null ? null : file,
                ["status"] = entry.Status,
                ["elapsedMs"] = Math.Round(entry.Ms),
                ["error"] = entry.Error,
            });
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "index.json"), index.ToJsonString(options) + "\n", cancellationToken);
        Console.WriteLine($"=== {raw.Count(r => r.Body is not null)} of {raw.Count} responses written to {Path.GetFullPath(directory)} ===");
        return raw.Any(r => r.Body is null) ? 1 : 0;
    }

    /// <summary>
    /// <c>--remask &lt;in&gt; &lt;out&gt;</c>: runs an existing dump through
    /// the masker again, offline -- for a masking rule added after the dump
    /// was taken. Already-masked values are simply masked once more.
    /// </summary>
    public static int Remask(string input, string output)
    {
        var files = Directory.GetFiles(input, "*.json")
            .ToDictionary(f => Path.GetFileName(f), f => JsonNode.Parse(File.ReadAllText(f))!, StringComparer.Ordinal);
        var masker = new DumpMasker();
        foreach (var node in files.Values)
        {
            masker.Collect(node);
        }

        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true };
        foreach (var (name, node) in files)
        {
            File.WriteAllText(Path.Combine(output, name), masker.Apply(node).ToJsonString(options) + "\n");
        }

        Console.WriteLine($"=== {files.Count} files re-masked into {Path.GetFullPath(output)} ===");
        return 0;
    }

    /// <summary><c>/redfish/v1/Systems/1/Storage/DE07C000</c> → <c>Systems_1_Storage_DE07C000.json</c>.</summary>
    internal static string FileNameOf(string path)
    {
        var trimmed = path.StartsWith("/redfish/v1/", StringComparison.Ordinal) ? path["/redfish/v1/".Length..] : path;
        var query = trimmed.IndexOf('?', StringComparison.Ordinal);
        var name = query < 0 ? trimmed : trimmed[..query] + (trimmed[(query + 1)..].StartsWith("$top", StringComparison.Ordinal) ? "_top" : "_filter");
        return Regex.Replace(name.TrimEnd('/'), "[^A-Za-z0-9.-]+", "_") + ".json";
    }
}

/// <summary>
/// Replaces identifying values with stable fakes of the same format, keeping
/// every property name and JSON type. Stable within one dump (the same serial
/// in Systems/1 and an IML message becomes the same fake), salted per run so
/// a fake cannot be brute-forced back to a short serial.
/// </summary>
/// <remarks>
/// Serials, UUIDs, WWNs (DurableName), asset tags and MACs keep their
/// character classes (digit → digit, hex letter → hex letter, case kept);
/// IPv4 → 192.0.2.x, IPv6 → 2001:db8::x; host names → <c>host-xxxxxx</c>
/// labels under <c>example.invalid</c>. Every collected original is then
/// replaced wherever it appears inside any other string.
/// </remarks>
internal sealed partial class DumpMasker
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void AddHost(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            Remember(host, FakeIp(host));
            return;
        }

        var dot = host.IndexOf('.', StringComparison.Ordinal);
        var label = dot < 0 ? host : host[..dot];
        Remember(label, "host-" + Hex(label, 6));
        if (dot >= 0)
        {
            Remember(host[(dot + 1)..], "example.invalid");
        }
    }

    public void Collect(JsonNode? node, string? property = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    Collect(child, name);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Collect(child, property);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0:
                CollectString(property ?? string.Empty, text);
                break;
        }
    }

    public JsonNode Apply(JsonNode node, string? property = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[name] = MaskText(text, name);
                    }
                    else if (child is not null)
                    {
                        Apply(child, name);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = MaskText(text, property);
                    }
                    else if (array[i] is { } child)
                    {
                        Apply(child, property);
                    }
                }

                break;
        }

        return node;
    }

    public string MaskText(string text, string? property = null)
    {
        if (_map.TryGetValue(text, out var whole))
        {
            return whole;
        }

        // Patterns first, on the original text, so no fake is masked twice.
        // Embedded: a GPT partition GUID and a bare MAC inside a UEFI device
        // path (Systems/1 Boot.UefiTargetBootSourceOverride, live iLO 5).
        text = EmbeddedUuid().Replace(text, m => Preserve(m.Value));
        text = Mac().Replace(text, m => Preserve(m.Value));
        text = UefiMac().Replace(text, m => Preserve(m.Value));

        // A dotted-quad firmware version ("10.54.7.0") is not an address.
        if (property is null || !Contains(property, "Version"))
        {
            text = Ipv4().Replace(text, m => FakeIp(m.Value));
        }

        // Longest first, so a FQDN is replaced before its own first label.
        foreach (var (original, fake) in _map.OrderByDescending(p => p.Key.Length))
        {
            text = text.Replace(original, fake, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private void CollectString(string property, string text)
    {
        if (Contains(property, "HostName") || Contains(property, "FQDN") || Contains(property, "DomainName"))
        {
            AddHost(text);
        }
        else if (Contains(property, "Serial") || Contains(property, "UUID") || Contains(property, "DurableName") ||
                 Contains(property, "AssetTag") || Contains(property, "WWN") || Uuid().IsMatch(text) || Mac().IsMatch(text))
        {
            Remember(text, Preserve(text));
        }
        else if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Remember(text, FakeIp(text));
        }
    }

    private void Remember(string original, string fake)
    {
        // Two- and three-character values ("OK", "1") would turn every
        // replacement pass into noise; nothing identifying is that short.
        if (original.Length >= 4)
        {
            _map.TryAdd(original, fake);
        }
    }

    private string FakeIp(string ip)
    {
        var h = Hash(ip);
        return ip.Contains(':', StringComparison.Ordinal)
            ? $"2001:db8::{h[0]:x2}{h[1]:x2}"
            : $"192.0.2.{1 + (h[0] % 254)}";
    }

    private string Preserve(string value)
    {
        var h = Hash(value);
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var b = h[i % h.Length] ^ (i / h.Length * 31);
            chars[i] = chars[i] switch
            {
                >= '0' and <= '9' => (char)('0' + (b % 10)),
                >= 'a' and <= 'f' => (char)('a' + (b % 6)),
                >= 'A' and <= 'F' => (char)('A' + (b % 6)),
                >= 'a' and <= 'z' => (char)('a' + (b % 26)),
                >= 'A' and <= 'Z' => (char)('A' + (b % 26)),
                _ => chars[i],
            };
        }

        return new string(chars);
    }

    private string Hex(string value, int length) => Convert.ToHexStringLower(Hash(value))[..length];

    private byte[] Hash(string value) =>
        HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value.ToUpperInvariant()));

    private static bool Contains(string property, string part) =>
        property.Contains(part, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b")]
    private static partial Regex EmbeddedUuid();

    [GeneratedRegex(@"(?<=MAC\()[0-9A-Fa-f]{12}")]
    private static partial Regex UefiMac();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b")]
    private static partial Regex Ipv4();
}
