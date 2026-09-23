using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Collectors.Simplivity;

/// <summary>How to reach one SimpliVity federation's REST API (an OVC or the MVA).</summary>
public sealed record SimplivityConnectionOptions
{
    public required string InstanceId { get; init; }

    public required Uri BaseAddress { get; init; }

    /// <summary>The read-only account — the vCenter account <c>svt-session-start</c> takes (§10.7).</summary>
    public required string Username { get; init; }

    public required Secret Password { get; init; }

    public bool AcceptUntrustedCertificate { get; init; }

    /// <summary>Per request. Token issue measured at 136–231 ms on Kibar.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
