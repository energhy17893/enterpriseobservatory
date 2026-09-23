using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// How many objects of each SimpliVity endpoint carry each field the S3
/// collector reads (<c>--fields</c>).
/// </summary>
/// <remarks>
/// Counts and enum values only — never a name, id or address — so the output
/// is safe with or without <c>--mask</c>. A field is "filled" when present and
/// not null; absent and null are both counted, apart, because an API that
/// leaves a field out by default and one that sends null mean different
/// things for the query to ask.
/// </remarks>
internal static class FieldCoverage
{
    /// <summary>Per endpoint, the fields SimplivityInventorySource reads.</summary>
    public static readonly (string Endpoint, string[] Fields)[] Read =
    [
        ("hosts", ["id", "name", "state", "upgrade_state", "version", "virtual_controller_name",
            "hypervisor_object_id", "compute_cluster_hypervisor_object_id"]),
        ("omnistack_clusters", ["id", "name", "arbiter_required", "arbiter_configured", "arbiter_connected",
            "upgrade_state", "version", "members", "hypervisor_object_id"]),
        ("virtual_machines", ["id", "name", "state", "ha_status", "ha_resynchronization_progress",
            "hypervisor_object_id", "hypervisor_instance_id"]),
        ("backups", ["id", "state", "type", "created_at", "virtual_machine_id"]),
    ];

    /// <summary>Fields whose values are an enum or a flag, worth a distribution.</summary>
    private static readonly HashSet<string> Enumerated = new(StringComparer.Ordinal)
    {
        "state", "upgrade_state", "ha_status", "arbiter_required", "arbiter_configured", "arbiter_connected", "type",
    };

    public static IEnumerable<string> Lines(string endpoint, JsonElement page, string[] fields)
    {
        var items = page.TryGetProperty(endpoint, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()]
            : new List<JsonElement>();

        yield return $"  {endpoint} ({items.Count} objects)";

        foreach (var field in fields)
        {
            var absent = items.Count(i => !i.TryGetProperty(field, out _));
            var nulls = items.Count(i => i.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Null);
            var filled = items.Count - absent - nulls;

            var line = $"    {field,-40} filled {filled}/{items.Count}, absent {absent}, null {nulls}";

            if (Enumerated.Contains(field) && filled > 0)
            {
                var values = items
                    .Where(i => i.TryGetProperty(field, out var v) && v.ValueKind != JsonValueKind.Null)
                    .Select(i => i.GetProperty(field).ToString())
                    .GroupBy(v => v, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Key}={g.Count()}");

                line += $"  [{string.Join(", ", values)}]";
            }

            yield return line;
        }
    }
}
