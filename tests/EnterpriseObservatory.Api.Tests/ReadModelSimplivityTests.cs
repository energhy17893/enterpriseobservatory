using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using K = EnterpriseObservatory.Application.Compliance.InventoryVerdictKeys;

namespace EnterpriseObservatory.Api.Tests;

/// <summary>
/// S5: the SimpliVity annotations (ADR-0027) as the entity page and the
/// SimpliVity page show them — projected, never judged; a value the source
/// did not give is Unknown, never SAFE (ADR-0026).
/// </summary>
public partial class ReadModelTests
{
    private const string Svt = "svt-1";

    private static readonly EntityId H1 = new("vc-1:host-1");
    private static readonly EntityId H2 = new("vc-1:host-2");
    private static readonly EntityId C1 = new("vc-1:domain-c1");

    private static EntityAnnotation Annotation(EntityId entity, DateTimeOffset readAt, params (string Key, string Value)[] settings) => new()
    {
        Entity = entity,
        Namespace = "simplivity",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        SourceInstanceId = Svt,
        ReadAtUtc = readAt,
    };

    private void GivenFederation(DateTimeOffset readAt)
    {
        GivenEntities(
            Host("vc-1:host-1", HealthState.Healthy),
            Host("vc-1:host-2", HealthState.Healthy),
            Cluster("vc-1:domain-c1"),
            Vm("vc-1:vm-safe"),
            Vm("vc-1:vm-degraded"),
            Vm("vc-1:vm-unread"),
            Vm("vc-1:vm-old-backup"));
        GivenRelationships(Edge(H1, C1, RelationshipKind.PartOf), Edge(H2, C1, RelationshipKind.PartOf));

        _graphs.Replace(_graphs.Current with
        {
            Annotations =
            [
                Annotation(H1, readAt, (K.SimplivityState, "ALIVE"), (K.SimplivityUpgradeState, "SUCCESS"),
                    (K.SimplivityVersion, "5.1.0"), (K.SimplivityVirtualControllerName, "OVC-1")),
                // state not answered: Unknown, not ALIVE.
                Annotation(H2, readAt, (K.SimplivityVersion, "5.1.0")),
                Annotation(C1, readAt, (K.SimplivityName, "SVT-C1"), (K.SimplivityArbiterRequired, "true"),
                    (K.SimplivityArbiterConfigured, "true"), (K.SimplivityArbiterConnected, "true"),
                    (K.SimplivityUpgradeState, "SUCCESS"), (K.SimplivityMembers, "2")),
                Annotation(new EntityId("vc-1:vm-safe"), readAt, (K.SimplivityHaStatus, "SAFE"),
                    (K.SimplivityBackupLastUtc, T0.AddHours(-2).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture))),
                Annotation(new EntityId("vc-1:vm-degraded"), readAt, (K.SimplivityHaStatus, "DEGRADED")),
                Annotation(new EntityId("vc-1:vm-unread"), readAt),
                Annotation(new EntityId("vc-1:vm-old-backup"), readAt, (K.SimplivityHaStatus, "SAFE"),
                    (K.SimplivityBackupLastUtc, T0.AddHours(-40).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                    (K.SimplivityBackupType, "POLICY")),
            ],
        });
    }

    [Fact]
    public void An_entity_page_carries_its_annotations_and_an_unanswered_judged_key_as_unknown()
    {
        GivenFederation(T0.AddMinutes(-3));
        GivenCollectors(Health(Svt, CollectorRole.Inventory, T0));

        var h1 = Model().Entity(H1.Value)!.Annotations;
        var state = Assert.Single(h1, a => a.Key == "state");
        Assert.Equal(("simplivity", "ALIVE", Svt, T0.AddMinutes(-3), false),
            (state.Namespace, state.Value, state.Source, state.ReadAtUtc, state.CarriedForward));

        // H2's source never said its state: listed, value null (Unknown), never omitted or ALIVE.
        var h2 = Model().Entity(H2.Value)!.Annotations;
        Assert.Null(Assert.Single(h2, a => a.Key == "state").Value);
        Assert.Null(Assert.Single(h2, a => a.Key == "upgrade_state").Value);
        Assert.Equal("5.1.0", Assert.Single(h2, a => a.Key == "version").Value);
    }

    [Fact]
    public void A_silent_sources_annotation_is_shown_as_carried_forward_with_its_read_time()
    {
        GivenFederation(T0.AddHours(-5));
        GivenCollectors(Health(Svt, CollectorRole.Inventory, T0.AddHours(-5)) with { Health = HealthState.Unknown });

        var state = Assert.Single(Model().Entity(H1.Value)!.Annotations, a => a.Key == "state");
        Assert.True(state.CarriedForward);
        Assert.Equal(T0.AddHours(-5), state.ReadAtUtc);

        var source = Assert.Single(Model().Simplivity([Svt]).Sources);
        Assert.False(source.Reporting);
        Assert.All(source.Clusters, c => Assert.True(c.CarriedForward));
        Assert.All(source.NotSafeVms, v => Assert.True(v.CarriedForward));
    }

    [Fact]
    public void The_simplivity_view_projects_the_federation_and_never_counts_unknown_as_safe()
    {
        GivenFederation(T0.AddMinutes(-3));
        GivenCollectors(Health(Svt, CollectorRole.Inventory, T0));

        var view = Model().Simplivity([Svt]);
        var source = Assert.Single(view.Sources);

        Assert.True(source.Reporting);
        Assert.Equal(T0.AddMinutes(-3), source.ReadAtUtc);

        var cluster = Assert.Single(source.Clusters);
        Assert.Equal(("SVT-C1", true, true, true, 2), (cluster.Name, cluster.ArbiterRequired, cluster.ArbiterConfigured,
            cluster.ArbiterConnected, cluster.Members));
        Assert.Equal(["vc-1:host-1", "vc-1:host-2"], cluster.Hosts.Select(h => h.EntityId));
        Assert.Null(cluster.Hosts[1].State);
        Assert.Empty(source.OtherHosts);

        Assert.Equal(1, source.HostStates["ALIVE"]);
        Assert.Equal(1, source.HostStates["Unknown"]);
        Assert.Equal(1, source.ArbitersConnected["true"]);

        Assert.Equal(2, source.VmHaStatuses["SAFE"]);
        Assert.Equal(1, source.VmHaStatuses["DEGRADED"]);
        Assert.Equal(1, source.VmHaStatuses["Unknown"]);

        // Worst first, Unknown last and present — not dropped, not SAFE.
        Assert.Equal([("vm-degraded", "DEGRADED"), ("vm-unread", null)],
            source.NotSafeVms.Select(v => (v.Name, v.HaStatus)));

        // The RPO is the M8.8 check's own, not a number of this page's.
        Assert.Equal(BackupFreshnessCheck.DefaultRpo.TotalHours, view.BackupRpoHours);
        Assert.Equal((2, 2), (source.Backups.WithBackup, source.Backups.WithoutBackup));
        var old = Assert.Single(source.Backups.OlderThanRpo);
        Assert.Equal(("vm-old-backup", T0.AddHours(-40), "POLICY"), (old.Name, old.LastBackupUtc, old.Type));
    }

    [Fact]
    public void A_configured_source_that_has_not_answered_is_listed_with_nothing_invented()
    {
        var source = Assert.Single(Model().Simplivity(["svt-new"]).Sources);

        Assert.Equal("svt-new", source.InstanceId);
        Assert.Null(source.CollectorHealth);
        Assert.False(source.Reporting);
        Assert.Null(source.ReadAtUtc);
        Assert.Empty(source.Clusters);
        Assert.Empty(source.HostStates);
    }
}
