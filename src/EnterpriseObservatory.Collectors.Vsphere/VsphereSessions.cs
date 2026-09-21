using System.Globalization;
using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>One signed-in session, as vCenter describes it.</summary>
public sealed record VsphereSession
{
    public required string Key { get; init; }

    public string UserName { get; init; } = string.Empty;

    public DateTimeOffset? LoginTimeUtc { get; init; }

    public DateTimeOffset? LastActiveUtc { get; init; }

    public string IpAddress { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;
}

/// <summary>What a session read returned.</summary>
public sealed record VsphereSessions
{
    /// <summary>
    /// Every session, or null when the account may not list them.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers. "Not allowed to look" must never
    /// read as "nobody is signed in", which is the one conclusion a session
    /// check exists to draw.
    /// </remarks>
    public IReadOnlyList<VsphereSession>? All { get; init; }

    /// <summary>Why <see cref="All"/> is null, in vCenter's words.</summary>
    public string? Unreadable { get; init; }

    /// <summary>The session this client is using.</summary>
    public VsphereSession? Current { get; init; }

    public static VsphereSessions Parse(string xml)
    {
        var page = PropertyCollectorParser.ParsePage(xml);
        var document = VsphereXml.Parse(xml);

        var denied = page.Objects
            .SelectMany(o => o.Missing)
            .FirstOrDefault(m => m.Path == "sessionList");

        var list = Property(document, "sessionList");

        return new VsphereSessions
        {
            All = list is null
                ? null
                : [.. list.Elements().Where(e => e.Name.LocalName == "UserSession").Select(Read)],
            Unreadable = list is not null
                ? null
                : denied is null ? "vCenter did not return the session list." : denied.FaultType,
            Current = Property(document, "currentSession") is { } current ? Read(current) : null,
        };
    }

    private static XElement? Property(XDocument document, string name) =>
        document.Descendants()
            .Where(e => e.Name.LocalName == "propSet")
            .FirstOrDefault(p => p.Elements().Any(e => e.Name.LocalName == "name" && e.Value == name))?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "val");

    private static VsphereSession Read(XElement session) => new()
    {
        Key = Child(session, "key") ?? string.Empty,
        UserName = Child(session, "userName") ?? string.Empty,
        LoginTimeUtc = Time(Child(session, "loginTime")),
        LastActiveUtc = Time(Child(session, "lastActiveTime")),
        IpAddress = Child(session, "ipAddress") ?? string.Empty,
        UserAgent = Child(session, "userAgent") ?? string.Empty,
    };

    private static string? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
}