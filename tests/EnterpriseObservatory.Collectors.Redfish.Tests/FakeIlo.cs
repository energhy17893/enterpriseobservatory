using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Redfish;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Redfish.Tests;

/// <summary>
/// An iLO 5 answering from the recorded, masked Kibar dump
/// (Fixtures/ilo5-live-masked). Each GET path serves its recorded reply;
/// the IML answers its <c>$filter=Created gt '…'</c> by filtering the
/// recorded entries, as the live iLO did.
/// </summary>
internal sealed class FakeIlo : HttpMessageHandler
{
    public static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ilo5-live-masked");

    private readonly Lock _gate = new();

    public FakeIlo()
    {
        foreach (var entry in JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "index.json")))!.AsArray())
        {
            if (entry!["file"]?.GetValue<string>() is { } file)
            {
                // The IML's $top page and its $filter reply share a path; the
                // later (the $filter reply, 196 entries) wins.
                var path = entry["path"]!.GetValue<string>().Split('?')[0];
                Docs[path] = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)))!;
            }
        }
    }

    /// <summary>Replies by path, without query. Tests edit these.</summary>
    public Dictionary<string, JsonNode> Docs { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public TimeSpan Delay { get; init; }

    public JsonNode System => Docs[RedfishInventorySource.SystemPath];

    public JsonNode Power => Docs[RedfishInventorySource.PowerPath];

    public JsonNode Thermal => Docs[RedfishInventorySource.ThermalPath];

    public string Uuid => System["UUID"]!.GetValue<string>();

    public string Serial => System["SerialNumber"]!.GetValue<string>();

    public RedfishChannel Channel() => new(this, new RedfishConnectionOptions
    {
        InstanceId = "ilo-test",
        BaseAddress = new Uri("https://ilo.test.local"),
        Username = "observatory",
        Password = Secret.From("hunter2"),
    }, new SourceRequestGate(RedfishChannel.Parallelism));

    public RedfishInventorySource Source(IRedfishFoldingDirectory directory, IClock clock) =>
        new("ilo-test", Channel(), directory, clock);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, CancellationToken.None);
        }

        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);

        lock (_gate)
        {
            Requests.Add($"{request.Method} {path}{query}");
        }

        if (request.Headers.Authorization is not { Scheme: "Basic" } auth ||
            Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)) != "observatory:hunter2")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (!Docs.TryGetValue(path, out var doc))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (path == RedfishInventorySource.ImlPath && query.Split("Created gt '") is [_, var rest])
        {
            var after = DateTimeOffset.Parse(rest.TrimEnd('\''), CultureInfo.InvariantCulture);
            var members = doc["Members"]!.AsArray()
                .Where(m => DateTimeOffset.Parse(m!["Created"]!.GetValue<string>(), CultureInfo.InvariantCulture) > after)
                .Select(m => m!.DeepClone())
                .ToArray();
            doc = new JsonObject { ["Members"] = new JsonArray(members), ["Members@odata.count"] = members.Length };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(doc.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>ESXi hosts by hardware UUID (any case) and serial, as the graph would answer.</summary>
internal sealed class FakeDirectory : IRedfishFoldingDirectory
{
    public Dictionary<string, EntityId> Uuids { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, EntityId> Serials { get; } = new(StringComparer.OrdinalIgnoreCase);

    public EntityId? HostByHardwareUuid(string uuid) => Uuids.TryGetValue(uuid.Trim(), out var id) ? id : null;

    public EntityId? HostBySerialNumber(string serial) => Serials.TryGetValue(serial.Trim(), out var id) ? id : null;
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
}
