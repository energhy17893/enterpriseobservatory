namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// What a collector must supply so the shared wire-level contract suite can
/// drive it through a fake transport instead of a real server.
/// </summary>
/// <remarks>
/// <para>
/// Each method is one scenario from <c>docs/reference-approaches.md</c> §10.2's
/// "asgari sözleşme vakaları" plus the two cases ADR-0025 adds. The fixture
/// hides how its vendor's wire protocol is scripted — SOAP for vSphere, REST
/// for a future Redfish collector — behind a vendor-neutral answer the shared
/// suite can assert on.
/// </para>
/// <para>
/// This targets the collector's own client (<c>VsphereClient</c> today),
/// because the behaviours here — server-side object cleanup, session
/// deduplication, refusing a malformed reply — are the transport
/// implementation's responsibility, not the thin <c>IInventorySource</c> /
/// <c>IObservationSource</c> that merely consumes what it returns. See
/// <see cref="IInventoryContractFixture"/> and <see cref="IObservationContractFixture"/>
/// for the source-level cases (slow source, self-metrics, long-run stability,
/// the produced/accepted/dropped balance).
/// </para>
/// </remarks>
public interface ITransportContractFixture
{
    /// <summary>Case 1 (start/stop, including stop without ever starting).</summary>
    /// <returns>
    /// True if constructing and disposing the transport without ever reading
    /// sent no request and threw nothing.
    /// </returns>
    Task<bool> DisposingWithoutAnyCallIsSilentAsync();

    /// <summary>The other half of case 1: a normal read answers every target.</summary>
    Task<int> ReadHealthyInventoryEntityCountAsync(int targetCount);

    /// <summary>
    /// Case 2: a read cut off mid-cycle must not leak a server-side object.
    /// </summary>
    /// <returns>
    /// True if every server-side object the transport created for the read it
    /// abandoned was also given back.
    /// </returns>
    Task<bool> CancelledReadLeavesNoServerObjectAsync();

    /// <summary>
    /// Our case: a single invalid property path fails the whole inventory read
    /// rather than silently returning what could be parsed.
    /// </summary>
    /// <returns>True if the read surfaced as a failure, not a partial success.</returns>
    Task<bool> SingleInvalidFieldFailsWholeReadAsync();

    /// <summary>Case 4: a malformed reply is never read as an empty success.</summary>
    Task<bool> MalformedReplyNeverBecomesEmptySuccessAsync();

    /// <summary>
    /// Our case: two calls that notice one expired session at the same moment
    /// must re-authenticate once between them, not twice.
    /// </summary>
    /// <returns>How many times the transport signed in again after expiry.</returns>
    Task<int> ReauthenticationsWhenTwoConcurrentCallsNoticeExpiredSessionAsync();
}
