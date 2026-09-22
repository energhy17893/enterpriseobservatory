using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// What one rule concluded about one subject in one evaluation (ADR-0026).
/// </summary>
/// <remarks>
/// <para>
/// Exactly three kinds, and the base is closed so a fourth cannot be added
/// outside this file: <see cref="ConditionPresent"/>,
/// <see cref="ConditionAbsent"/> and <see cref="Unknown"/>.
/// </para>
/// <para>
/// A fingerprint a rule holds an alert for and gives no verdict about is
/// <see cref="UnknownReason.NotReported"/> — not absent. That is the flipped
/// default the ADR is about: forgetting to say something now keeps an alarm
/// open instead of resolving it.
/// </para>
/// </remarks>
public abstract record SubjectVerdict
{
    private protected SubjectVerdict()
    {
    }

    /// <summary>
    /// Every fingerprint this verdict speaks for. A fingerprint listed here
    /// and not raised by a <see cref="ConditionPresent"/> is absent.
    /// </summary>
    public required IReadOnlyList<AlertFingerprint> Covers { get; init; }

    /// <summary>The entity the subject belongs to, when there is one. Used by the source clamp.</summary>
    public EntityId? Entity { get; init; }
}

/// <summary>The condition is there, on fresh evidence.</summary>
public sealed record ConditionPresent : SubjectVerdict
{
    /// <summary>One or more; each fingerprint must be in <see cref="SubjectVerdict.Covers"/>.</summary>
    public required IReadOnlyList<AlertDefinition> Alerts { get; init; }

    /// <summary>Time of the newest input this verdict rests on.</summary>
    public required DateTimeOffset EvidenceAtUtc { get; init; }
}

/// <summary>The condition is not there, on fresh evidence. The only verdict that can resolve.</summary>
public sealed record ConditionAbsent : SubjectVerdict
{
    public required DateTimeOffset EvidenceAtUtc { get; init; }

    /// <summary>Why it is not there. The history records it; it never changes the count.</summary>
    public AbsenceKind Because { get; init; } = AbsenceKind.ConditionCleared;

    /// <summary>
    /// N for these fingerprints when it differs from the rule's own, as the
    /// over-commit alert of <c>datastore-time-to-full</c> does (2 where filling is 3).
    /// </summary>
    public ResolutionPolicy? Resolution { get; init; }
}

/// <summary>Nothing could be said about the subject this cycle. Never opens, never resolves.</summary>
public sealed record Unknown : SubjectVerdict
{
    public required UnknownReason Reason { get; init; }

    /// <summary>What was missing, e.g. "cpu.costop.summation not in this cycle's batch".</summary>
    public required string Detail { get; init; }
}

/// <summary>An alert a rule holds: what <see cref="RuleContext.HeldBy"/> offers, read-only.</summary>
public sealed record HeldAlert(AlertFingerprint Fingerprint, EntityId? Entity);

/// <summary>
/// One rule's verdicts for one cycle, with the N that resolves its alerts.
/// </summary>
/// <param name="RuleId">The rule; stamped on every instance it raises.</param>
/// <param name="Resolution">The rule's <see cref="IAnalysisRule.Resolution"/>.</param>
/// <param name="Verdicts">What it concluded. Empty means nothing is known, and resolves nothing.</param>
public sealed record RuleEvaluation(
    string RuleId,
    ResolutionPolicy Resolution,
    IReadOnlyList<SubjectVerdict> Verdicts);

/// <summary>
/// Turns a rule that still reasons in two values into verdicts, mechanically.
/// </summary>
/// <remarks>
/// <para>
/// The first step of ADR-0026's conversion, kept deliberately dumb: what the
/// rule raised is <see cref="ConditionPresent"/>, and every alert it holds that
/// it did not raise is <see cref="ConditionAbsent"/> — today's reading, now
/// behind N and the reconciler's clamps. A rule that later learns to say
/// what it could not judge stops using this and returns its own
/// <see cref="Unknown"/> verdicts (design note §3).
/// </para>
/// <para>
/// Evidence is the cycle's time: the inputs a two-valued rule reads are this
/// cycle's, and the source clamp catches the ones that are not.
/// </para>
/// </remarks>
public static class TwoValuedVerdicts
{
    /// <param name="context">The cycle's context; its <see cref="RuleContext.HeldBy"/> names the held alerts.</param>
    /// <param name="ruleId">The rule whose held alerts are judged.</param>
    /// <param name="raised">What the rule raised this cycle.</param>
    /// <param name="unevaluated">
    /// Fingerprints the rule could not evaluate this cycle: <see cref="UnknownReason.RuleFailed"/>.
    /// </param>
    /// <param name="resolutionFor">An N for one fingerprint that differs from the rule's; null keeps the rule's.</param>
    public static IReadOnlyList<SubjectVerdict> From(
        RuleContext context,
        string ruleId,
        IEnumerable<AlertDefinition> raised,
        IReadOnlyCollection<AlertFingerprint>? unevaluated = null,
        Func<AlertFingerprint, ResolutionPolicy?>? resolutionFor = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(raised);

        var verdicts = new List<SubjectVerdict>();
        var present = new HashSet<AlertFingerprint>();

        foreach (var alert in raised)
        {
            // Info never enters the lifecycle, so an Info reading is not a
            // present condition: a held alert that eased to Info is absent,
            // as it always was.
            if (!AlertLifecycle.EntersLifecycle(alert.Severity))
            {
                continue;
            }

            present.Add(alert.Fingerprint);

            verdicts.Add(new ConditionPresent
            {
                Covers = [alert.Fingerprint],
                Alerts = [alert],
                Entity = alert.Entity,
                EvidenceAtUtc = context.NowUtc,
            });
        }

        var blind = unevaluated is null ? [] : unevaluated.ToHashSet();

        foreach (var held in context.HeldBy(ruleId))
        {
            if (present.Contains(held.Fingerprint))
            {
                continue;
            }

            verdicts.Add(blind.Contains(held.Fingerprint)
                ? new Unknown
                {
                    Covers = [held.Fingerprint],
                    Entity = held.Entity,
                    Reason = UnknownReason.RuleFailed,
                    Detail = $"'{ruleId}' could not read its input for this subject",
                }
                : new ConditionAbsent
                {
                    Covers = [held.Fingerprint],
                    Entity = held.Entity,
                    EvidenceAtUtc = context.NowUtc,
                    Resolution = resolutionFor?.Invoke(held.Fingerprint),
                });
        }

        return verdicts;
    }
}
