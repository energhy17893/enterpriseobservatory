using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Alerts;

/// <summary>Everything one reconciliation pass needs.</summary>
public sealed record AlertReconciliationRequest
{
    /// <summary>Problems seen this cycle, from every source combined.</summary>
    public IReadOnlyList<AlertDefinition> Observed { get; init; } = [];

    /// <summary>Instances as currently stored.</summary>
    public IReadOnlyList<AlertInstance> Stored { get; init; } = [];

    /// <summary>Flap counters, which outlive the instances they describe.</summary>
    public IReadOnlyList<FlapHistory> FlapHistories { get; init; } = [];

    public IReadOnlyList<MaintenanceWindow> MaintenanceWindows { get; init; } = [];

    public HysteresisPolicy Hysteresis { get; init; } = HysteresisPolicy.Default;

    public FlapPolicy Flap { get; init; } = FlapPolicy.Default;

    public required DateTimeOffset NowUtc { get; init; }
}

/// <summary>What the caller must persist and act on.</summary>
public sealed record AlertReconciliationResult
{
    /// <summary>The full set of instances after this cycle.</summary>
    public IReadOnlyList<AlertInstance> Instances { get; init; } = [];

    /// <summary>Instances that should be deleted from storage.</summary>
    public IReadOnlyList<AlertFingerprint> Retired { get; init; } = [];

    /// <summary>
    /// Instances owing a notification that is not suppressed.
    /// </summary>
    /// <remarks>
    /// Each carries its <see cref="AlertInstance.PendingNotification"/> so the
    /// dispatcher can choose a channel: paging someone for "it got better"
    /// is how notification fatigue starts.
    /// </remarks>
    public IReadOnlyList<AlertInstance> ToNotify { get; init; } = [];

    public IReadOnlyList<FlapHistory> FlapHistories { get; init; } = [];

    /// <summary>Instances visible in the alert inbox right now.</summary>
    public IReadOnlyList<AlertInstance> Visible =>
        [.. Instances.Where(i => i.IsVisible)];
}

/// <summary>
/// Folds one cycle's observations into the stored alert state.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no clock, no storage, no dispatch. The caller supplies the time and
/// persists the outcome, which keeps the whole of this behaviour testable
/// without infrastructure.
/// </para>
/// <para>
/// This is the single place alert state is advanced. Every view — the inbox,
/// an entity page, a vendor deep view — reads the result rather than computing
/// its own. In the previous product some screens read the per-cycle snapshot
/// list while others read lifecycle instances, so the same alert could appear
/// acknowledged on one page and open on another. See ADR-0007.
/// </para>
/// </remarks>
public static class AlertReconciler
{
    public static AlertReconciliationResult Reconcile(AlertReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stored = request.Stored.ToDictionary(i => i.Fingerprint);
        var flaps = request.FlapHistories.ToDictionary(f => f.Fingerprint);

        // Info never enters the lifecycle. Dropping it here rather than at each
        // call site means a new alert source cannot forget to.
        var observed = request.Observed
            .Where(a => AlertLifecycle.EntersLifecycle(a.Severity))
            .GroupBy(a => a.Fingerprint)
            // Two sources reporting the same problem is one problem. Keep the
            // worse reading rather than letting collector order decide.
            .ToDictionary(g => g.Key, g => g.MaxBy(a => a.Severity)!);

        // Count this cycle's cessations before deciding what is flapping, so a
        // signal that stops on this very cycle is judged on current evidence.
        foreach (var (fingerprint, instance) in stored)
        {
            // Derived alerts are excluded: tracking the instability of the
            // instability detector recurses and tells an operator nothing.
            if (instance.IsDerived ||
                observed.ContainsKey(fingerprint) ||
                !AlertLifecycle.OnAbsent(instance, request.NowUtc).CeasedFiring)
            {
                continue;
            }

            var history = flaps.TryGetValue(fingerprint, out var existing)
                ? existing
                : new FlapHistory { Fingerprint = fingerprint, ObjectName = instance.Title };

            flaps[fingerprint] = history.RecordCeased(request.NowUtc, request.Flap);
        }

        // A signal that will not hold still is a problem in its own right, and
        // one that leaves no instance behind. It is re-derived every cycle and
        // then treated exactly like anything a collector reported, so it
        // confirms under hysteresis and resolves on its own once the signal
        // settles — rather than being raised once and retired on the next pass
        // for not having been observed.
        foreach (var history in flaps.Values)
        {
            var derived = FlapDetection.Evaluate(history, request.NowUtc, request.Flap);
            if (derived is not null)
            {
                observed[derived.Fingerprint] = derived;
            }
        }

        var next = new Dictionary<AlertFingerprint, AlertInstance>();
        var retired = new List<AlertFingerprint>();

        foreach (var (fingerprint, definition) in observed)
        {
            stored.TryGetValue(fingerprint, out var existing);
            next[fingerprint] = AlertLifecycle.OnObserved(
                existing, definition, request.Hysteresis, request.NowUtc, request.MaintenanceWindows);
        }

        foreach (var (fingerprint, instance) in stored)
        {
            if (observed.ContainsKey(fingerprint))
            {
                continue;
            }

            var absence = AlertLifecycle.OnAbsent(instance, request.NowUtc);

            if (absence.Instance is null)
            {
                retired.Add(fingerprint);
            }
            else
            {
                next[fingerprint] = absence.Instance;
            }
        }

        var instances = next.Values.ToList();

        return new AlertReconciliationResult
        {
            Instances = instances,
            Retired = retired,
            ToNotify = [.. instances.Where(i => i.ShouldNotify)],
            FlapHistories = [.. flaps.Values],
        };
    }
}
