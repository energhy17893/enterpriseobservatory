using EnterpriseObservatory.VsphereProbe;

namespace EnterpriseObservatory.VsphereProbe.Tests;

/// <summary>
/// Proves the bytes-per-property attribution the --time-inventory report
/// added for #169's follow-up: a page's <c>propSet</c> elements get their
/// serialized size attributed to their object's type and their own property
/// path, and <see cref="InventoryTiming.SummarizePropertyBytes"/> rolls that
/// up per type -- sorted by bytes descending, with share % and average bytes
/// per object -- which is what tells a planner which property is worth
/// dropping.
/// </summary>
public sealed class InventoryTimingPropertyBytesTests
{
    // Two VMs: vm-1 carries a large "config.hardware.device" blob (the
    // property this measurement exists to catch), vm-2 only the small "name".
    private const string TwoVirtualMachinesOneBigOneSmallProperty = """
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="VirtualMachine">vm-1</obj>
              <propSet><name>name</name><val>db01</val></propSet>
              <propSet><name>config.hardware.device</name><val>XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX</val></propSet>
            </objects>
            <objects>
              <obj type="VirtualMachine">vm-2</obj>
              <propSet><name>name</name><val>db02</val></propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void PropertyBytesOf_attributes_each_propSet_to_its_object_type_and_path()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(TwoVirtualMachinesOneBigOneSmallProperty);

        var entries = InventoryTimingHandler.PropertyBytesOf(bytes);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("VirtualMachine", e.Type));

        var device = entries.Single(e => e.Path == "config.hardware.device");
        var names = entries.Where(e => e.Path == "name").ToList();
        Assert.Equal(2, names.Count);

        // The big property must dwarf either "name" occurrence -- that gap is
        // the whole point of measuring per property rather than per type.
        Assert.True(device.Bytes > names[0].Bytes * 2);
    }

    [Fact]
    public void SummarizePropertyBytes_ranks_the_big_property_first_with_correct_share_and_average()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(TwoVirtualMachinesOneBigOneSmallProperty);
        var entries = InventoryTimingHandler.PropertyBytesOf(bytes);

        List<InventoryCallTiming> calls =
        [
            new("RetrievePropertiesEx", 0, [("VirtualMachine", 2)], bytes.LongLength, 10, entries),
        ];

        var summary = InventoryTiming.SummarizePropertyBytes(calls);

        var vm = summary.Single(s => s.Type == "VirtualMachine");
        Assert.Equal(2, vm.Objects);
        Assert.Equal(0, vm.OtherCount);

        // Sorted by bytes descending: the device blob leads.
        Assert.Equal("config.hardware.device", vm.Top[0].Path);
        Assert.Equal("name", vm.Top[1].Path);

        Assert.Equal(vm.TotalBytes, vm.Top.Sum(r => r.Bytes));
        Assert.True(vm.Top[0].SharePercent > 50);
        Assert.Equal(vm.Top[0].Bytes / 2.0, vm.Top[0].AvgBytesPerObject);
    }

    [Fact]
    public void Unparseable_bytes_yield_no_entries_rather_than_throwing()
    {
        Assert.Empty(InventoryTimingHandler.PropertyBytesOf([1, 2, 3]));
    }
}
