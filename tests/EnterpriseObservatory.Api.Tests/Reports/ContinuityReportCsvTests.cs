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
            "\"Cluster\",\"Source\",\"HA settings collected\"," +
            "\"HA failing\",\"HA accepted\",\"HA excepted\",\"HA not evaluated\",\"HA stale\"," +
            "\"DRS failing\",\"DRS accepted\",\"DRS excepted\",\"DRS not evaluated\",\"DRS stale\"," +
            "\"Storage path failing\",\"Storage path accepted\",\"Storage path excepted\"," +
            "\"Storage path not evaluated\",\"Storage path stale\"," +
            "\"N+1 failing\",\"N+1 accepted\",\"N+1 excepted\",\"N+1 not evaluated\",\"N+1 stale\"," +
            "\"Storage path affected hosts\"\r\n",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_carries_every_state_count_and_the_affected_hosts()
    {
        var csv = ContinuityReportCsv.Write([Row()]);

        Assert.Contains(
            "\"Prod-Cluster\",\"vc-1\",\"yes\"," +
            "\"1\",\"2\",\"0\",\"3\",\"0\"," +
            "\"0\",\"0\",\"0\",\"0\",\"1\"," +
            "\"1\",\"0\",\"1\",\"0\",\"0\"," +
            "\"0\",\"0\",\"0\",\"2\",\"0\"," +
            "\"esx-01.corp.local; esx-02.corp.local\"\r\n",
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
        Ha = new ContinuityStateCounts { Failing = 1, Accepted = 2, NotEvaluated = 3, Passing = 9 },
        Drs = new ContinuityStateCounts { Stale = 1, Passing = 4 },
        StoragePath = new ContinuityStateCounts { Failing = 1, Excepted = 1 },
        StoragePathAffectedHosts = ["esx-01.corp.local", "esx-02.corp.local"],
        NPlusOne = new ContinuityStateCounts { NotEvaluated = 2 },
        HasFailing = true,
    };
}
