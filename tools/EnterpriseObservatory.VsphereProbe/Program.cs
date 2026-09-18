using System.Globalization;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Collectors.Vsphere;

// Read-only probe against a live vCenter.
//
// Every call it makes is a read. It exists to answer the questions only a real
// server can settle: whether the SOAP envelopes are accepted, whether paging
// behaves as expected, and — most importantly — whether the platform is
// actually collecting the counters the product's diagnostics depend on.
//
// Credentials come from the environment and are never printed. Pass --mask to
// redact object names so the output can be shared.
//
//   $env:EO_VCENTER_URL      = "https://vc01.corp.local"
//   $env:EO_VCENTER_USER     = "svc-observatory@vsphere.local"
//   $env:EO_VCENTER_PASSWORD = "..."
//   $env:EO_VCENTER_INSECURE = "true"     # only if the certificate is self-signed
//
//   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --mask

var mask = args.Contains("--mask", StringComparer.OrdinalIgnoreCase);

// Deliberately shrinkable so the continuation path can be exercised against an
// environment that would otherwise fit in a single page.
var pageSize = 250;
var pageSizeIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--page-size", StringComparison.OrdinalIgnoreCase));
if (pageSizeIndex >= 0 && pageSizeIndex + 1 < args.Length &&
    int.TryParse(args[pageSizeIndex + 1], out var requested) && requested > 0)
{
    pageSize = requested;
}

var url = Environment.GetEnvironmentVariable("EO_VCENTER_URL");
var user = Environment.GetEnvironmentVariable("EO_VCENTER_USER");
var password = Environment.GetEnvironmentVariable("EO_VCENTER_PASSWORD");
var insecure = string.Equals(
    Environment.GetEnvironmentVariable("EO_VCENTER_INSECURE"), "true", StringComparison.OrdinalIgnoreCase);

if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("Set EO_VCENTER_URL, EO_VCENTER_USER and EO_VCENTER_PASSWORD first.");
    return 2;
}

if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress))
{
    Console.Error.WriteLine("EO_VCENTER_URL is not a valid absolute URL.");
    return 2;
}

var options = new VsphereConnectionOptions
{
    BaseAddress = baseAddress,
    Username = user,
    Password = Secret.From(password),
    InstanceId = "probe",
    AcceptUntrustedCertificate = insecure,
    InventoryPageSize = pageSize,
};

using var handler = VsphereClient.CreateHandler(options);
using var http = new HttpClient(handler) { BaseAddress = baseAddress };
using var client = new VsphereClient(http, options);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

try
{
    Section("Connection");
    Console.WriteLine($"  endpoint                 {Show(baseAddress.Host, mask)}");
    Console.WriteLine($"  certificate validation   {(insecure ? "RELAXED (self-signed accepted)" : "enforced")}");

    var catalog = await client.GetCounterCatalogAsync(cancellation.Token);
    Console.WriteLine("  login                    ok");
    Console.WriteLine($"  counters defined         {catalog.Count}");

    // A counter key is not unique. Where it repeats the product picks one, and
    // picking on someone's behalf is worth showing them.
    var duplicates = VsphereCounterIndex.FindDuplicates(catalog);
    if (duplicates.Count > 0)
    {
        Console.WriteLine($"  duplicate counter keys   {duplicates.Count} (lowest level wins)");
        foreach (var duplicate in duplicates.Take(5))
        {
            var variants = string.Join(", ", duplicate.Definitions.Select(d =>
                $"id={d.Id} level={d.Level} statsType={(d.StatsType.Length == 0 ? "?" : d.StatsType)}"));
            Console.WriteLine($"    - {duplicate.Key}: {variants}");
        }
    }

    Section("Query sizing");
    var maxQueryMetrics = await client.GetMaxQueryMetricsAsync(cancellation.Token);
    Console.WriteLine(maxQueryMetrics is null
        ? "  maxQueryMetrics          not readable (falling back to documented default)"
        : $"  maxQueryMetrics          {maxQueryMetrics}");

    var sizer = new AdaptiveBatchSizer(maxQueryMetrics, VsphereCounters.Host.Count);
    Console.WriteLine($"  batch for host counters  {sizer.Current} entities per query");

    Section("Inventory");
    var payload = await client.RetrieveInventoryAsync(cancellation.Token);

    Console.WriteLine($"  vCenter                  {Show(payload.VCenterName, mask)}");
    Console.WriteLine($"  hosts                    {payload.Hosts.Count}");
    Console.WriteLine($"  virtual machines         {payload.VirtualMachines.Count}");
    Console.WriteLine($"  clusters                 {payload.Clusters.Count}");
    Console.WriteLine($"  datastores               {payload.Datastores.Count}");

    var total = payload.Hosts.Count + payload.VirtualMachines.Count +
                payload.Clusters.Count + payload.Datastores.Count;

    Console.WriteLine($"  page size                {pageSize}");
    Console.WriteLine($"  pages retrieved          {payload.PagesRetrieved}");

    var expectedPages = (int)Math.Ceiling(total / (double)pageSize);
    if (payload.PagesRetrieved > 1)
    {
        var consistent = payload.PagesRetrieved == expectedPages;
        Console.WriteLine(consistent
            ? $"  paging                   VERIFIED — {total} objects over {payload.PagesRetrieved} pages"
            : $"  paging                   SUSPECT — {total} objects over {payload.PagesRetrieved} pages, " +
              $"expected {expectedPages}");
    }
    else
    {
        Console.WriteLine($"  paging                   not exercised ({total} objects fit one page)");
        Console.WriteLine("                           re-run with --page-size 50 to prove the continuation path");
    }

    if (payload.Failures.Count > 0)
    {
        Console.WriteLine($"  unreadable properties    {payload.Failures.Count}");
        foreach (var failure in payload.Failures.Take(5))
        {
            var permission = failure.IsPermissionDenied ? "  [permission]" : string.Empty;
            Console.WriteLine($"    - {Show(failure.Target, mask)}: {failure.Detail}{permission}");
        }
    }

    var unreadableClusters = payload.Clusters.Count(c =>
        c.HighAvailabilityEnabled is null || c.DrsEnabled is null);
    if (unreadableClusters > 0)
    {
        Console.WriteLine($"  clusters with unreadable HA/DRS   {unreadableClusters}" +
                          "   (reported Unknown, never assumed off)");
    }

    if (payload.Hosts.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No hosts found; skipping the counter checks.");
        return 0;
    }

    Section("Counter availability");
    Console.WriteLine("  The disk latency triad is the product's diagnostic core: it is what lets the");
    Console.WriteLine("  platform say WHICH layer is slow rather than merely that something is.");
    Console.WriteLine("  It needs statistics level 2.");
    Console.WriteLine();

    var probeHost = payload.Hosts[0];
    var available = await client.GetAvailableCounterKeysAsync(
        probeHost.MoRef, VsphereEntityType.HostSystem, cancellation.Token);

    var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
    var byKey = VsphereCounterIndex.ByKey(catalog);
    var missing = 0;

    foreach (var key in VsphereCounters.Host)
    {
        var defined = byKey.TryGetValue(key, out var counter);
        var collected = availableSet.Contains(key);
        if (!collected)
        {
            missing++;
        }

        var state = !defined
            ? "NOT DEFINED on this vCenter"
            : collected
                ? $"available (level {counter!.Level})"
                : $"defined (level {counter!.Level}) but NOT being collected";

        Console.WriteLine($"  {(collected ? "ok  " : "MISS")}  {key,-34} {state}");
    }

    if (missing > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {missing} wanted counters are not being collected. Raise the statistics level");
        Console.WriteLine("  for the 5-minute interval in vCenter Server Settings > Statistics.");
        Console.WriteLine("  Until then those metrics are reported as Unknown, never as zero or healthy.");
    }

    Section("Sample read");
    var usable = VsphereCounters.Host.Where(availableSet.Contains).Select(k => byKey[k]).ToList();

    if (usable.Count == 0)
    {
        Console.WriteLine("  No usable host counters; nothing to sample.");
        return 0;
    }

    var samples = await client.QueryPerfAsync(
        [probeHost.MoRef], VsphereEntityType.HostSystem, usable, cancellation.Token);

    Console.WriteLine($"  host                     {Show(probeHost.Name, mask)}");
    Console.WriteLine($"  connection state         {probeHost.ConnectionState}");
    Console.WriteLine($"  series returned          {samples.Sum(s => s.Values.Count)}");
    Console.WriteLine();

    foreach (var value in samples.SelectMany(s => s.Values)
                 .OrderBy(v => v.CounterName, StringComparer.Ordinal))
    {
        var reading = value.Raw.ToString("0.##", CultureInfo.InvariantCulture);
        var interval = $"{value.Interval.TotalSeconds:0}s";
        Console.WriteLine($"  {value.CounterName,-34} {reading,10}  {value.Unit,-12} " +
                          $"rollup={value.Rollup,-10} interval={interval}");
    }

    Console.WriteLine();
    Console.WriteLine("  The interval above is read from the response, not assumed from the request:");
    Console.WriteLine("  a summation counter is meaningless without it.");

    Section("Result");
    Console.WriteLine("  The probe completed. Nothing was written to vCenter.");
    return 0;
}
catch (VsphereApiException ex)
{
    Section("Failed");
    Console.Error.WriteLine($"  {ex.Kind}: {ex.Message}");
    return 1;
}
catch (HttpRequestException ex)
{
    Section("Failed");
    Console.Error.WriteLine($"  Could not reach vCenter: {ex.Message}");
    Console.Error.WriteLine("  If the certificate is self-signed, set EO_VCENTER_INSECURE=true.");
    return 1;
}
catch (OperationCanceledException)
{
    Section("Failed");
    Console.Error.WriteLine("  Timed out after 3 minutes.");
    return 1;
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

// Keeps enough of a name to tell entries apart without disclosing it.
static string Show(string value, bool mask) =>
    !mask ? value : value.Length <= 4 ? "****" : $"{value[..2]}***{value[^2..]}";
