using System.Globalization;
using System.Text.RegularExpressions;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Reads the "last backup" custom attribute a backup product writes on a VM
/// (M8.8, backup freshness).
/// </summary>
/// <remarks>
/// <para>
/// Vendor-independent: the attribute is found by its name, not by product.
/// Measured live (docs/measurements/backup-freshness-shapes.md): Commvault's
/// <c>Last Backup</c> on 86 of 145 VMs, 84 values <c>dd.MM.yyyy HH:mm:ss</c>,
/// one with a single-digit day, one <c>MM/dd/yyyy HH:mm:ss</c>. Its sibling
/// <c>Backup Status</c> names the job and carries no time, so it is not read.
/// </para>
/// <para>
/// Only those shapes and ISO 8601 are read. A slash date whose two parts are
/// both 12 or less and differ could be either order and is left unread: a
/// wrong month is a backup a month out, reported with confidence. An unread
/// value is carried as it was, so the finding can say what it could not read.
/// </para>
/// <para>
/// The values carry no offset — they are the backup server's local time. They
/// are read in the collector's own time zone, and which one is recorded
/// beside the time.
/// </para>
/// </remarks>
public static partial class BackupAttributeParser
{
    /// <summary>The VM's custom values, requested whole: the element type is polymorphic.</summary>
    public const string CustomValuePath = "customValue";

    /// <summary><c>CustomFieldsManager.field</c>: the definitions, key to name.</summary>
    public const string FieldPath = "field";

    /// <summary>Named for a backup and for a time: <c>Last Backup</c>, <c>Backup Time</c>…</summary>
    public static bool IsLastBackupField(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
        TimeWord().IsMatch(name);

    /// <summary>The last-backup definitions that can sit on a VM, key to name.</summary>
    public static IReadOnlyDictionary<string, string> LastBackupFields(IEnumerable<PropertyNode> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            var type = definition.TextOf("managedObjectType");
            var key = definition.TextOf("key");
            var name = definition.TextOf("name");

            if (key.Length > 0 &&
                (type.Length == 0 || string.Equals(type, "VirtualMachine", StringComparison.Ordinal)) &&
                IsLastBackupField(name))
            {
                fields[key] = name;
            }
        }

        return fields;
    }

    /// <summary>
    /// The value as a UTC time and the clock it was read in, or a null time
    /// when it is not one of the read shapes.
    /// </summary>
    public static (DateTimeOffset? Utc, string Basis) ParseTimestamp(string value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (null, string.Empty);
        }

        if (IsoWithOffset().IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamped))
        {
            return (stamped.ToUniversalTime(), "the offset in the value");
        }

        return ReadWallClock(text) is { } local ? InZone(local, zone) : (null, string.Empty);
    }

    /// <summary>
    /// The backup verdicts of one VM: nothing when its custom values or the
    /// definitions were not read, <see cref="InventoryVerdicts.BackupRead"/>
    /// alone when it carries no last-backup attribute.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(
        PropertyObject vm,
        IReadOnlyDictionary<string, string>? lastBackupFields,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(zone);

        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);

        // An empty array arrives as an empty value; absence is "not read".
        var answered = vm.Structures.TryGetValue(CustomValuePath, out var values) ||
                       vm.Values.ContainsKey(CustomValuePath);

        if (lastBackupFields is null || !answered)
        {
            return verdicts;
        }

        verdicts[InventoryVerdicts.BackupRead] = "true";

        var candidates = (values ?? [])
            .Select(v => (Key: v.TextOf("key"), Value: v.TextOf("value")))
            .Where(v => v.Value.Length > 0 && lastBackupFields.ContainsKey(v.Key))
            .Select(v => (Name: lastBackupFields[v.Key], v.Value, Parsed: ParseTimestamp(v.Value, zone)))
            .ToList();

        if (candidates.Count == 0)
        {
            return verdicts;
        }

        // The newest readable time; with none readable, the first attribute,
        // so the finding can name what it could not read.
        var chosen = candidates
            .Where(c => c.Parsed.Utc is not null)
            .OrderByDescending(c => c.Parsed.Utc)
            .DefaultIfEmpty(candidates[0])
            .First();

        verdicts[InventoryVerdicts.BackupField] = chosen.Name;
        verdicts[InventoryVerdicts.BackupValue] = chosen.Value;

        if (chosen.Parsed.Utc is { } utc)
        {
            verdicts[InventoryVerdicts.BackupLastUtc] = utc.ToString("o", CultureInfo.InvariantCulture);
            verdicts[InventoryVerdicts.BackupTimeBasis] = chosen.Parsed.Basis;
        }

        return verdicts;
    }

    private static DateTime? ReadWallClock(string text)
    {
        var dot = DotDate().Match(text);
        if (dot.Success)
        {
            return Build(dot, day: 1, month: 2);
        }

        var slash = SlashDate().Match(text);
        if (slash.Success)
        {
            var first = int.Parse(slash.Groups[1].Value, CultureInfo.InvariantCulture);
            var second = int.Parse(slash.Groups[2].Value, CultureInfo.InvariantCulture);

            return first > 12 ? Build(slash, day: 1, month: 2)
                : second > 12 || first == second ? Build(slash, day: 2, month: 1)
                : null;
        }

        return IsoLocal().IsMatch(text) &&
               DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            ? iso
            : null;
    }

    private static DateTime? Build(Match match, int day, int month)
    {
        var y = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0;

        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), m) ||
            hour > 23 || minute > 59 || second > 59 || y is < 1 or > 9999)
        {
            return null;
        }

        return new DateTime(y, m, d, hour, minute, second, DateTimeKind.Unspecified);
    }

    private static (DateTimeOffset?, string) InZone(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            // A wall-clock time the zone skipped: nothing honest to convert to.
            return (null, string.Empty);
        }

        var offset = zone.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var basis = string.Create(CultureInfo.InvariantCulture, $"collector local time, UTC{sign}{offset:hh\\:mm}");

        return (new DateTimeOffset(local, offset).ToUniversalTime(), basis);
    }

    [GeneratedRegex(@"last|time|date|when", RegexOptions.IgnoreCase)]
    private static partial Regex TimeWord();

    [GeneratedRegex(@"^(\d{1,2})\.(\d{1,2})\.(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?$")]
    private static partial Regex DotDate();

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?$")]
    private static partial Regex SlashDate();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?$")]
    private static partial Regex IsoLocal();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffset();
}
