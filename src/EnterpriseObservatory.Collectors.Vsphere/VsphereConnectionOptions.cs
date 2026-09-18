using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>How to reach one vCenter.</summary>
public sealed record VsphereConnectionOptions
{
    /// <summary>Base address, e.g. <c>https://vc01.corp.local</c>.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>
    /// The account to read as.
    /// </summary>
    /// <remarks>
    /// This should be a dedicated read-only service account, not an
    /// administrator. All vSphere collection is read-only, so no write
    /// privilege is ever exercised — and product principle 5 says the
    /// monitoring tool must not be able to break production, which is a
    /// guarantee worth having technically rather than by good intentions.
    /// </remarks>
    public required string Username { get; init; }

    /// <summary>
    /// The password.
    /// </summary>
    /// <remarks>
    /// A <see cref="Secret"/> rather than a string, so that it cannot be
    /// logged, interpolated or serialised without someone writing
    /// <c>Reveal()</c> and someone else reading it in a diff. See ADR-0010.
    /// </remarks>
    public required Secret Password { get; init; }

    /// <summary>
    /// A stable name for this vCenter, used in entity ids and health tracking.
    /// </summary>
    /// <remarks>
    /// Explicit rather than derived from the address, because an address can
    /// change — and if it did, every entity id derived from it would change
    /// too, orphaning history and alerts.
    /// </remarks>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Whether to accept a certificate that does not chain to a trusted root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default and never implicit. vCenter ships with a self-signed
    /// certificate and many installations never replace it, so this has to be
    /// expressible — but as a decision someone made, recorded in
    /// configuration, not as something the collector does quietly on their
    /// behalf.
    /// </para>
    /// </remarks>
    public bool AcceptUntrustedCertificate { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Objects per inventory page.
    /// </summary>
    /// <remarks>
    /// Bounded because an unbounded retrieval can be refused outright on a
    /// large inventory. Configurable mainly so the paging path can be exercised
    /// deliberately: an environment that fits in one page never proves the
    /// continuation logic works, and silently stopping at the first page is the
    /// easiest way to report an estate as smaller and healthier than it is.
    /// </remarks>
    public int InventoryPageSize { get; init; } = 250;
}
