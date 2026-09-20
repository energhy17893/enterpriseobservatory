using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rung the product was built for and had never climbed.
/// </summary>
/// <remarks>
/// Three numbers arrive for every device on every host — the array, the
/// VMkernel, the queue — and the product read only their total, which by
/// construction cannot say which of the three is the slow one. Getting this
/// wrong in the confident direction sends an operator to the storage team over
/// a queue depth they own, which is the expensive kind of wrong answer.
/// </remarks>
public class StorageLayerSplitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Device = "disk.deviceLatency.average";
    private const string Kernel = "disk.kernelLatency.average";
    private const string Queue = "disk.queueLatency.average";

    private static Observation One(
        string counter,
        double ms,
        string instance = "naa.1",
        string host = "vc-1:host-esx01",
        string unit = "millisecond",
        bool vantage = false,
        bool fault = false) => new()
        {
            Entity = new EntityId(host),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = ms,
                Rollup = RollupType.Average,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = instance,
                InstanceIsVantagePoint = vantage,
                IsFaultCount = fault,
            },
        };

    /// <summary>One device's full triple: array, kernel, queue.</summary>
    private static Observation[] Triple(
        double device, double kernel, double queue,
        string instance = "naa.1", string host = "vc-1:host-esx01") =>
        [
            One(Device, device, instance, host),
            One(Kernel, kernel, instance, host),
            One(Queue, queue, instance, host),
        ];

    [Fact]
    public void A_device_waiting_in_the_hosts_own_queue_names_the_host_and_not_the_array()
    {
        // The request had not been issued yet, so nothing below the host can
        // be the cause. This is the verdict that saves the phone call to the
        // storage team, and the product could not produce it at all before.
        var alert = Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 2, kernel: 1, queue: 12)]));

        Assert.Equal("Host queue depth is the bottleneck", alert.Title);
        Assert.Contains("queue depth", alert.Description, StringComparison.Ordinal);
        Assert.Contains("nothing below this host", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_spending_its_time_in_the_hypervisor_names_the_hypervisor()
    {
        // Between the virtual machine and the adapter: a multipathing policy
        // or a path state, which is neither the array's fault nor a queue the
        // operator has oversubscribed. Collapsing it into either would send
        // somebody to the wrong console with a number that looked convincing.
        var alert = Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 2, kernel: 12, queue: 1)]));

        Assert.Equal("VMkernel storage stack is the bottleneck", alert.Title);
        Assert.Contains("multipathing", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_waiting_below_the_host_names_the_array_and_clears_the_host()
    {
        // The host issued the request promptly and waited. Saying so — and
        // saying which host-side explanations the same numbers rule out — is
        // what turns the blame argument into a measurement.
        var alert = Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 20, kernel: 2, queue: 1)]));

        Assert.Equal("Array or fabric is the bottleneck", alert.Title);
        Assert.Contains("below the host", alert.Description, StringComparison.Ordinal);
        Assert.Contains("cleared", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Blaming_the_array_takes_more_evidence_than_blaming_the_host()
    {
        // The same split mirrored. Twelve against four names the host; twelve
        // against four does not name the array. That asymmetry is the whole
        // position of this codebase written as an inequality: being wrong
        // about the array sends somebody to another team with a false
        // accusation, being wrong about the host sends somebody to a setting
        // they own. If the two multiples are ever made equal, this dies.
        Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 4, kernel: 1, queue: 12)]));

        Assert.Empty(StorageLayerSplit.Evaluate([.. Triple(device: 12, kernel: 1, queue: 4)]));

        // And it is ArrayMultiple doing the stopping, not the floor or the
        // missing-counter gate: relax only that and the same readings fire.
        Assert.Single(StorageLayerSplit.Evaluate(
            [.. Triple(device: 12, kernel: 1, queue: 4)],
            StorageLayerPolicy.Default with { ArrayMultiple = 2d }));
    }

    [Fact]
    public void Three_layers_within_noise_of_each_other_produce_no_verdict()
    {
        // Twelve, eleven and ten: something is slow and the numbers do not say
        // which layer owns it. Picking the largest would be a diagnosis
        // produced by rounding, and this codebase would rather say nothing
        // than send somebody somewhere on that basis.
        Assert.Empty(StorageLayerSplit.Evaluate([.. Triple(device: 12, kernel: 11, queue: 10)]));
    }

    [Fact]
    public void A_dominant_layer_that_is_not_actually_slow_is_not_reported()
    {
        // Four milliseconds against zeroes clears every multiple there is and
        // is still four milliseconds. Without this floor the rule would report
        // a verdict on every device in a healthy estate, because a quiet
        // estate is precisely where one layer trivially dwarfs the other two.
        List<Observation> readings = [.. Triple(device: 4, kernel: 0, queue: 0)];

        Assert.Empty(StorageLayerSplit.Evaluate(readings));

        Assert.Single(StorageLayerSplit.Evaluate(
            readings, StorageLayerPolicy.Default with { MinimumMilliseconds = 0d }));
    }

    [Fact]
    public void A_layer_at_one_millisecond_is_not_a_multiple_of_two_layers_at_zero()
    {
        // The estate's defining hazard, in the counter map's §5b: anything
        // under a millisecond truncates to zero, so the quiet layers read
        // exactly zero and every number is an infinite multiple of that. The
        // other side of the comparison is floored at one before multiplying,
        // and without that floor this rule would name a layer on every device
        // that ever reported a single millisecond.
        Assert.Empty(StorageLayerSplit.Evaluate(
            [.. Triple(device: 3, kernel: 0, queue: 0)],
            StorageLayerPolicy.Default with { MinimumMilliseconds = 0d }));
    }

    [Fact]
    public void A_device_that_reports_only_its_total_latency_is_not_judged()
    {
        // The counter the product used to read, and the reason this rule
        // exists: a total cannot be decomposed after the fact. Guessing a
        // layer from it would be the confident wrong answer with no evidence
        // at all behind it.
        Assert.Empty(StorageLayerSplit.Evaluate([One("disk.maxTotalLatency.latest", 40)]));
    }

    [Fact]
    public void A_device_missing_one_of_the_three_layers_is_not_judged()
    {
        // The ladder's third answer is "could not look", and it is different
        // from "innocent". With two of three terms the arithmetic still
        // produces a winner, and that winner would be an artefact of what was
        // collected rather than of what happened.
        Assert.Empty(StorageLayerSplit.Evaluate(
            [One(Device, 40), One(Kernel, 1)]));
    }

    [Fact]
    public void The_aggregate_across_a_hosts_devices_is_not_judged()
    {
        // Stored once per device and once as a computed maximum across them.
        // Judging the aggregate would raise a second, nameless alert for every
        // real one -- and worse, its sentence is "this host has a slow device"
        // with a refusal to say which, which is exactly what the per-device
        // series were kept to avoid.
        Assert.Empty(StorageLayerSplit.Evaluate(
            [.. Triple(device: 40, kernel: 1, queue: 1, instance: "")]));
    }

    [Fact]
    public void A_series_whose_instance_is_an_observer_is_not_judged_as_a_device()
    {
        // A datastore's latency measured from a host is a different rung. Its
        // instance names who looked, not what was looked at, and running a
        // layer split over it would read a volume's service time as a LUN's.
        Assert.Empty(StorageLayerSplit.Evaluate(
        [
            One(Device, 40, instance: "esx01", vantage: true),
            One(Kernel, 1, instance: "esx01", vantage: true),
            One(Queue, 1, instance: "esx01", vantage: true),
        ]));
    }

    [Fact]
    public void A_host_already_reporting_a_fault_is_not_also_given_a_layer_verdict()
    {
        // A bus reset explains latency better than any share of a total does,
        // and FaultCounters is already saying so with no threshold to argue
        // about. Two alerts for one fault is the outcome this rule was told to
        // avoid, so the weaker explanation stands down.
        List<Observation> readings =
        [
            .. Triple(device: 40, kernel: 1, queue: 1),
            One("storagePath.busResets.summation", 3, instance: "vmhba0:C0:T0:L1", fault: true),
        ];

        Assert.Empty(StorageLayerSplit.Evaluate(readings));

        Assert.Single(StorageLayerSplit.Evaluate(
            readings, StorageLayerPolicy.Default with { DeferToFaults = false }));
    }

    [Fact]
    public void A_fault_on_one_host_does_not_silence_a_different_host()
    {
        // The deferral is already coarser than anyone would like -- a path
        // name carries no LUN identity, so it silences a whole host. Letting
        // it cross hosts as well would make one cable fault blind the estate.
        var alert = Assert.Single(StorageLayerSplit.Evaluate(
        [
            .. Triple(device: 40, kernel: 1, queue: 1, host: "vc-1:host-esx02"),
            One("storagePath.busResets.summation", 3, instance: "vmhba0:C0:T0:L1",
                host: "vc-1:host-esx01", fault: true),
        ]));

        Assert.Equal(new EntityId("vc-1:host-esx02"), alert.Entity);
    }

    [Fact]
    public void Two_devices_on_one_host_are_judged_separately()
    {
        // The counter map's central warning: a counter is not a series. One
        // slow LUN among thirty-two is the sentence the field is looking for,
        // and a rule that judged the host would turn it into "storage is
        // slightly slow".
        var alerts = StorageLayerSplit.Evaluate(
        [
            .. Triple(device: 40, kernel: 1, queue: 1, instance: "naa.1"),
            .. Triple(device: 1, kernel: 1, queue: 40, instance: "naa.2"),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Title).Distinct().Count());
    }

    [Fact]
    public void One_device_never_produces_two_verdicts()
    {
        // "The array is slow and so is the queue" is not a diagnosis, it is
        // two people sent to two consoles over one fault. Layers are tried in
        // order and the first that wins ends the question, so a device where
        // several look high still yields exactly one.
        Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 30, kernel: 30, queue: 30)],
            StorageLayerPolicy.Default with { ArrayMultiple = 1d, HostMultiple = 1d }));
    }

    [Fact]
    public void The_fingerprint_names_the_device_rather_than_the_host()
    {
        // Two devices on one host are two problems with two fixes. A
        // fingerprint that stopped at the host would collapse them, and the
        // second fault would be invisible until the first was cleared.
        var alerts = StorageLayerSplit.Evaluate(
        [
            .. Triple(device: 40, kernel: 1, queue: 1, instance: "naa.1"),
            .. Triple(device: 40, kernel: 1, queue: 1, instance: "naa.2"),
        ]);

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_fingerprint_changes_when_the_blamed_layer_changes()
    {
        // Deliberately unlike PeerOutliers, whose fingerprint survives the
        // worst host changing. There the subject stays the same and only a
        // detail moves; here the verdict IS the subject. A queue problem that
        // becomes an array problem is a different fault, a different fix and a
        // different team, and keeping one alert open across that would leave a
        // history claiming the array was blamed all along.
        var queue = Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 2, kernel: 1, queue: 12)]));
        var array = Assert.Single(StorageLayerSplit.Evaluate([.. Triple(device: 20, kernel: 2, queue: 1)]));

        Assert.NotEqual(queue.Fingerprint, array.Fingerprint);
    }

    [Fact]
    public void A_counter_that_is_not_measured_in_milliseconds_is_not_a_latency_layer()
    {
        // The unit is the guard against a configured counter name colliding
        // with something that is not a duration at all. A throughput or a
        // count compared against a millisecond floor is a number multiplied by
        // a meaning it does not have.
        Assert.Empty(StorageLayerSplit.Evaluate(
        [
            One(Device, 4000, unit: "number"),
            One(Kernel, 1, unit: "number"),
            One(Queue, 1, unit: "number"),
        ]));
    }

    [Fact]
    public void A_collector_naming_its_layers_differently_is_obeyed()
    {
        // The counter names are configuration, not vocabulary. Nothing on a
        // counter value says which layer of a stack it measures, so the rule
        // is told; that is what keeps it from being the first place in the
        // application layer to hard-code a vendor's counter.
        var renamed = StorageLayerPolicy.Default with
        {
            ArrayCounter = "redfish.media.latency",
            KernelCounter = "redfish.stack.latency",
            QueueCounter = "redfish.queue.latency",
        };

        var alert = Assert.Single(StorageLayerSplit.Evaluate(
        [
            One("redfish.media.latency", 20),
            One("redfish.stack.latency", 2),
            One("redfish.queue.latency", 1),
        ], renamed));

        Assert.Equal("Array or fabric is the bottleneck", alert.Title);
    }

    [Fact]
    public void A_stricter_policy_is_obeyed()
    {
        // The numbers are defaults, not laws. An estate on spinning disk lives
        // above five milliseconds all day and needs a higher floor before any
        // of this means anything.
        Assert.Empty(StorageLayerSplit.Evaluate(
            [.. Triple(device: 20, kernel: 2, queue: 1)],
            StorageLayerPolicy.Default with { MinimumMilliseconds = 50d }));
    }

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(StorageLayerSplit.Evaluate([]));
    }
}
