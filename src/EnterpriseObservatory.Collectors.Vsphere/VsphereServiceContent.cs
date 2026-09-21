using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The manager objects a vCenter exposes.
/// </summary>
/// <remarks>
/// Discovered rather than hard-coded. The references are conventional
/// (<c>propertyCollector</c>, <c>PerfMgr</c>) but conventions are not
/// contracts, and a wrong guess fails by returning nothing — which looks like
/// an empty inventory rather than a bug.
/// </remarks>
public sealed record VsphereServiceContent
{
    public required string PropertyCollector { get; init; }

    public required string ViewManager { get; init; }

    public required string RootFolder { get; init; }

    public required string SessionManager { get; init; }

    public required string PerformanceManager { get; init; }

    /// <summary>
    /// The advanced-settings manager, or null when it was not offered.
    /// </summary>
    /// <remarks>
    /// Null is survivable: it only costs the ability to read
    /// <c>maxQueryMetrics</c>, and the batch sizer falls back to the documented
    /// default.
    /// </remarks>
    public string? SettingManager { get; init; }

    /// <summary>
    /// The event manager, or null when it was not offered.
    /// </summary>
    /// <remarks>
    /// Null costs event collection only, and is reported as "could not ask"
    /// rather than as an empty stream — a vCenter we cannot read events from is
    /// not a vCenter where nothing happened.
    /// </remarks>
    public string? EventManager { get; init; }

    /// <summary>The API version the server reports, e.g. <c>8.0.3.0</c>.</summary>
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The vCenter's own name, for the management-plane entity.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>Reads a <c>RetrieveServiceContent</c> response.</summary>
public static class VsphereServiceContentParser
{
    /// <summary>
    /// Parses service content, or null when the response did not contain any.
    /// </summary>
    public static VsphereServiceContent? TryParse(string xml)
    {
        XDocument document;
        try
        {
            document = VsphereXml.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var returnVal = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");
        if (returnVal is null)
        {
            return null;
        }

        var propertyCollector = Child(returnVal, "propertyCollector");
        var viewManager = Child(returnVal, "viewManager");
        var rootFolder = Child(returnVal, "rootFolder");
        var sessionManager = Child(returnVal, "sessionManager");
        var perfManager = Child(returnVal, "perfManager");

        // Without these five there is nothing this collector can do, and
        // guessing them would produce an empty result that looks like success.
        if (string.IsNullOrWhiteSpace(propertyCollector) ||
            string.IsNullOrWhiteSpace(viewManager) ||
            string.IsNullOrWhiteSpace(rootFolder) ||
            string.IsNullOrWhiteSpace(sessionManager) ||
            string.IsNullOrWhiteSpace(perfManager))
        {
            return null;
        }

        var about = returnVal.Elements().FirstOrDefault(e => e.Name.LocalName == "about");

        return new VsphereServiceContent
        {
            PropertyCollector = propertyCollector,
            ViewManager = viewManager,
            RootFolder = rootFolder,
            SessionManager = sessionManager,
            PerformanceManager = perfManager,
            SettingManager = Child(returnVal, "setting"),
            EventManager = Child(returnVal, "eventManager"),
            ApiVersion = about is null ? string.Empty : Child(about, "apiVersion") ?? string.Empty,
            Name = about is null ? string.Empty : Child(about, "name") ?? string.Empty,
        };
    }

    private static string? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
}
