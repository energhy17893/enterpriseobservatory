using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>Which cycle a rule belongs to, and so which cycle reconciles its alerts.</summary>
/// <remarks>
/// Reconciliation treats what it is given as the whole truth, so a rule's
/// alerts must be reconciled in the scope that evaluated them or the other
/// cycle would resolve them. See <see cref="Alerts.AlertScopes"/>.
/// </remarks>
public enum RuleScope
{
    /// <summary>Runs on the metric rhythm, over this cycle's samples.</summary>
    Metric,

    /// <summary>Runs on the inventory rhythm, over the graph this cycle merged.</summary>
    Inventory,
}

/// <summary>
/// One analysis rule, in the one shape the cycle knows how to run.
/// </summary>
/// <remarks>
/// <para>
/// The rules themselves stay static and pure — each has an <c>Evaluate</c>
/// that takes exactly what it reasons about, and its tests call that. An
/// implementation of this interface is a thin adapter: it picks the rule's
/// inputs out of a <see cref="RuleContext"/> and nothing else. Anything more
/// than that belongs in the rule, where its tests can see it.
/// </para>
/// <para>
/// The cycle runs every rule through <see cref="GuardedRule"/>, so an adapter
/// does not guard itself.
/// </para>
/// </remarks>
public interface IAnalysisRule
{
    /// <summary>
    /// Stable across releases: <see cref="GuardedRule"/> puts it in the
    /// fingerprint of the alert that says this rule failed.
    /// </summary>
    string RuleId { get; }

    RuleScope Scope { get; }

    IReadOnlyList<AlertDefinition> Evaluate(RuleContext context);
}

/// <summary>
/// Everything a rule may read, handed to it by the cycle.
/// </summary>
/// <remarks>
/// <para>
/// History is reachable only through read-only ports: a rule may ask what was
/// recorded, and cannot record, prune, compact or move a cursor.
/// </para>
/// <para>
/// The graph is read when a rule asks for it, not once for the cycle. A metric
/// rule reads the store's copy inside its own guard, so a store that throws
/// costs the rules that needed a graph and not the ones that did not.
/// </para>
/// </remarks>
public sealed record RuleContext
{
    /// <summary>This cycle's samples. Empty on the inventory rhythm, which has none.</summary>
    public IReadOnlyList<Observation> Observations { get; init; } = [];

    /// <summary>This cycle's inventory reads. Empty on the metric rhythm, which has none.</summary>
    public IReadOnlyList<InventorySnapshot> Snapshots { get; init; } = [];

    /// <summary>
    /// Produces the graph appropriate to the scope: the store's current copy on
    /// the metric rhythm, the graph this cycle just merged on the inventory one.
    /// </summary>
    public required Func<EntityGraph> ReadGraph { get; init; }

    /// <summary>The graph appropriate to the scope; see <see cref="ReadGraph"/>.</summary>
    public EntityGraph Graph => ReadGraph();

    public required DateTimeOffset NowUtc { get; init; }

    public required MonitoringOptions Options { get; init; }

    /// <summary>Recorded samples, for a rule that must compare with the past.</summary>
    public required ISeriesReader Series { get; init; }

    /// <summary>Collected vCenter events.</summary>
    public required IEventReader Events { get; init; }
}
