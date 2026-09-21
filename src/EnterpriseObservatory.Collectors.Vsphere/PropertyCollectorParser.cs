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
    /// is one element of the array.
    /// </para>
    /// <para>
    /// A tree rather than a flat field map, because vCenter's are not flat. A
    /// host's mounted volumes arrive as <c>HostFileSystemMountInfo</c>, whose
    /// <c>volume</c> is itself a structure with an <c>extent</c> array inside
    /// it — and that nesting is the whole content, since it is what says which
    /// LUN a datastore sits on. A one-level reader gave back the concatenated
    /// text of the subtree, which is the same class of quiet lie as the
    /// flattener it replaced.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<PropertyNode>> Structures { get; init; } =
        new Dictionary<string, IReadOnlyList<PropertyNode>>(StringComparer.Ordinal);

    /// <summary>Properties that were requested but could not be read.</summary>
    /// <remarks>
    /// vCenter reports these per object in <c>missingSet</c>, typically because
    /// the account lacks a privilege on that object. Kept apart from properties
    /// that were read as empty: not permitted and empty are different facts.
    /// </remarks>
    public IReadOnlyList<PropertyReadFailure> Missing { get; init; } = [];
}

/// <summary>
/// One node of a structured property value.
/// </summary>
/// <remarks>
/// Deliberately small: a name, the text if it has any, the declared type if
/// vCenter gave one, and the children. That is enough to read every structure
/// this collector needs and little enough that it cannot drift into being a
/// second XML library.
/// </remarks>
public sealed record PropertyNode
{
    public required string Name { get; init; }

    /// <summary>
    /// The node's own text, for a leaf.
    /// </summary>
    /// <remarks>
    /// Empty for a node with children rather than their concatenated text.
    /// Concatenating is how a structure comes back looking like a value, which
    /// is exactly the failure this type exists to end.
    /// </remarks>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// The <c>xsi:type</c> or <c>type</c> attribute, when there was one.
    /// </summary>
    /// <remarks>
    /// Carries two different things that both matter: the concrete subtype of
    /// a polymorphic field — a mounted volume is a <c>HostVmfsVolume</c> or an
    /// <c>HostNasVolume</c> and they are not interchangeable — and the managed
    /// object type of a reference, without which <c>host-3615</c> cannot say
    /// what it is.
    /// </remarks>
    public string Type { get; init; } = string.Empty;

    public IReadOnlyList<PropertyNode> Children { get; init; } = [];

    /// <summary>The first child with this name, or null.</summary>
    public PropertyNode? Child(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Every child with this name, in document order.</summary>
    public IEnumerable<PropertyNode> All(string name) =>
        Children.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>The text of the first child with this name, or empty.</summary>
    public string TextOf(string name) => Child(name)?.Text ?? string.Empty;

    /// <summary>The declared type of the first child with this name, or empty.</summary>
    public string TypeOf(string name) => Child(name)?.Type ?? string.Empty;
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
    /// <summary>The response elements of the two calls this parser serves.</summary>
    private static readonly HashSet<string> PropertyCollectorReplies = new(StringComparer.Ordinal)
    {
        "RetrievePropertiesExResponse",
        "ContinueRetrievePropertiesExResponse",
    };

    /// <summary>Reads one page of a property retrieval.</summary>
    /// <remarks>
    /// <para>
    /// An empty page is a claim — "there is nothing here" — and the graph acts
    /// on it: every entity this source used to report is marked vanished on
    /// the first miss and stops being sampled. So a reply that could not be
    /// read must not be allowed to look like one. This used to return an empty
    /// page for malformed XML, which made a truncated body or a proxy's error
    /// page indistinguishable from a vCenter with nothing in it.
    /// </para>
    /// <para>
    /// Two different things are told apart here. A property-collector reply
    /// with no <c>returnval</c> is vCenter saying the result is empty, and that
    /// stays a legitimate answer. Anything that is not a property-collector
    /// reply at all is a failed read, and failing the read is safe: the
    /// pipeline treats a source that did not answer as silent and keeps what
    /// it had.
    /// </para>
    /// </remarks>
    /// <exception cref="VsphereApiException">
    /// The reply is malformed, or is not a property-collector response.
    /// </exception>
    public static PropertyPage ParsePage(string xml)
    {
        XDocument document;
        try
        {
            document = VsphereXml.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new VsphereApiException(
                "vCenter's inventory reply could not be read, so nothing in it was believed.", ex);
        }

        if (!document.Descendants().Any(e => PropertyCollectorReplies.Contains(e.Name.LocalName)))
        {
            throw new VsphereApiException(
                "The inventory reply did not come from vCenter's property collector " +
                $"(it was a <{document.Root?.Name.LocalName}>), so nothing in it was believed.");
        }

        var returnVal = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");
        if (returnVal is null)
        {
            // The property collector answered and had nothing to return, which
            // is a legitimate answer: an inventory with nothing in it.
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
            var structures = new Dictionary<string, IReadOnlyList<PropertyNode>>(
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

    /// <summary>Reads one element and everything under it.</summary>
    /// <remarks>
    /// Repeated children are kept as repeated children rather than collapsed
    /// to the first. A VMFS volume may span several extents, and a reader that
    /// kept only one would report a datastore as living on a single LUN while
    /// quietly losing the others — which for a spanned volume is the half of
    /// the truth that explains the outage.
    /// </remarks>
    private static PropertyNode ReadStructure(XElement element)
    {
        var children = element.Elements().Select(ReadStructure).ToList();

        return new PropertyNode
        {
            Name = element.Name.LocalName,
            Text = children.Count == 0 ? element.Value.Trim() : string.Empty,
            Type = element.Attribute("type")?.Value
                ?? element.Attribute(XName.Get("type", XmlSchemaInstance))?.Value
                ?? string.Empty,
            Children = children,
        };
    }

    private const string XmlSchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

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
