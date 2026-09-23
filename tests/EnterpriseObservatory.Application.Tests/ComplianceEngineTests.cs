using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The generalised engine (K1): checks for any entity kind, subjects in the
/// finding's identity, and more than one catalogue.
/// </summary>
/// <remarks>
/// The two checks here are test-only stand-ins with the shapes K2's real ones
/// will have: one cluster-level setting (HA) and one per-subject rule table
/// (DRS rules, identified by uuid and labelled by name). The claims are the
/// engine's, not theirs.
/// </remarks>
public class ComplianceEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly OperatorIdentity Operator = OperatorIdentity.Verified("ertugrul");

    private const string HaControl = "eo-cont.ha-enabled";
    private const string RuleControl = "eo-cont.drs-rule";

    private static readonly EntityId Cluster1 = new("vc-1:domain-c1");

    /// <summary>A cluster-level check: one verdict, no subject.</summary>
    private sealed class HaEnabledCheck : IComplianceCheck
    {
        public EntityKind AppliesTo => EntityKind.Cluster;

        public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
        {
            if (!entity.Settings.TryGetValue("das.enabled", out var enabled))
            {
                return
                [
                    new CheckVerdict
                    {
                        Verdict = ComplianceVerdict.NotEvaluated,
                        Expected = "HA enabled",
                        Reason = "HA settings not read",
                    },
                ];
            }

            return
            [
                new CheckVerdict
                {
                    Verdict = string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)
                        ? ComplianceVerdict.Passing
                        : ComplianceVerdict.Failing,
                    Expected = "HA enabled",
                    Observed = enabled,
                },
            ];
        }
    }

    /// <summary>
    /// A per-subject check: settings <c>rule.&lt;uuid&gt;</c> = <c>&lt;name&gt;|ok</c>
    /// or <c>&lt;name&gt;|violated</c>; <c>drs.read</c> says the table was read.
    /// </summary>
    private sealed class DrsRuleCheck : IComplianceCheck
    {
        public EntityKind AppliesTo => EntityKind.Cluster;

        public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
        {
            if (!entity.Settings.ContainsKey("drs.read"))
            {
                return
                [
                    new CheckVerdict
                    {
                        Verdict = ComplianceVerdict.NotEvaluated,
                        Expected = "every DRS rule satisfied",
                        Reason = "DRS rules not read",
                    },
                ];
            }

            return
            [
                .. entity.Settings
                    .Where(s => s.Key.StartsWith("rule.", StringComparison.Ordinal))
                    .OrderBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s =>
                    {
                        var parts = s.Value.Split('|');

                        return new CheckVerdict
                        {
                            Subject = s.Key["rule.".Length..],
                            SubjectLabel = parts[0],
                            Verdict = parts[1] == "ok" ? ComplianceVerdict.Passing : ComplianceVerdict.Failing,
                            Expected = "rule satisfied",
                            Observed = parts[1],
                        };
                    }),
            ];
        }
    }

    internal static readonly IReadOnlyList<ContinuityCheck> TestChecks =
    [
        new(new ComplianceControl { ControlId = HaControl, Component = "Cluster", Title = "vSphere HA is on" },
            new HaEnabledCheck()),
        new(new ComplianceControl { ControlId = RuleControl, Component = "Cluster", Title = "DRS rules hold" },
            new DrsRuleCheck()),
    ];

    private static readonly ComplianceCatalogue Continuity = ContinuityCatalogue.Build(TestChecks);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        ContinuityCatalogue.ChecksById(TestChecks);

    private static Entity Cluster(string id = "vc-1:domain-c1", params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.Cluster,
        DisplayName = id,
        LastSeenUtc = T0,
        SourceInstanceId = id.Split(':')[0],
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static IReadOnlyList<ComplianceFinding> Evaluate(
        IReadOnlyList<Entity> estate,
        IReadOnlyList<ComplianceFinding>? previous = null,
        DateTimeOffset? now = null,
        IReadOnlyCollection<string>? reportingSources = null) =>
        ComplianceEvaluation.Evaluate(
            Continuity, estate, previous ?? [], now ?? T0,
            reportingSources: reportingSources, checksById: ById);

    private static IEnumerable<ComplianceFinding> Of(IEnumerable<ComplianceFinding> findings, string control) =>
        findings.Where(f => f.ControlId == control);

    // --- catalogue ----------------------------------------------------------------

    [Fact]
    public void The_continuity_catalogue_is_the_products_own_and_binds_by_control_id()
    {
        Assert.Equal("eo-continuity-1", Continuity.Release);
        Assert.Equal("Enterprise Observatory continuity", Continuity.Name);
        Assert.True(Continuity.BindsById);
        Assert.Equal([HaControl, RuleControl], Continuity.Controls.Select(c => c.ControlId));

        var bound = ComplianceEvaluation.Bind(Continuity, checksById: ById);

        Assert.All(bound, b => Assert.True(b.IsEvaluated));
        Assert.All(bound, b => Assert.Equal("Enterprise Observatory continuity", b.CatalogueName));
    }

    [Fact]
    public void A_continuity_control_with_no_registered_check_is_carried_with_its_reason()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(
            ContinuityCatalogue.Build([TestChecks[0]]), checksById: new Dictionary<string, IComplianceCheck>()));

        Assert.False(bound.IsEvaluated);
        Assert.Contains("No data collected", bound.NotEvaluatedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_registers_the_m8_continuity_checks()
    {
        // K2 moved the M8 rules in; see ContinuityChecksTests for each check.
        // M8.4 and M8.7 added six; see MaintenanceAndExpiryChecksTests.
        // M8.8 added one; see BackupFreshnessCheckTests.
        Assert.Equal(19, ContinuityCatalogue.Production.Count);
        Assert.All(
            ContinuityCatalogue.Build(ContinuityCatalogue.Production).Controls,
            c => Assert.StartsWith("eo-cont.", c.ControlId, StringComparison.Ordinal));
    }

    [Fact]
    public void The_scg_adapter_is_one_host_verdict_with_no_subject()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(ComplianceEvaluationTests.Catalogue(
            ComplianceEvaluationTests.LogForwarding)));

        var adapter = Assert.IsType<HostSettingCheck>(bound.Check);

        Assert.Equal(EntityKind.EsxiHost, adapter.AppliesTo);

        var verdict = Assert.Single(adapter.Judge(
            bound.Control,
            ComplianceEvaluationTests.Host(settings: ("Syslog.global.logHost", "")),
            new CheckContext { Graph = EntityGraph.Empty, NowUtc = T0 }));

        Assert.Equal(string.Empty, verdict.Subject);
        Assert.Equal(ComplianceVerdict.Failing, verdict.Verdict);
    }

    // --- checks of any entity kind -------------------------------------------------

    [Fact]
    public void A_cluster_check_judges_clusters_and_only_clusters()
    {
        var findings = Of(Evaluate(
        [
            Cluster(settings: ("das.enabled", "false")),
            ComplianceEvaluationTests.Host(settings: ("das.enabled", "false")),
            Cluster("vc-1:domain-c9") with { ObservationState = ObservationState.Vanished },
        ]), HaControl).ToList();

        var finding = Assert.Single(findings);

        Assert.Equal(Cluster1, finding.Entity);
        Assert.Equal(string.Empty, finding.Subject);
        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("false", finding.Observed);
        Assert.Equal("eo-continuity-1", finding.CatalogueRelease);
    }

    [Fact]
    public void Unread_input_is_not_evaluated_with_its_reason_never_a_failure()
    {
        var finding = Assert.Single(Of(Evaluate([Cluster()]), HaControl));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Equal("HA settings not read", finding.Reason);
        Assert.Null(finding.Observed);
    }

    [Fact]
    public void Two_subjects_on_one_entity_are_two_findings()
    {
        var findings = Of(Evaluate(
        [
            Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "keep-apart|violated"), ("rule.uuid-2", "keep-together|ok")]),
        ]), RuleControl).OrderBy(f => f.Subject, StringComparer.Ordinal).ToList();

        Assert.Equal(2, findings.Count);

        Assert.Equal("uuid-1", findings[0].Subject);
        Assert.Equal("keep-apart", findings[0].SubjectLabel);
        Assert.Equal(ComplianceVerdict.Failing, findings[0].Verdict);

        // Passing included: a subject the check can see is always a row.
        Assert.Equal("uuid-2", findings[1].Subject);
        Assert.Equal(ComplianceVerdict.Passing, findings[1].Verdict);
    }

    [Fact]
    public void An_entity_on_which_the_subject_cannot_exist_produces_no_row()
    {
        // A cluster with no DRS rules: not a pass, not a failure — nothing.
        Assert.Empty(Of(Evaluate([Cluster(settings: ("drs.read", "true"))]), RuleControl));
    }

    [Fact]
    public void An_acceptance_is_carried_per_subject_and_not_to_the_neighbour()
    {
        var estate = Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "a|violated"), ("rule.uuid-2", "b|violated")]);
        var first = Of(Evaluate([estate]), RuleControl).ToList();

        var accepted = first
            .Select(f => f.Subject == "uuid-1"
                ? f with { Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0, Reason = "CHG-1" } }
                : f)
            .ToList();

        var later = Of(Evaluate([estate], accepted, T0.AddHours(1)), RuleControl).ToList();

        Assert.NotNull(later.Single(f => f.Subject == "uuid-1").Acceptance);
        Assert.Null(later.Single(f => f.Subject == "uuid-2").Acceptance);
    }

    [Fact]
    public void A_fixed_subject_stays_as_a_passing_row()
    {
        var failing = Of(Evaluate(
            [Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "a|violated")])]), RuleControl).ToList();

        var accepted = failing.Select(f => f with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0 },
        }).ToList();

        var cluster = Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "a|ok")]) with { LastSeenUtc = T0.AddDays(1) };
        var fixedIt = Assert.Single(Of(Evaluate([cluster], accepted, T0.AddDays(1)), RuleControl));

        Assert.Equal("uuid-1", fixedIt.Subject);
        Assert.Equal(ComplianceVerdict.Passing, fixedIt.Verdict);
        Assert.Equal(T0.AddDays(1), fixedIt.FirstSeenUtc);
        Assert.Null(fixedIt.Acceptance);
    }

    [Fact]
    public void A_renamed_subject_keeps_its_finding_and_acceptance()
    {
        var before = Of(Evaluate(
            [Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "old-name|violated")])]), RuleControl)
            .Select(f => f with { Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0, Reason = "CHG-7" } })
            .ToList();

        var cluster = Cluster(settings: [("drs.read", "true"), ("rule.uuid-1", "new-name|violated")]) with
        {
            LastSeenUtc = T0.AddHours(1),
        };

        var after = Assert.Single(Of(Evaluate([cluster], before, T0.AddHours(1)), RuleControl));

        Assert.Equal("uuid-1", after.Subject);
        Assert.Equal("new-name", after.SubjectLabel);
        Assert.Equal("CHG-7", after.Acceptance!.Reason);
        Assert.Equal(T0, after.FirstSeenUtc);
    }

    [Fact]
    public void A_silent_source_keeps_its_last_verdict_as_stale_never_passing()
    {
        // The graph keeps a silent vCenter's cluster as last read.
        var cluster = Cluster(settings: ("das.enabled", "false"));
        var failing = Of(Evaluate([cluster]), HaControl).ToList();

        var stale = Assert.Single(Of(
            Evaluate([cluster], failing, T0.AddHours(6), reportingSources: ["vc-2"]), HaControl));

        Assert.True(stale.Stale);
        Assert.Equal(ComplianceVerdict.Failing, stale.Verdict);
        Assert.Equal(T0, stale.LastEvaluatedUtc);
        Assert.Equal(T0, stale.FirstSeenUtc);
    }

    [Fact]
    public void A_silent_source_whose_settings_were_never_read_is_not_turned_into_ha_off()
    {
        var stale = Assert.Single(Of(Evaluate([Cluster()], reportingSources: []), HaControl));

        Assert.True(stale.Stale);
        Assert.Equal(ComplianceVerdict.NotEvaluated, stale.Verdict);
        Assert.NotEqual(ComplianceVerdict.Passing, stale.Verdict);
    }

    [Fact]
    public void A_check_is_handed_the_estate_and_the_time()
    {
        CheckContext? seen = null;
        var probe = new ProbeCheck(c => seen = c);
        var catalogue = ContinuityCatalogue.Build([new(new ComplianceControl { ControlId = "eo-cont.probe" }, probe)]);
        var graph = new EntityGraph { Entities = new Dictionary<EntityId, Entity> { [Cluster1] = Cluster() } };

        ComplianceEvaluation.Evaluate(
            catalogue, [Cluster()], [], T0,
            checksById: new Dictionary<string, IComplianceCheck> { ["eo-cont.probe"] = probe },
            graph: graph);

        Assert.Same(graph, seen!.Graph);
        Assert.Equal(T0, seen.NowUtc);
        Assert.Null(seen.Demand);
    }

    private sealed class ProbeCheck(Action<CheckContext> seen) : IComplianceCheck
    {
        public EntityKind AppliesTo => EntityKind.Cluster;

        public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
        {
            seen(context);
            return [];
        }
    }

    // --- exceptions per subject -------------------------------------------------------

    [Fact]
    public void An_exception_without_a_subject_covers_every_subject_and_one_with_a_subject_only_that_one()
    {
        var waiver = new ComplianceWaiver
        {
            Id = "w",
            ControlId = RuleControl,
            Entity = Cluster1,
            Subject = "uuid-1",
            Reason = "r",
            Owner = "o",
            CreatedBy = "c",
            CreatedAtUtc = T0,
            ExpiresUtc = T0.AddDays(1),
        };

        Assert.True(waiver.Covers(RuleControl, Cluster1, "uuid-1", T0));
        Assert.False(waiver.Covers(RuleControl, Cluster1, "uuid-2", T0));
        Assert.True((waiver with { Subject = null }).Covers(RuleControl, Cluster1, "uuid-2", T0));
        Assert.True((waiver with { Subject = null, Entity = null }).Covers(RuleControl, new EntityId("x"), "uuid-2", T0));
    }

    // --- the service, with two catalogues ------------------------------------------------

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>The same partitioning as the real stores, keyed by subject.</summary>
    private sealed class Store : IComplianceStore
    {
        private List<ComplianceFinding> _findings = [];
        private readonly List<ComplianceWaiver> _exceptions = [];

        public List<string> EvaluatedReleases { get; } = [];

        public IReadOnlyList<ComplianceFinding> Findings => _findings;

        public IReadOnlyList<ComplianceWaiver> Exceptions => _exceptions;

        public void Evaluate(
            string catalogueRelease,
            DateTimeOffset nowUtc,
            Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate)
        {
            EvaluatedReleases.Add(catalogueRelease);
            _findings =
            [
                .. _findings.Where(f => f.CatalogueRelease != catalogueRelease),
                .. evaluate([.. _findings.Where(f => f.CatalogueRelease == catalogueRelease)]),
            ];
        }

        public ComplianceFinding? Mutate(
            string catalogueRelease,
            string controlId,
            EntityId entity,
            string subject,
            Func<ComplianceFinding, ComplianceFinding> change)
        {
            var index = _findings.FindIndex(f =>
                f.CatalogueRelease == catalogueRelease && f.ControlId == controlId && f.Entity == entity &&
                f.Subject == subject);

            if (index < 0)
            {
                return null;
            }

            _findings[index] = change(_findings[index]);
            return _findings[index];
        }

        public void AddException(ComplianceWaiver exception) => _exceptions.Add(exception);

        public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc) => false;

        public List<string?> TransitionReleases { get; } = [];

        public ComplianceTransitionsPage TransitionsSince(
            DateTimeOffset sinceUtc,
            DateTimeOffset? toUtc = null,
            string? catalogueRelease = null,
            string? controlId = null,
            EntityId? entity = null)
        {
            TransitionReleases.Add(catalogueRelease);
            return ComplianceTransitionsPage.Empty;
        }
    }

    private readonly Clock _clock = new(T0);
    private readonly Store _store = new();

    private ComplianceService Service() => new(
        [ComplianceEvaluationTests.Catalogue(ComplianceEvaluationTests.LogForwarding), Continuity],
        _store,
        _clock,
        ById);

    private static IReadOnlyList<Entity> Estate(params (string Key, string Value)[] clusterSettings) =>
    [
        ComplianceEvaluationTests.Host(settings: ("Syslog.global.logHost", "")),
        Cluster(settings: clusterSettings),
    ];

    [Fact]
    public void The_service_evaluates_every_catalogue_scg_first_with_the_same_sources()
    {
        var service = Service();

        var silent = Estate(("das.enabled", "false")).Select(e => e with { SourceInstanceId = "vc-9" }).ToList();

        service.Evaluate(silent, reportingSources: ["vc-1"]);

        Assert.Equal(["910-20260612-01", "eo-continuity-1"], _store.EvaluatedReleases);
        Assert.Equal("910-20260612-01", CatalogueDescriptor.VendorGuide(service.Catalogues)?.Release);
        Assert.Equal(2, service.Catalogues.Count);

        var findings = service.Findings();

        Assert.Contains(findings, f => f.CatalogueRelease == "910-20260612-01");
        Assert.Contains(findings, f => f.CatalogueRelease == "eo-continuity-1");
        Assert.All(findings, f => Assert.True(f.Stale));
    }

    [Fact]
    public void The_service_lists_the_controls_of_every_catalogue_tagged_with_their_catalogue()
    {
        var controls = Service().Controls();

        Assert.Equal(
            ["vcf-9.1", "Enterprise Observatory continuity", "Enterprise Observatory continuity"],
            controls.Select(c => c.CatalogueName));
    }

    [Fact]
    public void An_exception_on_one_subject_leaves_the_other_failing()
    {
        var service = Service();
        service.Evaluate(Estate(("drs.read", "true"), ("rule.uuid-1", "a|violated"), ("rule.uuid-2", "b|violated")));

        var added = service.AddException(RuleControl, Cluster1, "uuid-1", "Known, CHG-9", "infra", T0.AddDays(7), Operator);

        Assert.True(added.Applied);
        Assert.Equal("uuid-1", added.Exception!.Subject);

        var states = service.Findings()
            .Where(f => f.ControlId == RuleControl)
            .ToDictionary(f => f.Subject, f => f.StateAt(service.Exceptions(), _clock.UtcNow));

        Assert.Equal(FindingState.Excepted, states["uuid-1"]);
        Assert.Equal(FindingState.Failing, states["uuid-2"]);
    }

    [Fact]
    public void A_blank_exception_subject_means_every_subject()
    {
        var service = Service();

        var added = service.AddException(RuleControl, Cluster1, " ", "why", "who", T0.AddDays(7), Operator);

        Assert.Null(added.Exception!.Subject);
    }

    [Fact]
    public void A_subject_finding_is_accepted_by_its_subject()
    {
        var service = Service();
        service.Evaluate(Estate(("drs.read", "true"), ("rule.uuid-1", "a|violated"), ("rule.uuid-2", "b|violated")));

        var result = service.Accept(RuleControl, Cluster1, "uuid-2", "CHG-2", Operator);

        Assert.True(result.Applied);
        Assert.Equal("uuid-2", result.Finding!.Subject);

        var accepted = service.Findings().Where(f => f.Acceptance is not null).ToList();

        Assert.Equal("uuid-2", Assert.Single(accepted).Subject);
    }

    [Fact]
    public void A_continuity_control_is_known_to_the_exception_check()
    {
        var result = Service().AddException(HaControl, null, null, "why", "who", T0.AddDays(7), Operator);

        Assert.True(result.Applied);
    }

    [Fact]
    public void History_for_a_control_is_read_from_its_own_catalogue_and_otherwise_from_every_one()
    {
        var service = Service();

        service.TransitionsSince(T0.AddDays(-1), T0, RuleControl);
        Assert.Equal(["eo-continuity-1"], _store.TransitionReleases);

        _store.TransitionReleases.Clear();
        service.TransitionsSince(T0.AddDays(-1), T0);
        Assert.Equal(["910-20260612-01", "eo-continuity-1"], _store.TransitionReleases);
    }

    [Fact]
    public void A_catalogue_that_failed_to_load_does_not_stop_the_other()
    {
        var service = new ComplianceService(
            [ComplianceCatalogue.Unavailable("missing"), Continuity], _store, _clock, ById);

        service.Evaluate(Estate(("das.enabled", "true")));

        Assert.Equal(["eo-continuity-1"], _store.EvaluatedReleases);
    }
}
