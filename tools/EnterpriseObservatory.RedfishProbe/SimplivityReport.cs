using System.Text.Json;
using System.Text.RegularExpressions;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// The documents one SimpliVity measurement pass reads, live or bundled.
/// </summary>
internal sealed record SimplivityDocs
{
    public JsonElement? Version { get; init; }
    public JsonElement? Hosts { get; init; }
    public JsonElement? Clusters { get; init; }
    public JsonElement? VirtualMachines { get; init; }
    public JsonElement? Backups { get; init; }

    /// <summary>Live mode only: how long the token took to acquire.</summary>
    public TimeSpan? TokenAcquisition { get; init; }
}

/// <summary>
/// Turns a set of SimpliVity REST documents into the M6.0b measurement
/// report. §10.7/§10.8: REST answers state, not alarms -- alarms come from
/// the event stream (M2), so this prints distributions and enum values, not
/// a health verdict this probe has no mandate to make.
/// </summary>
internal static partial class SimplivityReport
{
    public static IReadOnlyList<string> Generate(SimplivityDocs docs)
    {
        var lines = new List<string>();

        Section(lines, "Version (no auth)");
        if (docs.Version is { } version)
        {
            lines.Add($"  REST_API_Version                {TextOf(version, "REST_API_Version")}");
            lines.Add($"  SVTFS_Version                    {TextOf(version, "SVTFS_Version")}");
        }
        else
        {
            lines.Add("  GET /api/version NOT READ");
        }

        Section(lines, "Hosts");
        var hosts = ArrayOf(docs.Hosts, "hosts");
        lines.Add($"  hosts read                      {hosts.Count}");
        Distribution(lines, hosts, "state", "  state");

        var moRefLike = hosts.Count(h => LooksLikeMoRef(TextOf(h, "hypervisor_object_id")));
        lines.Add($"  hypervisor_object_id looks like a vim25 moRef   {moRefLike} of {hosts.Count}");

        Section(lines, "Clusters");
        var clusters = ArrayOf(docs.Clusters, "omnistack_clusters");
        lines.Add($"  clusters read                    {clusters.Count}");
        foreach (var cluster in clusters)
        {
            var name = TextOf(cluster, "name");
            lines.Add($"  {name,-24}  arbiter_connected={TextOf(cluster, "arbiter_connected")}" +
                       $"  arbiter_required={TextOf(cluster, "arbiter_required")}" +
                       $"  arbiter_configured={TextOf(cluster, "arbiter_configured")}" +
                       $"  upgrade_state={TextOf(cluster, "upgrade_state")}");
        }

        Section(lines, "Virtual machines");
        var vms = ArrayOf(docs.VirtualMachines, "virtual_machines");
        lines.Add($"  VMs read                         {vms.Count}");
        Distribution(lines, vms, "ha_status", "  ha_status");

        Section(lines, "Backups");
        var backups = ArrayOf(docs.Backups, "backups");
        lines.Add($"  backups read                     {backups.Count}");
        Distribution(lines, backups, "state", "  state");

        Section(lines, "Token acquisition (POST /api/oauth/token)");
        lines.Add(docs.TokenAcquisition is { } elapsed
            ? $"  acquired in                      {elapsed.TotalMilliseconds:0} ms"
            : "  n/a — no network in --dry; live mode times the POST above");

        return lines;
    }

    /// <summary>
    /// vim25 moRefs are <c>type-number</c>, e.g. <c>host-21</c>,
    /// <c>vm-4502</c>. Not proof the id resolves to a live object -- only
    /// that the shape matches, which is the question M6.0b's constraints ask
    /// ("is <c>hypervisor_object_id</c> a moRef").
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z]+-\d+$")]
    private static partial Regex MoRefShape();

    private static bool LooksLikeMoRef(string value) => value.Length > 0 && MoRefShape().IsMatch(value);

    private static void Distribution(List<string> lines, IReadOnlyList<JsonElement> items, string field, string label)
    {
        var groups = items
            .Select(i => TextOf(i, field))
            .Select(v => v.Length == 0 ? "(none)" : v)
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");

        lines.Add($"{label}                         {string.Join(", ", groups)}");
    }

    private static List<JsonElement> ArrayOf(JsonElement? root, string property)
    {
        if (root is not { } value || !value.TryGetProperty(property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray()];
    }

    private static string TextOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static void Section(List<string> lines, string title)
    {
        lines.Add(string.Empty);
        lines.Add($"=== {title} ===");
    }
}
