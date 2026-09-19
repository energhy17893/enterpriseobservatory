using System.Globalization;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// Checks the counters the product asks for against a real catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The test that was missing. Every other test in this project takes the
/// counter list as given and checks what happens to the values — which is
/// exactly the shape of assumption that let two counter names that do not
/// exist survive from the first commit to the first live run.
/// </para>
/// <para>
/// The failure had no symptom. No exception, no log line, no red screen: the
/// collector asked for <c>datastore.totalLatency.average</c>, vCenter said it
/// had never heard of it, and the product recorded that in a field it then
/// discarded. Forty-one datastores, twenty-nine of them shared SAN volumes,
/// simply had no measurements — on a product whose central diagnostic is
/// storage latency.
/// </para>
/// <para>
/// So this compares against data taken from a server rather than against
/// anybody's memory. It only checks groups the fixture actually contains, so
/// adding a group is a deliberate act of pasting in what a real vCenter
/// returned.
/// </para>
/// </remarks>
public class CounterNamesExistTests
{
    private static readonly IReadOnlyDictionary<string, (string Unit, int Level)> Catalogue = Load();

    /// <summary>The groups the fixture covers, and therefore the ones checked.</summary>
    private static readonly HashSet<string> CoveredGroups =
        [.. Catalogue.Keys.Select(GroupOf)];

    public static TheoryData<string> Requested()
    {
        var data = new TheoryData<string>();

        foreach (var counter in VsphereCounters.Host
            .Concat(VsphereCounters.VirtualMachine)
            .Concat(VsphereCounters.Datastore)
            .Distinct(StringComparer.Ordinal)
            .Where(c => CoveredGroups.Contains(GroupOf(c))))
        {
            data.Add(counter);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Requested))]
    public void A_counter_the_product_asks_for_exists_on_a_real_vcenter(string key)
    {
        Assert.True(
            Catalogue.ContainsKey(key),
            $"'{key}' is not in a real vSphere 8 catalogue. Nothing will fail when this is " +
            "wrong: the collector reports it and carries on, and every entity of that kind " +
            "goes unmeasured while the product looks healthy. Check the name against a live " +
            "server — /api/connections/{id}/counters answers this.");
    }

    [Fact]
    public void The_counter_that_started_this_is_still_absent()
    {
        // Pinned rather than assumed. If a future vSphere adds these, the
        // fixture changes and this test says so — which is the moment to
        // reconsider, not a moment to be surprised by.
        Assert.False(Catalogue.ContainsKey("datastore.totalLatency.average"));
        Assert.False(Catalogue.ContainsKey("virtualDisk.totalLatency.average"));
    }

    [Fact]
    public void Storage_latency_is_asked_for_as_read_and_write_separately()
    {
        // vSphere has no combined latency counter for either kind, and the two
        // halves say different things — read points at the array or its cache,
        // write at the write path. Collapsing them was never an option; the
        // product just did not know that.
        Assert.Contains("datastore.totalReadLatency.average", VsphereCounters.Datastore);
        Assert.Contains("datastore.totalWriteLatency.average", VsphereCounters.Datastore);
        Assert.Contains("virtualDisk.totalReadLatency.average", VsphereCounters.VirtualMachine);
        Assert.Contains("virtualDisk.totalWriteLatency.average", VsphereCounters.VirtualMachine);
    }

    [Fact]
    public void Every_storage_latency_counter_is_measured_in_milliseconds()
    {
        // A unit mix-up is the same class of silent defect: the catalogue has
        // latency counters in microseconds a few lines from ones in
        // milliseconds, and a chart comparing the two would be wrong by a
        // thousand with nothing on screen to suggest it.
        foreach (var key in VsphereCounters.Datastore
            .Concat(VsphereCounters.VirtualMachine)
            .Where(k => k.Contains("Latency", StringComparison.Ordinal))
            .Where(Catalogue.ContainsKey))
        {
            Assert.Equal("millisecond", Catalogue[key].Unit);
        }
    }

    private static string GroupOf(string key) =>
        key.Split('.', 2) is [var group, ..] ? group : key;

    private static Dictionary<string, (string, int)> Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "vsphere8-counter-catalogue.tsv");

        var catalogue = new Dictionary<string, (string, int)>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('\t');

            if (parts.Length == 3)
            {
                catalogue[parts[0]] =
                    (parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture));
            }
        }

        return catalogue;
    }
}
