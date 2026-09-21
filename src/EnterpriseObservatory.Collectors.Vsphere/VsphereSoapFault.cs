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

    /// <summary>
    /// A name or argument the server does not recognise.
    /// </summary>
    /// <remarks>
    /// Usually an advanced setting that simply is not present on this
    /// installation. Distinguished from a real failure because the caller can
    /// often carry on without whatever it was asking for.
    /// </remarks>
    InvalidName,

    /// <summary>Something else.</summary>
    Other,
}

/// <summary>
/// What the caller was doing, so a fault can be read in context.
/// </summary>
/// <remarks>
/// Added after a live vCenter returned <c>'config.vpxd.stats.maxQueryMetrics'
/// is invalid or exceeds the maximum number of characters permitted</c> for an
/// unset option, and a context-free text match read "exceeds the maximum" as a
/// performance-query size refusal. A refusal is only a meaningful reading of a
/// performance query; interpreting one anywhere else is guessing.
/// </remarks>
public enum VsphereCallContext
{
    /// <summary>Anything other than a performance query.</summary>
    General = 0,

    /// <summary>A <c>QueryPerf</c> call, where a size refusal is possible.</summary>
    PerformanceQuery,
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

    /// <summary>
    /// Whether the caller can reasonably carry on without what it asked for.
    /// </summary>
    /// <remarks>
    /// An advanced setting that is not present on this installation costs the
    /// exact value and nothing else — falling back to a documented default is
    /// correct, and failing the whole cycle over it would not be.
    /// </remarks>
    public bool IsSurvivable => VsphereFaults.IsSurvivable(Kind);
}

/// <summary>Judgements about a fault kind, independent of how it arrived.</summary>
/// <remarks>
/// Here rather than on <see cref="VsphereSoapFault"/> because the same
/// judgement is needed about a <see cref="VsphereApiException"/>, which is a
/// different type carrying the same kind. It was written out twice — once
/// named and once as an inline catch filter — and two spellings of one rule
/// are two chances for it to drift, with the drift showing up as a cycle that
/// fails over something it used to survive.
/// </remarks>
public static class VsphereFaults
{
    /// <summary>
    /// Whether the caller can reasonably carry on without what it asked for.
    /// </summary>
    /// <remarks>
    /// Both cases were seen against a live server while reading
    /// <c>maxQueryMetrics</c>: a read-only account may not be granted
    /// Global.Settings, and the option itself does not exist until somebody
    /// sets it, which vCenter reports as an invalid name. Either costs the
    /// exact value and nothing else — the batch sizer falls back to a
    /// documented default — and failing a whole collection cycle over an
    /// optional reading would be the worse answer.
    /// </remarks>
    public static bool IsSurvivable(VsphereFaultKind kind) =>
        kind is VsphereFaultKind.InvalidName or VsphereFaultKind.NoPermission;
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
    public static VsphereSoapFault? TryRead(
        string responseBody,
        VsphereCallContext context = VsphereCallContext.General)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = VsphereXml.Parse(responseBody);
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
            Kind = Classify(faultType, message, context),
            FaultType = faultType,
            Message = message,
        };
    }

    private static VsphereFaultKind Classify(string faultType, string message, VsphereCallContext context)
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

        if (faultType.Contains("InvalidName", StringComparison.OrdinalIgnoreCase) ||
            faultType.Contains("InvalidArgument", StringComparison.OrdinalIgnoreCase))
        {
            return VsphereFaultKind.InvalidName;
        }

        // The query-size refusal: its documented text (Broadcom KB 301449) or
        // the RestrictedByAdministrator type; see
        // AdaptiveBatchSizer.IsQuerySizeRefusal. Only for a performance
        // query, because that is the only place the reading is meaningful — a
        // live vCenter returns "'config.vpxd.stats.maxQueryMetrics' is invalid
        // or exceeds the maximum number of characters permitted" for an unset
        // option, and a context-free match read that as a size refusal.
        if (context == VsphereCallContext.PerformanceQuery &&
            AdaptiveBatchSizer.IsQuerySizeRefusal(faultType, message))
        {
            return VsphereFaultKind.QuerySizeRefused;
        }

        return VsphereFaultKind.Other;
    }
}
