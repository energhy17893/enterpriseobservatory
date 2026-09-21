namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// How much of one thing a source managed to read this cycle.
/// </summary>
/// <remarks>
/// <para>
/// The product's first principle says it must not imply it looked when it did
/// not, and every rule written so far has had to honour that one entity at a
/// time — by declining a verdict and, at best, saying so in a finding of its
/// own. That does not scale and it answers the wrong question. An operator
/// does not want to hear that one rule abstained; they want to know **what
/// this product can see on this estate**, which is a fact about collection and
/// belongs to the collector rather than to whoever happened to need it.
/// </para>
/// <para>
/// Three numbers and a name. <see cref="Asked"/> is how many objects of the
/// type were queried, <see cref="Answered"/> is how many came back carrying
/// the property, and the gap is the part nobody can reason about. Deliberately
/// not a percentage: eight of ten and eight hundred of a thousand are both
/// "80%" and only one of them is a small estate where two hosts are quiet.
/// </para>
/// <para>
/// A missing row is not zero coverage. A property that was never requested has
/// no row at all, because "nobody asked" and "asked and got nothing" are
/// different facts — the same distinction
/// <see cref="Domain.Entity.Settings"/> makes between a missing key and an
/// empty value, and it is made the same way here for the same reason.
/// </para>
/// </remarks>
public sealed record PropertyCoverage
{
    /// <summary>The object type queried, in the vendor's own vocabulary.</summary>
    /// <remarks>
    /// Untranslated on purpose. A neutral name would have to be invented from
    /// one vendor, and a mapping designed against a single example ends up
    /// being vSphere wearing a label.
    /// </remarks>
    public required string ObjectType { get; init; }

    /// <summary>The property path, as the source names it.</summary>
    public required string Property { get; init; }

    /// <summary>How many objects of this type the source was asked about.</summary>
    public required int Asked { get; init; }

    /// <summary>How many of them came back carrying the property.</summary>
    public required int Answered { get; init; }

    /// <summary>
    /// Whether the source answered for every object it was asked about.
    /// </summary>
    public bool IsComplete => Answered >= Asked;

    /// <summary>
    /// Whether nothing at all came back, although something was asked.
    /// </summary>
    /// <remarks>
    /// The case that misleads. Partial coverage is visible — some objects
    /// carry the value and some do not — but a property nothing answered looks
    /// exactly like a property nothing needed to answer: every rule reading it
    /// stays quiet, the inbox stays clean, and the estate looks healthy
    /// because nobody looked at it.
    /// </remarks>
    public bool IsBlind => Asked > 0 && Answered == 0;
}
