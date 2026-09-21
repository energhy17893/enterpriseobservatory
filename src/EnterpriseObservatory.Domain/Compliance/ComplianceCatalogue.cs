namespace EnterpriseObservatory.Domain.Compliance;

/// <summary>
/// One control from a published security configuration guide, as the vendor
/// wrote it.
/// </summary>
/// <remarks>
/// Carried verbatim. The product's own reading of a control — which setting it
/// looks at and what counts as passing — lives in the evaluator that binds to
/// it, never in an edited copy of the vendor's text; an auditor comparing this
/// screen with Broadcom's file must find the same words in both.
/// </remarks>
public sealed record ComplianceControl
{
    /// <summary>The vendor's id, e.g. <c>esx-9.log-forwarding</c>. Stable within a release.</summary>
    public required string ControlId { get; init; }

    /// <summary>What the control is about, e.g. <c>ESX</c> or <c>VMware vCenter</c>.</summary>
    public string Component { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// The setting the vendor names, or <c>N/A</c> when the control is not a
    /// single setting. Sometimes several names separated by commas or lines.
    /// </summary>
    public string Parameter { get; init; } = string.Empty;

    public string InstallationDefault { get; init; } = string.Empty;

    public string BaselineValue { get; init; } = string.Empty;

    /// <summary>The vendor's priority, e.g. <c>P0</c> or <c>P0, Upon Feature Enablement</c>.</summary>
    public string Priority { get; init; } = string.Empty;

    /// <summary>The vendor's PowerCLI assessment, shown so an operator can check by hand.</summary>
    public string Assessment { get; init; } = string.Empty;
}

/// <summary>
/// A released guide, reduced to the controls that change something.
/// </summary>
/// <remarks>
/// <para>
/// Only controls whose installation default is <em>not</em> the baseline are
/// kept — the vendor's own <c>Is the Default? = NO</c> column. The others are
/// satisfied by an untouched installation; evaluating them would add a column
/// of green that says nothing and makes the real findings harder to see.
/// </para>
/// <para>
/// The release travels with every finding. Guides are re-issued, control ids
/// change between major versions (<c>esxi-8.logs-remote</c> became
/// <c>esx-9.log-forwarding</c>), and a finding that cannot say which edition
/// it was judged against cannot be defended in an audit.
/// </para>
/// </remarks>
public sealed record ComplianceCatalogue
{
    /// <summary>The vendor's release id, e.g. <c>803-20260612-01</c>.</summary>
    public required string Release { get; init; }

    /// <summary>A human name for the edition, e.g. <c>vsphere-8.0</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The controls that differ from the installation default.</summary>
    public IReadOnlyList<ComplianceControl> Controls { get; init; } = [];

    /// <summary>How many controls were left out because the default already meets them.</summary>
    public int DefaultControlsSkipped { get; init; }

    /// <summary>
    /// Why no catalogue could be loaded, or null when one was.
    /// </summary>
    /// <remarks>
    /// A catalogue that failed to load is carried as an empty one with this
    /// said, rather than stopping the service: monitoring must not go dark
    /// because a compliance file is missing, and an empty compliance screen
    /// with no reason would read as a clean estate.
    /// </remarks>
    public string? Problem { get; init; }

    /// <summary>
    /// The full account of why it could not be loaded, for the service log only.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Problem"/>, which every signed-in viewer
    /// reads. Paths on the server and the operating system's error text help
    /// whoever administers the host and tell anybody else how it is laid out.
    /// </remarks>
    public string? Diagnostic { get; init; }

    /// <summary>
    /// Whether controls bind to checks by their id rather than by the setting
    /// the vendor's parameter column names.
    /// </summary>
    /// <remarks>
    /// False for a vendor guide, whose ids change between editions while the
    /// settings do not. True for this product's own catalogue
    /// (<c>eo-continuity</c>), whose ids are ours and never change meaning —
    /// a control whose meaning changes gets a new id instead.
    /// </remarks>
    public bool BindsById { get; init; }

    public static ComplianceCatalogue Unavailable(string problem, string? diagnostic = null) => new()
    {
        Release = string.Empty,
        Name = string.Empty,
        Problem = problem,
        Diagnostic = diagnostic ?? problem,
    };
}
