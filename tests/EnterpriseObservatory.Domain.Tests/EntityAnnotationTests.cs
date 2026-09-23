using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Domain.Tests;

/// <summary>ADR-0027: a source annotates an entity it does not own; it does not replace it.</summary>
public class EntityAnnotationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly EntityRetentionPolicy Policy = EntityRetentionPolicy.Default;

    private const string Vc = "vc-1";
    private const string Svt = "svt-1";

    private static readonly EntityId HostId = EntityId.For(Vc, "host-21");
    private static readonly EntityId ClusterId = EntityId.For(Vc, "domain-c7");

    /// <summary>A vSphere host with everything the owner carries.</summary>
    private static Entity VsphereHost(DateTimeOffset? seen = null) => new()
    {
        Id = HostId,
        Kind = EntityKind.EsxiHost,
        DisplayName = "esx01.lab.example",
        SourceInstanceId = Vc,
        Health = HealthState.Healthy,
        LastSeenUtc = seen ?? T0,
        Marks = [IdentityMark.Create(IdentityMarkKind.HardwareUuid, "4C4C4544-0001", Vc)],
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Syslog.global.logHost"] = "udp://10.0.0.1",
            ["backup.lastUtc"] = "2026-09-22T02:00:00Z",
        },
    };

    private static Entity VsphereCluster() => new()
    {
        Id = ClusterId,
        Kind = EntityKind.Cluster,
        DisplayName = "SVT-Cluster-1",
        SourceInstanceId = Vc,
        LastSeenUtc = T0,
        DrsRules = [new DrsRule { Name = "keep-apart", Kind = DrsRuleKind.AntiAffinity, Enabled = true }],
    };

    private static readonly Relationship HostInCluster = new()
    {
        From = HostId,
        To = ClusterId,
        Kind = RelationshipKind.PartOf,
        ObservedAtUtc = T0,
    };

    private static EntityAnnotation Note(
        EntityId entity, string state = "ALIVE", string source = Svt, DateTimeOffset? at = null) => new()
    {
        Entity = entity,
        Namespace = "simplivity",
        Settings = new Dictionary<string, string> { ["simplivity.state"] = state },
        SourceInstanceId = source,
        ReadAtUtc = at ?? T0,
    };

    private static EntityGraph VsphereCycle(EntityGraph graph, DateTimeOffset at, params string[] reporting) =>
        graph.Merge([VsphereHost(at), VsphereCluster()], [HostInCluster], reporting.Length == 0 ? [Vc] : reporting, at, Policy);

    private static EntityGraph SimplivityCycle(EntityGraph graph, DateTimeOffset at, string state = "ALIVE") =>
        graph.Merge([], [], [Svt], at, Policy, [Note(HostId, state, at: at), Note(ClusterId, state, at: at)]);

    /// <summary>What the vSphere owner said, with the SimpliVity namespace taken back out.</summary>
    private static object OwnerView(EntityGraph graph, EntityId id)
    {
        var e = graph.Entities[id];

        return new
        {
            e.Kind,
            e.DisplayName,
            e.SourceInstanceId,
            e.Health,
            Marks = string.Join(",", e.Marks),
            DrsRules = string.Join(",", e.DrsRules.Select(r => $"{r.Name}/{r.Kind}/{r.Enabled}")),
            Settings = string.Join(",", e.Settings
                .Where(s => !s.Key.StartsWith("simplivity.", StringComparison.Ordinal))
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => $"{s.Key}={s.Value}")),
            Relationships = string.Join(",", graph.Relationships.Select(r => $"{r.From}>{r.To}:{r.Kind}")),
        };
    }

    [Fact]
    public void Vsphere_fields_are_identical_whichever_order_the_two_snapshots_merge_in()
    {
        var vsphereOnly = VsphereCycle(EntityGraph.Empty, T0);

        var vsphereThenSvt = SimplivityCycle(vsphereOnly, T0.AddMinutes(5));
        var svtThenVsphere = VsphereCycle(SimplivityCycle(EntityGraph.Empty, T0), T0);
        var svtThenVsphereThenSvt = SimplivityCycle(svtThenVsphere, T0.AddMinutes(5));
        var sameCycle = EntityGraph.Empty.Merge(
            [VsphereHost(), VsphereCluster()], [HostInCluster], [Vc, Svt], T0, Policy,
            [Note(HostId), Note(ClusterId)]);

        foreach (var id in new[] { HostId, ClusterId })
        {
            var expected = OwnerView(vsphereOnly, id);

            Assert.Equal(expected, OwnerView(vsphereThenSvt, id));
            Assert.Equal(expected, OwnerView(svtThenVsphere, id));
            Assert.Equal(expected, OwnerView(svtThenVsphereThenSvt, id));
            Assert.Equal(expected, OwnerView(sameCycle, id));
        }

        // And the annotation did land where it could.
        Assert.Equal("ALIVE", vsphereThenSvt.Entities[HostId].Settings["simplivity.state"]);
        Assert.Equal("ALIVE", sameCycle.Entities[ClusterId].Settings["simplivity.state"]);
        Assert.Equal(2, sameCycle.Entities.Count);
    }

    [Fact]
    public void An_annotation_never_creates_an_entity()
    {
        var graph = SimplivityCycle(EntityGraph.Empty, T0);

        Assert.Empty(graph.Entities);
        Assert.Empty(graph.Annotations);
    }

    [Fact]
    public void A_silent_source_s_namespace_is_carried_forward_through_the_owner_s_own_replacement()
    {
        var graph = SimplivityCycle(VsphereCycle(EntityGraph.Empty, T0), T0, state: "FAULTY");

        // SimpliVity silent; vSphere keeps reporting and replaces the entity each cycle.
        graph = VsphereCycle(graph, T0.AddHours(1));
        graph = VsphereCycle(graph, T0.AddDays(2));

        Assert.Equal("FAULTY", graph.Entities[HostId].Settings["simplivity.state"]);
    }

    [Fact]
    public void Past_the_two_day_limit_a_silent_source_s_namespace_reads_as_absent()
    {
        var graph = SimplivityCycle(VsphereCycle(EntityGraph.Empty, T0), T0);

        graph = VsphereCycle(graph, T0.AddDays(2).AddMinutes(1));

        Assert.False(graph.Entities[HostId].Settings.ContainsKey("simplivity.state"));
        Assert.Empty(graph.Annotations);
        Assert.Equal("udp://10.0.0.1", graph.Entities[HostId].Settings["Syslog.global.logHost"]);
    }

    [Fact]
    public void An_expired_namespace_is_removed_from_an_entity_whose_owner_is_silent_too()
    {
        var graph = SimplivityCycle(VsphereCycle(EntityGraph.Empty, T0), T0);

        // Nobody reports; the entity is kept as it was, overlay included, until the limit.
        graph = graph.Merge([], [], [], T0.AddDays(3), Policy);

        Assert.False(graph.Entities[HostId].Settings.ContainsKey("simplivity.state"));
    }

    [Fact]
    public void A_reporting_source_that_no_longer_annotates_an_entity_takes_its_keys_back()
    {
        var graph = SimplivityCycle(VsphereCycle(EntityGraph.Empty, T0), T0);

        graph = graph.Merge([], [], [Svt], T0.AddMinutes(5), Policy, [Note(ClusterId)]);

        Assert.False(graph.Entities[HostId].Settings.ContainsKey("simplivity.state"));
        Assert.True(graph.Entities[ClusterId].Settings.ContainsKey("simplivity.state"));
    }

    [Fact]
    public void Two_sources_writing_one_namespace_on_one_entity_in_one_cycle_throw()
    {
        var graph = VsphereCycle(EntityGraph.Empty, T0);

        var thrown = Assert.Throws<InvalidOperationException>(() => graph.Merge(
            [], [], [Svt, "svt-2"], T0, Policy, [Note(HostId), Note(HostId, source: "svt-2")]));

        Assert.Contains("ADR-0027", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_outside_the_namespace_throws()
    {
        var graph = VsphereCycle(EntityGraph.Empty, T0);
        var stray = Note(HostId) with
        {
            Settings = new Dictionary<string, string> { ["backup.lastUtc"] = "2026-09-23T00:00:00Z" },
        };

        Assert.Throws<ArgumentException>(() => graph.Merge([], [], [Svt], T0, Policy, [stray]));
    }

    [Fact]
    public void A_fresh_annotation_replaces_one_carried_from_another_source()
    {
        // The connection was renamed: the old name is silent, the new one answers.
        var graph = SimplivityCycle(VsphereCycle(EntityGraph.Empty, T0), T0, state: "FAULTY");

        graph = graph.Merge([], [], ["svt-renamed"], T0.AddHours(1), Policy,
            [Note(HostId, "ALIVE", source: "svt-renamed", at: T0.AddHours(1))]);

        Assert.Equal("ALIVE", graph.Entities[HostId].Settings["simplivity.state"]);
        Assert.Single(graph.Annotations, a => a.Entity == HostId);
    }
}
