using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The four decisions in the performance-query loop (roadmap package A):
/// the deadline (T1.1), the learned batch size (T1.2), the refusal by fault
/// type (T1.3) and the entity that no longer exists (T1.4).
/// </summary>
public class ReadBudgetDecisionTests
{
    private static VsphereObservationSource Source(SimulatedVcenter api, SyntheticTargets targets) =>
        new(api, targets, new StoppedClock());

    // --- T1.1: the deadline -----------------------------------------------

    [Fact]
    public async Task A_read_cut_off_by_the_deadline_returns_what_it_read()
    {
        // 2000 VMs at 250 ms a call is 42.5 s; the budget is 21.6 s. What was
        // read in that time is kept, and the rest is named as not read.
        using var api = new SimulatedVcenter { CancelAt = TimeSpan.FromSeconds(21.6) };

        var batch = await Source(api, new SyntheticTargets(2000)).ReadAsync(api.Token);

        var vms = batch.Observations.Select(o => o.Entity).Distinct().Count();
        Assert.InRange(vms, 900, 1100);

        var timeout = Assert.Single(batch.Failures, f => f.Kind == CollectionFailureKind.Timeout);
        Assert.Equal("VirtualMachine", timeout.Target);
        Assert.Contains($"{vms} of 2000", timeout.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Types_not_reached_before_the_deadline_are_named_as_not_read()
    {
        // Hosts are read first. A budget spent on them leaves the virtual
        // machines untouched, and that is said rather than left for someone
        // to notice as a gap.
        using var api = new SimulatedVcenter { CancelAt = TimeSpan.FromSeconds(2) };

        var batch = await Source(api, new SyntheticTargets(virtualMachines: 100, hosts: 200)).ReadAsync(api.Token);

        Assert.NotEmpty(batch.Observations);
        Assert.Contains(batch.Failures, f => f.Kind == CollectionFailureKind.Timeout && f.Target == "HostSystem");
        Assert.Contains(batch.Failures, f => f.Kind == CollectionFailureKind.Timeout && f.Target == "VirtualMachine");
    }

    [Fact]
    public async Task The_next_cycle_resumes_where_the_last_one_ran_out()
    {
        // Measured after the first cut of T1.1: keeping partial progress kept
        // the same first 996 virtual machines every cycle, and the other 1004
        // never had a sample at all. Blind in a fixed half of the estate is
        // worse than blind everywhere a little, because nothing rotates it.
        using var api = new SimulatedVcenter();
        var source = Source(api, new SyntheticTargets(2000));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            api.ResetCounters();
            api.CancelAt = TimeSpan.FromSeconds(21.6);

            var batch = await source.ReadAsync(api.Token);
            seen.UnionWith(batch.Observations.Select(o => o.Entity.Value));
        }

        Assert.Equal(2000, seen.Count);
    }

    [Fact]
    public async Task A_deadline_before_anything_was_read_is_still_a_timeout()
    {
        // An empty batch would read as a source that answered with nothing.
        using var api = new SimulatedVcenter { CancelAt = TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Source(api, new SyntheticTargets(2000)).ReadAsync(api.Token));
    }

    // --- T1.2: the learned batch size ----------------------------------------

    [Fact]
    public async Task A_batch_size_the_server_refused_is_not_asked_for_again_this_session()
    {
        // ADR-0005 §2: the size that worked is remembered for the session.
        // Before, every cycle started at 12 and was refused twice on the way
        // down to 3 — two wasted queries and a "reduced" report per cycle.
        using var api = new SimulatedVcenter { ReportedMaxQueryMetrics = null, ActualMetricLimit = 64 };
        var source = Source(api, new SyntheticTargets(200));

        await source.ReadAsync(api.Token);
        Assert.Equal(2, api.Refusals);

        api.ResetCounters();
        var second = await source.ReadAsync(api.Token);

        Assert.Equal(0, api.Refusals);
        Assert.All(api.BatchSizes, size => Assert.True(size <= 3, $"asked for {size}"));
        Assert.Equal(200, second.Observations.Select(o => o.Entity).Distinct().Count());
    }

    [Fact]
    public async Task A_remembered_size_is_still_reported_as_reduced()
    {
        // Remembering stops the relearning, not the truth: the read is still
        // slower than planned, and the operator can raise the limit.
        using var api = new SimulatedVcenter { ReportedMaxQueryMetrics = null, ActualMetricLimit = 64 };
        var source = Source(api, new SyntheticTargets(20));

        await source.ReadAsync(api.Token);
        var second = await source.ReadAsync(api.Token);

        Assert.Contains(second.Failures, f => f.Target == "VirtualMachine performance query");
    }

    [Fact]
    public void Remembered_sizes_are_per_type_and_counter_count()
    {
        var memory = new LearnedBatchSizes();
        memory.Remember(VsphereEntityType.VirtualMachine, counterCount: 17, size: 3);

        Assert.Equal(3, memory.For(VsphereEntityType.VirtualMachine, 17));
        Assert.Null(memory.For(VsphereEntityType.HostSystem, 17));
        Assert.Null(memory.For(VsphereEntityType.VirtualMachine, 16));
    }

    [Fact]
    public void A_remembered_size_caps_the_initial_batch_but_never_raises_it()
    {
        Assert.Equal(3, new AdaptiveBatchSizer(256, 17, remembered: 3).Current);
        Assert.Equal(12, new AdaptiveBatchSizer(256, 17, remembered: 500).Current);
        Assert.Equal(12, new AdaptiveBatchSizer(256, 17, remembered: 3).Planned);
    }

    // --- T1.3: the refusal, by fault type ------------------------------------

    private static string Fault(string faultType, string message) =>
        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><soapenv:Fault><faultcode>ServerFaultCode</faultcode>" +
        $"<faultstring>{message}</faultstring>" +
        $"<detail><{faultType} xmlns=\"urn:vim25\"/></detail>" +
        "</soapenv:Fault></soapenv:Body></soapenv:Envelope>";

    [Fact]
    public void A_refusal_is_recognised_by_its_fault_type_whatever_the_message_says()
    {
        // A localised vCenter does not say "restricted by administrator", and
        // the type is what vim25 promises; the wording is not.
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("RestrictedByAdministratorFault", "İstek işleme yönetici tarafından kısıtlandı"),
            VsphereCallContext.PerformanceQuery);

        Assert.Equal(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
    }

    [Fact]
    public void The_documented_text_is_the_primary_signal_whatever_the_fault_type()
    {
        // Broadcom KB 301449 documents the client-side text, "Request
        // processing is restricted by administrator"; no source documents the
        // fault type. So the text decides for any fault, and the type is a
        // second way in rather than a gate in front of the text.
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("SystemErrorFault", "Request processing is restricted by administrator"),
            VsphereCallContext.PerformanceQuery);

        Assert.Equal(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
    }

    [Fact]
    public void Restricted_by_administrator_outside_a_performance_query_is_not_a_size_refusal()
    {
        var fault = VsphereSoapFaultReader.TryRead(
            Fault("RestrictedByAdministratorFault", "Request processing is restricted by administrator."));

        Assert.NotEqual(VsphereFaultKind.QuerySizeRefused, fault!.Kind);
    }

    [Theory]
    [InlineData("RuntimeFault")]
    [InlineData("")]
    [InlineData("SomeNewFault")]
    public void The_message_is_recognised_under_any_fault_type(string faultType)
    {
        Assert.True(AdaptiveBatchSizer.IsQuerySizeRefusal(faultType, "Request processing is restricted by administrator"));
    }

    [Fact]
    public void Neither_signal_means_no_refusal()
    {
        Assert.False(AdaptiveBatchSizer.IsQuerySizeRefusal("SystemErrorFault", "A general system error occurred."));
    }

    // --- T1.4: an entity that no longer exists --------------------------------

    [Fact]
    public async Task A_deleted_first_target_does_not_cost_the_type()
    {
        // The probe asked moRefs[0], which was deleted after the inventory
        // read, and ManagedObjectNotFound dropped all 2000 virtual machines.
        using var api = new SimulatedVcenter { Deleted = ["vm-1"] };

        var batch = await Source(api, new SyntheticTargets(2000)).ReadAsync(api.Token);

        Assert.Equal(1999, batch.Observations.Select(o => o.Entity).Distinct().Count());
        var gone = Assert.Single(batch.Failures);
        Assert.Contains("vm-1", gone.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deleted_target_mid_list_costs_only_itself()
    {
        // vim25 refuses the whole query when one object in it is gone. Before,
        // that fault ended the type: every virtual machine after the batch
        // holding vm-1000 went unread.
        using var api = new SimulatedVcenter { Deleted = ["vm-1000", "vm-1500"] };

        var batch = await Source(api, new SyntheticTargets(2000)).ReadAsync(api.Token);

        var read = batch.Observations.Select(o => o.Entity.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(1998, read.Count);
        Assert.DoesNotContain("vm-1000", read);
        Assert.Contains("vm-2000", read);

        // Isolating the missing ones costs a handful of queries, not a second pass.
        Assert.InRange(api.PerfQueries, 167, 167 + 20);
    }

    [Fact]
    public async Task A_target_list_that_is_entirely_stale_is_reported_rather_than_probed_forever()
    {
        using var api = new SimulatedVcenter { Deleted = [.. Enumerable.Range(1, 10).Select(i => $"vm-{i}")] };

        var batch = await Source(api, new SyntheticTargets(10)).ReadAsync(api.Token);

        Assert.Empty(batch.Observations);
        Assert.Contains(batch.Failures, f => f.Target == "VirtualMachine");
        Assert.True(api.Calls < 10, $"{api.Calls} calls");
    }

    [Theory]
    [InlineData(VsphereFaultKind.NoPermission, CollectionFailureKind.AuthorizationDenied)]
    [InlineData(VsphereFaultKind.Other, CollectionFailureKind.ProtocolError)]
    public async Task Any_other_fault_on_a_batch_falls_back_to_one_query_per_entity_for_that_batch(
        VsphereFaultKind fault, CollectionFailureKind expected)
    {
        // OTel vcenterreceiver's rule. Before, a permission fault on one VM
        // cost every VM of the type; now it costs that VM, the batch it was
        // in is asked one entity at a time, and every other batch is untouched.
        using var api = new SimulatedVcenter { Faulty = new(StringComparer.Ordinal) { ["vm-30"] = fault } };

        var batch = await Source(api, new SyntheticTargets(60)).ReadAsync(api.Token);

        var read = batch.Observations.Select(o => o.Entity.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(59, read.Count);
        Assert.DoesNotContain("vm-30", read);

        // Five batches of 12, one of which (vm-25..vm-36) failed and was
        // re-asked as twelve single queries.
        Assert.Equal(5 + 12, api.PerfQueries);

        var failure = Assert.Single(batch.Failures);
        Assert.Equal(expected, failure.Kind);
        Assert.Contains("vm-30", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fault_on_every_entity_is_still_one_report_per_kind()
    {
        // The worst case of the per-entity fallback — a fault that applies to
        // the whole type — costs one query per entity, bounded by the read
        // budget, and is said once rather than sixty times.
        using var api = new SimulatedVcenter
        {
            Faulty = Enumerable.Range(1, 60).ToDictionary(
                i => $"vm-{i}", _ => VsphereFaultKind.NoPermission, StringComparer.Ordinal),
        };

        var batch = await Source(api, new SyntheticTargets(60)).ReadAsync(api.Token);

        Assert.Empty(batch.Observations);
        var failure = Assert.Single(batch.Failures);
        Assert.Contains("60", failure.Detail, StringComparison.Ordinal);
    }
}