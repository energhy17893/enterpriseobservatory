using System.Globalization;
using System.Xml.Linq;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Reads vim25 <c>Event</c> structures.
/// </summary>
/// <remarks>
/// <para>
/// Two responses carry them. The collector's <c>latestPage</c> arrives through
/// the property collector as a <c>val</c> of type <c>ArrayOfEvent</c> whose
/// children are the events; <c>ReadPreviousEvents</c> returns one
/// <c>returnval</c> per event. Both are an element whose <c>xsi:type</c> names
/// the event class, with the common <c>Event</c> fields as children.
/// </para>
/// <para>
/// <strong>Shaped from the published schema, not from a live server.</strong>
/// Nothing here has yet parsed a reply from a real vCenter; the fixtures in the
/// tests are what the schema says a reply looks like.
/// </para>
/// <para>
/// Every reader returns null when the reply could not be read at all and an
/// empty list when it said there were no events. The caller must not confuse
/// the two: one is "we did not see", the other is "nothing happened".
/// </para>
/// </remarks>
public static class VsphereEventParser
{
    private const string XmlSchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Reads the <c>latestPage</c> property of an EventHistoryCollector.</summary>
    public static IReadOnlyList<SourceEvent>? ParseLatestPage(string xml)
    {
        if (Load(xml) is not { } document)
        {
            return null;
        }

        var page = document.Descendants()
            .Where(e => e.Name.LocalName == "propSet")
            .FirstOrDefault(p => string.Equals(
                p.Elements().FirstOrDefault(c => c.Name.LocalName == "name")?.Value.Trim(),
                "latestPage",
                StringComparison.Ordinal))
            ?.Elements().FirstOrDefault(c => c.Name.LocalName == "val");

        // No propSet at all is how an empty array property comes back: the
        // property collector omits unset values rather than sending an empty
        // one. That is a collector with no events, not an unreadable reply.
        return page is null ? [] : [.. ReadAll(page.Elements())];
    }

    /// <summary>Reads a <c>ReadPreviousEvents</c> response.</summary>
    public static IReadOnlyList<SourceEvent>? ParseEvents(string xml) =>
        Load(xml) is { } document
            ? [.. ReadAll(document.Descendants().Where(e => e.Name.LocalName == "returnval"))]
            : null;

    /// <summary>Reads the collector reference a <c>CreateCollectorForEvents</c> returned.</summary>
    public static string? ParseCollector(string xml) =>
        Load(xml)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval")?.Value.Trim()
            is { Length: > 0 } moRef ? moRef : null;

    private static XDocument? Load(string xml)
    {
        try
        {
            return VsphereXml.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<SourceEvent> ReadAll(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            if (Read(element) is { } parsed)
            {
                yield return parsed;
            }
        }
    }

    /// <summary>Reads one event, or null when it lacks what makes it one.</summary>
    /// <remarks>
    /// A key and a creation time are required. Without the key an event cannot
    /// be placed against the mark, so keeping it would either duplicate it on
    /// every read or hide the events after it; without the time it cannot be
    /// kept for a bounded period or shown in order.
    /// </remarks>
    public static SourceEvent? Read(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!long.TryParse(Text(element, "key"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) ||
            !DateTimeOffset.TryParse(
                Text(element, "createdTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
        {
            return null;
        }

        var eventClass = ClassOf(element);

        return new SourceEvent
        {
            Key = key,
            ChainId = long.TryParse(
                Text(element, "chainId"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var chain) ? chain : null,
            CreatedAtUtc = created,
            EventClass = eventClass,
            TypeId = TypeIdOf(element, eventClass),
            Severity = Optional(Text(element, "severity")),
            Message = Text(element, "fullFormattedMessage") is { Length: > 0 } message
                ? message
                : Text(element, "message"),
            UserName = Optional(Text(element, "userName")),
            DatacenterName = Optional(Child(element, "datacenter") is { } dc ? Text(dc, "name") : string.Empty),
            ComputeResource = Reference(element, "computeResource"),
            Host = Reference(element, "host"),
            VirtualMachine = Reference(element, "vm"),
            Datastore = Reference(element, "ds"),
        };
    }

    /// <summary>
    /// What kind of event this really is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an <c>EventEx</c> or <c>ExtendedEvent</c> the class says nothing:
    /// every <c>esx.problem.*</c> the hosts raise arrives as <c>EventEx</c>,
    /// and the identity is in <c>eventTypeId</c>. Storing the class instead
    /// would put a lost storage path and an isolated host under one heading.
    /// </para>
    /// <para>
    /// When <c>eventTypeId</c> is absent the identity can still be recovered
    /// from <c>fullFormat</c>, whose text is <c>type|message</c>. Only when
    /// both are missing does the class stand in, and then it is at least
    /// honestly the class rather than a guess.
    /// </para>
    /// <para>
    /// A <c>TaskEvent</c> has the same problem in another shape: every task
    /// vCenter runs arrives as that one class, and what the task was is its
    /// <c>info.descriptionId</c> — <c>VirtualMachine.createSnapshot</c>,
    /// <c>VirtualMachine.powerOff</c>. That id is language-independent, which
    /// the rendered message ("Task: Create virtual machine snapshot") is not.
    /// Roadmap M2.4 reads it to name who took a snapshot.
    /// </para>
    /// </remarks>
    private static string TypeIdOf(XElement element, string eventClass)
    {
        if (Text(element, "eventTypeId") is { Length: > 0 } typeId)
        {
            return typeId;
        }

        if (string.Equals(eventClass, "TaskEvent", StringComparison.Ordinal) &&
            Child(element, "info") is { } info &&
            Text(info, "descriptionId") is { Length: > 0 } task)
        {
            return task;
        }

        if (Text(element, "fullFormat") is { Length: > 0 } format &&
            format.IndexOf('|', StringComparison.Ordinal) is > 0 and var bar)
        {
            return format[..bar].Trim();
        }

        return eventClass;
    }

    /// <summary>The event's class, from <c>xsi:type</c>, without a namespace prefix.</summary>
    private static string ClassOf(XElement element)
    {
        var declared = element.Attribute(XName.Get("type", XmlSchemaInstance))?.Value
            ?? element.Attribute("type")?.Value;

        if (string.IsNullOrWhiteSpace(declared))
        {
            // A bare <Event> or <returnval> with no type is the base class,
            // which is what the schema would make of it too.
            return "Event";
        }

        var colon = declared.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? declared[(colon + 1)..] : declared;
    }

    /// <summary>
    /// Reads one of the <c>*EventArgument</c> wrappers: a reference and a name.
    /// </summary>
    /// <remarks>
    /// Each wrapper names its reference after itself — <c>host/host</c>,
    /// <c>vm/vm</c> — except the datastore, which is <c>ds/datastore</c>. So
    /// the reference is taken as whichever child is not the name, rather than
    /// by a table of names that would be wrong for the next wrapper added.
    /// </remarks>
    private static EventObjectRef? Reference(XElement element, string wrapper)
    {
        if (Child(element, wrapper) is not { } argument)
        {
            return null;
        }

        var moRef = argument.Elements()
            .FirstOrDefault(c => c.Name.LocalName != "name")?.Value.Trim() ?? string.Empty;
        var name = Text(argument, "name");

        if (moRef.Length == 0 && name.Length == 0)
        {
            return null;
        }

        return new EventObjectRef { MoRef = moRef, Name = name.Length > 0 ? name : moRef };
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string Text(XElement element, string localName) =>
        Child(element, localName)?.Value.Trim() ?? string.Empty;

    private static string? Optional(string value) => value.Length > 0 ? value : null;
}
