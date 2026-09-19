using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Alerts;

/// <summary>Where maintenance windows live.</summary>
/// <remarks>
/// Durable, and kept after they end. A finished window is the answer to "why
/// was nobody paged last Tuesday", which is a question asked long after the
/// work is done — see <see cref="MaintenanceWindow"/>.
/// </remarks>
public interface IMaintenanceWindowStore
{
    /// <summary>Every window that has not yet been forgotten.</summary>
    IReadOnlyList<MaintenanceWindow> All();

    /// <summary>Windows that could suppress something right now.</summary>
    IReadOnlyList<MaintenanceWindow> ActiveAt(DateTimeOffset atUtc);

    void Add(MaintenanceWindow window);

    /// <summary>Ends a window now, or removes it if it never started.</summary>
    bool Remove(string id);

    /// <summary>Forgets windows that ended longer ago than the retention.</summary>
    int Forget(DateTimeOffset olderThanUtc);
}

/// <summary>Why a window was refused.</summary>
public enum MaintenanceFailure
{
    None = 0,

    NotFound,

    /// <summary>It ends before it starts, or has already ended.</summary>
    BadSchedule,

    /// <summary>
    /// Longer than the product is prepared to keep quiet for.
    /// </summary>
    /// <remarks>
    /// A window nobody remembers to end is a monitoring system that has quietly
    /// stopped notifying. Same argument as a silence needing a deadline.
    /// </remarks>
    TooLong,
}

/// <summary>What declaring a window did.</summary>
public sealed record MaintenanceResult
{
    public required bool Applied { get; init; }

    public MaintenanceWindow? Window { get; init; }

    public MaintenanceFailure Failure { get; init; }

    public static MaintenanceResult Done(MaintenanceWindow window) =>
        new() { Applied = true, Window = window };

    public static MaintenanceResult Refused(MaintenanceFailure failure) =>
        new() { Applied = false, Failure = failure };
}

/// <summary>
/// Declaring and ending planned work.
/// </summary>
/// <remarks>
/// <para>
/// A window suppresses <em>notification</em> and never observation. The
/// condition really happened, the alert is still raised and still visible, and
/// it carries the id of the window that silenced it — which is what makes the
/// window usable afterwards as an account of what broke during the work.
/// </para>
/// <para>
/// The domain has had all of this since ADR-0004's lifecycle work and nothing
/// ever passed a window to the reconciler, so the feature existed and did
/// nothing. This is the wiring.
/// </para>
/// </remarks>
public sealed class MaintenanceService(IMaintenanceWindowStore store, IClock clock)
{
    private readonly IMaintenanceWindowStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The longest a single window may run.
    /// </summary>
    /// <remarks>
    /// A fortnight covers any real piece of planned work, including a staged
    /// firmware campaign across an estate. Beyond that it is not a window, it
    /// is somebody switching the product off, and they should have to say so
    /// again.
    /// </remarks>
    public static TimeSpan MaximumDuration { get; } = TimeSpan.FromDays(14);

    /// <summary>How long a finished window is kept as a record.</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(90);

    public IReadOnlyList<MaintenanceWindow> All() => _store.All();

    /// <summary>Declares a window.</summary>
    public MaintenanceResult Declare(
        string title,
        string reason,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyList<EntityId> entities,
        OperatorIdentity declaredBy)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(declaredBy);

        var now = _clock.UtcNow;

        if (endUtc <= startUtc || endUtc <= now)
        {
            // A window that has already ended suppresses nothing and is only a
            // confusing row. Declaring one is a mistake worth reporting rather
            // than storing.
            return MaintenanceResult.Refused(MaintenanceFailure.BadSchedule);
        }

        if (endUtc - startUtc > MaximumDuration)
        {
            return MaintenanceResult.Refused(MaintenanceFailure.TooLong);
        }

        var window = new MaintenanceWindow
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = string.IsNullOrWhiteSpace(title) ? "Planned work" : title.Trim(),
            Reason = reason?.Trim() ?? string.Empty,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Entities = entities,
            DeclaredBy = declaredBy.AuditName,
            DeclaredAtUtc = now,
        };

        _store.Add(window);

        return MaintenanceResult.Done(window);
    }

    /// <summary>Ends a window early, or cancels one that has not started.</summary>
    public MaintenanceResult End(string id)
    {
        if (_store.All().FirstOrDefault(w => w.Id == id) is not { } window)
        {
            return MaintenanceResult.Refused(MaintenanceFailure.NotFound);
        }

        _store.Remove(id);

        return MaintenanceResult.Done(window);
    }

    /// <summary>Drops windows that ended long enough ago to be history.</summary>
    public int Forget() => _store.Forget(_clock.UtcNow - Retention);
}
