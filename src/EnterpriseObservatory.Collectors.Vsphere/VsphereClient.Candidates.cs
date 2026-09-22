using System.Diagnostics;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>What one isolated read of a candidate property path returned.</summary>
/// <remarks>
/// For the probe. A path is read on its own, never beside the collector's
/// list, because one invalid path fails the entire retrieval it is part of
/// (InvalidProperty) — so a candidate that faults here costs this one read
/// and nothing else.
/// </remarks>
public sealed record VsphereCandidateRead
{
    public required string Target { get; init; }

    public IReadOnlyList<PropertyObject> Objects { get; init; } = [];

    /// <summary>The fault that ended the read, or null when it completed.</summary>
    public string? Fault { get; init; }

    /// <summary>Reply size in characters, summed over every page.</summary>
    public long ReplyCharacters { get; init; }

    public TimeSpan Elapsed { get; init; }

    public int Pages { get; init; }
}

public sealed partial class VsphereClient
{
    /// <summary>
    /// Reads one property path from every object of one type, alone.
    /// </summary>
    /// <remarks>
    /// The measurement gate for new inventory paths: a path enters
    /// <c>InventoryProperties</c> only after this has read it without a fault
    /// on a live vCenter. Not called by collection.
    /// </remarks>
    public async Task<VsphereCandidateRead> ReadCandidatePathAsync(
        string managedObjectType,
        string path,
        CancellationToken cancellationToken)
    {
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var target = $"{managedObjectType}.{path}";
        var stopwatch = Stopwatch.StartNew();
        long characters = 0;

        string viewMoRef;
        try
        {
            viewMoRef = await CreateViewAsync(content, [managedObjectType], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VsphereApiException ex)
        {
            return new VsphereCandidateRead { Target = target, Fault = $"{ex.Kind}: {ex.Message}" };
        }

        var scope = new ServerHandleScope();
        scope.Register(new VsphereViewHandle(this, viewMoRef));

        try
        {
            var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [managedObjectType] = [path],
            };

            var (objects, pages) = await RetrieveAllPagesAsync(
                content, viewMoRef, properties, scope, cancellationToken, n => characters += n)
                .ConfigureAwait(false);

            return new VsphereCandidateRead
            {
                Target = target,
                Objects = objects,
                ReplyCharacters = characters,
                Elapsed = stopwatch.Elapsed,
                Pages = pages,
            };
        }
        catch (VsphereApiException ex)
        {
            return new VsphereCandidateRead
            {
                Target = target,
                Fault = $"{ex.Kind}: {ex.Message}",
                ReplyCharacters = characters,
                Elapsed = stopwatch.Elapsed,
            };
        }
        finally
        {
            await scope.DisposeAllAsync(CleanupGrace, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads properties of objects already known by reference, alone.</summary>
    public async Task<VsphereCandidateRead> ReadCandidateObjectsAsync(
        string managedObjectType,
        IReadOnlyList<string> moRefs,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moRefs);

        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var target = $"{managedObjectType}.{path}";

        if (moRefs.Count == 0)
        {
            return new VsphereCandidateRead { Target = target, Fault = "no objects to ask" };
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await SendAsync(
                VsphereSoapRequests.RetrieveObjectProperties(
                    content.PropertyCollector, managedObjectType, moRefs, [path]),
                cancellationToken).ConfigureAwait(false);

            return new VsphereCandidateRead
            {
                Target = target,
                Objects = PropertyCollectorParser.ParsePage(reply).Objects,
                ReplyCharacters = reply.Length,
                Elapsed = stopwatch.Elapsed,
                Pages = 1,
            };
        }
        catch (VsphereApiException ex)
        {
            return new VsphereCandidateRead
            {
                Target = target,
                Fault = $"{ex.Kind}: {ex.Message}",
                Elapsed = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>The root folder's reference: where vCenter-scoped alarms are raised.</summary>
    public async Task<string> GetRootFolderAsync(CancellationToken cancellationToken) =>
        (await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).RootFolder;

    /// <summary>The view manager's reference: what F4's cleanup is measured against.</summary>
    public async Task<string> GetViewManagerAsync(CancellationToken cancellationToken) =>
        (await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).ViewManager;

    /// <summary>The license manager's reference, or null when vCenter offers none.</summary>
    public async Task<string?> GetLicenseManagerAsync(CancellationToken cancellationToken) =>
        (await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).LicenseManager;

    /// <summary>The custom fields manager's reference, or null when vCenter offers none.</summary>
    public async Task<string?> GetCustomFieldsManagerAsync(CancellationToken cancellationToken) =>
        (await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).CustomFieldsManager;

    /// <summary>
    /// How many views this session's own <c>ViewManager.viewList</c> currently
    /// holds — the <c>views_held</c> self-metric (F4, ADR-0025 §3). Needs
    /// <c>System.View</c>, which the read-only account already has; measured
    /// live (<c>probe --from-store --views</c>) at about 23 ms.
    /// </summary>
    /// <remarks>
    /// Null when the property was not read — a fault, no object at all, or
    /// vCenter naming it in <c>missingSet</c> — rather than zero: "not allowed
    /// to look" is not an empty list anywhere else in this product, and a
    /// self-metric that quietly became zero on every failed read would let
    /// <c>views_held_max = 0</c> after 24 hours prove nothing was ever
    /// measured, not that cleanup works. A property vCenter omitted because
    /// the array is genuinely empty — the same convention
    /// <see cref="VsphereEventParser.ParseLatestPage"/> relies on — is the one
    /// case that legitimately reads as zero.
    /// </remarks>
    public async Task<int?> GetViewsHeldAsync(CancellationToken cancellationToken)
    {
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        return await GetViewsHeldAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> GetViewsHeldAsync(VsphereServiceContent content, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(
                VsphereSoapRequests.RetrieveObjectProperties(
                    content.PropertyCollector, "ViewManager", [content.ViewManager], ["viewList"]),
                cancellationToken).ConfigureAwait(false);

            var objects = PropertyCollectorParser.ParsePage(response).Objects;

            if (objects.Count == 0)
            {
                return null;
            }

            var viewManager = objects[0];

            // vCenter named it as unreadable rather than simply omitting it.
            if (viewManager.Missing.Any(m => string.Equals(m.Path, "viewList", StringComparison.Ordinal)))
            {
                return null;
            }

            // Omitted from Values entirely is how the property collector sends
            // an empty array (see ParseLatestPage's remarks) -- a real zero,
            // not a failure to read.
            return viewManager.Values.TryGetValue("viewList", out var raw)
                ? PropertyCollectorParser.SplitValues(raw).Count
                : 0;
        }
        catch (VsphereApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls <c>QueryComplianceStatus</c> with no filter, for the probe: its
    /// cost and the shape of what it returns.
    /// </summary>
    /// <returns>
    /// The reply's <c>returnval</c> elements as nodes, or a fault.
    /// </returns>
    public async Task<(IReadOnlyList<PropertyNode> Results, string? Fault, long Characters, TimeSpan Elapsed)>
        ReadComplianceStatusAsync(
            IReadOnlyList<string>? hostMoRefs, CancellationToken cancellationToken)
    {
        var content = await _channel.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        if (content.ComplianceManager is not { } manager)
        {
            return ([], "vCenter offers no complianceManager", 0, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await SendAsync(
                VsphereSoapRequests.QueryComplianceStatus(manager, hostMoRefs), cancellationToken)
                .ConfigureAwait(false);

            var results = VsphereXml.Parse(reply)
                .Descendants()
                .Where(e => e.Name.LocalName == "returnval")
                .Select(PropertyCollectorParser.ReadNode)
                .ToList();

            return (results, null, reply.Length, stopwatch.Elapsed);
        }
        catch (VsphereApiException ex)
        {
            return ([], $"{ex.Kind}: {ex.Message}", 0, stopwatch.Elapsed);
        }
    }
}
