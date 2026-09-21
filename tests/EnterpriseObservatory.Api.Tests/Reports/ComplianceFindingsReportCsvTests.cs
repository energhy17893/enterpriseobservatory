using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The compliance report's findings CSV carries the subject (K1) as
/// additional columns after the ones it already had.
/// </summary>
public class ComplianceFindingsReportCsvTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);

    private static ComplianceReportFindingRow Row(string subject = "", string? label = null) => new()
    {
        ControlId = "eo-cont.drs-rule",
        ControlTitle = "DRS rules hold",
        Priority = "",
        EntityId = "vc-1:domain-c1",
        EntityName = "cluster-1",
        State = FindingState.Failing,
        Expected = "rule satisfied",
        FirstSeenUtc = T0,
        LastEvaluatedUtc = T0,
        Stale = false,
        Subject = subject,
        SubjectLabel = label,
    };

    [Fact]
    public void The_subject_columns_come_after_the_existing_ones()
    {
        var csv = ComplianceFindingsReportCsv.Write([]);

        Assert.EndsWith("\"Exception expires (UTC)\",\"Subject\",\"Subject label\"\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_carries_its_subject_and_label()
    {
        var csv = ComplianceFindingsReportCsv.Write([Row("uuid-1", "keep-apart")]);

        Assert.Contains(",\"uuid-1\",\"keep-apart\"\r\n", csv, StringComparison.Ordinal);
    }
}
