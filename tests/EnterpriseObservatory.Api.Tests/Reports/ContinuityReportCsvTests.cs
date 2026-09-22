using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The continuity report's CSV mapping (M8.10, K3): the same projection the
/// page shows -- vCenter section, cluster rows, one summary row per
/// entity-level control. See <see cref="CsvWriterTests"/> for the quoting and
/// formula-injection guarantees this reuses rather than re-tests.
/// </summary>
public class ContinuityReportCsvTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_header_names_every_column()
    {
        var csv = ContinuityReportCsv.Write(Report());

        Assert.StartsWith(
            "\"Section\",\"Name\",\"Source\",\"Control\",\"Title\",\"Basis\"," +
            "\"Failing\",\"Accepted\",\"Excepted\",\"Not evaluated\",\"Passing\",\"Stale\",\"Detail\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cluster_writes_one_line_per_cluster_control_and_one_for_what_is_under_it()
    {
        var csv = ContinuityReportCsv.Write(Report());

        Assert.Contains(
            "\"Cluster\",\"Prod-Cluster\",\"vc-1\",\"eo-cont.ha-enabled\",\"vSphere HA is enabled\"," +
            "\"vSphere Availability guide\",\"1\",\"2\",\"0\",\"3\",\"9\",\"0\",\"\"\r\n",
            csv,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Cluster\",\"Prod-Cluster\",\"vc-1\",\"\",\"Hosts, VMs and datastores under it\",\"\"," +
            "\"1\",\"0\",\"1\",\"0\",\"0\",\"0\",\"esx-01.corp.local; esx-02.corp.local\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cluster_with_ha_not_collected_says_so_on_its_lines()
    {
        var report = Report();
        var csv = ContinuityReportCsv.Write(report with
        {
            Rows = [report.Rows[0] with { HaSettingsCollected = false }],
        });

        Assert.Contains("HA configuration not collected", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_level_control_is_one_summary_line_with_at_most_ten_names_and_the_rest_counted()
    {
        var csv = ContinuityReportCsv.Write(Report());

        Assert.Contains(
            "\"Estate\",\"EsxiHost\",\"\",\"eo-cont.cert-esxi\",\"ESXi certificate\",\"Tool default (vCheck 60 days)\"," +
            "\"12\",\"0\",\"0\",\"0\",\"8\",\"0\",\"failing: h01, h02, h03, h04, h05, h06, h07, h08, h09, h10, +2\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_with_no_citation_says_so_rather_than_leaving_the_basis_blank()
    {
        var csv = ContinuityReportCsv.Write(Report());

        Assert.Contains("\"eo-cont.test-probe\",\"Probe\",\"no citation — product policy\"", csv, StringComparison.Ordinal);
        Assert.Equal("no citation — product policy", ControlBasis.Label("  "));
        Assert.Equal("VMware KB 2004739", ControlBasis.Label("VMware KB 2004739"));
    }

    [Fact]
    public void The_vcenter_section_carries_its_certificate_and_its_root_alarms()
    {
        var csv = ContinuityReportCsv.Write(Report());

        Assert.Contains(
            "\"vCenter\",\"vcsa.corp.local\",\"vc-1\",\"eo-cont.cert-vcenter\",\"vCenter certificate\"," +
            "\"Tool default (vCheck 60 days)\",\"1\",\"0\",\"0\",\"0\",\"0\",\"0\",\"\"\r\n",
            csv,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"vCenter alarm\",\"vcsa.corp.local\",\"vc-1\",\"\",\"License expiry\",\"\"," +
            "\"\",\"\",\"\",\"\",\"\",\"\",\"Warning, Open\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_report_still_writes_the_header_alone()
    {
        var csv = ContinuityReportCsv.Write(Report() with { VCenters = [], Rows = [], ControlRows = [] });

        Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    private static ContinuityControlInfo Info(string id, string title, string citation, ContinuityReportScope scope, EntityKind kind) =>
        new() { ControlId = id, Title = title, Citation = citation, Scope = scope, AppliesTo = kind };

    private static ContinuityReportView Report() => new()
    {
        GeneratedAtUtc = T0,
        Summary = new ContinuityReportSummary
        {
            TotalClusters = 1,
            Evaluated = true,
            ByControl = new Dictionary<string, ContinuityStateCounts>(),
            Totals = new ContinuityStateCounts(),
            ClustersWithFailingCount = 1,
            ClustersWithFailingNames = ["Prod-Cluster"],
            HaInputsCollected = true,
        },
        Controls =
        [
            Info("eo-cont.ha-enabled", "vSphere HA is enabled", "vSphere Availability guide", ContinuityReportScope.Cluster, EntityKind.Cluster),
            Info("eo-cont.cert-esxi", "ESXi certificate", "Tool default (vCheck 60 days)", ContinuityReportScope.Entity, EntityKind.EsxiHost),
            Info("eo-cont.test-probe", "Probe", string.Empty, ContinuityReportScope.Entity, EntityKind.VirtualMachine),
            Info("eo-cont.cert-vcenter", "vCenter certificate", "Tool default (vCheck 60 days)", ContinuityReportScope.VCenter, EntityKind.VCenter),
        ],
        VCenters =
        [
            new ContinuityVCenterSection
            {
                VCenterId = "vc-1:vcenter",
                VCenterName = "vcsa.corp.local",
                Source = "vc-1",
                Controls = [new ContinuityControlCounts { ControlId = "eo-cont.cert-vcenter", Counts = new() { Failing = 1 } }],
                Findings = [],
                Alarms =
                [
                    new ContinuityAlarmView
                    {
                        Title = "License expiry",
                        Severity = AlertSeverity.Warning,
                        State = AlertLifecycleState.Open,
                        IsStale = false,
                        FirstSeenUtc = T0,
                    },
                ],
            },
        ],
        Rows =
        [
            new ContinuityReportRow
            {
                ClusterId = "vc-1:domain-c1",
                ClusterName = "Prod-Cluster",
                Source = "vc-1",
                HaSettingsCollected = true,
                Controls =
                [
                    new ContinuityControlCounts
                    {
                        ControlId = "eo-cont.ha-enabled",
                        Counts = new() { Failing = 1, Accepted = 2, NotEvaluated = 3, Passing = 9 },
                    },
                ],
                Contained = new ContinuityStateCounts { Failing = 1, Excepted = 1 },
                ContainedAffectedNames = ["esx-01.corp.local", "esx-02.corp.local"],
                ContainedAffectedMore = 0,
                Totals = new ContinuityStateCounts { Failing = 2 },
                HasFailing = true,
            },
        ],
        ControlRows =
        [
            new ContinuityControlRow
            {
                ControlId = "eo-cont.cert-esxi",
                Title = "ESXi certificate",
                Citation = "Tool default (vCheck 60 days)",
                AppliesTo = EntityKind.EsxiHost,
                Counts = new() { Failing = 12, Passing = 8 },
                FailingNames = [.. Enumerable.Range(1, 10).Select(i => $"h{i:00}")],
                MoreFailing = 2,
            },
            new ContinuityControlRow
            {
                ControlId = "eo-cont.test-probe",
                Title = "Probe",
                Citation = string.Empty,
                AppliesTo = EntityKind.VirtualMachine,
                Counts = new(),
                FailingNames = [],
                MoreFailing = 0,
            },
        ],
    };
}
