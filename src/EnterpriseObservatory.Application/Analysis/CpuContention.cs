using System.Globalization;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How much CPU waiting is worth saying, and who it is worth blaming.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is a default rather than a law, and none of them came
/// from a vendor. vROps has no CPU ready alert at all — <c>%RDY</c> appears
/// once, unquantified, inside "VM is idle" — and publishes no thresholds
/// anywhere: two numeric thresholds exist across its whole vCenter solution.
/// Dynatrace publishes its structure but not its levels. The only source that
/// commits to a figure is a Datadog blog post naming 5% and attributing it to
/// VMware, which is a blog and not documentation. So there was nothing to
/// copy, and the reasoning for each value is written beside it instead.
/// </para>
/// <para>
/// The counters are named here rather than compiled in, for the same reason
/// <see cref="CounterValue.IsFaultCount"/> is declared by the collector: the
/// application layer may not know what a vim25 counter is called. Holding the
/// names as policy is the weaker version of that — they are data an operator
/// can change, not a dependency on vSphere — and it is what could be done
/// without altering the domain. The stronger fix is a declared meaning on
/// <see cref="CounterValue"/>, which would let a Hyper-V or KVM collector
/// arrive without touching this file; see the remarks on
/// <see cref="CpuContention"/>.
/// </para>
/// </remarks>
public sealed record CpuContentionPolicy
{
    /// <summary>
    /// The ready percentage below which no verdict is reached about a VM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten percent of wall clock spent waiting for a physical core. This is
    /// the gate that keeps an idle VM quiet: a machine doing nothing can sit
    /// at a large <em>multiple</em> of its siblings while the absolute time it
    /// loses is a rounding error, and an alert about that teaches an operator
    /// to ignore the next one.
    /// </para>
    /// <para>
    /// Ten rather than the blog's five, deliberately, and the choice is a
    /// statement of position rather than a disagreement about arithmetic: this
    /// product would rather be silent than confidently wrong, so the only
    /// figure anyone publishes sits <em>inside</em> the silent band rather than
    /// on its edge. A VM losing a tenth of its wall clock waiting to be
    /// scheduled is not arguable whichever number turns out to be right.
    /// </para>
    /// <para>
    /// It never fires anything on its own. It qualifies a comparison — either
    /// against the VM's siblings or as a count of victims on a saturated host —
    /// exactly as <c>PeerOutlierPolicy.MinimumMilliseconds</c> does. Its job is
    /// not to be the truth about ready time; it is to keep trivial absolute
    /// numbers out of a ratio.
    /// </para>
    /// </remarks>
    public double ReadyPercent { get; init; } = 10d;

    /// <summary>
    /// How many times its siblings' median a VM's ready must reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate that survives a deliberately overcommitted cluster. A
    /// lab built to be oversubscribed has every VM waiting, and a fixed
    /// threshold reports the whole estate; a VM is only news here if it is
    /// waiting much more than the machines it shares a host with.
    /// </para>
    /// <para>
    /// Three rather than the four <c>PeerOutliers</c> uses, because the two
    /// rules split the work between their gates differently. There the floor
    /// is five milliseconds, which a busy array clears all day, so the ratio
    /// carries the whole claim. Here the floor is already a strong absolute
    /// statement that few healthy VMs reach, and the ratio only has to
    /// separate "this VM" from "this host". At four it would start dismissing
    /// a genuine single victim on a moderately loaded host, which is precisely
    /// the machine somebody filed the ticket about.
    /// </para>
    /// </remarks>
    public double SiblingMultiple { get; init; } = 3d;

    /// <summary>
    /// The smallest sibling median that may be divided by.
    /// </summary>
    /// <remarks>
    /// One percent. Below that the ready percentage of a quiet VM is mostly
    /// scheduler quantisation, and dividing by it makes every neighbour an
    /// enormous multiple of nothing — the same failure
    /// <c>PeerOutlierPolicy.Multiple</c> describes when it floors a median of
    /// zero at one millisecond. With this floor in place, on a genuinely quiet
    /// host it is <see cref="ReadyPercent"/> that decides, not the ratio.
    /// <para>
    /// At the shipped defaults it cannot bind, and that is worth stating
    /// rather than discovering: <see cref="SiblingMultiple"/> times this is
    /// three, and nothing reaches a verdict under <see cref="ReadyPercent"/>
    /// at ten. Mutation testing found it dormant. It is kept because it stops
    /// being dormant the moment the ready floor is tuned down, which is
    /// precisely the configuration in which a sibling median of a fifth of a
    /// percent starts being divided by.
    /// </para>
    /// </remarks>
    public double SiblingFloorPercent { get; init; } = 1d;

    /// <summary>
    /// How many VMs must share a host before any of them can be its outlier.
    /// </summary>
    /// <remarks>
    /// Three, for the reason <c>PeerOutlierPolicy.MinimumVantagePoints</c>
    /// gives: two cannot say which of them is wrong. It is also what keeps the
    /// rule silent about a host running a single large VM, where "worse than
    /// its siblings" has no meaning at all and the honest answer is that
    /// nothing was compared.
    /// </remarks>
    public int MinimumSiblings { get; init; } = 3;

    /// <summary>
    /// The host CPU usage above which a host may be called saturated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eighty-five percent. Below the mid-eighties an ESXi scheduler still has
    /// headroom, and a VM waiting there is more likely held by its own CPU
    /// limit, a NUMA or affinity placement, or its own vCPU width than by a
    /// shortage of cores — so blaming the host would send somebody to the
    /// wrong console.
    /// </para>
    /// <para>
    /// Lower than the figure a sampling-based product would use, and for a
    /// reason worth stating: Dynatrace pairs its high-usage gate with "in 3 of
    /// 5 samples", and this product has exactly one sample per cycle because
    /// the vSphere parser keeps only the last point of the window. A single
    /// 95% reading is common and means nothing. The trade made here is to put
    /// the usage bar where one sample is still credible and to demand the
    /// corroboration from <em>victims</em> instead — see
    /// <see cref="MinimumWaitingVirtualMachines"/>.
    /// </para>
    /// </remarks>
    public double HostSaturationPercent { get; init; } = 85d;

    /// <summary>
    /// How many waiting VMs a host must have before the host is blamed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two, and this is the gate that stops the most valuable verdict in the
    /// rule from also being its most embarrassing. A host at 95% with nobody
    /// waiting is a host doing its job, not a problem, and a host at 95% with
    /// exactly one victim is that one VM's story told under the host's name.
    /// Two waiting machines is the smallest population that is not one VM's
    /// problem.
    /// </para>
    /// <para>
    /// It is also the evidence that replaces the sample gate this codebase
    /// cannot implement: several VMs agreeing at one instant is a different
    /// kind of corroboration from one VM agreeing with itself over five
    /// samples, but it is corroboration, and it is available today.
    /// </para>
    /// </remarks>
    public int MinimumWaitingVirtualMachines { get; init; } = 2;

    /// <summary>
    /// The co-stop percentage above which a VM is called over-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three percent, and lower than <see cref="ReadyPercent"/> on purpose:
    /// the two counters are not comparable in scale. Ready time is the whole
    /// host's contention landing on one machine and a few percent of it is
    /// ordinary. Co-stop is a direct measurement of one VM's own width being
    /// wrong — the time its vCPUs spent stopped waiting for their siblings to
    /// be placed — and a correctly sized VM accrues almost none of it even on
    /// a busy host.
    /// </para>
    /// <para>
    /// Three rather than one because a twenty-second window can hold a single
    /// scheduling event without anything being wrong; three percent of it
    /// cannot be one event.
    /// </para>
    /// </remarks>
    public double CoStopPercent { get; init; } = 3d;

    /// <summary>
    /// The ready percentage a VM must stay under to be called over-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate that makes the co-stop verdict mean something different from
    /// the ready one. High co-stop <em>with</em> high ready is a host that is
    /// short of CPU, and telling the owner to remove vCPUs from their
    /// application server would be advice that makes the estate worse. Only
    /// when the VM is waiting for its own vCPUs to line up and not for cores
    /// to exist is the answer "this machine is too wide".
    /// </para>
    /// <para>
    /// Defaulted to the same ten percent as <see cref="ReadyPercent"/>, so
    /// that out of the box the two VM verdicts cannot both fire, but kept as
    /// its own value so that raising one does not silently widen the other.
    /// </para>
    /// </remarks>
    public double CoStopReadyCeilingPercent { get; init; } = 10d;

    /// <summary>The counter carrying a VM's CPU ready time.</summary>
    /// <remarks>
    /// Policy rather than a constant so that a collector for another platform
    /// can be pointed at this rule without it being rewritten, and so that
    /// nothing in the application layer hard-codes a vendor's vocabulary. See
    /// the type's remarks.
    /// </remarks>
    public string ReadyCounter { get; init; } = "cpu.ready.summation";

    /// <summary>The counter carrying a VM's SMP co-scheduling wait.</summary>
    public string CoStopCounter { get; init; } = "cpu.costop.summation";

    /// <summary>The counter carrying a host's overall CPU usage, as a percentage.</summary>
    public string HostUsageCounter { get; init; } = "cpu.usage.average";

    public static CpuContentionPolicy Default { get; } = new();
}

/// <summary>
/// Says who is waiting for CPU, and whether the answer is a machine or a host.
/// </summary>
/// <remarks>
/// <para>
/// The first rule to use the graph. <c>FaultCounters</c> judges one number,
/// <c>PeerOutliers</c> compares several measurements of one entity; this one
/// compares <em>different entities</em> and needs <c>VM RunsOn Host</c> to
/// know which of them belong together. That edge already exists, so this is a
/// new question over old data rather than new collection — which is what the
/// product architecture §4 says noisy-neighbour detection would be.
/// </para>
/// <para>
/// Comparative and not a threshold, because §4 argues a threshold gives the
/// wrong answer here: the same ready percentage is meaningless on an empty
/// host and a catastrophe on a full one. What follows are three verdicts over
/// one grouping, and they are one rule because they must be mutually
/// consistent — deciding "this VM" instead of "this host" requires having
/// computed both, and two rules could raise both at once about the same
/// waiting.
/// </para>
/// <para>
/// The verdicts, in the order they are decided:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>The host is short of CPU.</b> The host's own usage is high
/// <em>and</em> more than one of its VMs is waiting. Both halves are
/// required, and that structure is taken from Dynatrace's
/// <c>esxiHighCpuSaturation</c>, which is an AND of host usage, VM ready and
/// a CPU peak rather than a level on any one of them. The insight worth
/// borrowing is that a host at 95% with nobody waiting is a host doing its
/// job. This is the most valuable thing the rule can say, because it points
/// at one fix for many victims.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>This VM is waiting far more than its neighbours.</b> Only considered
/// when the host verdict did not fire, because on a saturated host the VM is
/// a symptom and naming it would send somebody to resize a machine that is
/// merely unlucky.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>This VM has too many vCPUs.</b> High co-stop with low ready. A separate
/// verdict rather than a condition on the first two, because the interesting
/// case is precisely the one an AND would erase: a VM losing time to its own
/// width while the host has cores to spare. A separate <em>rule</em> was
/// rejected for the opposite reason — it would need the same grouping and the
/// same silences, and could contradict the ready verdict about the same
/// machine with no way to order them.
/// </description>
/// </item>
/// </list>
/// <para>
/// What it deliberately does not say. It never reports "the cluster is
/// unbalanced" — that is a rung further down §4's ladder and needs
/// <c>PartOf</c> rather than <c>RunsOn</c>. It never converts ready time into
/// a per-vCPU figure, because the VM's vCPU count is not collected and
/// dividing by a number nobody has would be arithmetic dressed as a
/// measurement. And it says nothing at all about a VM it cannot place on a
/// host, or about a host whose own usage did not arrive: not looking and
/// looking and finding nothing are different answers, and only the second one
/// is silence earned.
/// </para>
/// <para>
/// One known false positive, stated rather than hidden. A VM held back by its
/// own configured CPU limit shows the same waiting as one held back by a busy
/// host, and this rule would call it a victim of the host. Dynatrace separates
/// the two with <c>guestCpuLimitReached</c>, which needs the VM's usage in MHz
/// against its configured limit; neither <c>cpu.usagemhz.average</c> nor
/// <c>cpu.maxlimited.summation</c> is collected today, so the distinction
/// cannot be drawn here.
/// </para>
/// </remarks>
public static class CpuContention
{
    /// <summary>Shown beside a contention alert.</summary>
    public const string Category = "CPU contention";

    /// <summary>
    /// Shown beside the co-stop alert, which is advice rather than a fault.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Category"/> because the two lead to different
    /// work. Contention is an incident somebody responds to now; an over-wide
    /// VM is a change somebody makes at the next maintenance window, and
    /// filing them together would bury one in the other.
    /// </remarks>
    public const string SizingCategory = "Right-sizing";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "cpu-contention";

    /// <summary>
    /// Who these alerts are attributed to.
    /// </summary>
    /// <remarks>
    /// The platform, not a collector, and the same for all three verdicts. A
    /// host verdict rests on several machines' readings rather than on one
    /// sample, and every one of these is something the product concluded
    /// rather than something a vendor reported — which is exactly what
    /// <c>IsDerived</c> means elsewhere. The value is part of the fingerprint,
    /// so it has to be stable above all else.
    /// </remarks>
    private const string Platform = "platform";

    private const string HostTitle = "Host is short of CPU";
    private const string VictimTitle = "Waiting far more than its neighbours";
    private const string WidthTitle = "More vCPUs than the host can place";

    /// <summary>
    /// Every CPU verdict this batch of observations supports.
    /// </summary>
    /// <param name="observations">One cycle's samples.</param>
    /// <param name="graph">
    /// The topology. Load-bearing rather than contextual: without
    /// <c>RunsOn</c> there are no siblings and no host, and the rule has
    /// nothing to compare. A VM the graph does not place is not judged.
    /// </param>
    /// <param name="policy">Defaults, and why they are what they are.</param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        EntityGraph graph,
        CpuContentionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(graph);

        var rules = policy ?? CpuContentionPolicy.Default;

        // Readings first, membership second, and the two never mix. These
        // dictionaries are keyed by whatever entity a sample was attributed
        // to and hold no opinion about which of those entities matter; only
        // GuestsByHost decides that, and it decides it once. A reading for
        // something the graph does not place is simply never looked up.
        var ready = PercentagesOf(observations, rules.ReadyCounter);
        var costop = PercentagesOf(observations, rules.CoStopCounter);
        var hostUsage = LevelsOf(observations, rules.HostUsageCounter);

        var alerts = new List<AlertDefinition>();

        foreach (var (host, guests) in GuestsByHost(graph))
        {
            // Only machines we actually measured. A powered-off VM reports
            // nothing, and counting it as a quiet sibling would drag the median
            // down and make every waiting machine look exceptional.
            var measured = guests.Where(ready.ContainsKey).ToList();

            var waiting = measured.Where(g => ready[g] >= rules.ReadyPercent).ToList();

            if (Saturated(host, waiting.Count, hostUsage, rules))
            {
                alerts.Add(HostIsShort(host, waiting, ready, hostUsage[host], rules));
            }
            else if (measured.Count >= rules.MinimumSiblings)
            {
                alerts.AddRange(Victims(host, measured, waiting, ready, rules));
            }

            // Width is judged per machine and needs no peers: a VM's vCPU count
            // is wrong or it is not, whoever it shares a host with. It is the
            // one verdict a host running a single VM can still receive.
            alerts.AddRange(
                guests.Where(g => TooWide(g, ready, costop, rules))
                      .Select(g => OverWide(g, ready[g], costop[g], rules)));
        }

        return alerts;
    }

    /// <summary>
    /// Whether the host itself is the answer rather than one of its guests.
    /// </summary>
    /// <remarks>
    /// A missing usage reading is not a low one. If the host's own counter did
    /// not arrive this cycle we have not established the host is busy, and the
    /// rule falls through to judging the guests — which is the honest order,
    /// because the guest verdict states what was measured and the host verdict
    /// would be asserting something that was not.
    /// </remarks>
    private static bool Saturated(
        EntityId host,
        int waiting,
        Dictionary<EntityId, double> hostUsage,
        CpuContentionPolicy rules) =>
        hostUsage.TryGetValue(host, out var usage) &&
        usage >= rules.HostSaturationPercent &&
        waiting >= rules.MinimumWaitingVirtualMachines;

    private static AlertDefinition HostIsShort(
        EntityId host,
        List<EntityId> waiting,
        Dictionary<EntityId, double> ready,
        double usage,
        CpuContentionPolicy rules) =>
        new()
        {
            // The host, and nothing about which guests are suffering. Which VM
            // is worst changes between cycles while the host stays short of
            // CPU, and a fingerprint carrying the victims would raise a fresh
            // alert every time the workload moved and lose the history of a
            // saturation that has run all afternoon. The same choice
            // PeerOutliers makes when it refuses to fingerprint on the worst
            // host.
            Fingerprint = AlertFingerprint.Create(
                Platform, HostTitle, Category, host.Value, "cpu-host-saturated"),
            Severity = AlertSeverity.Warning,
            Title = HostTitle,
            Description =
                $"This host is {Percent(usage)}% busy and {waiting.Count} of its virtual " +
                $"machines are waiting for a physical core (worst {Percent(Worst(waiting, ready))}% " +
                $"of the sample interval, threshold {Percent(rules.ReadyPercent)}%). Both halves " +
                "matter: a busy host with nobody waiting is a host doing its job. Several " +
                "machines waiting at once is the host rather than any one of them — move " +
                "workload off it, or reduce what is placed here, before resizing any single " +
                "virtual machine.",
            Category = Category,
            Source = Platform,
            Entity = host,
            IsDerived = true,
        };

    /// <summary>
    /// The guests that are waiting far more than the rest of their host.
    /// </summary>
    /// <remarks>
    /// Every qualifying guest rather than only the worst, unlike
    /// <c>PeerOutliers</c>, and the difference is in what is being compared.
    /// There the peers are several views of one resource and only one of them
    /// can be the odd view out. Here they are separate machines that are
    /// separately actionable, and two genuine noisy neighbours are two
    /// tickets. The ratio bounds how many there can be: a machine cannot be
    /// three times the median of a group most of which is also high.
    /// </remarks>
    private static IEnumerable<AlertDefinition> Victims(
        EntityId host,
        List<EntityId> measured,
        List<EntityId> waiting,
        Dictionary<EntityId, double> ready,
        CpuContentionPolicy rules)
    {
        foreach (var guest in waiting)
        {
            // The median of everyone else, so the candidate is not compared
            // with itself — one bad reading among three would otherwise drag
            // the median up and hide behind it.
            var peers = measured.Where(g => g != guest).Select(g => ready[g]).ToList();
            var median = Math.Max(Median(peers), rules.SiblingFloorPercent);

            if (ready[guest] < rules.SiblingMultiple * median)
            {
                continue;
            }

            yield return new AlertDefinition
            {
                // The machine and the counter, and deliberately not the host
                // it is running on. A VM that vMotions and keeps waiting is
                // the same problem, and a fingerprint carrying the host would
                // resolve the alert and open a new one at the moment the
                // evidence became most interesting — the machine took its
                // contention with it, which is a fact about the machine.
                Fingerprint = AlertFingerprint.Create(
                    Platform, VictimTitle, Category,
                    $"{guest.Value}/{rules.ReadyCounter}", "cpu-ready-outlier"),
                Severity = AlertSeverity.Warning,
                Title = VictimTitle,
                Description =
                    $"This virtual machine spent {Percent(ready[guest])}% of the sample " +
                    $"interval waiting for a physical core, against a median of " +
                    $"{Percent(median)}% across the {peers.Count} other measured machine(s) on " +
                    $"host '{host.Value}' (worst of them {Percent(peers.Count > 0 ? peers.Max() : 0d)}%). " +
                    "The host is not reported as short of CPU, so this is about this machine " +
                    "rather than its neighbours: check its CPU limit and shares, its vCPU " +
                    "count, and whether it is pinned.",
                Category = Category,
                Source = Platform,
                Entity = guest,
                IsDerived = true,
            };
        }
    }

    /// <summary>
    /// Whether a machine is losing time to its own width rather than to a
    /// shortage of cores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both readings are required. Without the ready figure there is no way to
    /// tell this verdict from the contention one, and guessing would produce
    /// advice — remove vCPUs — that makes a starved machine slower.
    /// </para>
    /// <para>
    /// Nothing checks the vCPU count, because it is not collected, and nothing
    /// needs to: co-stop is time a vCPU spent stopped waiting for its siblings
    /// and a uniprocessor machine has no siblings to wait for. A single-vCPU
    /// VM reads zero here by construction, so the floor is the same gate as a
    /// vCPU test would have been, and is one less thing to get wrong.
    /// </para>
    /// </remarks>
    private static bool TooWide(
        EntityId guest,
        Dictionary<EntityId, double> ready,
        Dictionary<EntityId, double> costop,
        CpuContentionPolicy rules) =>
        costop.TryGetValue(guest, out var stopped) &&
        ready.TryGetValue(guest, out var waiting) &&
        stopped >= rules.CoStopPercent &&
        waiting < rules.CoStopReadyCeilingPercent;

    private static AlertDefinition OverWide(
        EntityId guest, double ready, double costop, CpuContentionPolicy rules) =>
        new()
        {
            // The machine and the counter. The width travels with the machine,
            // so unlike the contention verdict there was never a host to leave
            // out — but the counter is still named, so that a machine which is
            // both over-wide and contended keeps two separate histories.
            Fingerprint = AlertFingerprint.Create(
                Platform, WidthTitle, SizingCategory,
                $"{guest.Value}/{rules.CoStopCounter}", "cpu-costop-oversized"),
            Severity = AlertSeverity.Warning,
            Title = WidthTitle,
            Description =
                $"This virtual machine lost {Percent(costop)}% of the sample interval to " +
                $"co-scheduling — its vCPUs stopped waiting for each other — while spending " +
                $"only {Percent(ready)}% waiting for a physical core. That combination is a " +
                "sizing problem rather than a busy host: the machine has more vCPUs than the " +
                "host can place at once. Removing vCPUs will make it faster, which is the " +
                "opposite of what the same symptom would call for on a saturated host.",
            Category = SizingCategory,
            Source = Platform,
            Entity = guest,
            IsDerived = true,
        };

    /// <summary>
    /// The VMs the graph places on each host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both ends must be in the graph, of the right kind, and not vanished. An
    /// edge to something we have stopped seeing is not evidence about anything
    /// that is running now.
    /// </para>
    /// <para>
    /// This is also as much protection as exists against a machine that has
    /// just powered on, whose first samples are a boot storm rather than
    /// contention. Inventory runs every few minutes and metrics every thirty
    /// seconds, so a VM that started a moment ago has no <c>RunsOn</c> edge yet
    /// and is not judged until it does. That is a real window and not a
    /// complete defence; the complete one is uptime or a multi-sample gate,
    /// and neither is available — the vSphere parser keeps only the last point
    /// of each window, so there is exactly one sample per cycle to reason over.
    /// </para>
    /// </remarks>
    private static Dictionary<EntityId, List<EntityId>> GuestsByHost(EntityGraph graph)
    {
        var byHost = new Dictionary<EntityId, List<EntityId>>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.RunsOn))
        {
            if (!IsLive(graph, edge.From, EntityKind.VirtualMachine) ||
                !IsLive(graph, edge.To, EntityKind.EsxiHost))
            {
                continue;
            }

            if (!byHost.TryGetValue(edge.To, out var guests))
            {
                byHost[edge.To] = guests = [];
            }

            if (!guests.Contains(edge.From))
            {
                guests.Add(edge.From);
            }
        }

        return byHost;
    }

    private static bool IsLive(EntityGraph graph, EntityId id, EntityKind kind) =>
        graph.Entities.TryGetValue(id, out var entity) &&
        entity.Kind == kind &&
        entity.ObservationState != ObservationState.Vanished;

    /// <summary>
    /// A summed duration counter as a percentage of its interval, per entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conversion lives in <see cref="CounterValue.AsPercentageOfInterval"/>
    /// and nowhere else, which is why that method exists: the previous product
    /// divided by the interval in several places and one of them was wrong.
    /// </para>
    /// <para>
    /// A sample whose rollup or unit says the conversion is meaningless is
    /// skipped rather than converted, and skipped rather than thrown over.
    /// <c>AsPercentageOfInterval</c> would throw, <c>GuardedRule</c> would turn
    /// that into an "analysis rule failed" alert, and one mislabelled counter
    /// would cost every verdict in the cycle — a collector defect billed to
    /// the operator as a broken product.
    /// </para>
    /// <para>
    /// Aggregates only. vSphere also reports these per vCPU, and a per-vCPU
    /// series is not a small virtual machine: comparing them against each other
    /// would report the busiest core of every guest in the estate.
    /// </para>
    /// <para>
    /// It does not ask the graph whether the entity is one this rule cares
    /// about, and that omission is deliberate rather than an oversight. This
    /// filtered on kind and liveness until mutation testing showed the check
    /// was undefended — and then showed why no test could defend it: nothing
    /// reaches a verdict except through <see cref="GuestsByHost"/>, so a
    /// reading for an unknown, vanished or wrong-kinded entity is already
    /// unreachable. A second gate that cannot be observed to fail is a number
    /// nobody can safely change later; the population is decided in one place.
    /// </para>
    /// </remarks>
    private static Dictionary<EntityId, double> PercentagesOf(
        IReadOnlyList<Observation> observations,
        string counter)
    {
        var values = new Dictionary<EntityId, double>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!Named(value.CounterName, counter) ||
                !value.IsAggregateInstance ||
                value.Rollup != RollupType.Summation ||
                !IsMilliseconds(value.Unit) ||
                value.Interval <= TimeSpan.Zero)
            {
                continue;
            }

            Keep(values, observation.Entity, value.AsPercentageOfInterval());
        }

        return values;
    }

    /// <summary>A percentage counter, taken as it arrived.</summary>
    /// <remarks>
    /// No conversion, and a unit check rather than trust: a host usage counter
    /// that arrived in hundredths of a percent would read 2669 and make every
    /// host in the estate saturated. The collector normalises this, and this
    /// rule does not rely on it having done so.
    /// </remarks>
    private static Dictionary<EntityId, double> LevelsOf(
        IReadOnlyList<Observation> observations,
        string counter)
    {
        var values = new Dictionary<EntityId, double>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!Named(value.CounterName, counter) ||
                !value.IsAggregateInstance ||
                !string.Equals(value.Unit, "percent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Keep(values, observation.Entity, value.Raw);
        }

        return values;
    }

    /// <summary>
    /// Keeps the worse of two readings for the same entity.
    /// </summary>
    /// <remarks>
    /// A cycle normally carries one sample per counter per entity. If it ever
    /// carries two, the higher one is the one a verdict should be reached on:
    /// taking whichever happened to be last in the list would make the rule's
    /// answer depend on collection order.
    /// </remarks>
    private static void Keep(Dictionary<EntityId, double> values, EntityId entity, double value) =>
        values[entity] = values.TryGetValue(entity, out var existing)
            ? Math.Max(existing, value)
            : value;

    private static bool Named(string counterName, string wanted) =>
        string.Equals(counterName, wanted, StringComparison.OrdinalIgnoreCase);

    private static bool IsMilliseconds(string unit) =>
        string.Equals(unit, "millisecond", StringComparison.OrdinalIgnoreCase);

    private static double Worst(
        List<EntityId> guests, Dictionary<EntityId, double> ready) =>
        guests.Count == 0 ? 0d : guests.Max(g => ready[g]);

    private static string Percent(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }
}
