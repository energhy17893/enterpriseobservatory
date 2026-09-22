using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.VsphereProbe;

/// <summary>
/// M8.8's gate: where a backup product leaves "last backup" on a VM, and in
/// what form.
/// </summary>
/// <remarks>
/// <para>
/// Every path is read alone (one invalid path fails a whole retrieval). Field
/// definitions are metadata and are printed by name; a field's VALUES never
/// are. A value is reduced to a skeleton — digits become <c>9</c>, words
/// outside a fixed vendor vocabulary become <c>A</c> — and to which date
/// patterns it matches, and only the counts of each are printed.
/// </para>
/// </remarks>
internal static partial class BackupFields
{
    private static readonly string[] VmPaths = ["customValue", "value", "summary.customValue", "availableField"];

    /// <summary>Words a backup product writes as labels; anything else is masked.</summary>
    private static readonly HashSet<string> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "last", "backup", "backups", "backed", "up", "job", "jobs", "time", "result", "success", "successful",
        "succeeded", "warning", "warnings", "failed", "failure", "error", "server", "name", "veeam", "by", "at",
        "on", "date", "status", "ok", "am", "pm", "utc", "gmt", "full", "incremental", "replica", "replication",
        "completed", "created", "point", "restore", "copy", "and", "of", "in", "from", "to", "with", "is", "none",
        "never", "policy", "protected", "snapshot", "agent", "vm", "commvault", "veritas", "netbackup", "rubrik",
        "cohesity", "nakivo", "avamar", "networker", "arcserve", "altaro", "hycu", "dell", "data", "domain",
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november",
        "december", "mon", "tue", "wed", "thu", "fri", "sat", "sun",
    };

    public static async Task RunAsync(VsphereClient client, CancellationToken cancellationToken)
    {
        Section("M8.8 backup freshness gate (each read alone; names, keys and counts only)");

        // (a) The field definitions.
        var manager = await client.GetCustomFieldsManagerAsync(cancellationToken);
        Console.WriteLine($"  customFieldsManager offered     {(manager is { Length: > 0 } ? "yes" : "NO")}");

        var definitions = new Dictionary<string, (string Name, string ObjectType, string Type)>(StringComparer.Ordinal);
        if (manager is { Length: > 0 })
        {
            var read = await client.ReadCandidateObjectsAsync("CustomFieldsManager", [manager], "field", cancellationToken);
            Header(read);

            foreach (var field in read.Objects.SelectMany(o =>
                         o.Structures.TryGetValue("field", out var nodes) ? nodes : []))
            {
                definitions[field.TextOf("key")] = (
                    field.TextOf("name"),
                    field.TextOf("managedObjectType") is { Length: > 0 } t ? t : "(any type)",
                    field.TextOf("type"));
            }

            // A single definition has no array wrapper with structure children
            // when it is alone; say so rather than print zero.
            if (read.Fault is null && definitions.Count == 0)
            {
                Console.WriteLine("  field                           no definitions returned as structures");
                foreach (var o in read.Objects)
                {
                    Console.WriteLine($"    value keys: {string.Join(", ", o.Values.Keys)}");
                }
            }

            Console.WriteLine($"  definitions                     {definitions.Count}");
            foreach (var (key, d) in definitions.OrderBy(d => int.TryParse(d.Key, out var k) ? k : int.MaxValue))
            {
                Console.WriteLine($"    key {key,-5} {d.ObjectType,-24} {d.Type,-16} {d.Name}{(IsBackupName(d.Name) ? "   <- backup-named" : string.Empty)}");
            }

            Console.WriteLine("  field element tree:");
            Tree(read.Objects.SelectMany(o => o.Structures.TryGetValue("field", out var n) ? n : []).ToList());
        }

        // (b) The VM paths, each alone.
        var reads = new Dictionary<string, VsphereCandidateRead>(StringComparer.Ordinal);
        foreach (var path in VmPaths)
        {
            var read = await client.ReadCandidatePathAsync("VirtualMachine", path, cancellationToken);
            reads[path] = read;
            Header(read);

            if (read.Fault is null)
            {
                var nodes = read.Objects
                    .SelectMany(o => o.Structures.TryGetValue(path, out var n) ? n : [])
                    .ToList();
                Console.WriteLine($"    VMs with a structure {read.Objects.Count(o => o.Structures.ContainsKey(path))}, " +
                                  $"with an empty/flat value {read.Objects.Count(o => o.Values.ContainsKey(path))}, " +
                                  $"absent {read.Objects.Count(o => !o.Structures.ContainsKey(path) && !o.Values.ContainsKey(path))}");
                Tree(path == "availableField" ? [.. nodes.Take(0)] : nodes);
                if (path == "availableField")
                {
                    Console.WriteLine($"    availableField entries {nodes.Count} (tree omitted; same shape as field)");
                }
            }
        }

        var values = reads["customValue"];
        if (values.Fault is not null)
        {
            Console.WriteLine("  customValue not read; value classes skipped");
            return;
        }

        Section("Per field key: VMs carrying it (counts only)");
        var perVm = values.Objects
            .Select(o => (o.MoRef, Values: o.Structures.TryGetValue("customValue", out var n)
                ? n.Select(v => (Key: v.TextOf("key"), Type: v.Type, Value: v.TextOf("value"))).ToList()
                : []))
            .ToList();

        foreach (var group in perVm.SelectMany(v => v.Values)
                     .GroupBy(v => v.Key)
                     .OrderBy(g => int.TryParse(g.Key, out var k) ? k : int.MaxValue))
        {
            var name = definitions.TryGetValue(group.Key, out var d) ? d.Name : "(no definition)";
            Console.WriteLine(
                $"  key {group.Key,-5} {name,-40} VMs {group.Count(),4}, non-empty {group.Count(v => v.Value.Length > 0),4}, " +
                $"types {string.Join('/', group.Select(v => v.Type).Distinct(StringComparer.Ordinal))}");
        }

        Console.WriteLine($"  VMs read                        {perVm.Count}");
        Console.WriteLine($"  VMs with any custom value       {perVm.Count(v => v.Values.Count > 0)}");

        // (c) The value format of every backup-named field.
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, definition) in definitions.Where(d => IsBackupName(d.Value.Name)))
        {
            var texts = perVm.SelectMany(v => v.Values).Where(v => v.Key == key).Select(v => v.Value).ToList();
            Section($"Value classes of key {key} ({definition.Name}) — {texts.Count} values, none printed");
            Classify(texts, now);
        }

        // Values mentioning backup under a field whose name does not.
        var elsewhere = perVm.SelectMany(v => v.Values)
            .Where(v => !(definitions.TryGetValue(v.Key, out var d) && IsBackupName(d.Name)) && IsBackupName(v.Value))
            .GroupBy(v => v.Key)
            .ToList();
        Section("Backup-mentioning values under other fields");
        Console.WriteLine($"  fields                          {elsewhere.Count}");
        foreach (var group in elsewhere)
        {
            Console.WriteLine($"  key {group.Key}: {group.Count()} values");
            Classify([.. group.Select(v => v.Value)], now);
        }

        // The collector's own reading, through the full inventory request with
        // customValue in it: the gate proper (one bad path fails all of it).
        Section("The collector's reading (full inventory request; counts only)");
        var payload = await client.RetrieveInventoryAsync(cancellationToken);
        var vms = payload.VirtualMachines;
        Console.WriteLine($"  inventory read                  ok, {vms.Count} VMs, failures {payload.Failures.Count}");
        Console.WriteLine($"  customValue coverage            " + string.Join(", ", payload.Coverage
            .Where(c => c.Property == "customValue").Select(c => $"{c.Answered}/{c.Asked}")));
        Console.WriteLine($"  backup.read                     {vms.Count(v => v.Verdicts.ContainsKey(InventoryVerdicts.BackupRead))}");
        Console.WriteLine($"  with a backup attribute         {vms.Count(v => v.Verdicts.ContainsKey(InventoryVerdicts.BackupField))}");
        Console.WriteLine($"  of them read as a time          {vms.Count(v => v.Verdicts.ContainsKey(InventoryVerdicts.BackupLastUtc))}");
        Console.WriteLine($"  time basis                      " + string.Join(", ", vms
            .Select(v => v.Verdicts.GetValueOrDefault(InventoryVerdicts.BackupTimeBasis))
            .OfType<string>().GroupBy(b => b).Select(g => $"{g.Key} ({g.Count()})")));
        var judged = vms
            .Select(v => v.Verdicts.GetValueOrDefault(InventoryVerdicts.BackupLastUtc))
            .OfType<string>()
            .Select(s => now - DateTimeOffset.Parse(s, CultureInfo.InvariantCulture))
            .ToList();
        Console.WriteLine($"  within 24 h {judged.Count(a => a <= TimeSpan.FromHours(24) && a >= TimeSpan.FromMinutes(-15))}, " +
                          $"older {judged.Count(a => a > TimeSpan.FromHours(24))}, " +
                          $"future {judged.Count(a => a < TimeSpan.FromMinutes(-15))}");

        // Vendor independence: some products write the VM's Notes instead.
        var notes = await client.ReadCandidatePathAsync("VirtualMachine", "config.annotation", cancellationToken);
        Section("Notes (config.annotation) mentioning backup");
        Header(notes);
        if (notes.Fault is null)
        {
            var lines = notes.Objects
                .Select(o => o.Values.TryGetValue("config.annotation", out var a) ? a : string.Empty)
                .Where(IsBackupName)
                .ToList();
            Console.WriteLine($"  VMs whose Notes mention backup  {lines.Count}");
            Classify([.. lines.SelectMany(l => l.Split('\n')).Where(IsBackupName).Select(l => l.Trim())], now);
        }
    }

    private static bool IsBackupName(string text) =>
        text.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("yedek", StringComparison.OrdinalIgnoreCase);

    /// <summary>Skeletons, date patterns and ages, as counts.</summary>
    private static void Classify(IReadOnlyList<string> texts, DateTimeOffset now)
    {
        Console.WriteLine($"  empty                           {texts.Count(t => t.Length == 0)}");

        Console.WriteLine("  skeletons (9 = digit, A+ = masked words):");
        foreach (var group in texts.Where(t => t.Length > 0).GroupBy(Skeleton).OrderByDescending(g => g.Count()).Take(15))
        {
            Console.WriteLine($"    {group.Count(),4}  {group.Key}");
        }

        Console.WriteLine("  date patterns:");
        foreach (var (label, regex) in DatePatterns)
        {
            var hits = texts.Select(t => regex.Match(t)).Where(m => m.Success).ToList();
            Console.WriteLine($"    {label,-40} {hits.Count,4}");

            if (label.StartsWith("slash", StringComparison.Ordinal) && hits.Count > 0)
            {
                var first = hits.Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                var second = hits.Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
                Console.WriteLine($"      first part > 12 (day first) {first.Count(n => n > 12)}, second part > 12 (month first) {second.Count(n => n > 12)}");
            }
        }

        var dots = texts.Select(t => DotParts().Match(t)).Where(m => m.Success).ToList();
        if (dots.Count > 0)
        {
            Console.WriteLine($"    dot: first part > 12 (day first) {dots.Count(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 12)}, " +
                              $"second part > 12 (month first) {dots.Count(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > 12)}");
        }

        // Bracketed segments by position: what kind of thing each one is.
        var segments = texts.Select(t => Bracketed().Matches(t).Select(m => m.Groups[1].Value).ToList()).ToList();
        var widest = segments.Count == 0 ? 0 : segments.Max(s => s.Count);
        for (var i = 0; i < widest; i++)
        {
            var at = segments.Where(s => s.Count > i).Select(s => s[i]).ToList();
            var dates = at.Count(s => DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            var status = at.Count(s => StatusWords.Contains(s));
            Console.WriteLine($"    [segment {i}] {at.Count} values, distinct {at.Distinct(StringComparer.Ordinal).Count()}, " +
                              $"yyyyMMdd dates {dates}, known status words {status}" +
                              (status > 0 ? " (" + string.Join(", ", at.Where(StatusWords.Contains).GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} {g.Count()}")) + ")" : string.Empty));
        }

        // Words at the same place in every value are the template, not data.
        var labels = texts.Where(t => t.Length > 0).Select(t => LabelWords().Matches(t).Select(m => m.Value).ToList()).ToList();
        if (labels.Count > 0 && labels.All(l => l.Count == labels[0].Count))
        {
            var constant = Enumerable.Range(0, labels[0].Count)
                .Where(i => labels.All(l => string.Equals(l[i], labels[0][i], StringComparison.Ordinal)))
                .Select(i => labels[0][i]);
            Console.WriteLine($"    template words (identical in every value): {string.Join(" | ", constant)}");
        }

        Console.WriteLine("  ages, dot read day-first and slash month-first, in this machine's local time:");
        var ages = texts.Select(t => (ParseUs(t) ?? ParseDot(t)) is { } when ? now - when : (TimeSpan?)null).ToList();
        Console.WriteLine($"    unparsed {ages.Count(a => a is null)}, future {ages.Count(a => a < TimeSpan.Zero)}, " +
                          $"<24h {ages.Count(a => a >= TimeSpan.Zero && a < TimeSpan.FromHours(24))}, " +
                          $"24-48h {ages.Count(a => a >= TimeSpan.FromHours(24) && a < TimeSpan.FromHours(48))}, " +
                          $"2-7d {ages.Count(a => a >= TimeSpan.FromHours(48) && a < TimeSpan.FromDays(7))}, " +
                          $"7-30d {ages.Count(a => a >= TimeSpan.FromDays(7) && a < TimeSpan.FromDays(30))}, " +
                          $">30d {ages.Count(a => a >= TimeSpan.FromDays(30))}");
    }

    private static DateTimeOffset? ParseUs(string text)
    {
        var m = SlashDateTime().Match(text);
        if (!m.Success)
        {
            return null;
        }

        return DateTime.TryParse(m.Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    private static DateTimeOffset? ParseDot(string text) =>
        DateTime.TryParseExact(text.Trim(), ["d.M.yyyy H:mm:ss", "dd.MM.yyyy HH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local)
            : null;

    private static readonly HashSet<string> StatusWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Success", "Warning", "Failed", "Failure", "Error", "None", "Pending", "Running", "Completed",
    };

    [GeneratedRegex(@"(\d{1,2})\.(\d{1,2})\.\d{4}")]
    private static partial Regex DotParts();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[\p{L} ]+(?=:)|^[\p{L} ]+(?=\[)")]
    private static partial Regex LabelWords();

    private static readonly (string Label, Regex Regex)[] DatePatterns =
    [
        ("iso yyyy-MM-dd[T ]HH:mm", IsoDateTime()),
        ("slash d/M/yyyy h:mm[:ss][ AM|PM]", SlashDateTime()),
        ("slash date only d/M/yyyy", SlashDate()),
        ("dot dd.MM.yyyy", DotDate()),
        ("AM/PM marker", AmPm()),
    ];

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"(\d{1,2})/(\d{1,2})/\d{4}\s+\d{1,2}:\d{2}(:\d{2})?(\s*[AP]M)?", RegexOptions.IgnoreCase)]
    private static partial Regex SlashDateTime();

    [GeneratedRegex(@"(\d{1,2})/(\d{1,2})/\d{4}")]
    private static partial Regex SlashDate();

    [GeneratedRegex(@"\d{1,2}\.\d{1,2}\.\d{4}")]
    private static partial Regex DotDate();

    [GeneratedRegex(@"\d\s*[AP]M\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmPm();

    [GeneratedRegex(@"\d+|\p{L}+|\s+|.")]
    private static partial Regex Token();

    [GeneratedRegex(@"A(?:[ ]A)+")]
    private static partial Regex MaskedRun();

    private static string Skeleton(string text)
    {
        var builder = new StringBuilder();
        foreach (Match token in Token().Matches(text))
        {
            var t = token.Value;
            builder.Append(
                char.IsDigit(t[0]) ? new string('9', t.Length)
                : char.IsLetter(t[0]) ? (Vocabulary.Contains(t) ? t : "A")
                : char.IsWhiteSpace(t[0]) ? " "
                : t);
        }

        var skeleton = MaskedRun().Replace(builder.ToString(), "A+");
        return skeleton.Length > 140 ? skeleton[..140] + "…" : skeleton;
    }

    private static void Header(VsphereCandidateRead read)
    {
        Console.WriteLine();
        Console.WriteLine(read.Fault is { } fault
            ? $"  {read.Target,-44} FAULT  {fault}"
            : string.Create(CultureInfo.InvariantCulture,
                $"  {read.Target,-44} SEEN   objects {read.Objects.Count}, pages {read.Pages}, " +
                $"reply {read.ReplyCharacters} chars, {read.Elapsed.TotalMilliseconds:0} ms, " +
                $"missing {read.Objects.Sum(o => o.Missing.Count)}"));
    }

    private static void Tree(IReadOnlyList<PropertyNode> nodes)
    {
        Walk(nodes, "      ", 1);

        static void Walk(IEnumerable<PropertyNode> level, string prefix, int depth)
        {
            foreach (var group in level.GroupBy(n => (n.Name, n.Type)).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                var type = group.Key.Type.Length == 0 ? string.Empty : $" <{group.Key.Type}>";
                Console.WriteLine($"{prefix}{group.Key.Name}{type} x{group.Count()}");
                if (depth < 3)
                {
                    Walk(group.SelectMany(n => n.Children), prefix + "  ", depth + 1);
                }
            }
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }
}
