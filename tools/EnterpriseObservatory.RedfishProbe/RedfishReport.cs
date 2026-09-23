using System.Globalization;
using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// The documents one iLO measurement pass reads, however they were obtained
/// -- a live GET or a bundled sample file. The report below does not care
/// which; that is what lets <c>--dry</c> prove the output format before a
/// credential exists (§10.7).
/// </summary>
internal sealed record RedfishDocs
{
    public JsonElement? Manager { get; init; }
    public JsonElement? Power { get; init; }
    public JsonElement? Thermal { get; init; }
    public JsonElement? System { get; init; }
    public IReadOnlyList<JsonElement> Drives { get; init; } = [];
    public IReadOnlyList<JsonElement> Memory { get; init; } = [];
    public JsonElement? FirmwareInventoryCollection { get; init; }
    public JsonElement? LogEntries { get; init; }

    /// <summary>Round trip per endpoint, live mode only. Empty in <c>--dry</c>.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timing { get; init; } =
        new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
}

/// <summary>
/// Turns a set of iLO Redfish documents into the M6.0b measurement report:
/// every field the definition of done asks for, as a counted or typed line,
/// never a raw value that would make the output unshareable.
/// </summary>
internal static class RedfishReport
{
    public static IReadOnlyList<string> Generate(RedfishDocs docs, bool mask)
    {
        var lines = new List<string>();

        Section(lines, "iLO generation and firmware");
        if (docs.Manager is { } manager)
        {
            var model = TextOf(manager, "Model");
            var managerFirmware = TextOf(manager, "FirmwareVersion");
            lines.Add($"  Managers/1.Model               {Mask.Show(model, mask)}");
            lines.Add($"  Managers/1.FirmwareVersion      {(managerFirmware.Length > 0 ? "present" : "ABSENT")}" +
                       (managerFirmware.Length > 0 ? $"  (value withheld; {managerFirmware.Length} chars)" : string.Empty));
        }
        else
        {
            lines.Add("  Managers/1                      NOT READ");
        }

        Section(lines, "Power / Thermal — Redundancy[]");
        var powerRedundancy = HasRedundancy(docs.Power);
        var thermalRedundancy = HasRedundancy(docs.Thermal);
        lines.Add($"  Chassis/1/Power  Redundancy[]   {(powerRedundancy ? "present" : "absent")}");
        lines.Add($"  Chassis/1/Thermal Redundancy[]  {(thermalRedundancy ? "present" : "absent")}");

        Section(lines, "Power supplies and fans");
        DescribeStatusArray(lines, docs.Power, "PowerSupplies", "PSU");
        DescribeStatusArray(lines, docs.Thermal, "Fans", "fan");

        Section(lines, "Drives");
        var predicted = docs.Drives
            .Select(d => BoolOf(d, "FailurePredicted"))
            .ToList();
        lines.Add($"  drives read                     {docs.Drives.Count}");
        lines.Add($"  FailurePredicted = true         {predicted.Count(p => p == true)}");
        lines.Add($"  FailurePredicted = false        {predicted.Count(p => p == false)}");
        lines.Add($"  FailurePredicted absent          {predicted.Count(p => p is null)}");
        var wearStatus = docs.Drives.Count(d => Child(d, "Oem", "Hpe", "WearStatus") is not null);
        lines.Add($"  Oem.Hpe.WearStatus present       {wearStatus} of {docs.Drives.Count}");

        Section(lines, "Memory");
        lines.Add($"  DIMMs read                      {docs.Memory.Count}");
        var dimmStatus = docs.Memory.Count(d => Child(d, "Oem", "Hpe", "DIMMStatus") is not null);
        lines.Add($"  Oem.Hpe.DIMMStatus present       {dimmStatus} of {docs.Memory.Count}");

        Section(lines, "Firmware inventory");
        if (docs.FirmwareInventoryCollection is { } firmware)
        {
            var reportedCount = IntOf(firmware, "Members@odata.count");
            var members = firmware.TryGetProperty("Members", out var membersArray) &&
                          membersArray.ValueKind == JsonValueKind.Array
                ? membersArray.GetArrayLength()
                : 0;
            lines.Add($"  Members@odata.count             {(reportedCount?.ToString(CultureInfo.InvariantCulture) ?? "absent")}");
            lines.Add($"  Members[] length                {members}");
        }
        else
        {
            lines.Add("  UpdateService/FirmwareInventory NOT READ");
        }

        Section(lines, "IML (LogServices/IML/Entries)");
        if (docs.LogEntries is { } logEntries)
        {
            var entries = logEntries.TryGetProperty("Members", out var members) &&
                          members.ValueKind == JsonValueKind.Array
                ? [.. members.EnumerateArray()]
                : new List<JsonElement>();

            DateTimeOffset? newest = null;
            foreach (var entry in entries)
            {
                var created = TextOf(entry, "Created");
                if (DateTimeOffset.TryParse(
                        created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
                    (newest is null || parsed > newest))
                {
                    newest = parsed;
                }
            }

            var hpeSeverity = entries.Count(e => Child(e, "Oem", "Hpe", "Severity") is not null);
            var repaired = entries.Count(e => Child(e, "Oem", "Hpe", "Repaired") is not null);

            lines.Add($"  entries                          {entries.Count}");
            lines.Add($"  newest Created                   {(newest is { } n ? n.ToString("O", CultureInfo.InvariantCulture) : "(none)")}");
            lines.Add($"  Oem.Hpe.Severity present          {hpeSeverity} of {entries.Count}");
            lines.Add($"  Oem.Hpe.Repaired present          {repaired} of {entries.Count}");
        }
        else
        {
            lines.Add("  LogServices/IML/Entries NOT READ");
        }

        Section(lines, "Aggregate health and AMS (iLO 6 only)");
        var aggregate = docs.System is { } sys ? Child(sys, "Oem", "Hpe", "AggregateHealthStatus") : null;
        lines.Add($"  Oem.Hpe.AggregateHealthStatus    {(aggregate is not null ? "present" : "ABSENT")}");
        var ams = aggregate is { } agg ? TextOf(agg, "AgentlessManagementService") : string.Empty;
        lines.Add($"  AgentlessManagementService       {(ams.Length > 0 ? ams : "absent")}");

        Section(lines, "Identity (Systems/1)");
        if (docs.System is { } system)
        {
            var serial = TextOf(system, "SerialNumber");
            var uuid = TextOf(system, "UUID");
            lines.Add($"  SerialNumber                     {(serial.Length > 0 ? "present" : "ABSENT")}  {Mask.Show(serial, mask)}");
            lines.Add($"  UUID                             {(uuid.Length > 0 ? "present" : "ABSENT")}  {Mask.Show(uuid, mask)}");
        }
        else
        {
            lines.Add("  Systems/1 NOT READ");
        }

        lines.Add("  vim25 hardware.systemInfo match  not compared here — correlate against");
        lines.Add("                                    VsphereProbe --host-identity output at live-run time");
        lines.Add("                                    (of the 10 hosts, how many matched)");

        Section(lines, "Round trip per endpoint");
        if (docs.Timing.Count == 0)
        {
            lines.Add("  n/a — no network in --dry; live mode times every GET above");
        }
        else
        {
            foreach (var (path, elapsed) in docs.Timing.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                lines.Add($"  {path,-44} {elapsed.TotalMilliseconds,8:0} ms");
            }
        }

        return lines;
    }

    private static bool HasRedundancy(JsonElement? element)
    {
        if (element is not { } value)
        {
            return false;
        }

        if (value.TryGetProperty("Redundancy", out var root) &&
            root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            return true;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("Redundancy", out var nested) &&
                    nested.ValueKind == JsonValueKind.Array && nested.GetArrayLength() > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void DescribeStatusArray(List<string> lines, JsonElement? root, string property, string label)
    {
        if (root is not { } value || !value.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            lines.Add($"  {property,-14} NOT READ");
            return;
        }

        var items = array.EnumerateArray().ToList();
        var byHealth = items
            .Select(i => Child(i, "Status") is { } status ? TextOf(status, "Health") : string.Empty)
            .Select(h => h.Length == 0 ? "(none)" : h)
            .GroupBy(h => h, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");

        lines.Add($"  {label,-4} count                       {items.Count}   status: {string.Join(", ", byHealth)}");
    }

    private static string TextOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool? BoolOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static int? IntOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>Walks a chain of object properties, or null the moment one is missing.</summary>
    private static JsonElement? Child(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static void Section(List<string> lines, string title)
    {
        lines.Add(string.Empty);
        lines.Add($"=== {title} ===");
    }
}
