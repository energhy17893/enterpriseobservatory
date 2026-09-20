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

    /// <summary>
    /// Property path to the structures it contained, for array-of-structure
    /// properties such as <c>triggeredAlarmState</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept apart from <see cref="Values"/> because a structure cannot be
    /// rendered as one string without deciding which field matters, and that
    /// decision belongs to the caller who knows what it asked for. Each entry
    /// is one element of the array, as field name to text.
    /// </para>
    /// <para>
    /// Managed object references keep their type: a field's value is the
    /// reference and <c>field@type</c> is the type attribute beside it. An
    /// alarm state names the object it is about in exactly this way, and losing
    /// the type would leave <c>host-3615</c> with nothing to say what it is.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> Structures
    { get; init; } = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(
        StringComparer.Ordinal);

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
            var structures =
                new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(
                    StringComparer.Ordinal);

            foreach (var propSet in Elements(objectContent, "propSet"))
            {
                var name = Child(propSet, "name");
                var value = Elements(propSet, "val").FirstOrDefault();
                if (string.IsNullOrWhiteSpace(name) || value is null)
                {
                    continue;
                }

                if (IsStructureArray(value))
                {
                    structures[name] = [.. value.Elements().Select(ReadStructure)];

                    // Deliberately not also in Values. A structure has no
                    // honest one-line rendering, and offering a wrong one is
                    // how it gets read by a caller who did not look.
                    continue;
                }

                values[name] = Flatten(value);
            }

            objects.Add(new PropertyObject
            {
                MoRef = obj.Value.Trim(),
                Type = obj.Attribute("type")?.Value ?? string.Empty,
                Values = values,
                Structures = structures,
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

        return children.Count == 0
            ? value.Value.Trim()
            : string.Join('', children.Select(c => c.Value.Trim()));
    }

    /// <summary>
    /// An array whose elements are structures rather than scalars.
    /// </summary>
    /// <remarks>
    /// vCenter returns <c>triggeredAlarmState</c> as an array of
    /// <c>AlarmState</c>, each carrying a key, the entity it concerns, the
    /// alarm it came from and a status. Any element having children of its own
    /// is enough to tell it apart from a list of names or references.
    /// </remarks>
    private static bool IsStructureArray(XElement value) =>
        value.Elements().Any(child => child.Elements().Any());

    private static IReadOnlyDictionary<string, string> ReadStructure(XElement element)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in element.Elements())
        {
            // Last one wins is wrong for a repeated field, but no structure
            // this reads has one; first wins keeps it predictable if that
            // changes, rather than silently depending on document order.
            fields.TryAdd(field.Name.LocalName, field.Value.Trim());

            if (field.Attribute("type")?.Value is { Length: > 0 } type)
            {
                fields.TryAdd($"{field.Name.LocalName}@type", type);
            }
        }

        return fields;
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
