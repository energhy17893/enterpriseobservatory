using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Simplivity;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives the SimpliVity inventory source (<see cref="SimplivityInventorySource"/>)
/// through a fake OmniStack REST endpoint. It owns no entity; a host it
/// accepts is an annotation on the vSphere host (ADR-0027).
/// </summary>
public sealed class SimplivityInventoryContractFixture : IInventoryContractFixture
{
    public string InstanceId => "svt-contract";

    public IInventorySource CreateHealthy(int hostCount) => Source(new FakeOmniStack(hostCount, 0));

    public IInventorySource CreateSlow() => Source(new FakeOmniStack(1, 0) { Delay = TimeSpan.FromSeconds(5) });

    public IInventorySource CreateWithFailures(int hostCount, int failureCount) =>
        Source(new FakeOmniStack(hostCount, failureCount));

    public IReadOnlyList<string> AcceptedTargets(InventorySnapshot snapshot) =>
        [.. snapshot.Annotations.Select(a => a.Entity.Value)];

    internal SimplivityInventorySource Source(FakeOmniStack ovc) =>
        new(InstanceId, ovc.Channel(), new FoldEverything(), new TestClock());

    /// <summary>A graph in which every host of <see cref="FakeOmniStack.Vcenter"/> exists.</summary>
    internal sealed class FoldEverything : ISimplivityFoldingDirectory
    {
        public string? VcenterFor(string instanceUuid) =>
            instanceUuid == FakeOmniStack.VcenterUuid ? FakeOmniStack.Vcenter : null;

        public bool Contains(EntityId entity) => true;

        public EntityId? VirtualMachineByInstanceUuid(string instanceUuid) => null;
    }
}

/// <summary>
/// An OmniStack REST endpoint serving <c>hosts</c> (the rest empty), OAuth
/// token and revoke, paged by <c>limit</c>/<c>offset</c>/<c>count</c>.
/// </summary>
/// <param name="hostCount">Hosts that fold onto <see cref="Vcenter"/>.</param>
/// <param name="strangerCount">Hosts of a vCenter nobody here reads: each is a "could not fold".</param>
internal sealed class FakeOmniStack(int hostCount, int strangerCount) : HttpMessageHandler
{
    public const string VcenterUuid = "5029a1b2-c3d4-4e5f-8a9b-0c1d2e3f4a5b";
    public const string Vcenter = "vc-contract";

    private readonly Lock _gate = new();
    private string _valid = "none";

    public List<string> Requests { get; } = [];

    public int TokenPosts { get; private set; }

    public TimeSpan Delay { get; init; }

    public string? RawBody { get; set; }

    /// <summary>Called on every GET, before it is answered — to cancel a read mid-cycle.</summary>
    public Action? OnGet { get; set; }

    public void ExpireToken()
    {
        lock (_gate)
        {
            _valid = "expired";
        }
    }

    /// <summary>
    /// Requests that could have left an object on the server: anything but a
    /// GET or a token issue. REST reads create none; the token is kept for the
    /// next cycle by design (F3) and given back at close.
    /// </summary>
    public int ServerObjectsCreated => Requests.Count(r =>
        !r.StartsWith("GET ", StringComparison.Ordinal) && r != "POST /api/oauth/token");

    public SimplivitySessionChannel Channel() => new(this, new SimplivityConnectionOptions
    {
        InstanceId = "svt-contract",
        BaseAddress = new Uri("https://ovc.contract.local"),
        Username = "observatory",
        Password = Secret.From("hunter2"),
    });

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
            Requests.Add($"{request.Method} {path}");

            if (path == "/api/oauth/token")
            {
                TokenPosts++;
                _valid = $"token-{TokenPosts}";
                return Json(new JsonObject { ["access_token"] = _valid });
            }

            if (path == "/api/oauth/revoke")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Headers.Authorization?.Parameter != _valid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
        }

        OnGet?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        if (RawBody is { } raw)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) };
        }

        var name = path["/api/".Length..];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var limit = int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture);
        var offset = int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
        var all = name == "hosts" ? Hosts() : [];

        return Json(new JsonObject
        {
            [name] = new JsonArray([.. all.Skip(offset).Take(limit)]),
            ["count"] = all.Count,
        });
    }

    private List<JsonNode> Hosts() =>
    [
        .. Enumerable.Range(1, hostCount).Select(i => Host(i, VcenterUuid)),
        .. Enumerable.Range(1, strangerCount).Select(i => Host(1000 + i, "00000000-0000-0000-0000-00000000dead")),
    ];

    private static JsonObject Host(int i, string uuid) => new()
    {
        ["id"] = $"svt-host-{i}",
        ["name"] = $"esx{i:00}.contract.local",
        ["state"] = "ALIVE",
        ["hypervisor_object_id"] = $"{uuid}:HostSystem:host-{i}",
    };

    private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}
