using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>A catalogue control, and what this product can do with it.</summary>
public sealed record BoundControl
{
    public required ComplianceControl Control { get; init; }

    /// <summary>The release of the catalogue the control belongs to.</summary>
    public string CatalogueRelease { get; init; } = string.Empty;

    /// <summary>The name of the catalogue the control belongs to — the screen's "source".</summary>
    public string CatalogueName { get; init; } = string.Empty;

    /// <summary>How the control is judged, or null when this product cannot judge it.</summary>
    public IComplianceCheck? Check { get; init; }

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
    /// <param name="catalogue">What to bind.</param>
    /// <param name="checks">The setting checks a vendor guide binds to; the defaults when null.</param>
    /// <param name="checksById">
    /// The checks a catalogue that <see cref="ComplianceCatalogue.BindsById"/>
    /// binds to, by control id; none when null.
    /// </param>
    public static IReadOnlyList<BoundControl> Bind(
        ComplianceCatalogue catalogue,
        IReadOnlyList<SettingCheck>? checks = null,
        IReadOnlyDictionary<string, IComplianceCheck>? checksById = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var available = checks ?? SettingChecks.Default;

        return
        [
            .. catalogue.Controls.Select(control =>
                (catalogue.BindsById ? BindById(control, checksById) : BindOne(control, available)) with
                {
                    CatalogueRelease = catalogue.Release,
                    CatalogueName = catalogue.Name,
                }),
        ];
    }

    /// <summary>The product's own catalogue: a control id names its check directly.</summary>
    private static BoundControl BindById(
        ComplianceControl control, IReadOnlyDictionary<string, IComplianceCheck>? checksById) =>
        checksById is not null && checksById.TryGetValue(control.ControlId, out var check)
            ? new BoundControl { Control = control, Check = check }
            : new BoundControl
            {
                Control = control,
                NotEvaluatedReason =
                    $"No data collected: this product has no check for '{control.ControlId}' yet.",
            };

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

        return new BoundControl { Control = control, Check = new HostSettingCheck(check) };
    }

    /// <summary>
    /// Evaluates every bound control against every live entity of the kind its check judges.
    /// </summary>
    /// <param name="catalogue">What to judge against.</param>
    /// <param name="entities">The estate, as the inventory last read it.</param>
    /// <param name="previous">What was concluded last time, for dates and acceptances.</param>
    /// <param name="nowUtc">When this evaluation happens; the latest a finding can be stamped.</param>
    /// <param name="checks">The setting checks available; the defaults when null.</param>
    /// <param name="checksById">For a catalogue that binds by control id: its checks.</param>
    /// <param name="graph">The estate as a graph, for checks that look beyond their entity; built from <paramref name="entities"/> when null.</param>
    /// <param name="demand">Precomputed demand for N+1 checks; null when none.</param>
    /// <param name="silentNamespaces">
    /// Annotation namespaces none of whose sources answered this cycle (see
    /// <see cref="SilentNamespaces"/>). A check that reads one keeps its last
    /// findings, stale, exactly as a silent entity source does.
    /// </param>
    /// <param name="reportingSources">
    /// The sources that answered in the inventory cycle this evaluation
    /// follows, or null to treat every host's source as having answered. A
    /// host whose source is not among them keeps the findings it last had,
    /// marked <see cref="ComplianceFinding.Stale"/>; it is judged on what the
    /// graph holds only when it has none.
    /// </param>
    /// <returns>
    /// One finding per verdict each check returns — keyed by (control, entity,
    /// subject) — for every evaluated control on every live entity of its
    /// kind. A vendor-guide control goes through <see cref="HostSettingCheck"/>:
    /// one host verdict, no subject, exactly as before. Controls with no check
    /// produce none — they are reported per control by <see cref="Bind"/>,
    /// because repeating "we do not read this" on every host would multiply one
    /// fact by the size of the estate.
    /// </returns>
    /// <remarks>
    /// Every finding is stamped with when its host was last read, not with
    /// <paramref name="nowUtc"/>: dating an old reading now would make a
    /// reading nobody took look current. A silent source's findings are
    /// carried rather than re-judged: the graph keeps its hosts
    /// (see <see cref="EntityGraph.Merge"/>) but not their settings across
    /// a restart.
    /// </remarks>
    public static IReadOnlyList<ComplianceFinding> Evaluate(
        ComplianceCatalogue catalogue,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<ComplianceFinding> previous,
        DateTimeOffset nowUtc,
        IReadOnlyList<SettingCheck>? checks = null,
        IReadOnlyCollection<string>? reportingSources = null,
        IReadOnlyDictionary<string, IComplianceCheck>? checksById = null,
        EntityGraph? graph = null,
        DemandSnapshot? demand = null,
        IReadOnlyCollection<string>? silentNamespaces = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(previous);

        var reporting = reportingSources is null
            ? null
            : new HashSet<string>(reportingSources, StringComparer.Ordinal);

        var before = previous
            .Where(f => string.Equals(f.CatalogueRelease, catalogue.Release, StringComparison.Ordinal))
            .GroupBy(f => (f.ControlId, f.Entity, f.Subject))
            .ToDictionary(g => g.Key, g => g.First());

        var lastByEntity = before.Values.ToLookup(f => (f.ControlId, f.Entity));

        var live = entities
            .Where(e => e.ObservationState != ObservationState.Vanished)
            .ToList();

        var context = new CheckContext
        {
            Graph = graph ?? new EntityGraph
            {
                Entities = entities
                    .GroupBy(e => e.Id)
                    .ToDictionary(g => g.Key, g => g.First()),
            },
            NowUtc = nowUtc,
            Demand = demand,
        };

        var findings = new List<ComplianceFinding>();

        foreach (var bound in Bind(catalogue, checks, checksById).Where(b => b.IsEvaluated))
        {
            var check = bound.Check!;

            foreach (var entity in live.Where(e => e.Kind == check.AppliesTo))
            {
                var stale = reporting is not null && !reporting.Contains(entity.SourceInstanceId);

                // A silent source keeps its last state (ADR-0026 §3). Not
                // re-judged: entity settings are not stored, so after a
                // restart the graph holds none, and judging them would turn
                // every verdict into "not evaluated" and back (24 Sep 2026).
                if (stale && lastByEntity[(bound.Control.ControlId, entity.Id)].ToList() is { Count: > 0 } kept)
                {
                    findings.AddRange(kept.Select(f => f with { Stale = true }));
                    continue;
                }

                // Never later than now: a collector whose clock runs ahead
                // must not date a reading in the future.
                var readAt = entity.LastSeenUtc < nowUtc ? entity.LastSeenUtc : nowUtc;

                foreach (var verdict in Distinct(check.Judge(bound.Control, entity, context)))
                {
                    var now = Finding(bound, entity, verdict, catalogue.Release, readAt) with { Stale = stale };

                    findings.Add(before.TryGetValue((now.ControlId, now.Entity, now.Subject), out var last)
                        ? Continue(last, now)
                        : now);
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// The annotation namespaces with no answering source this cycle.
    /// </summary>
    /// <remarks>
    /// A namespace is its connection kind (<see cref="ConnectionKinds"/>).
    /// Only enabled connections count: one disabled or removed has nothing
    /// left to wait for, and counting it silent would freeze its checks on
    /// their last verdict forever.
    /// </remarks>
    public static IReadOnlyList<string> SilentNamespaces(
        IEnumerable<SourceConnection> connections, IReadOnlyCollection<string> reportingSources)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(reportingSources);

        return
        [
            .. connections
                .Where(c => c.IsEnabled)
                .GroupBy(c => c.Kind, StringComparer.Ordinal)
                .Where(g => !g.Any(c => reportingSources.Contains(c.InstanceId, StringComparer.Ordinal)))
                .Select(g => g.Key),
        ];
    }

    /// <summary>One verdict per subject: a check that repeats one is taken at its first word.</summary>
    private static IEnumerable<CheckVerdict> Distinct(IReadOnlyList<CheckVerdict> verdicts) =>
        verdicts.GroupBy(v => v.Subject ?? string.Empty, StringComparer.Ordinal).Select(g => g.First());

    private static ComplianceFinding Finding(
        BoundControl bound, Entity entity, CheckVerdict verdict, string release, DateTimeOffset readAtUtc) => new()
        {
            ControlId = bound.Control.ControlId,
            CatalogueRelease = release,
            Entity = entity.Id,
            EntityName = entity.DisplayName,
            Subject = verdict.Subject ?? string.Empty,
            SubjectLabel = verdict.SubjectLabel,
            Verdict = verdict.Verdict,
            Expected = verdict.Expected,
            Observed = verdict.Observed,

            // Absent is unread, and unread is not a verdict: whatever was not
            // read comes back not evaluated, and always with a reason.
            Reason = verdict.Verdict == ComplianceVerdict.NotEvaluated && string.IsNullOrWhiteSpace(verdict.Reason)
                ? "The check did not say why it could not conclude."
                : verdict.Reason,
            FirstSeenUtc = readAtUtc,
            LastEvaluatedUtc = readAtUtc,
        };

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
