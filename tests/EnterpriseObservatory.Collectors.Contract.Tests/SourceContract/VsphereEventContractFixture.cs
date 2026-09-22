using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives today's vSphere event source (<see cref="VsphereEventSource"/>)
/// through a fake <see cref="IVsphereEventApi"/>.
/// </summary>
public sealed class VsphereEventContractFixture : IEventContractFixture
{
    public string InstanceId => "vc-contract";

    public (IEventSource Source, Func<int> Calls) CreateRejectingLogin()
    {
        var api = new RejectingApi(InstanceId);
        return (new VsphereEventSource(api, new TestClock()), () => api.Calls);
    }

    public IEventSource CreateSlow() => new VsphereEventSource(new SlowApi(InstanceId), new TestClock());

    /// <summary>A vCenter that refuses the credential, as vim25 says so.</summary>
    private sealed class RejectingApi(string instanceId) : IVsphereEventApi
    {
        public int Calls { get; private set; }

        public string InstanceId => instanceId;

        public Task<EventRead> ReadEventsAsync(EventMark? since, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            Calls++;
            throw new VsphereApiException(
                VsphereFaultKind.InvalidLogin,
                "Cannot complete login due to an incorrect user name or password.");
        }
    }

    /// <summary>A transport that ignores cancellation and answers far too late.</summary>
    private sealed class SlowApi(string instanceId) : IVsphereEventApi
    {
        public string InstanceId => instanceId;

        public async Task<EventRead> ReadEventsAsync(EventMark? since, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return new EventRead { Events = [] };
        }
    }
}
