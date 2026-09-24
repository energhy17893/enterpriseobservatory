using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>What a check concluded about one subject of one entity.</summary>
public sealed record CheckVerdict
{
    /// <summary>
    /// The thing the operator will fix, or empty for the entity itself.
    /// See <see cref="ComplianceFinding.Subject"/>.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>How the subject is shown; display only, never identity.</summary>
    public string? SubjectLabel { get; init; }

    public required ComplianceVerdict Verdict { get; init; }

    public required string Expected { get; init; }

    public string? Observed { get; init; }

    /// <summary>Why nothing was concluded; required for <see cref="ComplianceVerdict.NotEvaluated"/>.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Estate demand, precomputed outside the evaluation so it stays pure.
/// </summary>
/// <remarks>
/// Filled by <see cref="ContinuityDemand.Take"/> from the series store, the
/// same data the N+1 alarm read. The N+1 checks read its age and each
/// cluster's history — too little or stale history is <c>NotEvaluated</c>
/// with the reason, never a <c>Passing</c> from old numbers. The thresholds
/// it was taken with travel with it, so the check judges by the same policy
/// the numbers were computed under.
/// </remarks>
public sealed record DemandSnapshot
{
    /// <summary>When the snapshot was computed.</summary>
    public required DateTimeOffset TakenUtc { get; init; }

    /// <summary>Older than this, the snapshot is not judged at all.</summary>
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Less history than this, and "N+1 holds" is not said.</summary>
    public TimeSpan MinimumHistory { get; init; } = TimeSpan.FromDays(7);

    /// <summary>A forecast loss of headroom this close fails the check.</summary>
    public TimeSpan WarningWithin { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The post-failover utilisation ceiling the available figures were computed with.</summary>
    public double MaxUtilizationAfterFailover { get; init; } = 0.9;

    /// <summary>
    /// Every cluster with at least two hosts in service, by id. A cluster
    /// missing here has fewer, and N+1 cannot be asked of it.
    /// </summary>
    public IReadOnlyDictionary<EntityId, ClusterDemand> Clusters { get; init; } =
        new Dictionary<EntityId, ClusterDemand>();
}

/// <summary>One cluster's demand for N+1, in host-equivalents.</summary>
public sealed record ClusterDemand
{
    public required int HostCount { get; init; }

    public required ResourceDemand Cpu { get; init; }

    public required ResourceDemand Memory { get; init; }

    /// <summary>Why the history could not be read, when it could not; the resources are then not judged.</summary>
    public string? Unreadable { get; init; }
}

/// <summary>One resource's demand, the headroom it is judged against, and the history behind it.</summary>
public sealed record ResourceDemand
{
    /// <summary>Current demand; null when a host in service had no current reading.</summary>
    public double? DemandHosts { get; init; }

    public required double AvailableAfterFailoverHosts { get; init; }

    /// <summary>
    /// How much of the aggregated history was read (ADR-0026's shared "enough
    /// history" test); null when there is none.
    /// </summary>
    public Analysis.HistoryCoverage? History { get; init; }

    /// <summary>The newest point of the history, or null when there is none.</summary>
    public DateTimeOffset? HistoryEndUtc { get; init; }

    /// <summary>When the headroom is lost, or why that cannot be said.</summary>
    public Analysis.TimeToFullResult? Date { get; init; }
}

/// <summary>Estate-wide facts a check may need beyond its own entity.</summary>
public sealed record CheckContext
{
    /// <summary>Placement, datastore marks, sharing.</summary>
    public required EntityGraph Graph { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>For N+1 (K2); null when none was computed.</summary>
    public DemandSnapshot? Demand { get; init; }
}

/// <summary>A control's check, independent of the kind of entity it judges.</summary>
public interface IComplianceCheck
{
    /// <summary>Which entities this check judges; the engine only hands it these.</summary>
    EntityKind AppliesTo { get; }

    /// <summary>
    /// One verdict per subject the check can see, Passing included.
    /// </summary>
    /// <remarks>
    /// Zero only when the subject cannot exist on this entity (a cluster with
    /// no DRS rules): that is not a pass, and no finding is written. A check
    /// that could not read its input returns one <see cref="ComplianceVerdict.NotEvaluated"/>
    /// verdict with the reason — "HA settings not read", never "HA disabled".
    /// </remarks>
    IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context);

    /// <summary>
    /// The annotation namespaces (ADR-0027) this check reads besides the
    /// entity's own settings; while none of a namespace's sources answers,
    /// its findings are carried stale rather than re-judged.
    /// </summary>
    IReadOnlyCollection<string> ReadsNamespaces => [];
}

/// <summary>
/// The vendor guide's check: one host setting, judged as it always was.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="SettingChecks.Judge(SettingCheck, ComplianceControl, Entity)"/>
/// with the same inputs and outputs: one verdict, no subject. The whole SCG
/// path goes through it unchanged.
/// </remarks>
public sealed record HostSettingCheck(SettingCheck Setting) : IComplianceCheck
{
    public EntityKind AppliesTo => EntityKind.EsxiHost;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        var judgement = SettingChecks.Judge(Setting, control, entity);

        return
        [
            new CheckVerdict
            {
                Verdict = judgement.Verdict,
                Expected = judgement.Expected,
                Observed = judgement.Observed,
                Reason = judgement.Reason,
            },
        ];
    }
}

/// <summary>One control of the product's own catalogue, with its check.</summary>
public sealed record ContinuityCheck(ComplianceControl Control, IComplianceCheck Check);

/// <summary>
/// The product's own catalogue, <c>eo-continuity</c>: continuity rules
/// judged as findings beside the vendor guide.
/// </summary>
/// <remarks>
/// <para>
/// Defined in code, never written into the vendored SCG file, and evaluated
/// independently of it: the store replaces one release's findings at a time,
/// so neither catalogue's evaluation can touch the other's rows.
/// </para>
/// <para>
/// Release rule: adding a control does not bump the release; changing what an
/// existing control id means gets a new control id instead. So the release
/// changes only when the catalogue is restructured, and acceptances — which
/// live on the finding row, per release — survive normal growth.
/// </para>
/// </remarks>
public static class ContinuityCatalogue
{
    public const string Release = "eo-continuity-1";

    public const string Name = "Enterprise Observatory continuity";

    /// <summary>
    /// The checks production registers: the M8 continuity rules (K2), see
    /// <see cref="ContinuityControls"/>.
    /// </summary>
    public static IReadOnlyList<ContinuityCheck> Production => ContinuityControls.All;

    /// <summary>The catalogue of the given checks' controls, in order.</summary>
    public static ComplianceCatalogue Build(IReadOnlyList<ContinuityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return new ComplianceCatalogue
        {
            Release = Release,
            Name = Name,
            Controls = [.. checks.Select(c => c.Control)],
            BindsById = true,
        };
    }

    /// <summary>The checks by control id, as <see cref="ComplianceEvaluation.Bind"/> binds them.</summary>
    public static IReadOnlyDictionary<string, IComplianceCheck> ChecksById(IReadOnlyList<ContinuityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return checks.ToDictionary(c => c.Control.ControlId, c => c.Check, StringComparer.Ordinal);
    }
}
