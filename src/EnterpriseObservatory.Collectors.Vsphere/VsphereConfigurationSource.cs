using System.Globalization;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// One vCenter's configuration tier, for <see cref="ConfigurationCollectionPipeline"/>.
/// </summary>
/// <remarks>
/// What it reads is carried by the client into the next inventory read; this
/// only says how far the read got.
/// </remarks>
/// <param name="api">The client whose carry this fills.</param>
/// <param name="freshFor">
/// The configuration interval: a pass that finds the carry younger than this
/// asks vCenter nothing. That is the case right after start, when the first
/// fast read has just seeded the carry with both tiers; reading ~41 MB again
/// seconds later would say nothing new. Null always reads.
/// </param>
public sealed class VsphereConfigurationSource(IVsphereConfigurationApi api, TimeSpan? freshFor = null)
    : IConfigurationTierSource
{
    private readonly IVsphereConfigurationApi _api = api ?? throw new ArgumentNullException(nameof(api));

    public string InstanceId => _api.InstanceId;

    public async Task<ConfigurationRead> ReadAsync(CancellationToken cancellationToken)
    {
        if (freshFor is { } interval && _api.ConfigurationYoungerThan(interval))
        {
            return new ConfigurationRead { Skipped = true };
        }

        var read = await _api.RetrieveConfigurationAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<CollectionFailure> failures = read.Failures.Select(f => new CollectionFailure
        {
            Kind = f.IsPermissionDenied
                ? CollectionFailureKind.AuthorizationDenied
                : CollectionFailureKind.ProtocolError,
            Target = f.Target,
            Detail = f.Detail,
        });

        if (!read.Complete)
        {
            failures = failures.Append(new CollectionFailure
            {
                Kind = CollectionFailureKind.Timeout,
                Target = "configuration",
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"cut off by its budget after {read.Objects} objects; the rest keep their earlier reading"),
            });
        }

        return new ConfigurationRead
        {
            ObjectsRead = read.Objects,
            BytesRead = read.Bytes,
            Complete = read.Complete,
            Failures = [.. failures],
        };
    }
}
