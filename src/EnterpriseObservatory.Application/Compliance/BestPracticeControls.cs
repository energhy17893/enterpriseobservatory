using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The control ids of the product's own best-practice catalogue, <c>eo-bestpractice</c>.
/// </summary>
/// <remarks>
/// Same rules as <see cref="ContinuityControls"/>: one control per distinct
/// finding kind, an id never changes meaning. P3a judges data already
/// collected; P3b adds new collected paths and more controls.
/// </remarks>
public static class BestPracticeControls
{
    /// <summary>VM, subject <c>''</c>: the memory limit is below the configured memory.</summary>
    public const string MemoryLimitBelowConfigured = "eo-bp.memory-limit";

    /// <summary>vCenter, subject = adapter type: legacy virtual adapters are in use.</summary>
    public const string LegacyVirtualAdapters = "eo-bp.legacy-adapters";

    private const string PerformanceGuide = "vSphere 8.0 U3 Performance Best Practices guide";

    /// <summary>
    /// Every best-practice check production registers, in catalogue order.
    /// </summary>
    public static IReadOnlyList<BestPracticeCheck> All { get; } =
    [
        Check(MemoryLimitBelowConfigured, "Virtual Machine", "Memory limit is not below configured memory",
            PerformanceGuide, new MemoryLimitCheck()),
        Check(LegacyVirtualAdapters, "Virtual Machine",
            "No legacy E1000/E1000e network adapters or LSI Logic Parallel SCSI controllers",
            PerformanceGuide + "; VMware KB 438023 (rightsizing)", new LegacyVirtualAdapterCheck()),
    ];

    private static BestPracticeCheck Check(
        string id, string component, string title, string source, IComplianceCheck check) =>
        new(new ComplianceControl
        {
            ControlId = id,
            Component = component,
            Title = title,
            Parameter = "N/A",
            Source = source,
        }, check);
}
