using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Pins the maintenance and expiry checks' <see cref="InventoryVerdictKeys"/>
/// to the keys the vSphere collector files them under (<see cref="InventoryVerdicts"/>).
/// </summary>
/// <remarks>
/// The application layer may not reference a collector, so the names are
/// written twice; this is the compiler-less check that they agree, run where
/// both projects are in reach (as <c>ClusterHaSettingKeyDriftTests</c>).
/// </remarks>
public class InventoryVerdictKeyDriftTests
{
    public static TheoryData<string, string> Keys => new()
    {
        { InventoryVerdictKeys.ConnectedCdroms, InventoryVerdicts.ConnectedCdroms },
        { InventoryVerdictKeys.ConnectedIsoCdroms, InventoryVerdicts.ConnectedIsoCdroms },
        { InventoryVerdictKeys.ConsolidationNeeded, InventoryVerdicts.ConsolidationNeeded },
        { InventoryVerdictKeys.EvcEnabled, InventoryVerdicts.EvcEnabled },
        { InventoryVerdictKeys.EvcModeKey, InventoryVerdicts.EvcModeKey },
        { InventoryVerdictKeys.MountedHostCount, InventoryVerdicts.MountedHostCount },
        { InventoryVerdictKeys.CertificateNotAfter, InventoryVerdicts.CertificateNotAfter },
        { InventoryVerdictKeys.CertificateSha256, InventoryVerdicts.CertificateSha256 },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_check_reads_the_key_the_collector_writes(string checkReads, string collectorWrites) =>
        Assert.Equal(collectorWrites, checkReads);
}
