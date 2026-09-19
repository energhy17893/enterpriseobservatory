using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>
/// The spelling of an identifier, which turned out to matter.
/// </summary>
/// <remarks>
/// Ids are opaque by design (ADR-0003), so their spelling is ours to choose —
/// and the first choice put a slash in them. An id ends up in a URL path, where
/// a slash becomes an extra segment, so every entity page in the product
/// answered with something other than the entity. With a catch-all route in
/// front of the API the symptom was a 200 carrying HTML rather than a 404.
/// </remarks>
public class EntityIdTests
{
    [Fact]
    public void An_id_carries_the_source_that_named_it()
    {
        // Managed object references are unique within a vCenter but not between
        // them, so the source has to be part of the id.
        Assert.Equal("vc-1:host-1", EntityId.For("vc-1", "host-1").Value);
    }

    [Theory]
    [InlineData("vc-1/host-1")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData(@"back\slash")]
    public void An_id_containing_a_path_separator_is_refused(string value)
    {
        // Refused rather than escaped at each boundary: an identifier that is
        // safe everywhere is one fewer thing every caller has to remember, and
        // the caller who forgets finds out in production.
        var error = Assert.Throws<ArgumentException>(() => new EntityId(value));

        Assert.Contains("path separator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_generated_id_is_safe_by_construction()
    {
        Assert.DoesNotContain('/', EntityId.New().Value);
    }

    [Fact]
    public void Ordinary_identifiers_are_left_alone()
    {
        // Dots, dashes and colons all appear in real managed object references
        // and world-wide names.
        Assert.Equal("naa.60003ff-44dc", new EntityId("naa.60003ff-44dc").Value);
    }
}
