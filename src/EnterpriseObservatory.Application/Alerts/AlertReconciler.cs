using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Alerts;

/// <summary>
/// Everything one reconciliation pass needs.
/// </summary>
/// <remarks>
/// <see cref="Observed"/> and <see cref="Stored"/> must describe the same
/// scope. Reconciliation treats what it is given as the whole truth, so an
/// instance from another scope handed in here would be resolved for never
/// having been observed. Slicing happens at the store — see
/// <c>AlertScopes</c>.
/// </remarks>
public sealed record AlertReconciliationRequest
{
    /// <summary>
    /// The evaluation this pass is reconciling. See <c>AlertScopes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required, and required for a reason that is not tidiness. Until this
    /// existed the scope was stamped onto alerts by whoever remembered to —
    /// the two collection pipelines did, the analysis rules did not, and both
    /// stores stamped again on the way in to cover for them. Three places
    /// spelling out the same fact is three places to disagree, and they did:
    /// a rule's alert read "" from the live cache and "observation" from the
    /// database row beside it, so the answer depended on how long the process
    /// had been up.
    /// </para>
    /// <para>
    /// Now it is said once, here, and <see cref="AlertReconciler"/> applies it
    /// to everything it returns. Omitting it is a compile error rather than an
    /// alert filed under nothing, which is the point: a fourth store, or a
    /// fifth caller, has nothing left to get wrong.
    /// </para>
    /// </remarks>
    public required string Scope { get; init; }

    /// <summary>
    /// Problems seen this cycle, from every source in this scope combined.
    /// </summary>
    public IReadOnlyList<AlertDefinition> Observed { get; init; } = [];

    /// <summary>Instances currently stored for this scope.</summary>
    public IReadOnlyList<AlertInstance> Stored { get; init; } = [];

    /// <summary>Flap counters, which outlive the instances they describe.</summary>
    public IReadOnlyList<FlapHistory> FlapHistories { get; init; } = [];

    public IReadOnlyList<MaintenanceWindow> MaintenanceWindows { get; init; } = [];

    public HysteresisPolicy Hysteresis { get; init; } = HysteresisPolicy.Default;

    public FlapPolicy Flap { get; init; } = FlapPolicy.Default;

    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>
    /// Sources expected to report in this scope that did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule <c>EntityGraph.Merge</c> applies to entities, applied to
    /// alerts: a vCenter that did not answer has told us nothing, so nothing
    /// it would have re-reported may be taken to have stopped. A stored
    /// instance whose <see cref="AlertInstance.Entity"/> belongs to one of
    /// these (see <see cref="SourceOf"/>) and that was not observed is carried
    /// forward unchanged — not resolved, not counted as a cessation.
    /// </para>
    /// <para>
    /// Only the sources that were asked and did not answer, not every source
    /// absent from the reports: an entity left behind by a vCenter that was
    /// removed from configuration must not hold its alerts open forever.
    /// Alerts with no entity are judged as before; a silent source's own
    /// "unreachable" alert is observed, not carried.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> SilentSources { get; init; } = [];

    /// <summary>
    /// Which source an entity belongs to, or null when it is not known.
    /// Needed only with <see cref="SilentSources"/>.
    /// </summary>
    public Func<EntityId, string?>? SourceOf { get; init; }

    /// <summary>
    /// Fingerprints a rule could not evaluate this cycle.
    /// </summary>
    /// <remarks>
    /// A rule that could not read its input for one entity says so here
    /// rather than going quiet about it: a stored instance with one of these
    /// fingerprints that was not observed is carried forward unchanged, like
    /// one belonging to a silent source. The rule still has to report the
    /// failure itself — carrying forward keeps the finding, it does not
    /// explain why it stopped being rechecked.
    /// </remarks>
    public IReadOnlyCollection<AlertFingerprint> Unevaluated { get; init; } = [];
}

/// <summary>What the caller must persist and act on.</summary>
public sealed record AlertReconciliationResult
{
    /// <summary>The full set of instances after this cycle.</summary>
    /// <remarks>
    /// Every one of them carries <see cref="AlertReconciliationRequest.Scope"/>,
    /// whatever the definition it came from said and whatever the stored
    /// instance said before. A store may therefore file this result exactly as
    /// it is given it, and must: a store that stamps is a second opinion on a
    /// question that now has one answer, and a second opinion is only ever
    /// noticed when it differs.
    /// </remarks>
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
/// <para>
/// For the same reason it is also the single place an alert's scope is
/// decided. It knows the scope — the request names it — and it produces every
/// instance and every flap history the rest of the product will ever see, so
/// nothing downstream needs to know how to fill the field in, and nothing
/// upstream needs to remember to.
/// </para>
/// </remarks>
public static class AlertReconciler
{
    public static AlertReconciliationResult Reconcile(AlertReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Blank is the value the defect actually had, so it is the one worth
        // refusing. `required` stops a caller forgetting the field; this stops
        // a caller passing a scope it never worked out, which would file real
        // alerts under a slice no cycle ever reconciles — they would be
        // invisible in every inbox and never resolve.
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope);

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

        var carried = CarriedForward(request);

        // Count this cycle's cessations before deciding what is flapping, so a
        // signal that stops on this very cycle is judged on current evidence.
        foreach (var (fingerprint, instance) in stored)
        {
            // Derived alerts are excluded: tracking the instability of the
            // instability detector recurses and tells an operator nothing.
            // A carried instance did not cease; nobody looked.
            if (instance.IsDerived ||
                observed.ContainsKey(fingerprint) ||
                carried(instance) ||
                !AlertLifecycle.OnAbsent(instance, request.NowUtc).CeasedFiring)
            {
                continue;
            }

            var history = flaps.TryGetValue(fingerprint, out var existing)
                ? existing
                : new FlapHistory
                {
                    Fingerprint = fingerprint,
                    ObjectName = instance.Title,

                    // The scope being reconciled, not the one on the instance
                    // that ceased. They are the same value — the instance was
                    // read from this scope's slice — but only one of them is
                    // the answer to "where does the derived alert belong", and
                    // reading it off the instance is how this field came to be
                    // copied from a field that was itself sometimes empty.
                    Scope = request.Scope,
                };

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

            // Not observed because it was not looked for: kept exactly as it
            // was. Resolving it would say "the problem went away" when the
            // truth is "we could not look", and the two lead to opposite
            // actions — the second is already its own alert.
            if (carried(instance))
            {
                next[fingerprint] = instance;
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

        // Stamped once, on the way out, over everything: what a collector
        // reported, what a rule found, what a guarded write failed to do, what
        // flap detection derived, and what was already stored. Applied here
        // rather than to the observations on the way in because this is the
        // last point at which anything is produced — an instance that reaches
        // a caller without passing through this line does not exist.
        var instances = next.Values
            .Select(i => i with { Scope = request.Scope })
            .ToList();

        return new AlertReconciliationResult
        {
            Instances = instances,
            Retired = retired,
            ToNotify = [.. instances.Where(i => i.ShouldNotify)],
            FlapHistories = [.. flaps.Values.Select(f => f with { Scope = request.Scope })],
        };
    }

    /// <summary>
    /// Whether a stored instance that was not observed was simply not looked
    /// for this cycle. See <see cref="AlertReconciliationRequest.SilentSources"/>
    /// and <see cref="AlertReconciliationRequest.Unevaluated"/>.
    /// </summary>
    private static Func<AlertInstance, bool> CarriedForward(AlertReconciliationRequest request)
    {
        var unevaluated = request.Unevaluated.ToHashSet();
        var silent = new HashSet<string>(request.SilentSources, StringComparer.Ordinal);
        var sourceOf = request.SourceOf;

        if (unevaluated.Count == 0 && (silent.Count == 0 || sourceOf is null))
        {
            return static _ => false;
        }

        return instance =>
            unevaluated.Contains(instance.Fingerprint) ||
            (silent.Count > 0 &&
             sourceOf is not null &&
             instance.Entity is { } entity &&
             sourceOf(entity) is { } source &&
             silent.Contains(source));
    }
}
