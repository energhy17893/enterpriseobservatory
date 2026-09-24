using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Compliance findings and exceptions, against a real server.
/// </summary>
/// <remarks>
/// The claims are the ones a restart would break: that an acceptance and its
/// author come back, that a null observation stays null rather than becoming
/// an empty string (the difference between "not read" and "not configured"),
/// that an evaluation replaces its own release's findings and nobody else's,
/// that a verdict change leaves a transition behind, and that a withdrawn
/// exception is still on the record.
/// </remarks>
public class ComplianceStoreTests : IDisposable
{
    private const string Release = "803-20260612-01";

    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static ComplianceFinding Finding(
        string entity = "vc-1:host-1",
        ComplianceVerdict verdict = ComplianceVerdict.Failing,
        string? observed = "",
        string release = Release) => new()
        {
            ControlId = "esxi-8.logs-remote",
            CatalogueRelease = release,
            Entity = new EntityId(entity),
            EntityName = "esx-01",
            Verdict = verdict,
            Observed = observed,
            Expected = "set to a log target",
            Reason = observed is null ? "The host did not report it." : null,
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0.AddMinutes(5),
        };

    private long Count(string sql) => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    [SkippableFact]
    public void Findings_and_their_acceptance_survive_a_restart()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding(), Finding("vc-1:host-2", ComplianceVerdict.NotEvaluated, null)]);
        store.Mutate(Release, "esxi-8.logs-remote", new EntityId("vc-1:host-1"), "", f => f with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0.AddHours(1), Reason = "CHG-1" },
        });

        _live.Restart();

        var findings = new PostgresComplianceStore(_live.Database).Findings
            .OrderBy(f => f.Entity.Value, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(ComplianceVerdict.Failing, findings[0].Verdict);
        Assert.Equal(string.Empty, findings[0].Observed);
        Assert.Equal("ertugrul", findings[0].Acceptance!.By);
        Assert.Equal(T0.AddHours(1), findings[0].Acceptance!.AtUtc);
        Assert.Equal(T0, findings[0].FirstSeenUtc);

        Assert.Equal(ComplianceVerdict.NotEvaluated, findings[1].Verdict);
        Assert.Null(findings[1].Observed);
        Assert.Null(findings[1].Acceptance);
    }

    [SkippableFact]
    public void An_evaluation_removes_findings_that_left_it()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding("vc-1:host-1"), Finding("vc-1:host-2")]);
        store.Evaluate(Release, T0, previous => [.. previous.Where(f => f.Entity.Value == "vc-1:host-1")]);

        _live.Restart();

        Assert.Equal(
            "vc-1:host-1",
            Assert.Single(new PostgresComplianceStore(_live.Database).Findings).Entity.Value);
    }

    [SkippableFact]
    public void An_evaluation_leaves_another_catalogue_releases_findings_alone()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate("other-vendor-2026", T0, _ => [Finding(release: "other-vendor-2026")]);

        store.Evaluate(Release, T0, previous =>
        {
            // Handed only its own release's findings.
            Assert.Empty(previous);
            return [Finding()];
        });
        store.Evaluate(Release, T0, _ => []);

        _live.Restart();

        Assert.Equal(
            "other-vendor-2026",
            Assert.Single(new PostgresComplianceStore(_live.Database).Findings).CatalogueRelease);
    }

    [SkippableFact]
    public void An_evaluation_that_returns_another_releases_finding_is_refused()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        Assert.Throws<ArgumentException>(() =>
            store.Evaluate(Release, T0, _ => [Finding(release: "other-vendor-2026")]));
        Assert.Empty(store.Findings);
    }

    [SkippableFact]
    public void An_unchanged_evaluation_writes_only_the_evidence_time()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        IReadOnlyList<ComplianceFinding> estate =
        [
            .. Enumerable.Range(1, 50).Select(i => Finding($"vc-1:host-{i}")),
        ];

        store.Evaluate(Release, T0, _ => estate);

        // How many rows that costs is ComplianceFindingChangesTests' claim;
        // this one is that the set-based update lands on every row.
        var later = T0.AddMinutes(10);

        store.Evaluate(Release, later, previous =>
            [.. previous.Select(f => f with { LastEvaluatedUtc = later })]);

        var reopened = new PostgresComplianceStore(_live.Database);

        Assert.All(reopened.Findings, f => Assert.Equal(later, f.LastEvaluatedUtc));

        // Nothing changed verdict, so nothing beyond the first verdicts was
        // recorded as history.
        Assert.Equal(50, Count("SELECT count(*) FROM compliance_transition;"));
    }

    [SkippableFact]
    public void A_verdict_change_is_recorded_as_a_transition()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        var host = new EntityId("vc-1:host-1");

        store.Evaluate(Release, T0, _ => [Finding()]);
        store.Evaluate(Release, T0.AddMinutes(5), _ => [Finding() with { LastEvaluatedUtc = T0.AddMinutes(5) }]);
        store.Evaluate(Release, T0.AddHours(1), _ =>
        [
            Finding(verdict: ComplianceVerdict.Passing, observed: "udp://10.0.0.5:514") with
            {
                FirstSeenUtc = T0.AddHours(1),
                LastEvaluatedUtc = T0.AddHours(1),
            },
        ]);
        store.Evaluate(Release, T0.AddHours(2), _ => []);

        var history = store.Transitions(Release, "esxi-8.logs-remote", host, "");

        Assert.Collection(
            history,
            first =>
            {
                Assert.Null(first.From);
                Assert.Equal(ComplianceVerdict.Failing, first.To);
                Assert.Equal(string.Empty, first.Observed);
                Assert.Equal(T0, first.AtUtc);
            },
            fixedIt =>
            {
                Assert.Equal(ComplianceVerdict.Failing, fixedIt.From);
                Assert.Equal(ComplianceVerdict.Passing, fixedIt.To);
                Assert.Equal("udp://10.0.0.5:514", fixedIt.Observed);
                Assert.Equal(T0.AddHours(1), fixedIt.EvidenceUtc);
            },
            gone =>
            {
                Assert.Equal(ComplianceVerdict.Passing, gone.From);
                Assert.Null(gone.To);
                Assert.Equal(T0.AddHours(2), gone.AtUtc);
            });
    }

    [SkippableFact]
    public void Transitions_older_than_the_retention_are_pruned()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        store.Evaluate(Release, T0, _ => [Finding()]);
        store.Evaluate(Release, T0 + PostgresComplianceStore.TransitionRetention + TimeSpan.FromDays(1), _ =>
            [Finding(verdict: ComplianceVerdict.Passing, observed: "udp://x:514")]);

        var history = store.Transitions(Release, "esxi-8.logs-remote", new EntityId("vc-1:host-1"), "");

        Assert.Equal(ComplianceVerdict.Passing, Assert.Single(history).To);
    }

    [SkippableFact]
    public void A_restart_while_the_source_is_silent_writes_no_transition()
    {
        RequireDatabase();

        // The mechanism of 24 September 2026: entity settings are not stored,
        // so the graph a restart loads has none, and the first cycle after it
        // judged the silent vCenter's hosts on nothing.
        var catalogue = new ComplianceCatalogue
        {
            Release = Release,
            Name = "vsphere-8.0",
            Controls =
            [
                new ComplianceControl
                {
                    ControlId = "esxi-8.logs-remote",
                    Component = "VMware ESXi",
                    Parameter = "Syslog.global.logHost",
                    BaselineValue = "Site-Specific Log Server",
                },
            ],
        };

        var host = new Entity
        {
            Id = new EntityId("vc-1:host-1"),
            Kind = EntityKind.EsxiHost,
            DisplayName = "esx-01",
            SourceInstanceId = "vc-1",
            LastSeenUtc = T0,
            Settings = new Dictionary<string, string> { ["Syslog.global.logHost"] = "udp://10.0.0.5:514" },
        };

        var clock = new Clock { UtcNow = T0 };
        new PostgresEntityGraphStore(_live.Database).Replace(
            new EntityGraph { Entities = new Dictionary<EntityId, Entity> { [host.Id] = host } });
        new ComplianceService(catalogue, new PostgresComplianceStore(_live.Database), clock)
            .Evaluate([host], reportingSources: ["vc-1"]);

        var transitions = Count("SELECT count(*) FROM compliance_transition;");

        _live.Restart();

        clock.UtcNow = T0.AddHours(12);
        var graph = new PostgresEntityGraphStore(_live.Database).Current;
        var store = new PostgresComplianceStore(_live.Database);
        new ComplianceService(catalogue, store, clock).Evaluate([.. graph.Active], reportingSources: [], graph);

        var finding = Assert.Single(store.Findings);
        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.True(finding.Stale);
        Assert.Equal(transitions, Count("SELECT count(*) FROM compliance_transition;"));
    }

    [SkippableFact]
    public void A_restart_where_simplivity_answers_a_cycle_after_vsphere_writes_no_transition()
    {
        RequireDatabase();

        // Annotations are not stored either: after a restart the vCenter
        // answered first, the VM came back without its SimpliVity backup
        // time, and backup freshness went NotEvaluated until SimpliVity
        // answered a cycle later.
        var catalogue = ContinuityCatalogue.Build(
            [.. ContinuityCatalogue.Production.Where(c => c.Control.ControlId == ContinuityControls.BackupFreshness)]);
        var byId = ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production);

        IReadOnlyList<SourceConnection> connections =
        [
            new() { InstanceId = "vc-1", Kind = ConnectionKinds.Vsphere, BaseAddress = new Uri("https://vc-1"), Username = "r" },
            new() { InstanceId = "svt-1", Kind = ConnectionKinds.Simplivity, BaseAddress = new Uri("https://svt-1"), Username = "r" },
        ];

        var vm = new Entity
        {
            Id = new EntityId("vc-1:vm-1"),
            Kind = EntityKind.VirtualMachine,
            DisplayName = "vm-1",
            SourceInstanceId = "vc-1",
            LastSeenUtc = T0,
        };
        var annotated = vm with
        {
            Settings = new Dictionary<string, string>
            {
                [InventoryVerdictKeys.SimplivityBackupLastUtc] = T0.AddHours(-2).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        var clock = new Clock { UtcNow = T0 };

        void Cycle(PostgresComplianceStore store, Entity estate, IReadOnlyCollection<string> reporting) =>
            new ComplianceService([catalogue], store, clock, byId).Evaluate(
                [estate], reporting, EntityGraph.Empty with { Entities = new Dictionary<EntityId, Entity> { [estate.Id] = estate } },
                silentNamespaces: ComplianceEvaluation.SilentNamespaces(connections, reporting));

        new PostgresEntityGraphStore(_live.Database).Replace(
            new EntityGraph { Entities = new Dictionary<EntityId, Entity> { [vm.Id] = annotated } });
        Cycle(new PostgresComplianceStore(_live.Database), annotated, ["vc-1", "svt-1"]);

        var transitions = Count("SELECT count(*) FROM compliance_transition;");

        _live.Restart();

        var store = new PostgresComplianceStore(_live.Database);
        var loaded = new PostgresEntityGraphStore(_live.Database).Current.Entities[vm.Id];

        clock.UtcNow = T0.AddMinutes(2);
        Cycle(store, loaded, ["vc-1"]);

        var carried = Assert.Single(store.Findings);
        Assert.Equal(ComplianceVerdict.Passing, carried.Verdict);
        Assert.True(carried.Stale);

        clock.UtcNow = T0.AddMinutes(4);
        Cycle(store, annotated, ["vc-1", "svt-1"]);

        var back = Assert.Single(store.Findings);
        Assert.Equal(ComplianceVerdict.Passing, back.Verdict);
        Assert.False(back.Stale);
        Assert.Equal(transitions, Count("SELECT count(*) FROM compliance_transition;"));
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    [SkippableFact]
    public void A_stale_finding_stays_stale_across_a_restart()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding() with { Stale = true }]);

        _live.Restart();

        Assert.True(Assert.Single(new PostgresComplianceStore(_live.Database).Findings).Stale);
    }

    [SkippableFact]
    public void An_acceptance_is_written_to_its_own_releases_finding_only()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate("803-20250101-01", T0, _ => [Finding(release: "803-20250101-01")]);
        store.Evaluate(Release, T0, _ => [Finding()]);

        store.Mutate(Release, "esxi-8.logs-remote", new EntityId("vc-1:host-1"), "", f => f with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0 },
        });

        _live.Restart();

        var findings = new PostgresComplianceStore(_live.Database).Findings;

        Assert.NotNull(findings.Single(f => f.CatalogueRelease == Release).Acceptance);
        Assert.Null(findings.Single(f => f.CatalogueRelease == "803-20250101-01").Acceptance);
    }

    [SkippableFact]
    public void A_withdrawn_exception_stays_on_the_record_with_who_withdrew_it()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.AddException(new ComplianceWaiver
        {
            Id = "x1",
            ControlId = "esxi-8.logs-remote",
            Entity = null,
            Reason = "Lab",
            Owner = "infra",
            CreatedBy = "ertugrul",
            CreatedAtUtc = T0,
            ExpiresUtc = T0.AddDays(30),
        });

        _live.Restart();

        var reopened = new PostgresComplianceStore(_live.Database);
        var exception = Assert.Single(reopened.Exceptions);

        Assert.Null(exception.Entity);
        Assert.Equal(T0.AddDays(30), exception.ExpiresUtc);
        Assert.Null(exception.RemovedAtUtc);

        Assert.True(reopened.RemoveException("x1", "second-operator", T0.AddDays(1)));

        // A second withdrawal does not rewrite who did the first.
        Assert.False(reopened.RemoveException("x1", "third-operator", T0.AddDays(2)));

        _live.Restart();

        var withdrawn = Assert.Single(new PostgresComplianceStore(_live.Database).Exceptions);

        Assert.Equal("second-operator", withdrawn.RemovedBy);
        Assert.Equal(T0.AddDays(1), withdrawn.RemovedAtUtc);
        Assert.False(withdrawn.Covers("esxi-8.logs-remote", new EntityId("vc-1:host-1"), "", T0.AddDays(1)));
    }

    // --- history: filters and the row cap (architecture review 3) ----------

    [SkippableFact]
    public void TransitionsSince_narrows_by_control_and_entity_in_sql_not_after_the_read()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        // Two controls, two entities each -- the release's whole first
        // evaluation writes one "new finding" transition per row.
        store.Evaluate(Release, T0, _ =>
        [
            Finding("vc-1:host-1"),
            Finding("vc-1:host-2"),
            Finding("vc-1:host-1") with { ControlId = "esxi-8.other-control" },
            Finding("vc-1:host-2") with { ControlId = "esxi-8.other-control" },
        ]);

        var byControl = store.TransitionsSince(T0.AddDays(-1), controlId: "esxi-8.logs-remote");
        Assert.All(byControl.Transitions, t => Assert.Equal("esxi-8.logs-remote", t.ControlId));
        Assert.Equal(2, byControl.Transitions.Count);

        var byEntity = store.TransitionsSince(T0.AddDays(-1), entity: new EntityId("vc-1:host-1"));
        Assert.All(byEntity.Transitions, t => Assert.Equal("vc-1:host-1", t.Entity.Value));
        Assert.Equal(2, byEntity.Transitions.Count);

        var byBoth = store.TransitionsSince(
            T0.AddDays(-1), controlId: "esxi-8.other-control", entity: new EntityId("vc-1:host-2"));
        var only = Assert.Single(byBoth.Transitions);
        Assert.Equal("esxi-8.other-control", only.ControlId);
        Assert.Equal("vc-1:host-2", only.Entity.Value);
    }

    [SkippableFact]
    public void TransitionsSince_excludes_transitions_after_the_end_of_the_window()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        store.Evaluate(Release, T0, _ => [Finding()]);
        store.Evaluate(Release, T0.AddDays(1), _ => [Finding() with { Verdict = ComplianceVerdict.Passing }]);

        var upToCreation = store.TransitionsSince(T0.AddDays(-1), toUtc: T0.AddHours(1));
        Assert.Single(upToCreation.Transitions);

        var both = store.TransitionsSince(T0.AddDays(-1), toUtc: T0.AddDays(2));
        Assert.Equal(2, both.Transitions.Count);
    }

    // --- P2 (revised): "verdict at start" for the net posture change delta -----

    [SkippableFact]
    public void LastTransitionsAtOrBefore_returns_the_latest_row_per_identity_at_or_before_the_time()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        // A finding failed, was fixed, then failed again -- three rows for
        // the same identity. Only the last one at or before T0 + 2h matters.
        store.Evaluate(Release, T0, _ => [Finding("vc-1:host-1")]);
        store.Evaluate(Release, T0.AddHours(1), _ => [Finding("vc-1:host-1", ComplianceVerdict.Passing)]);
        store.Evaluate(Release, T0.AddHours(3), _ => [Finding("vc-1:host-1")]);

        var atStart = store.LastTransitionsAtOrBefore(Release, T0.AddHours(2));
        var only = Assert.Single(atStart);

        Assert.Equal("esxi-8.logs-remote", only.ControlId);
        Assert.Equal("vc-1:host-1", only.Entity.Value);
        Assert.Equal(ComplianceVerdict.Passing, only.To);

        // Before the finding existed at all: nothing comes back, never an
        // invented starting verdict.
        Assert.Empty(store.LastTransitionsAtOrBefore(Release, T0.AddHours(-1)));
    }

    [SkippableFact]
    public void LastTransitionsAtOrBefore_is_scoped_to_its_own_release_and_keeps_subjects_apart()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        store.Evaluate(Release, T0, _ => [Finding("vc-1:host-1")]);
        store.Evaluate("other-release", T0, _ => [Finding("vc-1:host-1") with { CatalogueRelease = "other-release" }]);

        store.Evaluate("eo-continuity-1", T0, _ =>
        [
            Finding("vc-1:host-1") with
            {
                CatalogueRelease = "eo-continuity-1",
                ControlId = "eo-cont.drs-rule",
                Subject = "rule-a",
            },
            Finding("vc-1:host-1") with
            {
                CatalogueRelease = "eo-continuity-1",
                ControlId = "eo-cont.drs-rule",
                Subject = "rule-b",
            },
        ]);

        var scg = store.LastTransitionsAtOrBefore(Release, T0.AddHours(1));
        var only = Assert.Single(scg);
        Assert.Equal(Release, only.CatalogueRelease);

        var continuity = store.LastTransitionsAtOrBefore("eo-continuity-1", T0.AddHours(1));
        Assert.Equal(["rule-a", "rule-b"], continuity.Select(t => t.Subject).Order());
    }

    [SkippableFact]
    public void A_history_wider_than_the_row_cap_comes_back_truncated_rather_than_whole()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        // Bulk-inserted directly: one row past the cap through
        // PostgresComplianceStore.Evaluate would be one evaluation per row,
        // far too slow for a test that only needs the row count to exist.
        _live.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO compliance_transition
                    (catalogue_release, control_id, entity_id, from_verdict, to_verdict, at_utc)
                SELECT @release, 'esxi-8.logs-remote', 'vc-1:host-1', NULL, 'Failing',
                       @base + (n || ' seconds')::interval
                FROM generate_series(1, @count) AS n;
                """;
            command.Parameters.AddWithValue("release", Release);
            command.Parameters.AddWithValue("base", T0.UtcDateTime);
            command.Parameters.AddWithValue("count", ComplianceTransitionsPage.MaxRows + 1);
            command.ExecuteNonQuery();
        });

        var page = store.TransitionsSince(T0.AddDays(-1), catalogueRelease: Release);

        Assert.True(page.Truncated);
        Assert.Equal(ComplianceTransitionsPage.MaxRows, page.Transitions.Count);
    }
}
