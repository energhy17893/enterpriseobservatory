using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

public class IdentityResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static Entity Make(string name, EntityKind kind, params IdentityMark[] marks) => new()
    {
        Id = EntityId.New(),
        Kind = kind,
        DisplayName = name,
        Marks = marks,
        LastSeenUtc = Now,
    };

    [Fact]
    public void A_shared_hardware_uuid_is_enough_to_match()
    {
        var esxi = Make("esx01", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "4C4C4544-0032", "vsphere"));
        var bmc = Make("esx01-ilo", EntityKind.Bmc,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "4C4C4544-0032", "ilo"));

        var evidence = IdentityResolver.Match(esxi.Marks, bmc.Marks);

        Assert.Single(evidence);
        Assert.Equal(IdentityMarkKind.HardwareUuid, evidence[0].Kind);
    }

    [Fact]
    public void A_shared_ip_alone_is_not_enough_to_match()
    {
        // An address is reused, a BMC has its own address, and NAT makes it
        // ambiguous. The previous product treated an IP intersection as
        // sufficient, which can fold two unrelated machines into one.
        var a = Make("host-a", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.IpAddress, "10.5.1.15", "vsphere"));
        var b = Make("host-b", EntityKind.Bmc,
            IdentityMark.Create(IdentityMarkKind.IpAddress, "10.5.1.15", "ilo"));

        Assert.Empty(IdentityResolver.Match(a.Marks, b.Marks));
    }

    [Fact]
    public void Two_weak_marks_together_are_enough_to_match()
    {
        var a = Make("host-a", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.IpAddress, "10.5.1.15", "vsphere"),
            IdentityMark.Create(IdentityMarkKind.Fqdn, "esx01.corp.local", "vsphere"));
        var b = Make("host-b", EntityKind.PhysicalServer,
            IdentityMark.Create(IdentityMarkKind.IpAddress, "10.5.1.15", "oneview"),
            IdentityMark.Create(IdentityMarkKind.Fqdn, "esx01.corp.local", "oneview"));

        Assert.Equal(2, IdentityResolver.Match(a.Marks, b.Marks).Count);
    }

    [Fact]
    public void A_shared_short_hostname_alone_does_not_match()
    {
        // Cluster members following a naming convention collide constantly.
        var a = Make("a", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.ShortHostname, "esx01", "vsphere"));
        var b = Make("b", EntityKind.Bmc,
            IdentityMark.Create(IdentityMarkKind.ShortHostname, "esx01", "ilo"));

        Assert.Empty(IdentityResolver.Match(a.Marks, b.Marks));
    }

    [Fact]
    public void Marks_are_normalized_so_spelling_differences_still_match()
    {
        var a = Make("a", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.SerialNumber, " ABC123 ", "vsphere"));
        var b = Make("b", EntityKind.PhysicalServer,
            IdentityMark.Create(IdentityMarkKind.SerialNumber, "abc123", "oneview"));

        Assert.Single(IdentityResolver.Match(a.Marks, b.Marks));
    }

    [Fact]
    public void Identity_is_transitive_across_three_sources()
    {
        // vCenter sees the host, iLO sees the BMC, OneView sees the chassis.
        // vCenter and OneView share nothing directly; they are linked only
        // through iLO. All three must still land in one component.
        var esxi = Make("esx01", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-1", "vsphere"));
        var bmc = Make("esx01-ilo", EntityKind.Bmc,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-1", "ilo"),
            IdentityMark.Create(IdentityMarkKind.SerialNumber, "sn-9", "ilo"));
        var chassis = Make("bay-3", EntityKind.PhysicalServer,
            IdentityMark.Create(IdentityMarkKind.SerialNumber, "sn-9", "oneview"));

        var result = IdentityResolver.Resolve([esxi, bmc, chassis], Now);

        Assert.Single(result.Components);
        Assert.Equal(3, result.Components[0].Count);
    }

    [Fact]
    public void Unmatched_entities_each_form_their_own_component()
    {
        var a = Make("a", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-a", "vsphere"));
        var b = Make("b", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-b", "vsphere"));

        var result = IdentityResolver.Resolve([a, b], Now);

        Assert.Equal(2, result.Components.Count);
        Assert.Empty(result.SameAsEdges);
    }

    [Fact]
    public void Every_match_carries_the_evidence_that_justifies_it()
    {
        // A match is a retractable claim, not a merge. If it turns out to be
        // wrong we need to be able to explain and withdraw it. See ADR-0004.
        var esxi = Make("esx01", EntityKind.EsxiHost,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-1", "vsphere"));
        var bmc = Make("esx01-ilo", EntityKind.Bmc,
            IdentityMark.Create(IdentityMarkKind.HardwareUuid, "uuid-1", "ilo"));

        var result = IdentityResolver.Resolve([esxi, bmc], Now);

        var edge = Assert.Single(result.SameAsEdges);
        Assert.Equal(RelationshipKind.SameAs, edge.Kind);
        Assert.NotEmpty(edge.Evidence);
        Assert.Equal(IdentityMarkKind.HardwareUuid, edge.Evidence[0].Kind);
    }

    [Fact]
    public void Resolving_an_empty_set_yields_nothing()
    {
        var result = IdentityResolver.Resolve([], Now);

        Assert.Empty(result.Components);
        Assert.Empty(result.SameAsEdges);
    }
}
