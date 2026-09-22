namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>
/// Why nothing could be said about a condition this cycle (ADR-0026).
/// </summary>
/// <remarks>
/// Every value means "we did not look" or "we cannot tell". None of them is a
/// verdict about the estate, which is why none of them can resolve an alert.
/// </remarks>
public enum UnknownReason
{
    /// <summary>The rule said nothing about a subject it holds an alert for. The default.</summary>
    NotReported,

    /// <summary>The source did not answer this cycle ("kaynak cevap vermedi").</summary>
    SourceSilent,

    /// <summary>A counter or property is missing for this subject ("sayaç toplanmıyor").</summary>
    InputNotCollected,

    /// <summary>Too few points, no baseline, warm-up ("seri yetersiz").</summary>
    InsufficientSeries,

    /// <summary>The evidence is older than the scope allows ("girdi bayat").</summary>
    InputStale,

    /// <summary>The input is there but below the level the rule can judge at.</summary>
    NotJudgeable,

    /// <summary>The rule threw, or one subject's read threw.</summary>
    RuleFailed,
}

/// <summary>Why a condition is not there. Recorded in the history; never changes the count.</summary>
public enum AbsenceKind
{
    ConditionCleared,
    SubjectRemoved,
    Superseded,
    Expired,
}

/// <summary>
/// How many consecutive fresh absences resolve a confirmed alert (ADR-0026 point 2).
/// </summary>
/// <remarks>
/// Deliberately without a default on <c>IAnalysisRule</c>: a rule that does not
/// say how sure it must be before it calls a condition gone does not compile.
/// </remarks>
public sealed record ResolutionPolicy
{
    /// <summary>N, at least one.</summary>
    public required int ConsecutiveAbsent
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    }

    /// <summary>
    /// N = 1: a direct producer, which runs every cycle and is its own evidence
    /// (design note §1.2). The type says so: an instance with no rule id.
    /// </summary>
    public static ResolutionPolicy Immediate { get; } = new() { ConsecutiveAbsent = 1 };
}

/// <summary>A fresh statement that a condition is not there.</summary>
public sealed record AlertAbsence
{
    /// <summary>Time of the newest input the statement rests on.</summary>
    public required DateTimeOffset EvidenceAtUtc { get; init; }

    public AbsenceKind Because { get; init; } = AbsenceKind.ConditionCleared;
}

/// <summary>A statement that nothing could be said about a condition this cycle.</summary>
public sealed record AlertUnknown
{
    public required UnknownReason Reason { get; init; }

    /// <summary>What was missing, in words, e.g. "cpu.costop.summation not in this cycle's batch".</summary>
    public required string Detail { get; init; }
}
