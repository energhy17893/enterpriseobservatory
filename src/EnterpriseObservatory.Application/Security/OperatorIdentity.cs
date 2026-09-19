namespace EnterpriseObservatory.Application.Security;

/// <summary>
/// Who is acting.
/// </summary>
/// <remarks>
/// <para>
/// Carries whether the product actually knows who this is. The audit trail's
/// whole purpose is answering "why did this fire and who closed it" (ADR-0007),
/// and a name we cannot verify recorded as though we could is worse than no
/// name: it reads as authoritative and is not.
/// </para>
/// <para>
/// So an unverified actor is recorded as unverified, in the stored transition,
/// forever. Nobody reading the history later has to know how the installation
/// was configured at the time.
/// </para>
/// </remarks>
public sealed record OperatorIdentity
{
    /// <summary>What to call them: a username, or an address when unverified.</summary>
    public required string Name { get; init; }

    /// <summary>Whether an authenticated identity produced this name.</summary>
    public required bool IsVerified { get; init; }

    /// <summary>
    /// What goes into the audit trail.
    /// </summary>
    /// <remarks>
    /// Prefixed rather than flagged alongside, because the transition record
    /// stores a single actor string and a prefix cannot be dropped by a
    /// reader who did not know to look for a second field.
    /// </remarks>
    public string AuditName => IsVerified ? Name : $"unverified:{Name}";

    public static OperatorIdentity Verified(string name) =>
        new() { Name = name, IsVerified = true };

    /// <summary>
    /// An actor the product cannot identify, named by where the request came
    /// from.
    /// </summary>
    public static OperatorIdentity Unverified(string origin) =>
        new() { Name = string.IsNullOrWhiteSpace(origin) ? "unknown" : origin, IsVerified = false };
}
