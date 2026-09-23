using System.Text.Json;
using EnterpriseObservatory.RedfishProbe;

// A read-only measurement probe for hardware the product does not collect
// from yet: HPE iLO 5/6 (Redfish) and HPE SimpliVity (OmniStack REST). It
// exists because M6.0's gate is "measure before requesting" — the same rule
// that produced VsphereProbe's --candidates and --host-identity switches —
// and because no credential has been entered for either kind yet
// (docs/reference-approaches.md §10.7/§10.8). Until one is, --dry proves the
// output format against bundled DMTF/HPE sample documents; nothing here
// writes to a device: the only POSTs made anywhere in this tool are
// SimpliVity's oauth/token and oauth/revoke.
//
//   dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --dry --kind redfish
//   dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --dry --kind simplivity
//   dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --from-store [name] --kind redfish --mask
//   dotnet run --project tools/EnterpriseObservatory.RedfishProbe -- --from-store [name] --kind simplivity --fields all

var mask = args.Contains("--mask", StringComparer.OrdinalIgnoreCase);
var dry = args.Contains("--dry", StringComparer.OrdinalIgnoreCase);
var shapes = args.Contains("--shapes", StringComparer.OrdinalIgnoreCase);

// --fields default|explicit|optional|all (SimpliVity only): how many objects
// carry each field the collector reads, per query variant -- the default
// response, an explicit fields= list, or show_optional_fields=true. The live
// collector read no ha_status at all (23 September 2026, 1051 log line:
// 0 VMs not SAFE while this probe's fields= read found DEGRADED=1).
var fieldsIndex = Array.FindIndex(args, a => string.Equals(a, "--fields", StringComparison.OrdinalIgnoreCase));
var fieldsMode = fieldsIndex >= 0 && fieldsIndex + 1 < args.Length ? args[fieldsIndex + 1].ToLowerInvariant() : null;

if (fieldsMode is not (null or "default" or "explicit" or "optional" or "all"))
{
    Console.Error.WriteLine("--fields takes default, explicit, optional or all.");
    return 2;
}

var kindIndex = Array.FindIndex(args, a => string.Equals(a, "--kind", StringComparison.OrdinalIgnoreCase));
var kind = kindIndex >= 0 && kindIndex + 1 < args.Length ? args[kindIndex + 1].ToLowerInvariant() : null;

if (kind is not ("redfish" or "simplivity"))
{
    Console.Error.WriteLine("Pass --kind redfish or --kind simplivity.");
    return 2;
}

var storeIndex = Array.FindIndex(args, a => string.Equals(a, "--from-store", StringComparison.OrdinalIgnoreCase));

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

if (dry)
{
    return kind == "redfish"
        ? await RunDryRedfish(mask, shapes)
        : RunDrySimplivity(mask, shapes);
}

if (storeIndex < 0)
{
    Console.Error.WriteLine(
        "Live runs read a stored connection. Pass --from-store [connection name], or use --dry to " +
        "exercise the parsers against bundled sample JSON with no network.");
    return 2;
}

string instanceId;
string url;
string user;
string password;
bool insecure;

try
{
    var named = storeIndex + 1 < args.Length && !args[storeIndex + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[storeIndex + 1]
        : string.Empty;

    (instanceId, _, url, user, password, insecure) = StoredConnection.Read(kind, named);
}
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw.
catch (Exception ex)
#pragma warning restore CA1031
{
    Console.Error.WriteLine($"Could not read a stored '{kind}' connection: {ex.Message}");
    return 2;
}

if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress))
{
    Console.Error.WriteLine($"'{instanceId}' does not have a valid absolute URL.");
    return 2;
}

Console.WriteLine($"=== Connection ===");
Console.WriteLine($"  instance                         {Mask.Show(instanceId, mask)}");
Console.WriteLine($"  endpoint                         {Mask.Show(baseAddress.Host, mask)}");
Console.WriteLine($"  certificate validation            {(insecure ? "RELAXED (self-signed accepted)" : "enforced")}");

return kind == "redfish"
    ? await RunLiveRedfishAsync(baseAddress, user, password, insecure, mask, shapes, cancellation.Token)
    : await RunLiveSimplivityAsync(baseAddress, user, password, insecure, mask, shapes, fieldsMode, cancellation.Token);

static async Task<int> RunDryRedfish(bool mask, bool shapes)
{
    Console.WriteLine("=== --dry: parsers over bundled DMTF sample JSON, no network ===");

    var power = LoadSample("Redfish", "power.json");
    var thermal = LoadSample("Redfish", "thermal.json");
    var system = LoadSample("Redfish", "computersystem.json");
    var manager = LoadSample("Redfish", "manager.json");
    var storageCollection = LoadSample("Redfish", "storage-collection.json");
    var storageMember = LoadSample("Redfish", "storage.json");
    var drive = LoadSample("Redfish", "drive-1.json");
    var memory = LoadSample("Redfish", "memory-dimm1.json");
    var firmware = LoadSample("Redfish", "firmwareinventory-collection.json");
    var logEntries = LoadSample("Redfish", "logentries.json");

    // Same walk as the live path (StorageWalker), fed from a fixed lookup
    // instead of the network: the point of --dry is proving the walk, not
    // just the fixed single document RunDryRedfish used to load.
    Task<JsonElement?> ReadFixture(string path) => Task.FromResult(path.Contains("/Drives/", StringComparison.Ordinal)
        ? drive
        : storageMember);

    var walk = await StorageWalker.WalkAsync(storageCollection, ReadFixture, CancellationToken.None);

    if (shapes)
    {
        ShapeDump.Print("Managers/1", manager);
        ShapeDump.Print("Chassis/1/Power", power, depth: 6);
        ShapeDump.Print("Chassis/1/Thermal", thermal, depth: 6);
        ShapeDump.Print("Systems/1", system, depth: 6);
        ShapeDump.Print("Systems/1/Storage (collection)", storageCollection);
        ShapeDump.Print("Systems/1/Storage/* (first controller)", walk.Controllers.Count > 0 ? walk.Controllers[0] : null);
        ShapeDump.Print("Systems/1/Storage/*/Drives/* (first)", walk.Drives.Count > 0 ? walk.Drives[0] : null);
        ShapeDump.Print("Systems/1/Memory/*", memory);
        ShapeDump.Print("UpdateService/FirmwareInventory", firmware);
        ShapeDump.Print("Systems/1/LogServices/IML/Entries", logEntries);
        return 0;
    }

    var docs = new RedfishDocs
    {
        Manager = manager,
        Power = power,
        Thermal = thermal,
        System = system,
        Controllers = walk.Controllers,
        Drives = walk.Drives,
        Memory = memory is { } m ? [m] : [],
        FirmwareInventoryCollection = firmware,
        LogEntries = logEntries,
    };

    foreach (var line in RedfishReport.Generate(docs, mask))
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine("Sample sources: DMTF Redfish-Publications mockups public-rackmount1 (Power,");
    Console.WriteLine("Thermal, ComputerSystem, Manager, LogServices, FirmwareInventory) and");
    Console.WriteLine("public-localstorage (Storage, Drive, Memory) —");
    Console.WriteLine("https://github.com/DMTF/Redfish-Publications/tree/main/mockups");
    Console.WriteLine("These are generic DMTF samples: HPE's Oem.Hpe.* extensions are absent from");
    Console.WriteLine("them by construction, which is why this run reports them ABSENT rather than");
    Console.WriteLine("measuring a value for them. A live iLO is expected to add them.");

    return 0;
}

static int RunDrySimplivity(bool mask, bool shapes)
{
    Console.WriteLine("=== --dry: parsers over bundled HPE SimpliVity sample JSON, no network ===");

    var version = LoadSample("SimpliVity", "version.json");
    var hosts = LoadSample("SimpliVity", "hosts.json");
    var clusters = LoadSample("SimpliVity", "omnistack_clusters.json");
    var vms = LoadSample("SimpliVity", "virtual_machines.json");
    var backups = LoadSample("SimpliVity", "backups.json");

    if (shapes)
    {
        ShapeDump.Print("GET /api/version", version);
        ShapeDump.Print("GET /api/hosts", hosts);
        ShapeDump.Print("GET /api/omnistack_clusters", clusters);
        ShapeDump.Print("GET /api/virtual_machines", vms);
        ShapeDump.Print("GET /api/backups", backups);
        return 0;
    }

    var docs = new SimplivityDocs
    {
        Version = version,
        Hosts = hosts,
        Clusters = clusters,
        VirtualMachines = vms,
        Backups = backups,
    };

    foreach (var line in SimplivityReport.Generate(docs, mask))
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine("Sample source: HPE's own published OmniStack REST API definition —");
    Console.WriteLine("https://github.com/HewlettPackard/hpe-simplivity-swagger (simplivity-swagger.json,");
    Console.WriteLine("API version 1.25). Instance documents built from that schema's field names,");
    Console.WriteLine("enums and declared examples; HPE does not publish full response bodies, and");
    Console.WriteLine("this estate has no SimpliVity host to record one from yet (M6.0's gate).");

    return 0;
}

static async Task<int> RunLiveRedfishAsync(
    Uri baseAddress, string user, string password, bool insecure, bool mask, bool shapes,
    CancellationToken cancellationToken)
{
    using var client = new RedfishClient(baseAddress, user, password, insecure);
    var timing = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    async Task<JsonElement?> ReadAsync(string path)
    {
        var read = await client.GetAsync(path, cancellationToken);
        timing[path] = read.Elapsed;

        if (read.Ok)
        {
            return read.Document!.RootElement;
        }

        Console.WriteLine($"  {path,-40} NOT READ  " +
                          (read.StatusCode is { } code ? $"HTTP {code}" : read.Error ?? "no response"));
        return null;
    }

    // Legacy Power/Thermal are the primary path on both generations
    // (§10.7); Systems/1 for identity and iLO 6's AggregateHealthStatus;
    // Storage/Drives, Memory, firmware and IML round out the report.
    var manager = await ReadAsync("/redfish/v1/Managers/1");
    var power = await ReadAsync("/redfish/v1/Chassis/1/Power");
    var thermal = await ReadAsync("/redfish/v1/Chassis/1/Thermal");
    var system = await ReadAsync("/redfish/v1/Systems/1");
    var firmware = await ReadAsync("/redfish/v1/UpdateService/FirmwareInventory");
    var logEntries = await ReadAsync("/redfish/v1/Systems/1/LogServices/IML/Entries");

    // Ids are not fixed across generations -- HPE iLO names a controller
    // Storage/DE00A000, not Storage/1 -- so the collection's own
    // Members[].@odata.id is followed instead of a guessed path.
    var storageCollection = await ReadAsync("/redfish/v1/Systems/1/Storage");
    var storageWalk = await StorageWalker.WalkAsync(storageCollection, ReadAsync, cancellationToken);
    if (storageWalk.Capped)
    {
        Console.WriteLine($"  Systems/1/Storage walk capped at {StorageWalker.MaxFollowedLinks} followed links");
    }

    var memory = new List<JsonElement>();
    var memoryCollection = await ReadAsync("/redfish/v1/Systems/1/Memory");
    if (memoryCollection is { } memoryDoc &&
        memoryDoc.TryGetProperty("Members", out var memoryLinks) && memoryLinks.ValueKind == JsonValueKind.Array)
    {
        foreach (var link in memoryLinks.EnumerateArray())
        {
            if (link.TryGetProperty("@odata.id", out var id) && id.GetString() is { Length: > 0 } path &&
                await ReadAsync(path) is { } dimm)
            {
                memory.Add(dimm);
            }
        }
    }

    if (shapes)
    {
        ShapeDump.Print("Managers/1", manager);
        ShapeDump.Print("Chassis/1/Power", power, depth: 6);
        ShapeDump.Print("Chassis/1/Thermal", thermal, depth: 6);
        ShapeDump.Print("Systems/1", system, depth: 6);
        ShapeDump.Print("Systems/1/Storage (collection)", storageCollection);
        ShapeDump.Print(
            "Systems/1/Storage/* (first controller)",
            storageWalk.Controllers.Count > 0 ? storageWalk.Controllers[0] : null);
        ShapeDump.Print(
            "Systems/1/Storage/*/Drives/* (first)",
            storageWalk.Drives.Count > 0 ? storageWalk.Drives[0] : null);
        ShapeDump.Print("UpdateService/FirmwareInventory", firmware);
        ShapeDump.Print("Systems/1/LogServices/IML/Entries", logEntries);
        return 0;
    }

    var docs = new RedfishDocs
    {
        Manager = manager,
        Power = power,
        Thermal = thermal,
        System = system,
        Controllers = storageWalk.Controllers,
        Drives = storageWalk.Drives,
        Memory = memory,
        FirmwareInventoryCollection = firmware,
        LogEntries = logEntries,
        Timing = timing,
    };

    foreach (var line in RedfishReport.Generate(docs, mask))
    {
        Console.WriteLine(line);
    }

    return 0;
}

static async Task<int> RunLiveSimplivityAsync(
    Uri baseAddress, string user, string password, bool insecure, bool mask, bool shapes, string? fieldsMode,
    CancellationToken cancellationToken)
{
    using var client = new SimplivityClient(baseAddress, insecure);
    var timing = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    try
    {
        var versionRead = await client.GetVersionAsync(cancellationToken);
        timing["/api/version"] = versionRead.Elapsed;
        var version = versionRead.Ok ? versionRead.Document!.RootElement : (JsonElement?)null;

        // LoginAsync prints its own TCP probe and per-attempt diagnostics
        // (§10.8's live measurement hit a TLS failure with no inner
        // exception printed anywhere -- this is where that gap closed).
        var (loggedIn, loginElapsed, _) = await client.LoginAsync(user, password, cancellationToken);

        if (!loggedIn)
        {
            return 1;
        }

        async Task<JsonElement?> ReadAsync(string path)
        {
            var read = await client.GetAsync(path, cancellationToken);
            timing[path] = read.Elapsed;

            if (read.Ok)
            {
                return read.Document!.RootElement;
            }

            Console.WriteLine($"  {path,-52} NOT READ  " +
                              (read.StatusCode is { } code ? $"HTTP {code}" : read.Error ?? "no response"));
            return null;
        }

        // Every page: the API caps a page at `limit` (500 by default) and
        // reports the total in `count`, so one read of 500 VMs said nothing
        // about the estate (23 September 2026). Capped at 100 pages.
        async Task<JsonElement?> ReadAllAsync(string path, string array)
        {
            const int limit = 500;
            var items = new List<JsonElement>();
            JsonElement? count = null;
            var separator = path.Contains('?') ? '&' : '?';

            for (var offset = 0; offset < limit * 100; offset += limit)
            {
                if (await ReadAsync($"{path}{separator}limit={limit}&offset={offset}") is not { } page)
                {
                    return items.Count == 0 ? null : Merged();
                }

                count ??= page.TryGetProperty("count", out var total) ? total.Clone() : null;
                var read = page.TryGetProperty(array, out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(e => e.Clone()).ToList()
                    : [];
                items.AddRange(read);

                if (read.Count < limit || (count is { } c && items.Count >= c.GetInt32()))
                {
                    break;
                }
            }

            return Merged();

            JsonElement Merged() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                [array] = items,
                ["count"] = count,
            });
        }

        if (fieldsMode is not null)
        {
            string[] variants = fieldsMode == "all" ? ["default", "explicit", "optional"] : [fieldsMode];

            foreach (var variant in variants)
            {
                Console.WriteLine($"=== --fields {variant} ===");

                foreach (var (endpoint, fields) in FieldCoverage.Read)
                {
                    var query = variant switch
                    {
                        "explicit" => $"?fields={string.Join(',', fields)}",
                        "optional" => "?show_optional_fields=true",
                        _ => string.Empty,
                    };

                    if (await ReadAllAsync($"/api/{endpoint}{query}", endpoint) is not { } all)
                    {
                        continue;
                    }

                    foreach (var line in FieldCoverage.Lines(endpoint, all, fields))
                    {
                        Console.WriteLine(line);
                    }
                }
            }

            return 0;
        }

        var hosts = await ReadAllAsync("/api/hosts", "hosts");
        var clusters = await ReadAllAsync("/api/omnistack_clusters", "omnistack_clusters");
        var vms = await ReadAllAsync(
            "/api/virtual_machines?fields=id,name,state,ha_status,host_id,omnistack_cluster_id", "virtual_machines");
        var backups = await ReadAllAsync("/api/backups", "backups");

        if (shapes)
        {
            ShapeDump.Print("GET /api/version", version);
            ShapeDump.Print("GET /api/hosts", hosts);
            ShapeDump.Print("GET /api/omnistack_clusters", clusters);
            ShapeDump.Print("GET /api/virtual_machines", vms);
            ShapeDump.Print("GET /api/backups", backups);
            return 0;
        }

        var docs = new SimplivityDocs
        {
            Version = version,
            Hosts = hosts,
            Clusters = clusters,
            VirtualMachines = vms,
            Backups = backups,
            TokenAcquisition = loginElapsed,
        };

        foreach (var line in SimplivityReport.Generate(docs, mask))
        {
            Console.WriteLine(line);
        }

        return 0;
    }
    finally
    {
        // Unconditional: the constraint is "always revoke, including when a
        // later call fails", so this runs whether the try block returned,
        // threw, or the token was never used for anything else.
        await client.RevokeAsync(CancellationToken.None);
    }
}

static JsonElement? LoadSample(string kindFolder, string fileName)
{
    var path = Path.Combine(AppContext.BaseDirectory, "SampleData", kindFolder, fileName);

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Missing bundled sample: {path}");
        return null;
    }

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.Clone();
}
