using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// A vCenter whose calls cost simulated time rather than real time.
/// </summary>
/// <remarks>
/// <para>
/// Built for the read-budget measurement (docs/measurements/read-budget-2000vm.md)
/// and kept for the tests that pin its outcome. Real delays would make a
/// 2000-VM read take a minute of wall clock per scenario; a virtual clock makes
/// it free and exact, and the question being asked — how many calls, how many
/// seconds, does it fit — is arithmetic over the call sequence anyway.
/// </para>
/// <para>
/// The size limit is enforced the strict way (entities × counters against the
/// server's metric limit), which is the reading ADR-0005 §2 sizes for. The
/// runner's cooperative cancellation is simulated by <see cref="CancelAt"/>:
/// once the virtual clock passes it, the token handed to the source is
/// cancelled and the call in flight throws, exactly as a cancelled HTTP call
/// would.
/// </para>
/// </remarks>
internal sealed class SimulatedVcenter : IVsphereApi, IDisposable
{
    private readonly CancellationTokenSource _budget = new();

    public string InstanceId => "vc-sim";

    public void Dispose() => _budget.Dispose();

    /// <summary>What the server says <c>maxQueryMetrics</c> is; null = unreadable.</summary>
    public int? ReportedMaxQueryMetrics { get; init; } = AdaptiveBatchSizer.DefaultMaxQueryMetrics;

    /// <summary>What the server actually enforces, in metrics per query.</summary>
    public int ActualMetricLimit { get; init; } = AdaptiveBatchSizer.DefaultMaxQueryMetrics;

    /// <summary>Simulated cost of one round trip.</summary>
    public TimeSpan PerCall { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Managed objects that were deleted after the target list was built.</summary>
    public HashSet<string> Deleted { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Virtual time at which the source's token is cancelled, if any.</summary>
    public TimeSpan? CancelAt { get; set; }

    /// <summary>The fault type a refusal carries, as the SOAP detail would name it.</summary>
    public string RefusalFaultType { get; init; } = "RestrictedByAdministratorFault";

    public TimeSpan Elapsed { get; private set; }

    public int Calls { get; private set; }

    public int PerfQueries { get; private set; }

    public int Refusals { get; private set; }

    /// <summary>The token to hand the source, cancelled at <see cref="CancelAt"/>.</summary>
    public CancellationToken Token => _budget.Token;

    public List<int> BatchSizes { get; } = [];

    public void ResetCounters()
    {
        Elapsed = TimeSpan.Zero;
        Calls = 0;
        PerfQueries = 0;
        Refusals = 0;
        BatchSizes.Clear();
    }

    public static List<VsphereCounter> Catalog() =>
    [
        .. VsphereCounters.Host
            .Concat(VsphereCounters.VirtualMachine)
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) =>
            {
                var parts = key.Split('.');
                return new VsphereCounter
                {
                    Id = index + 1,
                    Group = parts[0],
                    Name = parts[1],
                    Rollup = VsphereCounter.ParseRollup(parts[2]),
                    Unit = "number",
                    Level = 1,
                };
            }),
    ];

    private readonly List<VsphereCounter> _catalog = Catalog();

    private void Spend(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Calls++;
        Elapsed += PerCall;

        if (CancelAt is { } at && Elapsed > at)
        {
            // The call was in flight when the budget ran out; it is lost.
            _budget.Cancel();
            ct.ThrowIfCancellationRequested();
        }
    }

    public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken cancellationToken)
    {
        Spend(cancellationToken);
        return Task.FromResult<IReadOnlyList<VsphereCounter>>(_catalog);
    }

    public Task<int?> GetMaxQueryMetricsAsync(CancellationToken cancellationToken)
    {
        Spend(cancellationToken);
        return Task.FromResult(ReportedMaxQueryMetrics);
    }

    public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
        string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Spend(cancellationToken);

        if (Deleted.Contains(entityMoRef))
        {
            throw NotFound(entityMoRef);
        }

        return Task.FromResult<IReadOnlyList<string>>([.. _catalog.Select(c => c.Key)]);
    }

    public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
        IReadOnlyList<string> entityMoRefs,
        VsphereEntityType entityType,
        IReadOnlyList<VsphereCounter> counters,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Spend(cancellationToken);
        PerfQueries++;
        BatchSizes.Add(entityMoRefs.Count);

        if (entityMoRefs.Count * counters.Count > ActualMetricLimit)
        {
            Refusals++;
            throw Refusal();
        }

        // vim25 refuses the whole query when any one object in it is gone.
        if (entityMoRefs.FirstOrDefault(Deleted.Contains) is { } gone)
        {
            throw NotFound(gone);
        }

        return Task.FromResult<IReadOnlyList<PerfEntitySamples>>(
        [
            .. entityMoRefs.Select(moRef => new PerfEntitySamples
            {
                EntityMoRef = moRef,
                Values =
                [
                    .. counters.Select(c => new CounterValue
                    {
                        CounterName = c.Key,
                        Raw = 1,
                        Rollup = c.Rollup,
                        Interval = TimeSpan.FromSeconds(20),
                        Unit = c.Unit,
                    }),
                ],
            }),
        ]);
    }

    /// <summary>A refusal as the transport would raise it, from a SOAP body.</summary>
    /// <remarks>
    /// Built by reading a fault body rather than by constructing the exception
    /// directly, so the harness goes through the same classification the live
    /// client does — that is what T1.3 changes.
    /// </remarks>
    private Exception Refusal()
    {
        var body =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<soapenv:Body><soapenv:Fault><faultcode>ServerFaultCode</faultcode>" +
            "<faultstring>Request processing is restricted by administrator</faultstring>" +
            $"<detail><{RefusalFaultType} xmlns=\"urn:vim25\"/></detail>" +
            "</soapenv:Fault></soapenv:Body></soapenv:Envelope>";

        var fault = VsphereSoapFaultReader.TryRead(body, VsphereCallContext.PerformanceQuery)!;

        return fault.Kind == VsphereFaultKind.QuerySizeRefused
            ? new VsphereQuerySizeRefusedException(fault.Message)
            : new VsphereApiException(fault.Kind, fault.Message);
    }

    private static VsphereApiException NotFound(string moRef) =>
        new(VsphereFaultKind.ManagedObjectNotFound,
            $"The object 'vim.VirtualMachine:{moRef}' has already been deleted or has not been completely created");
}

/// <summary>A target list of N virtual machines, every one resolvable.</summary>
internal sealed class SyntheticTargets(int virtualMachines, int hosts = 0) : IVsphereSampleTargetProvider
{
    public VsphereSampleTargets Current { get; } = new()
    {
        VirtualMachines = [.. Enumerable.Range(1, virtualMachines).Select(i => $"vm-{i}")],
        Hosts = [.. Enumerable.Range(1, hosts).Select(i => $"host-{i}")],
    };

    public EntityId? ResolveEntity(string moRef) => new EntityId(moRef);

    public EntityId? ResolveVolume(string volumeIdentifier) => null;

    public string? DisplayNameOf(string moRef) => null;
}

internal sealed class StoppedClock : IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
}
