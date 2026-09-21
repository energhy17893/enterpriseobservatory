using System.Globalization;
using EnterpriseObservatory.Collectors.Vsphere;
using Xunit.Abstractions;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The 2000-VM read-budget measurement (roadmap T1.1, package A).
/// </summary>
/// <remarks>
/// Prints the table recorded in docs/measurements/read-budget-2000vm.md. Run
/// with <c>--logger "console;verbosity=detailed"</c> to see it. Per-call
/// latency is an assumption, swept over three values: this estate has never
/// had a QueryPerf round trip timed.
/// </remarks>
public class ReadBudgetMeasurementTests(ITestOutputHelper output)
{
    private static readonly TimeSpan[] Latencies =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
    ];

    private const int VirtualMachines = 2000;

    [Fact]
    public async Task Measure_a_2000_vm_observation_read()
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"VM counters: {VsphereCounters.VirtualMachine.Count}; " +
            $"initial batch at limit 256: {new AdaptiveBatchSizer(256, VsphereCounters.VirtualMachine.Count).Current}"));
        output.WriteLine("| scenario | latency | cycle | calls | QueryPerf | refusals | seconds | samples (VMs) | failures |");
        output.WriteLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var latency in Latencies)
        {
            await Scenario("limit readable (256)", latency, new SimulatedVcenter { PerCall = latency });
            await Scenario("limit unreadable, actual 64", latency, new SimulatedVcenter
            {
                PerCall = latency,
                ReportedMaxQueryMetrics = null,
                ActualMetricLimit = 64,
            });
            await Scenario("vm-1 deleted", latency, new SimulatedVcenter
            {
                PerCall = latency,
                Deleted = ["vm-1"],
            });
            await Scenario("vm-1000 deleted", latency, new SimulatedVcenter
            {
                PerCall = latency,
                Deleted = ["vm-1000"],
            });
        }
    }

    private async Task Scenario(string name, TimeSpan latency, SimulatedVcenter vcenter)
    {
        using var api = vcenter;
        var source = new VsphereObservationSource(api, new SyntheticTargets(VirtualMachines), new StoppedClock());

        for (var cycle = 1; cycle <= 2; cycle++)
        {
            api.ResetCounters();

            string samples;
            string failures;
            try
            {
                var batch = await source.ReadAsync(api.Token);
                samples = batch.Observations.Select(o => o.Entity).Distinct().Count()
                    .ToString(CultureInfo.InvariantCulture);
                failures = string.Join("; ", batch.Failures.Select(f => $"{f.Kind}:{f.Target}"));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                samples = "0 (read threw)";
                failures = ex.GetType().Name;
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {latency.TotalMilliseconds:0} ms | {cycle} | {api.Calls} | {api.PerfQueries} | " +
                $"{api.Refusals} | {api.Elapsed.TotalSeconds:0.0} | {samples} | {failures} |"));
        }
    }
}
