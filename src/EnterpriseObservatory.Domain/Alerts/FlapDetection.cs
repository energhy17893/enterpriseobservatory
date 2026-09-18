namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>
/// When a signal is unstable enough to be a problem in its own right.
/// </summary>
public sealed record FlapPolicy
{
    /// <summary>How many times a problem may start and stop inside the window.</summary>
    public int Threshold { get; init; } = 5;

    /// <summary>The sliding window transitions are counted over.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(1);

    public static FlapPolicy Default { get; } = new();
}

/// <summary>
/// How often one problem has started and stopped recently.
/// </summary>
/// <remarks>
/// <para>
/// This survives the retirement of the alert instances themselves, which is the
/// whole point: a problem that appears and vanishes fifty times a day leaves no
/// instance behind, so without this it would be perfectly invisible — even
/// though it is very likely the most important thing happening.
/// </para>
/// <para>
/// Suppressing flapping noise and losing the fact that something is flapping
/// are two different things. The first is desirable, the second is a bug.
/// </para>
/// </remarks>
public sealed record FlapHistory
{
    public required AlertFingerprint Fingerprint { get; init; }

    /// <summary>Carried so the derived alert can name what is unstable.</summary>
    public required string ObjectName { get; init; }

    /// <summary>
    /// The scope of the alert whose instability this records.
    /// </summary>
    /// <remarks>
    /// Carried so the derived alert lands in the same evaluation as the thing
    /// it describes. In any other scope it would be raised by one cycle and
    /// resolved by the next. See <see cref="AlertDefinition.Scope"/>.
    /// </remarks>
    public string Scope { get; init; } = string.Empty;

    /// <summary>When the problem stopped firing, most recent last.</summary>
    public IReadOnlyList<DateTimeOffset> CeasedAtUtc { get; init; } = [];

    /// <summary>
    /// Records that the problem stopped firing, dropping anything that has
    /// aged out of the window.
    /// </summary>
    public FlapHistory RecordCeased(DateTimeOffset atUtc, FlapPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var cutoff = atUtc - policy.Window;
        var kept = CeasedAtUtc.Where(t => t > cutoff).Append(atUtc).ToList();

        return this with { CeasedAtUtc = kept };
    }

    /// <summary>How many times the problem stopped firing inside the window.</summary>
    public int CountInWindow(DateTimeOffset nowUtc, FlapPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var cutoff = nowUtc - policy.Window;
        return CeasedAtUtc.Count(t => t > cutoff);
    }

    public bool IsFlapping(DateTimeOffset nowUtc, FlapPolicy policy) =>
        CountInWindow(nowUtc, policy) >= policy.Threshold;
}

/// <summary>Turns instability into an alert of its own.</summary>
public static class FlapDetection
{
    /// <summary>The source name derived flapping alerts are attributed to.</summary>
    public const string Source = "platform";

    /// <summary>
    /// Describes a signal as unstable, if it currently is.
    /// </summary>
    /// <returns>The derived alert, or null when the signal is steady enough.</returns>
    /// <remarks>
    /// <para>
    /// Deliberately a <em>different</em> alert rather than a variant of the
    /// original, because it tells the operator something different. "The PSU
    /// failed" is a hardware problem; "this PSU reading will not hold still"
    /// is a reliability problem, and the action is not the same.
    /// </para>
    /// <para>
    /// Called on every cycle, not once when the threshold is first crossed.
    /// The derived alert is fed through the ordinary lifecycle like any other
    /// observation, so it confirms under hysteresis, can be acknowledged, and
    /// resolves on its own once the transitions age out of the window. Raising
    /// it once and remembering that we had would instead leave it to be retired
    /// on the next cycle for not being observed.
    /// </para>
    /// <para>
    /// Severity is Warning rather than Critical: instability is worth knowing
    /// about, but the underlying condition — if it is real — will raise its own
    /// alert once it stops flapping.
    /// </para>
    /// </remarks>
    public static AlertDefinition? Evaluate(
        FlapHistory history,
        DateTimeOffset nowUtc,
        FlapPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        if (!history.IsFlapping(nowUtc, policy))
        {
            return null;
        }

        var count = history.CountInWindow(nowUtc, policy);

        return new AlertDefinition
        {
            Fingerprint = FingerprintFor(history.Fingerprint, history.ObjectName),
            Severity = AlertSeverity.Warning,
            Title = "Unstable signal",
            Description =
                $"This condition started and stopped {count} times within " +
                $"{policy.Window.TotalMinutes:0} minutes. Either the underlying fault is " +
                "intermittent or the threshold that produces it is set too close to normal " +
                "operating values.",
            Category = "Reliability",
            Source = Source,
            Scope = history.Scope,
            IsDerived = true,
        };
    }

    /// <summary>
    /// The fingerprint of the derived alert, tied to the original so the two
    /// can be correlated but never merged.
    /// </summary>
    public static AlertFingerprint FingerprintFor(AlertFingerprint original, string objectName) =>
        AlertFingerprint.Create(
            Source,
            "Unstable signal",
            "Reliability",
            objectName,
            $"flapping:{original.Value}");
}
