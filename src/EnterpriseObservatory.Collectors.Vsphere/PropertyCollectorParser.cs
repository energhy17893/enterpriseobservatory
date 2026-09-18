using System.Globalization;
using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>One managed object and the properties that were read from it.</summary>
public sealed record PropertyObject
{
    /// <summary>Managed object reference, e.g. <c>host-123</c>.</summary>
    public required string MoRef { get; init; }

    /// <summary>Its type, e.g. <c>HostSystem</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Property path to value, e.g. <c>summary.overallStatus</c>.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Properties that were requested but could not be read.</summary>
    /// <remarks>
    /// vCenter reports these per object in <c>missingSet</c>, typically because
    /// the account lacks a privilege on that object. Kept apart from properties
    /// that were read as empty: not permitted and empty are different facts.
    /// </remarks>
    public IReadOnlyList<PropertyReadFailure> Missing { get; init; } = [];
}

/// <summary>A property that could not be read, and why.</summary>
public sealed record PropertyReadFailure
{
    public required string Path { get; init; }

    public required string FaultType { get; init; }

    public bool IsPermissionDenied =>
        FaultType.Contains("NoPermission", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A page of <c>RetrievePropertiesEx</c> results.</summary>
public sealed record PropertyPage
{
    public IReadOnlyList<PropertyObject> Objects { get; init; } = [];

    /// <summary>
    /// Continuation token, or null when this is the last page.
    /// </summary>
    /// <remarks>
    /// Ignoring this silently truncates the inventory at whatever the server
    /// chose to return — typically a few hundred objects — and the result looks
    /// like a small, healthy estate rather than a bug.
    /// </remarks>
    public string? ContinuationToken { get; init; }

    public bool HasMore => !string.IsNullOrEmpty(ContinuationToken);
}

/// <summary>Reads <c>RetrievePropertiesEx</c> responses.</summary>
public static class PropertyCollectorParser
{
    public static PropertyPage ParsePage(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return new PropertyPage();
        }

        var returnVal = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");
        if (returnVal is null)
        {
            // No returnval at all means an empty result, which is a legitimate
            // answer: an inventory with nothing in it.
            return new PropertyPage();
        }

        var objects = new List<PropertyObject>();

        foreach (var objectContent in Elements(returnVal, "objects"))
        {
            var obj = Elements(objectContent, "obj").FirstOrDefault();
            if (obj is null)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var propSet in Elements(objectContent, "propSet"))
            {
                var name = Child(propSet, "name");
                var value = Elements(propSet, "val").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name) && value is not null)
                {
                    values[name] = Flatten(value);
                }
            }

            objects.Add(new PropertyObject
            {
                MoRef = obj.Value.Trim(),
                Type = obj.Attribute("type")?.Value ?? string.Empty,
                Values = values,
                Missing = [.. ReadMissing(objectContent)],
            });
        }

        return new PropertyPage
        {
            Objects = objects,
            ContinuationToken = Child(returnVal, "token"),
        };
    }

    /// <summary>
    /// Renders a property value as text.
    /// </summary>
    /// <remarks>
    /// Most values are scalars. Arrays — a host's IP addresses, a VM's
    /// datastores — arrive as repeated child elements, and are joined with a
    /// separator the caller splits on. Keeping this one place means the caller
    /// never has to know which properties are arrays.
    /// </remarks>
    private static string Flatten(XElement value)
    {
        var children = value.Elements().ToList();
        if (children.Count == 0)
        {
            return value.Value.Trim();
        }

        return string.Join('', children.Select(c =>
            c.Elements().Any() ? c.Elements().First().Value.Trim() : c.Value.Trim()));
    }

    /// <summary>Splits a value produced by <see cref="Flatten"/>.</summary>
    public static IReadOnlyList<string> SplitValues(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split('', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IEnumerable<PropertyReadFailure> ReadMissing(XElement objectContent)
    {
        foreach (var missing in Elements(objectContent, "missingSet"))
        {
            var path = Child(missing, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var faultType = Elements(missing, "fault").FirstOrDefault()
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "fault")
                ?.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value
                ?? string.Empty;

            yield return new PropertyReadFailure { Path = path, FaultType = faultType };
        }
    }

    /// <summary>Reads a boolean property, or null when it was not readable.</summary>
    /// <remarks>
    /// Null rather than false. For cluster HA and DRS the difference decides
    /// whether a best-practice check runs at all — see
    /// <see cref="VsphereCluster.HighAvailabilityEnabled"/>.
    /// </remarks>
    public static bool? ReadBoolean(IReadOnlyDictionary<string, string> values, string path) =>
        values.TryGetValue(path, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : null;

    /// <summary>Reads a long property, or null when it was not readable.</summary>
    public static long? ReadLong(IReadOnlyDictionary<string, string> values, string path) =>
        values.TryGetValue(path, out var raw) &&
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Reads a string property, or null when it was not readable.</summary>
    public static string? ReadString(IReadOnlyDictionary<string, string> values, string path) =>
        values.TryGetValue(path, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;

    private static IEnumerable<XElement> Elements(XElement element, string localName) =>
        element.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Child(XElement element, string localName) =>
        Elements(element, localName).FirstOrDefault()?.Value;
}
