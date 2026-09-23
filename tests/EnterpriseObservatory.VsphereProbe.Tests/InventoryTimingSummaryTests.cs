using EnterpriseObservatory.VsphereProbe;

namespace EnterpriseObservatory.VsphereProbe.Tests;

/// <summary>
/// Proves <see cref="InventoryTiming.Summarize"/> rolls up the per-call
/// timings <see cref="InventoryTimingHandler"/> records the way the
/// --time-inventory report needs: one row per SOAP call name with pages,
/// objects, bytes and total ms summed, and the object-type breakdown merged
/// across every page of that call rather than kept per page.
/// </summary>
public sealed class InventoryTimingSummaryTests
{
    [Fact]
    public void Groups_pages_of_the_same_call_and_sums_objects_bytes_and_ms()
    {
        List<InventoryCallTiming> calls =
        [
            new("RetrievePropertiesEx", 0, [("HostSystem", 3), ("VirtualMachine", 200)], 50_000, 800, []),
            new("ContinueRetrievePropertiesEx", 1, [("VirtualMachine", 50)], 12_000, 300, []),
            new("RetrieveObjectProperties", -1, [], 900, 40, []),
        ];

        var summary = InventoryTiming.Summarize(calls);

        Assert.Equal(3, summary.Count);

        var pages = summary.Single(s => s.CallName == "RetrievePropertiesEx");
        Assert.Equal(1, pages.Calls);
        Assert.Equal(203, pages.Objects);
        Assert.Equal(50_000, pages.Bytes);
        Assert.Equal(800, pages.TotalMs);

        var backup = summary.Single(s => s.CallName == "RetrieveObjectProperties");
        Assert.Equal(1, backup.Calls);
        Assert.Equal(0, backup.Objects);
        Assert.Equal(900, backup.Bytes);
        Assert.Equal(40, backup.TotalMs);
    }

    [Fact]
    public void Merges_object_type_counts_across_every_page_of_one_call_name()
    {
        List<InventoryCallTiming> calls =
        [
            new("RetrievePropertiesEx", 0, [("HostSystem", 3), ("VirtualMachine", 200)], 50_000, 800, []),
            new("ContinueRetrievePropertiesEx", 1, [("VirtualMachine", 50)], 12_000, 300, []),
            new("ContinueRetrievePropertiesEx", 2, [("VirtualMachine", 30), ("Datastore", 5)], 9_000, 250, []),
        ];

        var summary = InventoryTiming.Summarize(calls);

        var continuePages = summary.Single(s => s.CallName == "ContinueRetrievePropertiesEx");
        Assert.Equal(2, continuePages.Calls);
        Assert.Equal(85, continuePages.Objects);
        Assert.Equal(21_000, continuePages.Bytes);
        Assert.Equal(550, continuePages.TotalMs);

        // Not one entry per page: VirtualMachine appeared on both continuation
        // pages and must be one merged count, largest type first.
        Assert.Equal(
            [("VirtualMachine", 80), ("Datastore", 5)],
            continuePages.TypeCounts);
    }

    [Fact]
    public void Empty_call_list_summarizes_to_nothing()
    {
        Assert.Empty(InventoryTiming.Summarize([]));
    }
}
