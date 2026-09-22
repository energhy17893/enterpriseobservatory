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

// A connection the product already holds, read from its database and decrypted
// with its own key ring. Diagnosing a connection should never require retyping
// its password: a shell rewrites what it is given, and that is how this product
// spent a day chasing a credential that had been correct all along.
//
//   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- //     --from-store C:/ProgramData/EnterpriseObservatory/observatory.db
var storeIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--from-store", StringComparison.OrdinalIgnoreCase));

string? url;
string? user;
string? password;
bool insecure;

if (storeIndex >= 0)
{
    try
    {
        var named = storeIndex + 1 < args.Length &&
            !args[storeIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[storeIndex + 1]
                : string.Empty;

        (url, user, password, insecure) =
            EnterpriseObservatory.VsphereProbe.StoredConnection.Read(named);
    }
#pragma warning disable CA1031 // Justified: a probe reports, it does not throw.
    catch (Exception ex)
#pragma warning restore CA1031
    {
        Console.Error.WriteLine($"Could not read a stored connection: {ex.Message}");
        return 2;
    }
}
else
{
    url = Environment.GetEnvironmentVariable("EO_VCENTER_URL");
    user = Environment.GetEnvironmentVariable("EO_VCENTER_USER");
    password = Environment.GetEnvironmentVariable("EO_VCENTER_PASSWORD");
    insecure = string.Equals(
        Environment.GetEnvironmentVariable("EO_VCENTER_INSECURE"), "true", StringComparison.OrdinalIgnoreCase);
}

if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine(
        "Pass --from-store [connection name], or set EO_VCENTER_URL, EO_VCENTER_USER and " +
        "EO_VCENTER_PASSWORD first.");
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

using var handler = VsphereSessionChannel.CreateHandler(options);
using var channel = new VsphereSessionChannel(handler, options);
var client = new VsphereClient(channel, options);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

try
{
    // What this session's views are, if the account is allowed to look. F4
    // moves server-side cleanup to the runner, and its done-criterion is "no
    // views accumulate". A read-only account may not be allowed to read the
    // list at all -- and not being allowed to look is not an empty list, so
    // this says which of the two it is rather than printing a reassuring 0.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --views
    if (args.Contains("--views", StringComparer.OrdinalIgnoreCase))
    {
        Section("Views");

        // The list is per session, so this cannot see what the service's own
        // session holds -- only what this probe's does. That makes the useful
        // question "does a full read leave its view behind", asked by doing
        // one here and counting before and after.
        var viewManager = await client.GetViewManagerAsync(cancellation.Token);
        var before = await client.ReadCandidateObjectsAsync(
            "ViewManager", [viewManager], "viewList", cancellation.Token);

        await client.RetrieveInventoryAsync(cancellation.Token);

        var views = await client.ReadCandidateObjectsAsync(
            "ViewManager", [viewManager], "viewList", cancellation.Token);

        if (views.Fault is { } fault)
        {
            Console.WriteLine($"  view list                NOT READABLE by this account ({fault})");
            Console.WriteLine("                           Then F4's \"no views accumulate\" cannot be confirmed");
            Console.WriteLine("                           from here, and says so rather than reading 0 as proof.");
            return 0;
        }

        // A reference array comes back either as repeated structures or as one
        // joined value, depending on the shape the parser met; count whichever
        // this vCenter sent rather than assuming.
        static int Count(VsphereCandidateRead read) => read.Objects.Sum(o =>
            (o.Structures.TryGetValue("viewList", out var nodes) ? nodes.Count : 0) +
            (o.Values.TryGetValue("viewList", out var joined)
                ? PropertyCollectorParser.SplitValues(joined).Count
                : 0));

        var count = Count(views);

        Console.WriteLine($"  before an inventory read {Count(before)}");
        Console.WriteLine($"  after one               {count}   (per session: this probe's, not the service's)");
        Console.WriteLine($"  read in                  {views.Elapsed.TotalMilliseconds:0} ms");

        return 0;
    }

    // Who is signed in as this account, and nothing else. The question it
    // answers is whether the product leaves sessions behind: run it while the
    // service is up, stop the service, run it again.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --sessions
    if (args.Contains("--sessions", StringComparer.OrdinalIgnoreCase))
    {
        Section("Sessions");

        var sessions = await client.ReadSessionsAsync(cancellation.Token);
        var mine = sessions.Current;

        Console.WriteLine($"  signed in as             {Show(mine?.UserName ?? user, mask)}");
        Console.WriteLine($"  this probe's session     login {mine?.LoginTimeUtc:HH:mm:ss}Z, key ...{Tail(mine?.Key)}");

        // Asked of the server rather than assumed: does Logout end a session
        // here. It is the same call the service makes when it stops, when a
        // connection is edited or removed, and after every Test.
        if (args.Contains("--confirm-logout", StringComparer.OrdinalIgnoreCase))
        {
            var ended = await channel.LogoutAndConfirmAsync(cancellation.Token);

            Console.WriteLine(ended switch
            {
                true => "  logout                   CONFIRMED - vCenter no longer recognises the old session",
                false => "  logout                   NOT EFFECTIVE - vCenter still answered on the old session",
                null => "  logout                   nothing to log out of",
            });

            var again = await client.ReadSessionsAsync(cancellation.Token);
            Console.WriteLine(
                $"  signed in again          login {again.Current?.LoginTimeUtc:HH:mm:ss}Z, key ...{Tail(again.Current?.Key)} " +
                $"({(again.Current?.Key == mine?.Key ? "SAME session" : "a new session")})");

            return ended == true ? 0 : 1;
        }

        if (sessions.All is not { } all)
        {
            // Said plainly: not being allowed to look is not an empty list.
            Console.WriteLine($"  session list             NOT READABLE by this account ({sessions.Unreadable})");
            Console.WriteLine("                           Listing sessions needs the Sessions privilege; a read-only");
            Console.WriteLine("                           account normally lacks it. Check in the vSphere Client instead:");
            Console.WriteLine("                           Administration > Deployment > Sessions (or Monitor > Sessions).");
            return 0;
        }

        var ours = all
            .Where(s => string.Equals(s.UserName, mine?.UserName ?? user, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.LoginTimeUtc)
            .ToList();

        Console.WriteLine($"  sessions on this vCenter {all.Count}");
        Console.WriteLine($"  of them this account's   {ours.Count}  (one is this probe, and ends when it exits)");
        Console.WriteLine();

        foreach (var session in ours)
        {
            var idle = session.LastActiveUtc is { } active
                ? string.Create(CultureInfo.InvariantCulture, $"{(DateTimeOffset.UtcNow - active).TotalSeconds,6:0} s idle")
                : "      ? idle";
            var who = session.Key == mine?.Key ? "this probe" : session.UserAgent;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    login {session.LoginTimeUtc:HH:mm:ss}Z   {idle}   {Show(session.IpAddress, mask),-16} {who}"));
        }

        return 0;
    }

    // Why maxQueryMetrics could not be read: one read-only query, one line,
    // never the value.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --why-max-query-metrics
    if (args.Contains("--why-max-query-metrics", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  maxQueryMetrics          {await client.DiagnoseMaxQueryMetricsAsync(cancellation.Token)}");
        return 0;
    }

    // Which keys this vCenter returns and in what form, names only. Settles
    // the readers that were written from the schema rather than from a server.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --shapes
    if (args.Contains("--shapes", StringComparer.OrdinalIgnoreCase))
    {
        Section("Inventory shape (names and counts only)");

        foreach (var line in await client.DescribeInventoryShapeAsync(cancellation.Token))
        {
            Console.WriteLine("  " + line);
        }

        return 0;
    }

    // Collection PR 2's gate: the root folder's triggered alarms and each
    // host's maximum EVC mode, each read alone.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --candidates-pr2
    if (args.Contains("--candidates-pr2", StringComparer.OrdinalIgnoreCase))
    {
        await EnterpriseObservatory.VsphereProbe.Candidates.RunPr2Async(client, cancellation.Token);
        return 0;
    }

    // M8.8's gate: custom field definitions, the VM custom value paths each
    // read alone, and the backup field's value format as counts only.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --candidates-backup
    if (args.Contains("--candidates-backup", StringComparer.OrdinalIgnoreCase))
    {
        await EnterpriseObservatory.VsphereProbe.BackupFields.RunAsync(client, cancellation.Token);
        return 0;
    }

    // Collection PR 1's gate: every candidate path and call, each read alone,
    // before any of them enters the collector's request list.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --candidates
    if (args.Contains("--candidates", StringComparer.OrdinalIgnoreCase))
    {
        await EnterpriseObservatory.VsphereProbe.Candidates.RunAsync(
            client, baseAddress, user, password, insecure, cancellation.Token);
        return 0;
    }

    Section("Connection");
    Console.WriteLine($"  endpoint                {Show(baseAddress.Host, mask)}");
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

    // maxObjects is a ceiling, not an instruction. A live vCenter 8 asked for
    // 250 returned 100 and a continuation token, which is well within the
    // schema — so more pages than the arithmetic predicts proves the
    // continuation path rather than casting doubt on it. Only the opposite
    // would be a fault, and it is the silent one: stopping early looks like a
    // small healthy estate.
    if (payload.PagesRetrieved > 1)
    {
        Console.WriteLine(
            $"  paging                   VERIFIED — {total} objects over {payload.PagesRetrieved} pages");
        Console.WriteLine(
            $"                           (the server paged at its own size, not the {pageSize} asked for)");
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

    // vCenter's own judgements. Zero of them is the common and correct answer
    // on a healthy estate, so the line is printed either way: an estate with no
    // alarms and a collector that stopped reading them look identical from the
    // inbox.
    Section("Alarms vCenter has raised");
    Console.WriteLine($"  triggered                {payload.TriggeredAlarms.Count}   (de-duplicated across the tree)");

    foreach (var alarm in payload.TriggeredAlarms
        .OrderBy(a => a.OverallStatus, StringComparer.Ordinal)
        .Take(10))
    {
        var name = alarm.AlarmName is { Length: > 0 } resolved
            ? Show(resolved, mask)
            : $"(unresolved {alarm.AlarmMoRef})";

        var attached = alarm.EntityMoRef == payload.RootFolderMoRef
            ? "   [raised on the root folder: goes on the vCenter entity]"
            : payload.Hosts.Any(h => h.MoRef == alarm.EntityMoRef) ||
                       payload.VirtualMachines.Any(v => v.MoRef == alarm.EntityMoRef) ||
                       payload.Datastores.Any(d => d.MoRef == alarm.EntityMoRef) ||
                       payload.Clusters.Any(c => c.MoRef == alarm.EntityMoRef)
            ? string.Empty
            : "   [on an object this collector does not read]";

        Console.WriteLine(
            $"    {alarm.OverallStatus,-6} {name}  on {alarm.EntityType} " +
            $"{Show(alarm.EntityMoRef, mask)}{attached}");
    }

    // The numbers the fullness rule is judging, printed whether or not it
    // fires. An estate where nothing crosses the threshold and an estate where
    // the capacity properties came back null produce the same empty inbox, and
    // the difference between them is the difference between "nothing is wrong"
    // and "nothing is being checked".
    if (payload.Datastores.Count > 0)
    {
        Section("Datastore fullness");

        var measured = payload.Datastores
            .Select(d => (
                d.Name,
                d.Accessible,
                Percent: d is { CapacityBytes: > 0 and { } cap, FreeSpaceBytes: >= 0 and { } free }
                    ? (cap - free) / (double)cap * 100d
                    : (double?)null,
                FreeGb: (d.FreeSpaceBytes ?? 0) / 1024d / 1024d / 1024d))
            .OrderByDescending(d => d.Percent ?? -1)
            .ToList();

        var unreadable = measured.Count(d => d.Percent is null);
        var critical = measured.Count(d => d.Percent >= 95d);
        var warning = measured.Count(d => d.Percent is >= 85d and < 95d);

        Console.WriteLine($"  measured                 {measured.Count - unreadable} of {measured.Count}");
        Console.WriteLine($"  unreadable capacity      {unreadable}   (reported Unknown, never as full)");
        Console.WriteLine($"  inaccessible             {measured.Count(d => d.Accessible == false)}");
        Console.WriteLine($"  at or above 95%          {critical}   -> Critical");
        Console.WriteLine($"  85% to 95%               {warning}   -> Warning");
        Console.WriteLine();
        Console.WriteLine("  fullest five:");

        foreach (var datastore in measured.Take(5))
        {
            var percent = datastore.Percent is { } p
                ? string.Create(CultureInfo.InvariantCulture, $"{p,5:0.0}%")
                : "    ?";

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {percent}  {datastore.FreeGb,9:0.#} GB free   {Show(datastore.Name, mask)}"));
        }
    }

    if (payload.Hosts.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No hosts found; skipping the counter checks.");
        return 0;
    }

    // Real QueryPerf round trips, timed; the read-budget measurement's missing
    // number. Read-only.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --time-queryperf 30
    var timingIndex = Array.FindIndex(args, a =>
        string.Equals(a, "--time-queryperf", StringComparison.OrdinalIgnoreCase));
    if (timingIndex >= 0)
    {
        var calls = timingIndex + 1 < args.Length &&
                    int.TryParse(args[timingIndex + 1], out var n) && n >= 20
            ? n
            : 20;

        Section("QueryPerf round trips");
        return await EnterpriseObservatory.VsphereProbe.QueryPerfTiming.RunAsync(
            client, payload, catalog, calls, cancellation.Token);
    }

    // Whether a host's newest real-time slot is complete when first returned
    // (the H3 late-sample regression). Read-only.
    //
    //   dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --late-samples 120
    var lateIndex = Array.FindIndex(args, a =>
        string.Equals(a, "--late-samples", StringComparison.OrdinalIgnoreCase));
    if (lateIndex >= 0)
    {
        var seconds = lateIndex + 1 < args.Length &&
                      int.TryParse(args[lateIndex + 1], out var s) && s is >= 30 and <= 150
            ? s
            : 100;

        Section("Late real-time samples");
        return args.Contains("--follow", StringComparer.OrdinalIgnoreCase)
            ? await EnterpriseObservatory.VsphereProbe.LateSamples.FollowAsync(
                client, payload, catalog, seconds, cancellation.Token)
            : await EnterpriseObservatory.VsphereProbe.LateSamples.RunAsync(
                client, payload, catalog, seconds, cancellation.Token,
                fast: args.Contains("--fast", StringComparer.OrdinalIgnoreCase));
    }

    // The full map, and then nothing else: it is a reference document, not a
    // section of a health check, and burying it under a sample read would make
    // it something nobody pastes into a file.
    if (args.Contains("--map", StringComparer.OrdinalIgnoreCase))
    {
        await EnterpriseObservatory.VsphereProbe.CounterMap.WriteAsync(
            client, payload, mask, cancellation.Token);
        return 0;
    }

    Section("Counter availability");
    Console.WriteLine("  The disk latency triad is the product's diagnostic core: it is what lets the");
    Console.WriteLine("  platform say WHICH layer is slow rather than merely that something is.");
    Console.WriteLine("  It needs statistics level 2.");
    Console.WriteLine();

    var probeHost = payload.Hosts[0];
    var available = await client.GetAvailableCounterKeysAsync(
        probeHost.MoRef, VsphereEntityType.HostSystem, DateTimeOffset.UtcNow, cancellation.Token);

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

    // Does the HOST supply the datastore counters the datastore itself will
    // not? In vim25 a counter's entity is not always the object it describes:
    // per-datastore latency is collected on the host, with the datastore as the
    // instance — the same shape as per-LUN disk latency. If that is what is
    // happening here, asking the datastore for it can never work, however
    // correct the name and the interval.
    var datastoreOnHost = availableSet
        .Where(k => k.StartsWith("datastore.", StringComparison.Ordinal))
        .OrderBy(k => k, StringComparer.Ordinal)
        .ToList();

    Section("Datastore counters supplied by the host");
    Console.WriteLine($"  count                    {datastoreOnHost.Count}");
    foreach (var key in datastoreOnHost)
    {
        Console.WriteLine($"    {key}");
    }

    // The join, tested rather than assumed. A host reports these per volume,
    // naming the volume in the counter instance; a datastore's summary.url is
    // the only place the inventory carries the same identifier. If the two do
    // not meet, every number measured here belongs to nothing that can be
    // shown, and the product would be storing latency against an instance
    // string no screen can resolve to a name.
    if (datastoreOnHost.Count > 0)
    {
        Section("Host instance to datastore");

        var hostMetrics = await client.GetAvailableMetricsAsync(
            probeHost.MoRef, VsphereEntityType.HostSystem, DateTimeOffset.UtcNow,
            cancellation.Token);

        var instances = hostMetrics
            .Where(m => m.Counter.Key.StartsWith("datastore.", StringComparison.Ordinal))
            .Select(m => m.Instance)
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ds:///vmfs/volumes/<identifier>/ — the identifier is the last
        // non-empty segment, which is a VMFS UUID for block storage and a
        // generated one for NFS.
        static string? Identifier(string? url) => url?
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        var byIdentifier = payload.Datastores
            .Where(d => Identifier(d.Url) is { Length: > 0 })
            .ToDictionary(d => Identifier(d.Url)!, d => d, StringComparer.OrdinalIgnoreCase);

        var matched = instances.Count(byIdentifier.ContainsKey);

        Console.WriteLine($"  datastore instances      {instances.Count}   on this one host");
        Console.WriteLine($"  datastores with a url    {byIdentifier.Count} of {payload.Datastores.Count}");
        Console.WriteLine($"  instances that resolve   {matched}");
        Console.WriteLine();

        foreach (var instance in instances.Take(8))
        {
            Console.WriteLine(byIdentifier.TryGetValue(instance, out var datastore)
                ? $"    {Show(instance, mask),-40} -> {Show(datastore.Name, mask)}"
                : $"    {Show(instance, mask),-40} -> UNRESOLVED");
        }

        if (matched == 0 && instances.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing resolved. The instance is not the volume identifier from");
            Console.WriteLine("  summary.url on this vCenter, and attributing these numbers to a");
            Console.WriteLine("  datastore would be guessing.");
        }

        // Whether a zero here means "fast" or "not measured". vSphere reports
        // the total latency counters as whole milliseconds, so an all-flash
        // array serving every request in 400 microseconds reports 0 — and a
        // product whose central diagnostic is storage latency would then have
        // nothing to say about the storage it is most likely to be asked
        // about. The microsecond counter exists for exactly that, but only
        // reports while Storage I/O Control is active, so it is read alongside
        // SIOC's own activity rather than trusted on its own.
        Section("Sub-millisecond visibility");

        // A local rather than an inline array argument: the analyser objects
        // to a constant array handed straight to a call, and it is right that
        // the list is a thing with a name.
        string[] judged =
        [
            "datastore.totalReadLatency.average",
            "datastore.totalWriteLatency.average",
            "datastore.datastoreVMObservedLatency.latest",
            "datastore.siocActiveTimePercentage.average",
            "datastore.numberReadAveraged.average",
            "datastore.numberWriteAveraged.average",
        ];

        var resolution = judged
            .Where(byKey.ContainsKey)
            .Select(k => byKey[k])
            .ToList();

        var storage = await client.QueryPerfAsync(
            [.. payload.Hosts.Select(h => h.MoRef)], VsphereEntityType.HostSystem, resolution,
            DateTimeOffset.UtcNow, cancellation.Token);

        foreach (var group in storage.SelectMany(s => s.Values)
            .Where(v => v.Instance.Length > 0)
            .GroupBy(v => v.CounterName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var readings = group.ToList();
            var nonZero = readings.Count(v => v.Raw > 0);

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {group.Key,-44} {nonZero,5} of {readings.Count,-5} above zero   " +
                $"max {readings.Max(v => v.Raw),10:0.###} {readings[0].Unit}"));
        }

        Console.WriteLine();
        Console.WriteLine("  A latency counter reading zero while the IOPS counters do not is not a");
        Console.WriteLine("  fast volume. Whole-millisecond counters truncate anything faster than");
        Console.WriteLine("  1ms to 0, and VMObservedLatency reports only while SIOC is active.");

        // How much detail is being thrown away. The product collapses a host's
        // per-device series into one value per counter, which answers "is this
        // host's storage slow" and cannot answer "which LUN" or "which path" —
        // the lower half of the diagnostic ladder. The numbers below are what
        // that costs on a real host.
        Section("Per-device detail available on one host");

        // The counters the product actually collects on a host, and then the
        // storage groups the diagnostic ladder needs next. Ordering by device
        // count instead would fill the screen with sys.resource*, which has an
        // instance per running world and answers nothing anybody asks.
        foreach (var group in hostMetrics
            .Where(m => VsphereCounters.Host.Contains(m.Counter.Key, StringComparer.OrdinalIgnoreCase) &&
                        !m.Counter.Key.StartsWith("datastore.", StringComparison.Ordinal))
            .GroupBy(m => m.Counter.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var devices = group.Count(m => m.Instance.Length > 0);
            var hasAggregate = group.Any(m => m.Instance.Length == 0);
            var sample = group.FirstOrDefault(m => m.Instance.Length > 0)?.Instance ?? string.Empty;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {group.Key,-40} {devices,4} devices  " +
                $"{(hasAggregate ? "+ aggregate" : "no aggregate "),-13} " +
                $"{(sample.Length > 0 ? "e.g. " + Show(sample, mask) : string.Empty)}"));
        }

        Console.WriteLine();
        Console.WriteLine("  The product currently stores one value per counter here, collapsing the");
        Console.WriteLine("  devices. That answers 'is this host's storage slow' and cannot answer");
        Console.WriteLine("  'which LUN' or 'which path'.");
        Console.WriteLine();

        foreach (var prefix in new[] { "storagePath.", "disk." })
        {
            var inGroup = hostMetrics
                .Where(m => m.Counter.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            var counters = inGroup.Select(m => m.Counter.Key).Distinct(StringComparer.Ordinal).Count();
            var devices = inGroup.Select(m => m.Instance)
                .Where(i => i.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {prefix,-14} {counters,3} counters x {devices,4} devices = {inGroup.Count,5} series available"));
        }
    }

    Section("Sample read");
    var usable = VsphereCounters.Host.Where(availableSet.Contains).Select(k => byKey[k]).ToList();

    if (usable.Count == 0)
    {
        Console.WriteLine("  No usable host counters; nothing to sample.");
        return 0;
    }

    var samples = await client.QueryPerfAsync(
        [probeHost.MoRef], VsphereEntityType.HostSystem, usable,
        DateTimeOffset.UtcNow, cancellation.Token);

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

    // The question a host probe cannot answer. Datastores are read on the
    // 5-minute historical interval rather than the real-time feed, and the two
    // behave differently enough that "hosts work" says nothing about them:
    // against a live estate every datastore came back unmeasured while every
    // host was fine.
    Section("Datastore availability");

    if (payload.Datastores.Count == 0)
    {
        Console.WriteLine("  No datastores in this inventory.");
    }
    else
    {
        var probeDatastore = payload.Datastores[0];
        var datastoreInterval = VsphereIntervals.IntervalSecondsFor(VsphereEntityType.Datastore);

        var datastoreAvailable = await client.GetAvailableCounterKeysAsync(
            probeDatastore.MoRef, VsphereEntityType.Datastore,
            DateTimeOffset.UtcNow, cancellation.Token);

        Console.WriteLine($"  datastore                {Show(probeDatastore.Name, mask)}");
        Console.WriteLine($"  interval                 {datastoreInterval}s");
        Console.WriteLine($"  counters it will supply  {datastoreAvailable.Count}");
        Console.WriteLine();

        foreach (var key in datastoreAvailable.OrderBy(k => k, StringComparer.Ordinal))
        {
            var level = byKey.TryGetValue(key, out var known)
                ? known.Level.ToString(CultureInfo.InvariantCulture)
                : "?";

            Console.WriteLine($"    {key,-52} level={level}");
        }

        Console.WriteLine();

        var wantedDatastore = VsphereCounters.Datastore;
        var supplied = new HashSet<string>(datastoreAvailable, StringComparer.OrdinalIgnoreCase);

        foreach (var key in wantedDatastore)
        {
            var defined = byKey.ContainsKey(key);
            var verdict = supplied.Contains(key)
                ? "available"
                : defined ? "defined but not supplied" : "not defined on this vCenter";

            Console.WriteLine($"  wanted: {key,-44} {verdict}");
        }
    }

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
finally
{
    // The probe signs in, so it signs out. It used to leave a session behind
    // on every run, which is an odd habit for the tool that checks for them.
    await channel.LogoutAsync(CancellationToken.None);
}

// Enough of a session key to tell two apart, not enough to be one.
static string Tail(string? key) =>
    string.IsNullOrEmpty(key) ? "?" : key[^Math.Min(6, key.Length)..];

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

// Keeps enough of a name to tell entries apart without disclosing it.
static string Show(string value, bool mask) =>
    !mask ? value : value.Length <= 4 ? "****" : $"{value[..2]}***{value[^2..]}";
