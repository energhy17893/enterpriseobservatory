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
        { InventoryVerdictKeys.LegacyAdapterE1000, InventoryVerdicts.LegacyAdapterE1000 },
        { InventoryVerdictKeys.LegacyAdapterE1000e, InventoryVerdicts.LegacyAdapterE1000e },
        { InventoryVerdictKeys.LegacyAdapterLsiLogic, InventoryVerdicts.LegacyAdapterLsiLogic },
        { InventoryVerdictKeys.ConsolidationNeeded, InventoryVerdicts.ConsolidationNeeded },
        { InventoryVerdictKeys.EvcEnabled, InventoryVerdicts.EvcEnabled },
        { InventoryVerdictKeys.EvcModeKey, InventoryVerdicts.EvcModeKey },
        { InventoryVerdictKeys.HostMaxEvcModeKey, InventoryVerdicts.HostMaxEvcModeKey },
        { InventoryVerdictKeys.MountedHostCount, InventoryVerdicts.MountedHostCount },
        { InventoryVerdictKeys.CertificateNotAfter, InventoryVerdicts.CertificateNotAfter },
        { InventoryVerdictKeys.CertificateSha256, InventoryVerdicts.CertificateSha256 },
        { InventoryVerdictKeys.BackupRead, InventoryVerdicts.BackupRead },
        { InventoryVerdictKeys.BackupField, InventoryVerdicts.BackupField },
        { InventoryVerdictKeys.BackupValue, InventoryVerdicts.BackupValue },
        { InventoryVerdictKeys.BackupLastUtc, InventoryVerdicts.BackupLastUtc },
        { InventoryVerdictKeys.BackupTimeBasis, InventoryVerdicts.BackupTimeBasis },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_check_reads_the_key_the_collector_writes(string checkReads, string collectorWrites) =>
        Assert.Equal(collectorWrites, checkReads);
}
