using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// A live vCenter 8 with 732 counters defines
/// <c>disk.scsiReservationCnflctsPct.average</c> twice. Building a dictionary
/// straight from the key threw, which is how the first live run ended.
/// </summary>
public class VsphereCounterIndexTests
{
    private static VsphereCounter Counter(
        int id, string group, string name, RollupType rollup, int level = 1, string statsType = "absolute") => new()
        {
            Id = id,
            Group = group,
            Name = name,
            Rollup = rollup,
            Unit = "percent",
            Level = level,
            StatsType = statsType,
        };

    [Fact]
    public void A_catalogue_with_a_repeated_key_does_not_throw()
    {
        VsphereCounter[] catalog =
        [
            Counter(1, "cpu", "usage", RollupType.Average),
            Counter(2, "disk", "scsiReservationCnflctsPct", RollupType.Average, level: 2, statsType: "absolute"),
            Counter(3, "disk", "scsiReservationCnflctsPct", RollupType.Average, level: 4, statsType: "rate"),
        ];

        var index = VsphereCounterIndex.ByKey(catalog);

        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void The_lowest_statistics_level_wins()
    {
        // It is the definition most installations will actually be collecting.
        VsphereCounter[] catalog =
        [
            Counter(9, "disk", "x", RollupType.Average, level: 4),
            Counter(3, "disk", "x", RollupType.Average, level: 2),
        ];

        Assert.Equal(3, VsphereCounterIndex.ByKey(catalog)["disk.x.average"].Id);
    }

    [Fact]
    public void The_lowest_id_breaks_a_remaining_tie()
    {
        // Purely so the result does not depend on response ordering.
        VsphereCounter[] forward = [Counter(7, "disk", "x", RollupType.Average), Counter(4, "disk", "x", RollupType.Average)];
        VsphereCounter[] reversed = [.. forward.Reverse()];

        Assert.Equal(4, VsphereCounterIndex.ByKey(forward)["disk.x.average"].Id);
        Assert.Equal(4, VsphereCounterIndex.ByKey(reversed)["disk.x.average"].Id);
    }

    [Fact]
    public void Counters_differing_only_in_rollup_are_not_duplicates()
    {
        // They mean different things and both are wanted.
        VsphereCounter[] catalog =
        [
            Counter(1, "cpu", "ready", RollupType.Summation),
            Counter(2, "cpu", "ready", RollupType.Average),
        ];

        Assert.Equal(2, VsphereCounterIndex.ByKey(catalog).Count);
        Assert.Empty(VsphereCounterIndex.FindDuplicates(catalog));
    }

    [Fact]
    public void Duplicates_are_reportable_rather_than_silently_resolved()
    {
        // The product is choosing on the operator's behalf; that is worth being
        // able to see.
        VsphereCounter[] catalog =
        [
            Counter(2, "disk", "x", RollupType.Average, level: 2, statsType: "absolute"),
            Counter(3, "disk", "x", RollupType.Average, level: 4, statsType: "rate"),
        ];

        var duplicate = Assert.Single(VsphereCounterIndex.FindDuplicates(catalog));

        Assert.Equal("disk.x.average", duplicate.Key);
        Assert.Equal(2, duplicate.Definitions.Count);
        Assert.Equal(2, duplicate.Chosen.Id);
    }

    [Fact]
    public void Key_matching_ignores_case()
    {
        VsphereCounter[] catalog = [Counter(1, "Disk", "DeviceLatency", RollupType.Average)];

        Assert.True(VsphereCounterIndex.ByKey(catalog).ContainsKey("disk.devicelatency.average"));
    }

    [Fact]
    public void An_empty_catalogue_is_not_an_error()
    {
        Assert.Empty(VsphereCounterIndex.ByKey([]));
        Assert.Empty(VsphereCounterIndex.FindDuplicates([]));
    }
}
