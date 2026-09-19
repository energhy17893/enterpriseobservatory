using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Alerts;

/// <summary>Why an operator's action did not take effect.</summary>
public enum AlertActionRefusal
{
    None = 0,

    /// <summary>No alert with that fingerprint. Usually one that has resolved.</summary>
    NotFound,

    /// <summary>A silence must end in the future.</summary>
    DeadlineInThePast,
}

/// <summary>What an operator's action did.</summary>
/// <remarks>
/// A result rather than an exception, because "that alert is gone" is an
/// ordinary outcome: an operator acknowledging something from a screen that is
/// thirty seconds old is not making a mistake.
/// </remarks>
public sealed record AlertActionResult
{
    public required bool Applied { get; init; }

    public AlertInstance? Instance { get; init; }

    public AlertActionRefusal Refusal { get; init; }

    public static AlertActionResult Done(AlertInstance instance) =>
        new() { Applied = true, Instance = instance };

    public static AlertActionResult Refused(AlertActionRefusal refusal) =>
        new() { Applied = false, Refusal = refusal };
}

/// <summary>
/// What an operator can do to an alert.
/// </summary>
/// <remarks>
/// <para>
/// The only place operator intent reaches alert state, as
/// <see cref="Monitoring.MonitoringCycle"/> is the only place observation does.
/// Both go through the same store and the same lock, so an acknowledgement
/// cannot be lost to a cycle that happened to be running.
/// </para>
/// <para>
/// The rules themselves are in <see cref="AlertLifecycle"/> and stay there.
/// This decides nothing about what a transition means; it locates the alert,
/// applies the transition and reports what happened.
/// </para>
/// </remarks>
public sealed class AlertOperations(IAlertStateStore store, IClock clock)
{
    private readonly IAlertStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>An operator takes ownership. Notifications stop; the alert stays visible.</summary>
    /// <remarks>
    /// Idempotent. Acknowledging something already acknowledged is not an
    /// error, and a screen refreshed twice must not become an argument.
    /// </remarks>
    public AlertActionResult Acknowledge(AlertFingerprint fingerprint, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return Apply(fingerprint, (instance, now) =>
            AlertLifecycle.Acknowledge(instance, actor.AuditName, now));
    }

    /// <summary>
    /// An operator declares the problem handled, whether or not it is still
    /// firing.
    /// </summary>
    /// <remarks>
    /// The clear outlives the condition: re-observing the same fault does not
    /// reopen it, and only the fault disappearing ends it. That is what makes
    /// clearing useful for a known, accepted condition — and why it is recorded
    /// with a name.
    /// </remarks>
    public AlertActionResult Clear(AlertFingerprint fingerprint, OperatorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return Apply(fingerprint, (instance, now) =>
            AlertLifecycle.Clear(instance, actor.AuditName, now));
    }

    /// <summary>Mutes an alert until a deadline, after which it returns on its own.</summary>
    /// <remarks>
    /// A deadline rather than a toggle, on purpose. Something switched off
    /// indefinitely is something nobody remembers to switch back on, and a
    /// monitoring system full of those is one that has quietly stopped
    /// monitoring. See product principle 4.
    /// </remarks>
    public AlertActionResult Silence(
        AlertFingerprint fingerprint, OperatorIdentity actor, DateTimeOffset untilUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (untilUtc <= _clock.UtcNow)
        {
            return AlertActionResult.Refused(AlertActionRefusal.DeadlineInThePast);
        }

        return Apply(fingerprint, (instance, now) =>
            AlertLifecycle.Silence(instance, actor.AuditName, untilUtc, now));
    }

    private AlertActionResult Apply(
        AlertFingerprint fingerprint, Func<AlertInstance, DateTimeOffset, AlertInstance> transition)
    {
        // The clock is read once, outside the store's lock, so that every
        // transition this action produces carries the same instant.
        var now = _clock.UtcNow;

        var result = _store.Mutate(fingerprint, instance => transition(instance, now));

        return result is null
            ? AlertActionResult.Refused(AlertActionRefusal.NotFound)
            : AlertActionResult.Done(result);
    }
}
