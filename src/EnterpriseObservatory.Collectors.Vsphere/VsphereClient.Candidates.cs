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
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
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

        try
        {
            var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [managedObjectType] = [path],
            };

            var (objects, pages) = await RetrieveAllPagesAsync(
                content, viewMoRef, properties, cancellationToken, n => characters += n)
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
            await TryDestroyViewAsync(viewMoRef, cancellationToken).ConfigureAwait(false);
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

        await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var target = $"{managedObjectType}.{path}";

        if (moRefs.Count == 0)
        {
            return new VsphereCandidateRead { Target = target, Fault = "no objects to ask" };
        }

        var content = _serviceContent!;
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
        (await EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).RootFolder;

    /// <summary>The license manager's reference, or null when vCenter offers none.</summary>
    public async Task<string?> GetLicenseManagerAsync(CancellationToken cancellationToken) =>
        (await EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).LicenseManager;

    /// <summary>The custom fields manager's reference, or null when vCenter offers none.</summary>
    public async Task<string?> GetCustomFieldsManagerAsync(CancellationToken cancellationToken) =>
        (await EnsureSessionAsync(cancellationToken).ConfigureAwait(false)).CustomFieldsManager;

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
        var content = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

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
