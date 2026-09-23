using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>Who publishes a catalogue's controls.</summary>
/// <remarks>
/// Not closed to two values on purpose: M9's healthcheck feeds
/// (<c>reference-approaches.md</c> §9.2 — VMSA, CISA KEV) are a third owner
/// of a catalogue this product judges, not a third kind of finding.
/// </remarks>
public enum CatalogueOwner
{
    /// <summary>Broadcom's Security Configuration Guide.</summary>
    Broadcom,

    /// <summary>This product's own catalogue, defined and versioned in code.</summary>
    Product,

    /// <summary>An external advisory feed (not shipped yet; M9).</summary>
    Cisa,
}

/// <summary>How this product came to hold a catalogue's controls.</summary>
public enum CatalogueKind
{
    /// <summary>A vendor's document, ingested unchanged, evaluated by a setting reader.</summary>
    Passthrough,

    /// <summary>This product's own rules, judged against the graph rather than one setting.</summary>
    Computed,

    /// <summary>An external feed matched against the estate by an identifier (not shipped yet; M9).</summary>
    Feed,
}

/// <summary>
/// A catalogue registered with the compliance engine, described for the
/// posture screen's scorecard and the <c>/api/compliance</c> response.
/// </summary>
/// <remarks>
/// <para>
/// Not a replacement for <see cref="ComplianceCatalogue"/>: the domain type
/// stays the vendor's or the product's controls, carried unedited (ADR-0024
/// alternative C). This is the registry entry that describes the catalogue
/// as a whole — who owns it, how it is judged, what it is licensed under —
/// for the screens that must show more than one catalogue side by side (K3
/// §1.1) without assuming which one is "the" catalogue.
/// </para>
/// <para>
/// <see cref="Id"/> is a stable key across editions of the same catalogue
/// (SCG's <c>vsphere-8.0</c> and <c>vcf-9.1</c> are both <c>scg</c>);
/// <see cref="Release"/> is the specific version a finding was judged
/// against and changes when the vendor re-issues the guide or this product
/// restructures its own.
/// </para>
/// </remarks>
public sealed record CatalogueDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CatalogueOwner Owner { get; init; }

    public required CatalogueKind Kind { get; init; }

    public required string Release { get; init; }

    /// <summary>What the catalogue's content is licensed under; empty for the product's own.</summary>
    public string Licence { get; init; } = string.Empty;

    /// <summary>The stable id of this product's own continuity catalogue.</summary>
    public const string ContinuityId = "eo-continuity";

    /// <summary>The stable id of this product's own best-practice catalogue.</summary>
    public const string BestPracticeId = "eo-bestpractice";

    /// <summary>The stable id of any Broadcom SCG edition.</summary>
    public const string ScgId = "scg";

    /// <summary>
    /// The vendor guide among loaded catalogues, found by ownership, never by
    /// list position -- P1 removes that assumption. Null only when no
    /// catalogue owned by Broadcom is loaded, which cannot happen in
    /// production (<c>ComplianceService</c> always registers the vendor
    /// guide) but keeps this honest for a catalogue list a test hands in.
    /// </summary>
    public static ComplianceCatalogue? VendorGuide(IReadOnlyList<ComplianceCatalogue> catalogues) =>
        catalogues.FirstOrDefault(c => Of(c).Owner == CatalogueOwner.Broadcom);

    /// <summary>Describes a loaded catalogue; works for any of <see cref="ComplianceService.Catalogues"/>, in any order.</summary>
    public static CatalogueDescriptor Of(ComplianceCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var isContinuity = string.Equals(catalogue.Name, ContinuityCatalogue.Name, StringComparison.Ordinal);
        var isBestPractice = string.Equals(catalogue.Name, BestPracticeCatalogue.Name, StringComparison.Ordinal);
        var isProduct = isContinuity || isBestPractice;

        return new CatalogueDescriptor
        {
            Id = isContinuity ? ContinuityId : isBestPractice ? BestPracticeId : ScgId,
            Name = catalogue.Name,
            Owner = isProduct ? CatalogueOwner.Product : CatalogueOwner.Broadcom,
            Kind = catalogue.BindsById ? CatalogueKind.Computed : CatalogueKind.Passthrough,
            Release = catalogue.Release,
            Licence = isProduct ? string.Empty : $"Broadcom SCG licence (catalogues/scg/{catalogue.Name}/LICENSE)",
        };
    }
}
