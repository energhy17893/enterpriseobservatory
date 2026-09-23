using System.Text.Json;

namespace EnterpriseObservatory.RedfishProbe;

/// <summary>
/// <c>--shapes</c>: names, JSON kinds and counts only. Never a value -- not a
/// serial, not a status string, not a date. This is the mode that is safe to
/// paste into a pull request straight from a live iLO or OVC.
/// </summary>
internal static class ShapeDump
{
    public static void Print(string label, JsonElement? root, int depth = 2)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {label} ---");

        if (root is not { } value)
        {
            Console.WriteLine("  NOT READ");
            return;
        }

        Walk(value, string.Empty, 1, depth);
    }

    private static void Walk(JsonElement element, string prefix, int at, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var value = property.Value;
            var (kind, count) = value.ValueKind switch
            {
                JsonValueKind.Array => ("array", value.GetArrayLength()),
                JsonValueKind.Object => ("object", value.EnumerateObject().Count()),
                JsonValueKind.String => ("string", (int?)null),
                JsonValueKind.Number => ("number", (int?)null),
                JsonValueKind.True or JsonValueKind.False => ("bool", (int?)null),
                JsonValueKind.Null => ("null", (int?)null),
                _ => ("?", (int?)null),
            };

            Console.WriteLine(count is { } c
                ? $"  {prefix}{property.Name} <{kind}> x{c}"
                : $"  {prefix}{property.Name} <{kind}>");

            if (at >= depth)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                Walk(value, prefix + "  ", at + 1, depth);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var first = value.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    Walk(first, prefix + "  ", at + 1, depth);
                }
            }
        }
    }
}
