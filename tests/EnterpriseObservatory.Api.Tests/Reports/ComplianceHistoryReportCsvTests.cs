using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The compliance report's change-history CSV column mapping, and the
/// truncation note architecture review 3 added. See
/// <see cref="CsvWriterTests"/> for the quoting and formula-injection
/// guarantees this reuses rather than re-tests.
/// </summary>
public class ComplianceHistoryReportCsvTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);

    private static ComplianceReportTransitionRow Row() => new()
    {
        ControlId = "esx-9.log-forwarding",
        EntityId = "vc-1:host-1",
        From = null,
        To = Domain.Compliance.ComplianceVerdict.Failing,
        Observed = "",
        AtUtc = T0,
    };

    [Fact]
    public void The_header_names_every_column()
    {
        var csv = ComplianceHistoryReportCsv.Write([]);

        Assert.StartsWith("Control,Entity id,From,To,Observed,At (UTC)\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_carries_the_transition()
    {
        var csv = ComplianceHistoryReportCsv.Write([Row()]);

        Assert.Contains(
            "esx-9.log-forwarding,vc-1:host-1,,Failing,,2026-09-21T07:00:00Z\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void No_note_is_appended_when_the_history_was_not_truncated()
    {
        var csv = ComplianceHistoryReportCsv.Write([Row()], truncated: false);

        Assert.DoesNotContain("Truncated", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_history_gets_a_trailing_note_naming_the_cap()
    {
        var csv = ComplianceHistoryReportCsv.Write([Row()], truncated: true);

        Assert.Contains($"Truncated,\"More than {ComplianceTransitionsPage.MaxRows:N0}", csv, StringComparison.Ordinal);
    }
}
