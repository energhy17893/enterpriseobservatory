using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>A catalogue control, and what this product can do with it.</summary>
public sealed record BoundControl
{
    public required ComplianceControl Control { get; init; }

    /// <summary>How the control is judged, or null when this product cannot judge it.</summary>
    public SettingCheck? Check { get; init; }

    /// <summary>
    /// Why the control is not evaluated at all. Set exactly when <see cref="Check"/> is null.
    /// </summary>
    public string? NotEvaluatedReason { get; init; }

    public bool IsEvaluated => Check is not null;
}

/// <summary>
/// Judges a catalogue against the estate.
/// </summary>
/// <remarks>
/// <para>
/// Pure: given the catalogue, the entities and what was concluded last time,
/// it returns what is concluded now. Storage, the clock and people's decisions
/// are all outside it, which is what lets its tests say exactly what a host
/// with an empty log target produces.
/// </para>
/// <para>
/// A control binds to a check by the setting the guide names, not by its id.
/// Ids change between editions — <c>esxi-8.logs-remote</c> is
/// <c>esx-9.log-forwarding</c> — and the setting does not, so the same check
/// serves both editions and a new one with no code change. Only where the
/// guide names no setting (<c>N/A</c>: services, time, switch security,
/// lockdown) does a check list the ids it answers.
/// </para>
/// </remarks>
public static class ComplianceEvaluation
{
    /// <summary>
    /// Pairs every control with a check, or with the reason there is none.
    /// </summary>
    /// <remarks>
    /// Every control comes back. A control this product cannot judge is
    /// carried as "not evaluated — no data collected" with its reason, never
    /// dropped: a catalogue that quietly shrank to what the product can read
    /// would report a compliance posture it never measured.
    /// </remarks>
    public static IReadOnlyList<BoundControl> Bind(
        ComplianceCatalogue catalogue, IReadOnlyList<SettingCheck>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var available = checks ?? SettingChecks.Default;

        return [.. catalogue.Controls.Select(control => BindOne(control, available))];
    }

    private static BoundControl BindOne(ComplianceControl control, IReadOnlyList<SettingCheck> checks)
    {
        var parameter = control.Parameter.Trim();

        // By the setting the guide names first; by id only for the controls
        // whose guide row names none (see SettingCheck.ControlIds).
        var check =
            checks.FirstOrDefault(c =>
                c.ReadsAdvancedSetting &&
                string.Equals(c.Setting, parameter, StringComparison.OrdinalIgnoreCase)) ??
            checks.FirstOrDefault(c => c.ControlIds.Contains(control.ControlId, StringComparer.Ordinal));

        if (check is null)
        {
            return new BoundControl
            {
                Control = control,
                NotEvaluatedReason = IsNamedSetting(parameter)
                    ? $"No data collected: this product does not read '{parameter}' yet."
                    : "No data collected: this control is not a single setting, and this product " +
                      "does not yet read what it asks about.",
            };
        }

        if (!AppliesToHosts(control))
        {
            return new BoundControl
            {
                Control = control,
                NotEvaluatedReason =
                    $"No data collected: '{parameter}' is read from hosts only, and this control " +
                    $"is about {Describe(control.Component)}.",
            };
        }

        return new BoundControl { Control = control, Check = check };
    }

    /// <summary>
    /// Evaluates every bound control against every live host.
    /// </summary>
    /// <param name="catalogue">What to judge against.</param>
    /// <param name="entities">The estate, as the inventory last read it.</param>
    /// <param name="previous">What was concluded last time, for dates and acceptances.</param>
    /// <param name="nowUtc">When this evaluation happens; the latest a finding can be stamped.</param>
    /// <param name="checks">The checks available; the defaults when null.</param>
    /// <param name="reportingSources">
    /// The sources that answered in the inventory cycle this evaluation
    /// follows, or null to treat every host's source as having answered. A
    /// host whose source is not among them is judged on the settings it last
    /// reported and its findings are marked <see cref="ComplianceFinding.Stale"/>.
    /// </param>
    /// <returns>
    /// One finding per evaluated control per live host. Controls with no check
    /// produce none — they are reported per control by <see cref="Bind"/>,
    /// because repeating "we do not read this" on every host would multiply one
    /// fact by the size of the estate.
    /// </returns>
    /// <remarks>
    /// Every finding is stamped with when its host was last read, not with
    /// <paramref name="nowUtc"/>. The graph keeps a silent vCenter's hosts as
    /// they were (see <see cref="EntityGraph.Merge"/>), so judging them again
    /// re-derives an old verdict from old settings; dating that verdict now
    /// would make a reading nobody took look current.
    /// </remarks>
    public static IReadOnlyList<ComplianceFinding> Evaluate(
        ComplianceCatalogue catalogue,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<ComplianceFinding> previous,
        DateTimeOffset nowUtc,
        IReadOnlyList<SettingCheck>? checks = null,
        IReadOnlyCollection<string>? reportingSources = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(previous);

        var reporting = reportingSources is null
            ? null
            : new HashSet<string>(reportingSources, StringComparer.Ordinal);

        var before = previous
            .Where(f => string.Equals(f.CatalogueRelease, catalogue.Release, StringComparison.Ordinal))
            .GroupBy(f => (f.ControlId, f.Entity))
            .ToDictionary(g => g.Key, g => g.First());

        var hosts = entities
            .Where(e => e.Kind == EntityKind.EsxiHost && e.ObservationState != ObservationState.Vanished)
            .ToList();

        var findings = new List<ComplianceFinding>();

        foreach (var bound in Bind(catalogue, checks).Where(b => b.IsEvaluated))
        {
            foreach (var host in hosts)
            {
                var stale = reporting is not null && !reporting.Contains(host.SourceInstanceId);

                // Never later than now: a collector whose clock runs ahead
                // must not date a reading in the future.
                var readAt = host.LastSeenUtc < nowUtc ? host.LastSeenUtc : nowUtc;

                var now = Judge(bound, host, catalogue.Release, readAt) with { Stale = stale };

                findings.Add(before.TryGetValue((now.ControlId, now.Entity), out var last)
                    ? Continue(last, now)
                    : now);
            }
        }

        return findings;
    }

    private static ComplianceFinding Judge(
        BoundControl bound, Entity host, string release, DateTimeOffset readAtUtc)
    {
        var check = bound.Check!;

        var finding = new ComplianceFinding
        {
            ControlId = bound.Control.ControlId,
            CatalogueRelease = release,
            Entity = host.Id,
            EntityName = host.DisplayName,
            Verdict = ComplianceVerdict.NotEvaluated,
            FirstSeenUtc = readAtUtc,
            LastEvaluatedUtc = readAtUtc,
        };

        // Absent is unread, and unread is not a verdict: whatever the host did
        // not report comes back not evaluated, with the reason.
        var judgement = SettingChecks.Judge(check, bound.Control, host);

        return finding with
        {
            Verdict = judgement.Verdict,
            Expected = judgement.Expected,
            Observed = judgement.Observed,
            Reason = judgement.Reason,
        };
    }

    /// <summary>
    /// Carries over what the last evaluation knew that this one cannot.
    /// </summary>
    /// <remarks>
    /// The first-seen date survives while the verdict holds and resets when it
    /// changes. An acceptance survives until the finding passes: somebody took
    /// the failure on, and a setting that is still wrong has not released them
    /// from it. Once it passes the acceptance has done its job, and a later
    /// regression is a new failure that nobody has seen yet.
    /// </remarks>
    private static ComplianceFinding Continue(ComplianceFinding last, ComplianceFinding now) =>
        now with
        {
            FirstSeenUtc = last.Verdict == now.Verdict ? last.FirstSeenUtc : now.FirstSeenUtc,
            Acceptance = now.Verdict == ComplianceVerdict.Passing ? null : last.Acceptance,
        };

    /// <summary>Whether the guide's parameter column names one setting this product could read.</summary>
    private static bool IsNamedSetting(string parameter) =>
        parameter.Length > 0 &&
        !parameter.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
        !parameter.Contains(',', StringComparison.Ordinal) &&
        !parameter.Contains('\n', StringComparison.Ordinal) &&
        !parameter.Contains(' ', StringComparison.Ordinal);

    /// <summary>
    /// Whether a control is about an ESX host.
    /// </summary>
    /// <remarks>
    /// Decided by the id's prefix, which both editions use consistently
    /// (<c>esxi-8.</c>, <c>esx-9.</c>). The component column cannot be used:
    /// the 9.1 edition files virtual machine and guest controls under
    /// <c>ESX</c> too.
    /// </remarks>
    private static bool AppliesToHosts(ComplianceControl control) =>
        control.ControlId.StartsWith("esx-", StringComparison.OrdinalIgnoreCase) ||
        control.ControlId.StartsWith("esxi-", StringComparison.OrdinalIgnoreCase);

    private static string Describe(string component) =>
        string.IsNullOrWhiteSpace(component) ? "something else" : component;
}
