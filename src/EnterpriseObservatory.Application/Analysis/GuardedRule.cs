using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>What a guarded rule produced: its verdicts, or the fact that it failed.</summary>
/// <param name="Verdicts">
/// The rule's verdicts. When it threw: an <see cref="UnknownReason.RuleFailed"/>
/// verdict for every alert it held, and nothing else.
/// </param>
/// <param name="Failures">"Analysis rule failed", when it threw; otherwise empty.</param>
public sealed record GuardedEvaluation(
    IReadOnlyList<SubjectVerdict> Verdicts,
    IReadOnlyList<AlertDefinition> Failures);

/// <summary>
/// Runs one analysis rule so that its failure costs only that rule.
/// </summary>
/// <remarks>
/// <para>
/// Collection sources have had this since the beginning — <c>SourceRunner</c>
/// catches per source, because one misbehaving integration must not end the
/// cycle for the rest — and storage has had it too. Rules had neither. A rule
/// that threw propagated all the way to the worker, which logged the cycle as
/// failed and moved on, losing that cycle's collection alerts and its
/// notifications along with the rule's own findings. A bug in a peer
/// comparison would have stopped the product reporting that a vCenter was
/// unreachable, which is a far more important thing to say.
/// </para>
/// <para>
/// The exception becomes an alert rather than a log line, for the reason
/// ADR-0005 gives and <c>StorageFailure</c> follows: "we are not analysing"
/// belongs in the product, not in a file on the server. Being an ordinary
/// alert also means it is reconciled in the same scope as everything else, so
/// it resolves by itself on the first cycle the rule survives.
/// </para>
/// <para>
/// Until ADR-0026 this had a cost it stated plainly: a rule that threw
/// reported nothing, reconciliation read nothing as "gone", and every alert the
/// rule held was resolved without being rechecked. Now a rule that throws
/// gives its held alerts <see cref="UnknownReason.RuleFailed"/>: they stay
/// open, marked stale, and the failure alert says why.
/// </para>
/// </remarks>
public static class GuardedRule
{
    public const string Category = "Configuration";

    /// <summary>The check id of the failure alert, the fingerprint's last segment.</summary>
    public const string FailedCheckId = "analysis-rule-failed";

    /// <summary>
    /// Evaluates <paramref name="rule"/>, or reports that it could not be.
    /// </summary>
    /// <param name="ruleId">
    /// Stable across releases: it is part of the fingerprint, so renaming it
    /// resolves the old alert and raises a new one for the same problem.
    /// </param>
    /// <param name="rule">The evaluation.</param>
    /// <param name="held">The alerts the rule holds, which stay open if it throws.</param>
    public static GuardedEvaluation Run(
        string ruleId,
        Func<IReadOnlyList<SubjectVerdict>> rule,
        IReadOnlyList<HeldAlert>? held = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(rule);

        try
        {
            return new GuardedEvaluation(rule(), []);
        }
        catch (OperationCanceledException)
        {
            // The cycle is shutting down, not the rule misbehaving. Turning
            // this into an alert would raise one on every restart, and an
            // alert that fires when nothing is wrong is one nobody reads.
            throw;
        }
#pragma warning disable CA1031 // Justified: see the remarks above. A rule is
        // arbitrary analysis code over vendor data, so it may throw anything —
        // and the one outcome that must not happen is the cycle ending.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var detail = $"'{ruleId}' threw {ex.GetType().Name}: {ex.Message}";

            return new GuardedEvaluation(
                [
                    .. (held ?? []).Select(h => new Unknown
                    {
                        Covers = [h.Fingerprint],
                        Entity = h.Entity,
                        Reason = UnknownReason.RuleFailed,
                        Detail = detail,
                    }),
                ],
                [Failed(ruleId, ex)]);
        }
    }

    private static AlertDefinition Failed(string ruleId, Exception error) =>
        new()
        {
            Fingerprint = AlertFingerprint.Create(
                "platform", Title, Category, ruleId, FailedCheckId),

            // Warning, not Critical, and for the same reason an unreachable
            // collector is: we do not know the estate is broken, only that we
            // have stopped looking at part of it. Claiming more would be
            // fabrication, and this alert is about our own blindness.
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description =
                $"The analysis rule '{ruleId}' threw {error.GetType().Name} and was skipped: " +
                $"{error.Message} Anything this rule would have found is not being reported, " +
                "and anything it had already reported is kept open and marked stale, because " +
                "nobody has rechecked it. The rest of this cycle ran normally.",
            Category = Category,
            Source = "platform",
            IsDerived = true,
        };

    private const string Title = "Analysis rule failed";
}
