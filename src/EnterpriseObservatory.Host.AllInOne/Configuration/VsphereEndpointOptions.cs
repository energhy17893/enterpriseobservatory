namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>One configured vCenter, as it appears in configuration.</summary>
/// <remarks>
/// Separate from <c>VsphereConnectionOptions</c> because configuration is
/// strings and a connection is not. Binding straight onto the collector's type
/// would put a <see cref="Uri"/> and a password in the shape of a settings
/// file, and validation would have nowhere to live.
/// </remarks>
public sealed class VsphereEndpointOptions
{
    /// <summary>A stable name for this vCenter. Entity ids are built from it.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Base address, e.g. <c>https://vc01.corp.local</c>.</summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// A dedicated read-only service account.
    /// </summary>
    /// <remarks>
    /// Never an administrator. Every vSphere call the product makes is a read,
    /// so no write privilege is ever exercised — and product principle 5 wants
    /// that guaranteed by the account rather than by our good behaviour.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password, which must not come from a settings file.
    /// </summary>
    /// <remarks>
    /// Bound through the ordinary configuration system so user secrets,
    /// environment variables and a key vault all work without inventing a
    /// mechanism — and then checked, because the previous product's credential
    /// leak was a password sitting in a JSON file that was never meant to hold
    /// one. See <c>CredentialSourceGuard</c>.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether to accept a certificate that does not chain to a trusted root.
    /// </summary>
    /// <remarks>
    /// vCenter ships self-signed and many installations never replace it, so
    /// this has to be expressible — as a decision someone recorded, never as
    /// something the collector quietly does on their behalf.
    /// </remarks>
    public bool AcceptUntrustedCertificate { get; set; }

    /// <summary>Objects per inventory page. See the collector's options.</summary>
    public int InventoryPageSize { get; set; } = 250;

    /// <summary>What is wrong with this entry, or empty if nothing is.</summary>
    /// <remarks>
    /// Returns every problem rather than the first. Starting a service four
    /// times to be told about four missing fields is a small cruelty that costs
    /// nothing to avoid.
    /// </remarks>
    public IReadOnlyList<string> Validate(int index)
    {
        var problems = new List<string>();
        var where = string.IsNullOrWhiteSpace(InstanceId) ? $"VCenters[{index}]" : InstanceId;

        if (string.IsNullOrWhiteSpace(InstanceId))
        {
            problems.Add($"{where}: InstanceId is required. Entity ids are built from it, " +
                         "so it must be stable and must not be derived from the address.");
        }

        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri))
        {
            problems.Add($"{where}: BaseAddress '{BaseAddress}' is not an absolute URL.");
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            // Not negotiable, and not the same question as whether the
            // certificate is trusted: over http the credentials are readable by
            // anyone on the path.
            problems.Add($"{where}: BaseAddress must be https.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add($"{where}: Username is required.");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            problems.Add($"{where}: no password was supplied. Set it with user secrets or the " +
                         $"environment variable VCenters__{index}__Password — not in appsettings.json.");
        }

        if (InventoryPageSize < 1)
        {
            problems.Add($"{where}: InventoryPageSize must be at least 1.");
        }

        return problems;
    }
}
