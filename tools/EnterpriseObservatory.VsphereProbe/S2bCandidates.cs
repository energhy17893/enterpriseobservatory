using System.Globalization;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// S2b's gate (eo-simplivity, cross-environment controls): every new path
/// read alone, the lockdown exception call tried once per host, and counts
/// of what each control would find.
/// </summary>
/// <remarks>
/// Names, types and counts, as <see cref="Candidates"/>. Two exceptions,
/// both needed to decide and neither a secret: the port group names of
/// vmkernel adapters (which one is SimpliVity storage/federation is HPE's
/// naming, read here rather than assumed), and nothing else. The OVC VMs are
/// told apart here by HPE's default <c>OmniStackVC-</c> name prefix — an
/// estimate for the report only; the check matches the SimpliVity
/// annotation's <c>virtual_controller_name</c> on the host.
/// </remarks>
internal static class S2bCandidates
{
    private static readonly (string Type, string Path)[] Paths =
    [
        ("VirtualMachine", "config.memoryAllocation.reservation"),
        ("VirtualMachine", "resourceConfig.memoryAllocation.reservation"),
        ("VirtualMachine", "resourcePool"),
        ("ClusterComputeResource", "resourcePool"),
        ("HostSystem", "configManager.hostAccessManager"),
        ("HostSystem", "config.network.vnic"),
        ("HostSystem", "summary.hardware.cpuModel"),
    ];

    public static async Task RunAsync(VsphereClient client, string? rawDirectory, CancellationToken cancellationToken)
    {
        Section("S2b candidates (each read alone; names and counts only)");

        var reads = new Dictionary<string, VsphereCandidateRead>(StringComparer.Ordinal);

        foreach (var (type, path) in Paths)
        {
            var read = await client.ReadCandidatePathAsync(type, path, cancellationToken);
            reads[read.Target] = read;
            Describe(read);
        }

        async Task<VsphereCandidateRead> Also(string type, string path)
        {
            var read = await client.ReadCandidatePathAsync(type, path, cancellationToken);
            reads[read.Target] = read;
            return read;
        }

        var names = await Also("VirtualMachine", "name");
        var hostOf = await Also("VirtualMachine", "runtime.host");
        var memory = await Also("VirtualMachine", "config.hardware.memoryMB");
        var parents = await Also("HostSystem", "parent");
        var vswitch = await Also("HostSystem", "config.network.vswitch");
        var lockdown = await Also("HostSystem", "config.lockdownMode");
        var configurationEx = await Also("ClusterComputeResource", "configurationEx");
        var summary = await Also("ClusterComputeResource", "summary");
        var maxEvc = await Also("HostSystem", "summary.maxEVCModeKey");

        Section("OVC VMs (name prefix OmniStackVC-, estimate)");
        var ovcs = Value(names, "name").Where(p => p.Value.StartsWith("OmniStackVC-", StringComparison.Ordinal))
            .Select(p => p.Key).ToList();
        var reservation = Value(reads["VirtualMachine.config.memoryAllocation.reservation"], "config.memoryAllocation.reservation");
        var resourceReservation = Value(reads["VirtualMachine.resourceConfig.memoryAllocation.reservation"], "resourceConfig.memoryAllocation.reservation");
        var memoryMb = Value(memory, "config.hardware.memoryMB");
        var pool = Value(reads["VirtualMachine.resourcePool"], "resourcePool");
        var host = Value(hostOf, "runtime.host");
        var parent = Value(parents, "parent");
        var rootPool = Value(reads["ClusterComputeResource.resourcePool"], "resourcePool");

        Console.WriteLine($"  OVC VMs                          {ovcs.Count}");
        Console.WriteLine($"  config and resourceConfig agree  {ovcs.Count(v => reservation.GetValueOrDefault(v) == resourceReservation.GetValueOrDefault(v))} of {ovcs.Count}; all VMs {reservation.Count(p => resourceReservation.GetValueOrDefault(p.Key) == p.Value)} of {reservation.Count}");
        Console.WriteLine($"  reservation == memoryMB          {ovcs.Count(v => reservation.GetValueOrDefault(v) is { } r && r == memoryMb.GetValueOrDefault(v))} of {ovcs.Count}");
        Console.WriteLine($"  in the cluster's root pool       {ovcs.Count(v => host.GetValueOrDefault(v) is { } h && parent.GetValueOrDefault(h) is { } c && rootPool.GetValueOrDefault(c) is { } root && root == pool.GetValueOrDefault(v))} of {ovcs.Count}");
        Console.WriteLine($"  resourcePool absent              {ovcs.Count(v => !pool.ContainsKey(v))}; all VMs {Value(names, "name").Count - pool.Count}");

        Section("vmkernel adapters (port group names; mtu)");
        var vnicRead = reads["HostSystem.config.network.vnic"];
        var vnics = vnicRead.Objects.SelectMany(o => o.Structures.TryGetValue("config.network.vnic", out var n) ? n : []).ToList();
        foreach (var group in vnics
            .GroupBy(n => (Portgroup: n.TextOf("portgroup") is { Length: > 0 } pg ? pg : "(dvs port)", Mtu: n.Child("spec")?.TextOf("mtu") is { Length: > 0 } m ? m : "(none)"))
            .OrderBy(g => g.Key.Portgroup, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {group.Key.Portgroup,-40} mtu {group.Key.Mtu,-7} x{group.Count()}");
        }

        Console.WriteLine("  vswitch mtu      " + string.Join(", ", vswitch.Objects
            .SelectMany(o => o.Structures.TryGetValue("config.network.vswitch", out var n) ? n : [])
            .GroupBy(n => n.TextOf("mtu") is { Length: > 0 } m ? m : "(none)")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}({g.Count()})")));
        Tree([.. vswitch.Objects.SelectMany(o => o.Structures.TryGetValue("config.network.vswitch", out var n) ? n : [])], 2);

        // What the collector's parser makes of the two, per host: the SVT_
        // lines only, tallied.
        var switchesByHost = vswitch.Objects.ToDictionary(o => o.MoRef, StringComparer.Ordinal);
        var parsed = vnicRead.Objects.Select(o =>
        {
            var structures = new Dictionary<string, IReadOnlyList<PropertyNode>>(o.Structures, StringComparer.Ordinal);
            if (switchesByHost.TryGetValue(o.MoRef, out var s) && s.Structures.TryGetValue("config.network.vswitch", out var sw))
            {
                structures["config.network.vswitch"] = sw;
            }

            return InventoryVerdictParser.Read(o with { Structures = structures });
        }).ToList();

        foreach (var key in new[] { InventoryVerdicts.VmkernelMtu, InventoryVerdicts.PortGroupSwitchMtu })
        {
            Console.WriteLine($"  {key,-22} hosts with the key {parsed.Count(p => p.ContainsKey(key))}; SVT_ lines: " + string.Join(", ", parsed
                .SelectMany(p => (p.GetValueOrDefault(key) ?? string.Empty).Split('\n'))
                .Where(l => l.StartsWith("SVT_", StringComparison.OrdinalIgnoreCase))
                .GroupBy(l => l, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} x{g.Count()}")));
        }

        Section("Lockdown exceptions (QueryLockdownExceptions per host)");
        Console.WriteLine("  lockdownMode     " + string.Join(", ", Value(lockdown, "config.lockdownMode")
            .GroupBy(p => p.Value).Select(g => $"{g.Key}({g.Count()})")));
        var managers = Value(reads["HostSystem.configManager.hostAccessManager"], "configManager.hostAccessManager");
        var outcomes = new List<string>();
        foreach (var manager in managers.Values)
        {
            var (users, fault, reply) = await client.ReadLockdownExceptionsAsync(manager, cancellationToken);
            outcomes.Add(fault is null ? $"read, {users.Count} users, {reply.Length} chars" : $"FAULT {fault}");
        }

        foreach (var outcome in outcomes.GroupBy(o => o).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {outcome.Key}  x{outcome.Count()}");
        }

        Section("DRS must-run groups (per cluster with one)");
        var index = 0;
        foreach (var cluster in configurationEx.Objects.OrderBy(o => o.MoRef, StringComparer.Ordinal))
        {
            index++;
            var groups = PropertyCollectorParser.ReadClusterGroups(cluster.Structures);
            var must = PropertyCollectorParser.ReadDrsRules(cluster.Structures)
                .Where(r => r.Enabled && r.Mandatory && r.Kind == EnterpriseObservatory.Domain.DrsRuleKind.VmHostAffine)
                .ToList();
            if (must.Count == 0)
            {
                continue;
            }

            var perHost = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var rule in must)
            {
                var vms = groups.FirstOrDefault(g => g.Name == rule.VmGroupName)?.MemberMoRefs ?? [];
                foreach (var h in groups.FirstOrDefault(g => g.Name == rule.HostGroupName)?.MemberMoRefs ?? [])
                {
                    (perHost.TryGetValue(h, out var set) ? set : perHost[h] = new(StringComparer.Ordinal)).UnionWith(vms);
                }
            }

            Console.WriteLine($"  cluster #{index}   must rules {must.Count}, hosts bound {perHost.Count}, max VMs per host {(perHost.Count == 0 ? 0 : perHost.Values.Max(s => s.Count))}");
        }

        Section("EVC and CPU model per cluster");
        var cpu = Value(reads["HostSystem.summary.hardware.cpuModel"], "summary.hardware.cpuModel");
        var evcMax = Value(maxEvc, "summary.maxEVCModeKey");
        index = 0;
        foreach (var cluster in summary.Objects.OrderBy(o => o.MoRef, StringComparer.Ordinal))
        {
            index++;
            var evcOn = cluster.Structures.TryGetValue("summary", out var s) && s.Any(n => n.Name == "currentEVCModeKey" && n.Text.Length > 0);
            var members = parent.Where(p => p.Value == cluster.MoRef).Select(p => p.Key).ToList();
            var models = members.Select(cpu.GetValueOrDefault).OfType<string>().Distinct(StringComparer.Ordinal).Count();
            var modes = members.Select(evcMax.GetValueOrDefault).OfType<string>().Distinct(StringComparer.Ordinal).Count();
            if (models > 1 || modes > 1)
            {
                Console.WriteLine($"  cluster #{index}   EVC {(evcOn ? "on " : "off")}   hosts {members.Count}, cpu models {models}, max EVC modes {modes}");
            }
        }

        Console.WriteLine($"  clusters with one cpu model and one EVC mode are not listed ({index} clusters)");

        if (rawDirectory is not null)
        {
            await WriteRawAsync(client, rawDirectory, ovcs, host, parent, managers, cancellationToken);
        }
    }

    /// <summary>One OVC, its host and cluster, as vCenter sent them: fixtures, masked before they are kept.</summary>
    private static async Task WriteRawAsync(
        VsphereClient client,
        string directory,
        List<string> ovcs,
        Dictionary<string, string> host,
        Dictionary<string, string> parent,
        Dictionary<string, string> managers,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var vm = ovcs.FirstOrDefault(v => host.ContainsKey(v));
        if (vm is null)
        {
            Console.WriteLine("  raw: no OVC VM to write");
            return;
        }

        var h = host[vm];
        var c = parent.GetValueOrDefault(h) ?? string.Empty;
        var files = new (string File, string Type, string MoRef, string Path)[]
        {
            ("vm-reservation.xml", "VirtualMachine", vm, "config.memoryAllocation.reservation"),
            ("vm-resourceconfig.xml", "VirtualMachine", vm, "resourceConfig.memoryAllocation.reservation"),
            ("vm-resourcepool.xml", "VirtualMachine", vm, "resourcePool"),
            ("cluster-resourcepool.xml", "ClusterComputeResource", c, "resourcePool"),
            ("host-vnic.xml", "HostSystem", h, "config.network.vnic"),
            ("host-vswitch.xml", "HostSystem", h, "config.network.vswitch"),
            ("host-accessmanager.xml", "HostSystem", h, "configManager.hostAccessManager"),
        };

        foreach (var (file, type, moRef, path) in files)
        {
            var reply = await client.ReadCandidateRawAsync(type, moRef, path, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, file), reply, cancellationToken);
        }

        if (managers.TryGetValue(h, out var manager))
        {
            var (_, _, reply) = await client.ReadLockdownExceptionsAsync(manager, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "host-lockdown-exceptions.xml"), reply, cancellationToken);
        }

        Console.WriteLine($"  raw: {files.Length + 1} replies written, UNMASKED -- mask before keeping");
    }

    private static Dictionary<string, string> Value(VsphereCandidateRead read, string path) =>
        read.Objects
            .Where(o => o.Values.ContainsKey(path))
            .ToDictionary(o => o.MoRef, o => o.Values[path], StringComparer.Ordinal);

    private static void Describe(VsphereCandidateRead read)
    {
        Console.WriteLine();
        if (read.Fault is { } fault)
        {
            Console.WriteLine($"  {read.Target,-50} FAULT  {fault}");
            return;
        }

        var property = read.Target[(read.Target.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var values = read.Objects.Count(o => o.Values.ContainsKey(property));
        var structures = read.Objects.Count(o => o.Structures.ContainsKey(property));
        var missing = read.Objects.SelectMany(o => o.Missing).Where(m => m.Path == property)
            .GroupBy(m => m.FaultType).Select(g => $"{g.Key} x{g.Count()}");

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {read.Target,-50} SEEN   objects {read.Objects.Count}, value {values}, structure {structures}, " +
            $"missing [{string.Join(' ', missing)}], reply {read.ReplyCharacters} chars ({(read.Objects.Count == 0 ? 0 : read.ReplyCharacters / read.Objects.Count)}/object), {read.Elapsed.TotalMilliseconds:0} ms"));

        Tree([.. read.Objects.SelectMany(o => o.Structures.TryGetValue(property, out var n) ? n : [])], 3);
    }

    private static void Tree(IReadOnlyList<PropertyNode> nodes, int depth)
    {
        void Walk(IEnumerable<PropertyNode> level, string prefix, int at)
        {
            foreach (var group in level.GroupBy(n => (n.Name, n.Type)).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                var type = group.Key.Type.Length == 0 ? string.Empty : $" <{group.Key.Type}>";
                Console.WriteLine($"      {prefix}{group.Key.Name}{type} x{group.Count()}");
                if (at < depth)
                {
                    Walk(group.SelectMany(n => n.Children), prefix + "  ", at + 1);
                }
            }
        }

        Walk(nodes, string.Empty, 1);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }
}
