using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>What a vCenter fault means for the caller.</summary>
public enum VsphereFaultKind
{
    /// <summary>Not a fault at all.</summary>
    None = 0,

    /// <summary>Credentials rejected. Retrying will not help and may lock the account.</summary>
    InvalidLogin,

    /// <summary>Authenticated but not permitted. A role change, not a retry.</summary>
    NoPermission,

    /// <summary>The session expired. Log in again and repeat the call.</summary>
    NotAuthenticated,

    /// <summary>The query asked for too much at once. Ask for less.</summary>
    QuerySizeRefused,

    /// <summary>The object was deleted between listing and reading it.</summary>
    ManagedObjectNotFound,

    /// <summary>Something else.</summary>
    Other,
}

/// <summary>A fault vCenter returned.</summary>
public sealed record VsphereSoapFault
{
    public required VsphereFaultKind Kind { get; init; }

    /// <summary>The fault type as the server named it, e.g. <c>NoPermission</c>.</summary>
    public string FaultType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Whether repeating the identical call could plausibly succeed.
    /// </summary>
    /// <remarks>
    /// Drives the collection policy: reported failures are taken at face value
    /// and not retried, and getting this wrong in the permissive direction is
    /// how a monitoring account ends up locked out by its own retry loop.
    /// </remarks>
    public bool IsWorthRetrying => Kind is VsphereFaultKind.NotAuthenticated or VsphereFaultKind.Other;
}

/// <summary>Reads SOAP faults out of a vCenter response.</summary>
public static class VsphereSoapFaultReader
{
    /// <summary>
    /// Finds a fault in a response body, if there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// vCenter returns faults as HTTP 500 with a SOAP fault body, so the status
    /// code alone cannot distinguish "your credentials are wrong" from "the
    /// server is broken". The body must be read.
    /// </para>
    /// <para>
    /// The concrete fault type lives in <c>detail</c> as an element whose name
    /// is the fault, e.g. <c>&lt;NoPermissionFault&gt;</c>. Matching is by local
    /// name so namespace prefixes do not matter.
    /// </para>
    /// </remarks>
    public static VsphereSoapFault? TryRead(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(responseBody);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var fault = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is null)
        {
            return null;
        }

        var message = fault.Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text")?.Value?.Trim()
            ?? string.Empty;

        var faultType = fault.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "detail")
            ?.Elements()
            .FirstOrDefault()
            ?.Name.LocalName
            ?? string.Empty;

        return new VsphereSoapFault
        {
            Kind = Classify(faultType, message),
            FaultType = faultType,
            Message = message,
        };
    }

    private static VsphereFaultKind Classify(string faultType, string message)
    {
        // The fault type is authoritative when present.
        if (faultType.Contains("InvalidLogin", StringComparison.OrdinalIgnoreCase))
        {
            return VsphereFaultKind.InvalidLogin;
        }

        if (faultType.Contains("NoPermission", StringComparison.OrdinalIgnoreCase))
        {
            return VsphereFaultKind.NoPermission;
        }

        if (faultType.Contains("NotAuthenticated", StringComparison.OrdinalIgnoreCase))
        {
            return VsphereFaultKind.NotAuthenticated;
        }

        if (faultType.Contains("ManagedObjectNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return VsphereFaultKind.ManagedObjectNotFound;
        }

        // The query-size refusal has no dedicated fault type; vCenter reports it
        // as a generic RuntimeFault whose message is the only clue. Matching on
        // text is unavoidable here.
        if (AdaptiveBatchSizer.IsQuerySizeRefusal(message))
        {
            return VsphereFaultKind.QuerySizeRefused;
        }

        return VsphereFaultKind.Other;
    }
}
