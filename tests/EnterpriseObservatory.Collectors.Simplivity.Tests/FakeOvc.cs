using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Simplivity.Tests;

/// <summary>
/// An OmniStack REST endpoint in memory: OAuth token and revoke, and
/// <c>limit</c>/<c>offset</c>/<c>count</c> paging over whatever collections a
/// test loads.
/// </summary>
internal sealed class FakeOvc : HttpMessageHandler
{
    public const string VcenterUuid = "5029a1b2-c3d4-4e5f-8a9b-0c1d2e3f4a5b";
    public const string Vcenter = "vc-kibar";

    private readonly Lock _gate = new();
    private string _valid = "none";

    public Dictionary<string, List<JsonNode>> Collections { get; } = new(StringComparer.Ordinal)
    {
        ["hosts"] = [],
        ["omnistack_clusters"] = [],
        ["virtual_machines"] = [],
        ["backups"] = [],
    };

    public List<string> Requests { get; } = [];

    public int TokenPosts { get; private set; }

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.Unauthorized;

    /// <summary>Refuse every bearer token, fresh or not.</summary>
    public bool RefuseAllTokens { get; set; }

    /// <summary>Answer this raw body to every GET instead.</summary>
    public string? RawBody { get; set; }

    /// <summary>Delay every reply, ignoring cancellation.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Kills the current token, as the 10-minute idle limit does.</summary>
    public void ExpireToken()
    {
        lock (_gate)
        {
            _valid = "expired";
        }
    }

    public static FakeOvc FromFixtures()
    {
        var ovc = new FakeOvc();

        foreach (var name in ovc.Collections.Keys.ToList())
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!;
            ovc.Collections[name] = [.. json[name]!.AsArray().Select(n => n!.DeepClone())];
        }

        return ovc;
    }

    /// <summary><paramref name="count"/> hosts shaped like the fixture's first, <c>host-1</c>…</summary>
    public static List<JsonNode> Hosts(int count, string uuid = VcenterUuid)
    {
        var template = FromFixtures().Collections["hosts"][0];

        return [.. Enumerable.Range(1, count).Select(i =>
        {
            var host = template.DeepClone();
            host["id"] = $"svt-host-{i}";
            host["name"] = $"esx{i:00}.kibar.local";
            host["hypervisor_object_id"] = $"{uuid}:HostSystem:host-{i}";
            return host;
        })];
    }

    public SimplivitySessionChannel Channel() => new(this, new SimplivityConnectionOptions
    {
        InstanceId = "svt-kibar",
        BaseAddress = new Uri("https://ovc.example.local"),
        Username = "observatory@vsphere.local",
        Password = Secret.From("hunter2"),
    });

    public SimplivityInventorySource Source(FakeDirectory directory) =>
        new("svt-kibar", Channel(), directory, new FixedClock());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, CancellationToken.None);
        }

        var path = request.RequestUri!.AbsolutePath;

        lock (_gate)
        {
            Requests.Add($"{request.Method} {request.RequestUri.PathAndQuery}");

            if (path == "/api/oauth/token")
            {
                TokenPosts++;

                if (TokenStatus != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(TokenStatus);
                }

                _valid = $"token-{TokenPosts}";
                return Json(new JsonObject { ["access_token"] = _valid, ["token_type"] = "bearer" });
            }

            if (path == "/api/oauth/revoke")
            {
                return new HttpResponseMessage(RevokeStatus);
            }

            if (RefuseAllTokens || request.Headers.Authorization?.Parameter != _valid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
        }

        if (RawBody is { } raw)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) };
        }

        var name = path["/api/".Length..];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var limit = int.Parse(query["limit"] ?? "500", System.Globalization.CultureInfo.InvariantCulture);
        var offset = int.Parse(query["offset"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var all = Collections[name];

        return Json(new JsonObject
        {
            [name] = new JsonArray([.. all.Skip(offset).Take(limit).Select(n =>
                string.Equals(query["show_optional_fields"], "true", StringComparison.Ordinal)
                    ? n.DeepClone()
                    : DefaultShaped(name, n))]),
            ["count"] = all.Count,
            ["limit"] = limit,
            ["offset"] = offset,
        });
    }

    /// <summary>
    /// HPE's "optional" fields, measured absent from the default reply on Kibar
    /// (23 September 2026, RedfishProbe --fields; reference-approaches §10.7).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> OptionalFields = new Dictionary<string, string[]>
    {
        ["virtual_machines"] = ["ha_status", "ha_resynchronization_progress", "hypervisor_instance_id"],
        ["omnistack_clusters"] = ["upgrade_state"],
    };

    /// <summary>What the OVC answers without <c>show_optional_fields=true</c>.</summary>
    private static JsonObject DefaultShaped(string collection, JsonNode row)
    {
        var copy = row.DeepClone().AsObject();

        foreach (var field in OptionalFields.GetValueOrDefault(collection, []))
        {
            copy.Remove(field);
        }

        return copy;
    }

    private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}

/// <summary>The graph as the folding port sees it: one vCenter and the entities it owns.</summary>
internal sealed class FakeDirectory(IEnumerable<EntityId> entities) : ISimplivityFoldingDirectory
{
    private readonly HashSet<EntityId> _entities = [.. entities];

    public Dictionary<string, EntityId> VirtualMachines { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static FakeDirectory For(params string[] moRefs) =>
        new(moRefs.Select(m => EntityId.For(FakeOvc.Vcenter, m)));

    public string? VcenterFor(string instanceUuid) =>
        string.Equals(instanceUuid, FakeOvc.VcenterUuid, StringComparison.OrdinalIgnoreCase) ? FakeOvc.Vcenter : null;

    public bool Contains(EntityId entity) => _entities.Contains(entity);

    public EntityId? VirtualMachineByInstanceUuid(string instanceUuid) =>
        VirtualMachines.TryGetValue(instanceUuid, out var id) ? id : null;
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
}
