using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// The measurement gate for new inventory paths and calls (collection PR 1).
/// </summary>
/// <remarks>
/// <para>
/// Every candidate is read on its own, so a path vCenter refuses costs that
/// one read and nothing else: in the collector's list one invalid path fails
/// the entire retrieval (InvalidProperty, measured with
/// <c>configurationEx.dasConfig</c>).
/// </para>
/// <para>
/// Names, types and counts only. No value is printed — not a date, a key, a
/// status or a name — so the output can go into a pull request as it is.
/// </para>
/// </remarks>
internal static class Candidates
{
    /// <summary>M8.4, M8.7 and the "export first" verdicts, per type.</summary>
    private static readonly (string Type, string Path)[] Paths =
    [
        // Export first: vCenter's own verdicts.
        ("HostSystem", "configIssue"),
        ("VirtualMachine", "configIssue"),
        ("ClusterComputeResource", "configIssue"),
        ("Datastore", "configIssue"),
        ("HostSystem", "runtime.healthSystemRuntime"),
        ("VirtualMachine", "runtime.connectionState"),
        ("Datastore", "summary.maintenanceMode"),

        // M8.4: maintenance and vMotion blockers.
        ("VirtualMachine", "runtime.consolidationNeeded"),
        ("VirtualMachine", "config.hardware.device"),
        ("Datastore", "host"),
        ("HostSystem", "summary.currentEVCModeKey"),
        ("HostSystem", "summary.maxEVCModeKey"),
        ("ClusterComputeResource", "summary.currentEVCModeKey"),
        ("ClusterComputeResource", "summary"),

        // M8.7: expiry radar.
        ("HostSystem", "config.certificate"),
        ("HostSystem", "configManager.certificateManager"),
    ];

    /// <summary>
    /// M6.0b's gate: whether vim25's own idea of a host's identity
    /// (<c>hardware.systemInfo</c> and <c>summary.hardware.otherIdentifyingInfo</c>)
    /// is readable at all, before any collector or cross-source match against
    /// Redfish's <c>Systems/1.{SerialNumber,UUID}</c> is written.
    /// </summary>
    /// <remarks>
    /// Both are new property paths: the product's own gate rule is "measure
    /// before requesting", so this read alone -- names and counts, never a
    /// value -- is the measurement. <c>otherIdentifyingInfo</c> is an array of
    /// structures the same shape collection PR 1 already measured for
    /// <c>config.hardware.device</c> and <c>Datastore.host</c>: read once,
    /// described by <see cref="Describe"/> and walked by <see cref="Tree"/>
    /// like every other structure array here.
    /// </remarks>
    public static async Task RunHostIdentityAsync(VsphereClient client, CancellationToken cancellationToken)
    {
        Section("Host identity candidates (each read alone; names and counts only)");

        var systemInfo = await client.ReadCandidatePathAsync(
            "HostSystem", "hardware.systemInfo", cancellationToken);
        Describe(systemInfo);

        var otherIdentifyingInfo = await client.ReadCandidatePathAsync(
            "HostSystem", "summary.hardware.otherIdentifyingInfo", cancellationToken);
        Describe(otherIdentifyingInfo);
    }

    public static async Task RunAsync(
        VsphereClient client,
        Uri baseAddress,
        string user,
        string password,
        bool insecure,
        CancellationToken cancellationToken)
    {
        Section("Candidate property paths (each read alone; names and counts only)");

        var reads = new Dictionary<string, VsphereCandidateRead>(StringComparer.Ordinal);

        foreach (var (type, path) in Paths)
        {
            var read = await client.ReadCandidatePathAsync(type, path, cancellationToken);
            reads[read.Target] = read;
            Describe(read);
        }

        Section("M8.4 detail");
        CdromDetail(reads["VirtualMachine.config.hardware.device"]);
        MountDetail(reads["Datastore.host"]);

        Section("M8.7 detail");
        CertificateBytesDetail(reads["HostSystem.config.certificate"]);
        await CertificateManagerDetailAsync(client, reads["HostSystem.configManager.certificateManager"], cancellationToken);
        await LicenseDetailAsync(client, cancellationToken);
        await TlsDetailAsync(baseAddress, cancellationToken);

        var hosts = reads["HostSystem.configIssue"].Objects.Select(o => o.MoRef).ToList();
        foreach (var (label, filter) in new (string, IReadOnlyList<string>?)[]
                 {
                     ("no filter", null),
                     ($"entity = {hosts.Count} hosts", hosts),
                 })
        {
            Section($"Host profile compliance (QueryComplianceStatus, {label})");
            var (results, fault, characters, elapsed) =
                await client.ReadComplianceStatusAsync(filter, cancellationToken);
            Console.WriteLine(fault is null
                ? $"  results {results.Count}   reply {characters} chars   {elapsed.TotalMilliseconds:0} ms"
                : $"  FAULT {fault}   {elapsed.TotalMilliseconds:0} ms");
            Tree(results, depth: 2);
        }

        Section("M8.9 appliance REST with the same read-only account (status codes only)");
        await ApplianceRestAsync(baseAddress, user, password, insecure, cancellationToken);
    }

    /// <summary>
    /// Collection PR 2's gate: the root folder's triggered alarms and each
    /// host's maximum EVC mode, read alone. Names, types and counts only.
    /// </summary>
    public static async Task RunPr2Async(VsphereClient client, CancellationToken cancellationToken)
    {
        Section("Collection PR 2 candidates (each read alone; names and counts only)");

        var root = await client.GetRootFolderAsync(cancellationToken);
        var rootAlarms = await client.ReadCandidateObjectsAsync(
            "Folder", [root], "triggeredAlarmState", cancellationToken);
        Describe(rootAlarms);
        RootAlarmDetail(rootAlarms);

        // What the collector already reads, for comparison: the union of the
        // four types' alarm keys against the root folder's.
        var collected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { "HostSystem", "VirtualMachine", "ClusterComputeResource", "Datastore" })
        {
            var read = await client.ReadCandidatePathAsync(type, "triggeredAlarmState", cancellationToken);
            Console.WriteLine(read.Fault is null
                ? $"  {type,-24} triggeredAlarmState  objects {read.Objects.Count}, alarm states {AlarmStates(read).Count}"
                : $"  {type,-24} triggeredAlarmState  FAULT {read.Fault}");
            collected.UnionWith(AlarmStates(read).Select(s => s.TextOf("key")));
        }

        var rootKeys = AlarmStates(rootAlarms).Select(s => s.TextOf("key")).ToHashSet(StringComparer.Ordinal);
        Console.WriteLine($"  root keys also on a collected object   {rootKeys.Count(collected.Contains)}");
        Console.WriteLine($"  root keys on no collected object       {rootKeys.Count(k => !collected.Contains(k))}");
        Console.WriteLine($"  collected keys missing from the root   {collected.Count(k => !rootKeys.Contains(k))}");

        var maxEvc = await client.ReadCandidatePathAsync("HostSystem", "summary.maxEVCModeKey", cancellationToken);
        Describe(maxEvc);

        var parents = await client.ReadCandidatePathAsync("HostSystem", "parent", cancellationToken);
        var summaries = await client.ReadCandidatePathAsync("ClusterComputeResource", "summary", cancellationToken);
        EvcDetail(maxEvc, parents, summaries);
    }

    private static List<PropertyNode> AlarmStates(VsphereCandidateRead read) =>
        read.Fault is not null
            ? []
            : [.. read.Objects.SelectMany(o =>
                o.Structures.TryGetValue("triggeredAlarmState", out var states) ? states : [])];

    private static void RootAlarmDetail(VsphereCandidateRead read)
    {
        var states = AlarmStates(read);
        Console.WriteLine($"  root alarm states              {states.Count}");
        Console.WriteLine("  by entity type                 " + string.Join(", ", states
            .GroupBy(s => s.TypeOf("entity") is { Length: > 0 } t ? t : "(untyped)")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}({g.Count()})")));
        Console.WriteLine("  by overallStatus               " + string.Join(", ", states
            .GroupBy(s => s.TextOf("overallStatus") is { Length: > 0 } t ? t : "(none)")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}({g.Count()})")));

        var rootMoRef = read.Objects.Count > 0 ? read.Objects[0].MoRef : string.Empty;
        Console.WriteLine($"  raised on the root folder itself   {states.Count(s => s.TextOf("entity") == rootMoRef)}");
        Console.WriteLine($"  with key, entity and alarm         {states.Count(s =>
            s.TextOf("key").Length > 0 && s.TextOf("entity").Length > 0 && s.TextOf("alarm").Length > 0)}");
        Console.WriteLine($"  distinct keys                      {states.Select(s => s.TextOf("key")).Distinct(StringComparer.Ordinal).Count()}");
    }

    /// <summary>Per cluster: EVC on or off, hosts, distinct max modes. Counts only.</summary>
    private static void EvcDetail(
        VsphereCandidateRead maxEvc, VsphereCandidateRead parents, VsphereCandidateRead summaries)
    {
        if (maxEvc.Fault is not null || parents.Fault is not null || summaries.Fault is not null)
        {
            Console.WriteLine("  EVC detail not read");
            return;
        }

        var parentOf = parents.Objects.ToDictionary(
            o => o.MoRef, o => o.Values.GetValueOrDefault("parent") ?? string.Empty, StringComparer.Ordinal);
        var modeOf = maxEvc.Objects.ToDictionary(
            o => o.MoRef, o => o.Values.GetValueOrDefault("summary.maxEVCModeKey"), StringComparer.Ordinal);

        var index = 0;
        foreach (var cluster in summaries.Objects.OrderBy(o => o.MoRef, StringComparer.Ordinal))
        {
            index++;
            var evcOn = cluster.Structures.TryGetValue("summary", out var summary) &&
                        summary.Any(n => n.Name == "currentEVCModeKey" && n.Text.Length > 0);
            var hosts = parentOf.Where(p => p.Value == cluster.MoRef).Select(p => p.Key).ToList();
            var read = hosts.Where(h => modeOf.GetValueOrDefault(h) is { Length: > 0 }).ToList();
            var distinct = read.Select(h => modeOf[h]).Distinct(StringComparer.Ordinal).Count();

            Console.WriteLine(
                $"  cluster #{index}   EVC {(evcOn ? "on " : "off")}   hosts {hosts.Count}, " +
                $"maxEVCModeKey read {read.Count}, distinct {distinct}");
        }

        var standalone = parentOf.Count(p => !summaries.Objects.Any(c => c.MoRef == p.Value));
        Console.WriteLine($"  hosts outside a cluster        {standalone}");
    }

    private static void Describe(VsphereCandidateRead read)
    {
        Console.WriteLine();
        if (read.Fault is { } fault)
        {
            Console.WriteLine($"  {read.Target,-50} FAULT  {fault}");
            return;
        }

        var property = read.Target[(read.Target.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var total = read.Objects.Count;
        var values = read.Objects.Where(o => o.Values.ContainsKey(property)).ToList();
        var nonEmpty = values.Count(o => o.Values[property].Length > 0);
        var structures = read.Objects.Count(o => o.Structures.ContainsKey(property));
        var missing = read.Objects.SelectMany(o => o.Missing).Where(m => m.Path == property)
            .GroupBy(m => m.FaultType.Length == 0 ? "(untyped)" : m.FaultType)
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToList();
        var absent = total - values.Count - structures - read.Objects.Count(o => o.Missing.Any(m => m.Path == property));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {read.Target,-50} SEEN   objects {total}, value {values.Count} (non-empty {nonEmpty}), " +
            $"structure {structures}, missing {missing.Count} {string.Join(' ', missing)}, absent {absent}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {string.Empty,-50}        pages {read.Pages}, reply {read.ReplyCharacters} chars, {read.Elapsed.TotalMilliseconds:0} ms"));

        var nodes = read.Objects
            .Where(o => o.Structures.ContainsKey(property))
            .SelectMany(o => o.Structures[property])
            .ToList();

        // The device list is hundreds of types deep; its detail is below.
        Tree(nodes, depth: property == "config.hardware.device" ? 1 : 4);
    }

    /// <summary>Element names and xsi:types, aggregated, to a depth.</summary>
    private static void Tree(IReadOnlyList<PropertyNode> nodes, int depth)
    {
        var lines = new List<string>();
        Walk(nodes, string.Empty, 1);

        foreach (var line in lines.Take(60))
        {
            Console.WriteLine("      " + line);
        }

        if (lines.Count > 60)
        {
            Console.WriteLine($"      ... {lines.Count - 60} more lines");
        }

        void Walk(IEnumerable<PropertyNode> level, string prefix, int at)
        {
            foreach (var group in level
                .GroupBy(n => (n.Name, n.Type))
                .OrderBy(g => g.Key.Name, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Type, StringComparer.Ordinal))
            {
                var type = group.Key.Type.Length == 0 ? string.Empty : $" <{group.Key.Type}>";
                lines.Add($"{prefix}{group.Key.Name}{type} x{group.Count()}");

                if (at < depth)
                {
                    Walk(group.SelectMany(n => n.Children), prefix + "  ", at + 1);
                }
            }
        }
    }

    private static void CdromDetail(VsphereCandidateRead read)
    {
        if (read.Fault is not null)
        {
            Console.WriteLine("  CD/ISO: device list not read");
            return;
        }

        var byVm = read.Objects
            .Select(o => o.Structures.TryGetValue("config.hardware.device", out var devices)
                ? devices.Where(d => d.Type == "VirtualCdrom").ToList()
                : [])
            .ToList();

        var cdroms = byVm.SelectMany(c => c).ToList();
        bool Connected(PropertyNode cd) =>
            cd.Child("connectable")?.TextOf("connected") == "true";

        Console.WriteLine($"  VirtualCdrom devices          {cdroms.Count} on {byVm.Count(c => c.Count > 0)} VMs");
        foreach (var backing in cdroms.GroupBy(c => c.TypeOf("backing") is { Length: > 0 } t ? t : "(no backing)"))
        {
            Console.WriteLine($"    backing {backing.Key,-44} {backing.Count(),4}, connected {backing.Count(Connected)}");
        }

        Console.WriteLine($"  VMs with a connected CD       {byVm.Count(c => c.Any(Connected))}");
        Console.WriteLine($"  connectable children          " +
                          string.Join(", ", cdroms.SelectMany(c => c.Child("connectable")?.Children ?? [])
                              .GroupBy(n => n.Name).Select(g => $"{g.Key}({g.Count()})")));
    }

    private static void MountDetail(VsphereCandidateRead read)
    {
        if (read.Fault is not null)
        {
            Console.WriteLine("  datastore mounts not read");
            return;
        }

        var perDatastore = read.Objects
            .Select(o => o.Structures.TryGetValue("host", out var mounts)
                ? mounts.Count(m => m.Child("mountInfo")?.TextOf("mounted") != "false")
                : 0)
            .ToList();

        Console.WriteLine("  mounted hosts per datastore   " + string.Join(", ", perDatastore
            .GroupBy(n => n).OrderBy(g => g.Key).Select(g => $"{g.Key} host(s): {g.Count()}")));
    }

    private static void CertificateBytesDetail(VsphereCandidateRead read)
    {
        if (read.Fault is not null)
        {
            Console.WriteLine("  config.certificate not read");
            return;
        }

        var parsed = 0;
        var notAfter = 0;
        foreach (var raw in read.Objects
            .Select(o => o.Values.TryGetValue("config.certificate", out var v) ? v : null)
            .OfType<string>())
        {
            try
            {
                var bytes = PropertyCollectorParser.SplitValues(raw)
                    .Select(b => unchecked((byte)sbyte.Parse(b, CultureInfo.InvariantCulture)))
                    .ToArray();
                using var certificate = X509CertificateLoader.LoadCertificate(bytes);
                parsed++;
                if (certificate.NotAfter > DateTime.MinValue)
                {
                    notAfter++;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException
                                           or System.Security.Cryptography.CryptographicException)
            {
            }
        }

        Console.WriteLine($"  config.certificate            parses as X.509 on {parsed} of {read.Objects.Count} hosts, notAfter readable on {notAfter}");
    }

    private static async Task CertificateManagerDetailAsync(
        VsphereClient client, VsphereCandidateRead managers, CancellationToken cancellationToken)
    {
        var refs = managers.Objects
            .Select(o => o.Values.TryGetValue("configManager.certificateManager", out var v) ? v : null)
            .OfType<string>()
            .Where(v => v.Length > 0)
            .ToList();

        foreach (var path in new[] { "certificateInfo" })
        {
            var read = await client.ReadCandidateObjectsAsync("HostCertificateManager", refs, path, cancellationToken);
            Describe(read);
            var withNotAfter = read.Objects.Count(o =>
                o.Structures.TryGetValue(path, out var nodes) && nodes.Any(n => n.Name == "notAfter"));
            Console.WriteLine($"  HostCertificateManager with certificateInfo.notAfter: {withNotAfter} of {refs.Count}");
        }
    }

    private static async Task LicenseDetailAsync(VsphereClient client, CancellationToken cancellationToken)
    {
        var manager = await client.GetLicenseManagerAsync(cancellationToken);
        if (manager is null)
        {
            Console.WriteLine("  vCenter offers no licenseManager");
            return;
        }

        var read = await client.ReadCandidateObjectsAsync("LicenseManager", [manager], "licenses", cancellationToken);
        Describe(read);

        var licenses = read.Objects.SelectMany(o =>
            o.Structures.TryGetValue("licenses", out var nodes) ? nodes : []).ToList();
        var propertyKeys = licenses
            .SelectMany(l => l.All("properties"))
            .Select(p => p.TextOf("key"))
            .GroupBy(k => k)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}({g.Count()})");

        Console.WriteLine($"  licenses {licenses.Count}; property keys: {string.Join(", ", propertyKeys)}");
    }

    private static async Task TlsDetailAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(baseAddress.Host, baseAddress.Port, cancellationToken);

            X509Certificate2? seen = null;
#pragma warning disable CA5359 // Justified: the certificate is read for its dates; no request is sent.
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    // Read, never trusted for anything: no request is sent.
                    if (certificate is not null)
                    {
                        seen = new X509Certificate2(certificate);
                    }

                    return true;
                });
#pragma warning restore CA5359

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = baseAddress.Host }, cancellationToken);

            Console.WriteLine(seen is null
                ? "  vCenter TLS handshake          completed, no certificate presented"
                : $"  vCenter TLS handshake          certificate seen, notAfter readable: {seen.NotAfter > DateTime.MinValue}");
            seen?.Dispose();
        }
#pragma warning disable CA1031 // A probe reports, it does not throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine($"  vCenter TLS handshake          FAILED ({ex.GetType().Name})");
        }
    }

    private static async Task ApplianceRestAsync(
        Uri baseAddress, string user, string password, bool insecure, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler();
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };

        string? token = null;
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

            using var response = await http.SendAsync(login, cancellationToken);
            Console.WriteLine($"  POST /api/session                          {(int)response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Trim('"');
            }
        }

        Console.WriteLine($"  session created                            {(token is { Length: > 0 } ? "yes" : "no")}");
        if (token is not { Length: > 0 })
        {
            return;
        }

        try
        {
            foreach (var path in new[] { "/api/appliance/health/system", "/api/appliance/recovery/backup/job" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("vmware-api-session-id", token);
                using var response = await http.SendAsync(request, cancellationToken);
                Console.WriteLine($"  GET {path,-39}{(int)response.StatusCode}");
            }
        }
        finally
        {
            using var logout = new HttpRequestMessage(HttpMethod.Delete, "/api/session");
            logout.Headers.TryAddWithoutValidation("vmware-api-session-id", token);
            using var response = await http.SendAsync(logout, CancellationToken.None);
            Console.WriteLine($"  DELETE /api/session                        {(int)response.StatusCode}");
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }
}
