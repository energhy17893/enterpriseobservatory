using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Reports;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The continuity report's CSV column mapping (M8.10). See
/// <see cref="CsvWriterTests"/> for the quoting and formula-injection
/// guarantees this reuses rather than re-tests.
/// </summary>
public class ContinuityReportCsvTests
{
    [Fact]
    public void The_header_names_every_column()
    {
        var csv = ContinuityReportCsv.Write([]);

        Assert.StartsWith(
            "\"Cluster\",\"Source\",\"HA settings collected\",\"HA critical\",\"HA warning\",\"DRS critical\"," +
            "\"DRS warning\",\"Storage path critical\",\"Storage path warning\",\"Storage path affected hosts\"," +
            "\"N+1 critical\",\"N+1 warning\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_carries_every_count_and_the_affected_hosts()
    {
        var csv = ContinuityReportCsv.Write([Row()]);

        Assert.Contains(
            "\"Prod-Cluster\",\"vc-1\",\"yes\",\"1\",\"2\",\"0\",\"1\",\"1\",\"0\"," +
            "\"esx-01.corp.local; esx-02.corp.local\",\"0\",\"0\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cluster_with_ha_not_collected_says_so_rather_than_a_bare_no()
    {
        var csv = ContinuityReportCsv.Write([Row() with { HaSettingsCollected = false }]);

        Assert.Contains("\"no\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void No_rows_still_writes_the_header_alone()
    {
        var csv = ContinuityReportCsv.Write([]);

        Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    private static ContinuityReportRow Row() => new()
    {
        ClusterId = "vc-1:domain-c1",
        ClusterName = "Prod-Cluster",
        Source = "vc-1",
        HaSettingsCollected = true,
        HaCriticalCount = 1,
        HaWarningCount = 2,
        DrsCriticalCount = 0,
        DrsWarningCount = 1,
        StoragePathCriticalCount = 1,
        StoragePathWarningCount = 0,
        StoragePathAffectedHosts = ["esx-01.corp.local", "esx-02.corp.local"],
        NPlusOneCriticalCount = 0,
        NPlusOneWarningCount = 0,
        HasCritical = true,
    };
}
