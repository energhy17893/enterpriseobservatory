namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>
/// A period during which work is expected to disturb part of the estate.
/// </summary>
/// <remarks>
/// <para>
/// A window suppresses <em>notification</em>, never observation. The condition
/// really happened and the alert stays visible, carrying the id of the window
/// that silenced it. Hiding it would be exactly the fabrication product
/// principle 1 forbids — and it would also make the window useless as an alibi
/// afterwards, when someone asks what actually broke during the work.
/// </para>
/// </remarks>
public sealed record MaintenanceWindow
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// Who declared it.
    /// </summary>
    /// <remarks>
    /// A window stops people being told about real failures. Afterwards, when
    /// somebody asks why nobody was paged, "there was a window" is half an
    /// answer; the other half is who decided there should be. Same reason every
    /// alert transition carries an actor — see ADR-0013.
    /// </remarks>
    public string DeclaredBy { get; init; } = string.Empty;

    public DateTimeOffset DeclaredAtUtc { get; init; }

    /// <summary>Why the work is happening, in the operator's words.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// The entities this covers. Empty means the whole estate.
    /// </summary>
    /// <remarks>
    /// Estate-wide windows are deliberately expressible because that is what a
    /// datacentre power test actually is, but they are blunt: everything goes
    /// quiet, including the failure the test was meant to reveal.
    /// </remarks>
    public IReadOnlyList<EntityId> Entities { get; init; } = [];

    public bool IsActiveAt(DateTimeOffset atUtc) => atUtc >= StartUtc && atUtc < EndUtc;

    /// <summary>Whether the window is over.</summary>
    public bool HasEnded(DateTimeOffset atUtc) => atUtc >= EndUtc;

    /// <summary>Whether it has not started yet.</summary>
    public bool IsScheduled(DateTimeOffset atUtc) => atUtc < StartUtc;

    /// <summary>Whether this window applies to a given entity.</summary>
    public bool Covers(EntityId? entity)
    {
        if (Entities.Count == 0)
        {
            return true; // estate-wide
        }

        return entity is { } id && Entities.Contains(id);
    }

    /// <summary>Whether this window suppresses notification for an entity right now.</summary>
    public bool Suppresses(EntityId? entity, DateTimeOffset atUtc) =>
        IsActiveAt(atUtc) && Covers(entity);
}

/// <summary>Finds the maintenance window responsible for silencing an alert.</summary>
public static class MaintenanceSuppression
{
    /// <summary>
    /// Returns the id of the active window covering the entity, or null.
    /// </summary>
    /// <remarks>
    /// The id rather than a bare bool, so the UI can say <em>which</em> window
    /// is responsible and the operator can judge whether it should be.
    /// </remarks>
    public static string? WindowSuppressing(
        EntityId? entity,
        IReadOnlyList<MaintenanceWindow> windows,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(windows);

        foreach (var window in windows)
        {
            if (window.Suppresses(entity, atUtc))
            {
                return window.Id;
            }
        }

        return null;
    }
}
