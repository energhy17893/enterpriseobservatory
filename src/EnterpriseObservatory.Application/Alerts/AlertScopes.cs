namespace EnterpriseObservatory.Application.Alerts;

/// <summary>
/// The evaluations that own alerts.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation takes what it is given as the complete truth and resolves
/// anything stored but absent. That is only sound within a single evaluation,
/// and the product has two running on different clocks — inventory every few
/// minutes, metrics every few seconds. Without a scope the faster one would
/// silently resolve everything the slower one raised.
/// </para>
/// <para>
/// Scoped by role rather than by source. A per-source scope would leave the
/// alerts of a removed vCenter permanently unreconciled, since no cycle would
/// ever evaluate that scope again; the two roles always exist, so nothing is
/// ever orphaned.
/// </para>
/// </remarks>
public static class AlertScopes
{
    /// <summary>Alerts decided by the inventory cycle.</summary>
    public const string Inventory = "inventory";

    /// <summary>Alerts decided by the metric cycle.</summary>
    public const string Observation = "observation";
}
