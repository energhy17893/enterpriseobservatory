using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// What counts as memory genuinely being taken away, and who to say it about.
/// </summary>
/// <remarks>
/// <para>
/// Four counters and two numbers, and only one of the numbers has a source.
/// The rate floor rests on Broadcom's <i>Performance Best Practices for VMware
/// vSphere</i>, whose memory guidance treats any non-zero swapped or compressed
/// memory as a sign of significant memory pressure — "above zero", not a level
/// somebody has to argue about. The guest count is this product's own, argued
/// beside it, and borrowed in shape from
/// <see cref="CpuContentionPolicy.MinimumWaitingVirtualMachines"/>.
/// </para>
/// <para>
/// The counters are named here rather than compiled in, for the reason
/// <see cref="CpuContentionPolicy"/> gives: the application layer may not name
/// a vim25 counter, and holding the names as policy is the version of that
/// which does not need the domain changed.
/// </para>
/// </remarks>
public sealed record MemoryPressurePolicy
{
    /// <summary>
    /// The rate, in KB/s, at or above which any one mechanism counts as active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One kilobyte a second, and that is "above zero" written as the smallest
    /// value the platform reports rather than as a literal zero — the same move
    /// <see cref="StorageLatencyBlindSpotPolicy.MinimumMeasurableMilliseconds"/>
    /// makes. vSphere reports these rates in whole KB/s, so anything that
    /// reads at all reads at least one.
    /// </para>
    /// <para>
    /// The "above zero" is the cited part: Broadcom's performance best
    /// practices call any non-zero swapped or compressed memory significant
    /// pressure. What this does not do is pick a larger number to look
    /// reasonable. Unlike ready time, swap and compression have no ordinary
    /// band on a healthy host — ESXi reaches for them only after ballooning has
    /// not been enough — so there is nothing for a higher floor to filter out
    /// except the early part of a real event.
    /// </para>
    /// <para>
    /// "Sustained" is not decided here, and could not be: the vSphere parser
    /// keeps one point per window, and no rule can yet read a series. It is
    /// decided in two places this rule relies on and does not own. Each
    /// reading is itself an average over its twenty-second window rather than
    /// an instant, and every verdict is a warning, which
    /// <see cref="HysteresisPolicy.WarningConsecutiveHits"/> will not confirm
    /// until it has been seen on consecutive cycles. That is why no verdict
    /// here is ever raised as critical, even for swap-in: a critical is
    /// confirmed on the first sighting, and would quietly remove the only
    /// sustain gate this rule has.
    /// </para>
    /// </remarks>
    public double MinimumRateKiloBytesPerSecond { get; init; } = 1d;

    /// <summary>
    /// How many unlimited guests must be under pressure before the host is blamed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two, and this product's own number — nobody publishes one. It exists
    /// because a host's swap and compression rates are not a statement about
    /// the host: ESXi only ever swaps and compresses <em>virtual machines'</em>
    /// memory, so the host counter is the sum of what happened to its guests.
    /// One guest held under its own memory limit swaps on a host with gigabytes
    /// free, and the host counter reads exactly as it would for a host that is
    /// genuinely short.
    /// </para>
    /// <para>
    /// So the host verdict wants corroboration, and two is the smallest number
    /// that is not one machine's story told under the host's name — the
    /// argument <see cref="CpuContentionPolicy.MinimumWaitingVirtualMachines"/>
    /// makes, for the same kind of evidence. Below it the rule names the guests
    /// instead, which is both true and the thing an operator can act on.
    /// </para>
    /// </remarks>
    public int MinimumPressuredGuests { get; init; } = 2;

    /// <summary>The counter carrying memory being read back in from swap.</summary>
    /// <remarks>
    /// The rung that hurts. A page swapped back in is a guest that touched
    /// memory it no longer had and stalled on disk to get it.
    /// </remarks>
    public string SwapInCounter { get; init; } = "mem.swapinRate.average";

    /// <summary>The counter carrying memory being written out to swap.</summary>
    public string SwapOutCounter { get; init; } = "mem.swapoutRate.average";

    /// <summary>The counter carrying memory being compressed.</summary>
    /// <remarks>
    /// The rung before swap: ESXi compresses a page it would otherwise have
    /// swapped. A host compressing is under pressure and still coping.
    /// </remarks>
    public string CompressionCounter { get; init; } = "mem.compressionRate.average";

    /// <summary>The counter carrying compressed memory being read back.</summary>
    public string DecompressionCounter { get; init; } = "mem.decompressionRate.average";

    public static MemoryPressurePolicy Default { get; } = new();
}

/// <summary>
/// Says where memory is actually being taken away — swapped or compressed —
/// and whether the answer is a host or one machine.
/// </summary>
/// <remarks>
/// <para>
/// Rates, and deliberately not levels. <c>mem.usage</c> is not read at all:
/// high usage on a consolidated host is the host doing its job. Ballooning is
/// not read either, and that is the decision the architecture §10 step 1c
/// names as the answer itself. A balloon is the reclamation mechanism
/// <em>working</em> — early, cheap and on a healthy estate present on most
/// machines most of the time — which is why Datadog's advice to alert on it
/// ships a standing false alarm, and why Dynatrace's memory detection leaves
/// it out. Swap and compression are what ESXi reaches for once the balloon
/// was not enough. They are late, and they are almost never wrong.
/// </para>
/// <para>
/// So a balloon alone, however large, is silence here, and that silence is
/// structural rather than a gate: the balloon counter is never looked up.
/// <c>mem.active</c> is collected to make ballooning judgeable — "reclaiming"
/// against "starving" — and that is a different rule, with a different
/// false-positive problem, which this one does not need to solve because it
/// never asks the question.
/// </para>
/// <para>
/// The verdicts, in the order they are decided for each host:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>The host is swapping or compressing.</b> The host's own rate is up
/// <em>and</em> either several guests without a memory limit are under
/// pressure too, or no guest on it was measured at all. The second arm is the
/// honest fallback: if per-guest rates did not arrive the host counter is all
/// there is, and staying silent about it would be the product choosing not
/// to look.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>This machine is being swapped or compressed.</b> A guest's own rate is
/// up and its host verdict did not cover it — because the host did not fire,
/// or because this is a machine the host verdict cannot speak for.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>This machine is swapping under its own memory limit.</b> The memory
/// twin of <c>CpuContention</c>'s limit verdict and the false positive this
/// rule would otherwise ship with: a limited guest swaps on a host with
/// memory to spare, and the host's counter cannot tell. It is always said
/// about the machine, never folded into the host, because the fix is a
/// change to that one machine.
/// </description>
/// </item>
/// </list>
/// <para>
/// A machine the graph does not know, or does not know the kind of, is not
/// judged: the three sentences above are different advice for a host and a
/// guest, and guessing which one a reading belongs to would be guessing the
/// advice.
/// </para>
/// </remarks>
public static class MemoryPressure
{
    /// <summary>Shown beside a memory pressure alert.</summary>
    public const string Category = "Memory pressure";

    /// <summary>
    /// Shown beside the limit verdict, which is a configuration change rather
    /// than an incident — the split <c>CpuContention.SizingCategory</c> makes.
    /// </summary>
    public const string SizingCategory = "Right-sizing";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "memory-pressure";

    /// <summary>Who these are attributed to. See <c>CpuContention</c>.</summary>
    private const string Platform = "platform";

    private const string HostTitle = "Host is swapping or compressing memory";
    private const string GuestTitle = "Memory is being swapped or compressed";
    private const string LimitTitle = "Swapping under its own memory limit";

    /// <summary>
    /// Every memory pressure verdict this batch of observations supports.
    /// </summary>
    /// <param name="observations">One cycle's samples.</param>
    /// <param name="graph">
    /// What each reading is about and which guests run where. A host's counter
    /// is the sum of its guests', so without <c>RunsOn</c> the rule could not
    /// tell a short host from one limited machine.
    /// </param>
    /// <param name="policy">Defaults, and why they are what they are.</param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        EntityGraph graph,
        MemoryPressurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(graph);

        var rules = policy ?? MemoryPressurePolicy.Default;

        var rates = RatesOf(observations, rules);
        var alerts = new List<AlertDefinition>();

        // Guests the host verdict spoke for. Everything else under pressure
        // is named on its own below, so a machine cannot fall between the two.
        var covered = new HashSet<EntityId>();

        foreach (var (host, guests) in GuestsByHost(graph))
        {
            if (!rates.TryGetValue(host, out var hostRates) || !UnderPressure(hostRates, rules))
            {
                continue;
            }

            var measured = guests.Where(rates.ContainsKey).ToList();
            var pressured = measured
                .Where(g => UnderPressure(rates[g], rules) && !IsLimited(graph, g))
                .ToList();

            if (measured.Count > 0 && pressured.Count < rules.MinimumPressuredGuests)
            {
                continue;
            }

            alerts.Add(HostVerdict(host, hostRates, pressured.Count, measured.Count, rules));
            covered.UnionWith(pressured);
        }

        foreach (var (entity, readings) in rates)
        {
            if (covered.Contains(entity) ||
                !IsLive(graph, entity, EntityKind.VirtualMachine) ||
                !UnderPressure(readings, rules))
            {
                continue;
            }

            alerts.Add(IsLimited(graph, entity)
                ? LimitVerdict(entity, readings, graph.Entities[entity].Sizing!, rules)
                : GuestVerdict(entity, readings, rules));
        }

        return alerts;
    }

    /// <summary>The four rates for one entity. A missing one is absent, not zero.</summary>
    private sealed class Rates
    {
        public double? SwapIn { get; set; }
        public double? SwapOut { get; set; }
        public double? Compression { get; set; }
        public double? Decompression { get; set; }
    }

    /// <summary>
    /// Whether any one mechanism was active at or above the floor.
    /// </summary>
    /// <remarks>
    /// Any, not all and not the sum. Each of the four is ESXi taking memory
    /// away or paying to give it back, and the architecture's "swap <em>or</em>
    /// compression" is literal: a host compressing and not yet swapping is the
    /// early half of the same event, not a lesser one. Summing would let four
    /// sub-floor readings add up to a verdict none of them supports.
    /// </remarks>
    private static bool UnderPressure(Rates rates, MemoryPressurePolicy rules) =>
        Active(rates.SwapIn, rules) ||
        Active(rates.SwapOut, rules) ||
        Active(rates.Compression, rules) ||
        Active(rates.Decompression, rules);

    private static bool Active(double? rate, MemoryPressurePolicy rules) =>
        rate is { } value && value >= rules.MinimumRateKiloBytesPerSecond;

    /// <summary>
    /// Whether a machine carries a configured memory limit.
    /// </summary>
    /// <remarks>
    /// A fact from inventory, not a reading, and the one way in which this is
    /// weaker than <c>CpuContention</c>'s limit verdict: there is no memory
    /// twin of <c>cpu.maxlimited</c> saying the limit is <em>biting</em>. A
    /// limited machine that swaps is almost always swapping because of the
    /// limit — ESXi enforces it by exactly this reclamation — but "almost" is
    /// stated rather than hidden. An unknown sizing reads as unlimited, so
    /// during the minutes after a restart before inventory lands a limited
    /// guest is counted as evidence against its host; the host verdict may
    /// fire where it later would not.
    /// </remarks>
    private static bool IsLimited(EntityGraph graph, EntityId guest) =>
        graph.Entities.TryGetValue(guest, out var entity) &&
        entity.Sizing?.IsMemoryLimited == true;

    private static AlertDefinition HostVerdict(
        EntityId host, Rates rates, int pressured, int measured, MemoryPressurePolicy rules) =>
        new()
        {
            // The host, and not which guests are suffering — they change from
            // cycle to cycle while the host stays short, and a fingerprint
            // carrying them would restart the history every time. The choice
            // CpuContention makes for its host verdict.
            Fingerprint = AlertFingerprint.Create(
                Platform, HostTitle, Category, host.Value, "memory-host-pressure"),
            Severity = AlertSeverity.Warning,
            Title = HostTitle,
            Description =
                $"This host is taking memory away from its virtual machines: {Describe(rates, rules)}. " +
                Corroboration(pressured, measured) +
                "Swap and compression are what ESXi reaches for after ballooning was not " +
                "enough, so this is not a busy host but a short one: move workload off it, add " +
                "memory, or check for reservations that leave it nothing to reclaim.",
            Category = Category,
            Source = Platform,
            Entity = host,
            IsDerived = true,
        };

    /// <summary>
    /// What the host verdict rests on, said so it cannot be over-read.
    /// </summary>
    private static string Corroboration(int pressured, int measured) =>
        measured == 0
            ? "No per-machine memory rates arrived for its guests this cycle, so this " +
              "cannot yet say which of them it is happening to — or rule out a single " +
              "machine held under its own memory limit. "
            : $"{pressured} of its {measured} measured guest(s) without a memory limit are " +
              "being reclaimed at the same time, which is the host rather than any one of them. ";

    private static AlertDefinition GuestVerdict(EntityId guest, Rates rates, MemoryPressurePolicy rules) =>
        new()
        {
            // The machine alone, not its host: a guest that vMotions and keeps
            // swapping took its problem with it. Constant across the four
            // mechanisms, so a machine that moves from compression to swap is
            // one getting worse, not two separate alerts.
            Fingerprint = AlertFingerprint.Create(
                Platform, GuestTitle, Category, guest.Value, "memory-guest-pressure"),
            Severity = AlertSeverity.Warning,
            Title = GuestTitle,
            Description =
                $"ESXi is taking memory away from this virtual machine: {Describe(rates, rules)}. " +
                "A balloon is reclamation working; this is what happens after it was not enough, " +
                "and the guest pays for every page read back from swap or decompressed. Its " +
                "host is not reported as short of memory, so look at this machine first: its " +
                "reservation and shares against its neighbours, and whether it is sized for " +
                "what it is running.",
            Category = Category,
            Source = Platform,
            Entity = guest,
            IsDerived = true,
        };

    private static AlertDefinition LimitVerdict(
        EntityId guest, Rates rates, EntitySizing sizing, MemoryPressurePolicy rules) =>
        new()
        {
            Fingerprint = AlertFingerprint.Create(
                Platform, LimitTitle, SizingCategory, guest.Value, "memory-limit-pressure"),
            Severity = AlertSeverity.Warning,
            Title = LimitTitle,
            Description =
                $"ESXi is taking memory away from this virtual machine: {Describe(rates, rules)}. " +
                $"It carries a configured memory limit of {sizing.MemoryLimitMb} MB, and a " +
                "limit is enforced by exactly this — swap and compression — whatever the host " +
                "has free. The host is not being blamed for this machine. Check the limit: one " +
                "set during a migration or a test and never removed is the usual cause, and " +
                "raising or clearing it is a change to this machine alone.",
            Category = SizingCategory,
            Source = Platform,
            Entity = guest,
            IsDerived = true,
        };

    /// <summary>The mechanisms that were active, and how fast.</summary>
    private static string Describe(Rates rates, MemoryPressurePolicy rules)
    {
        var parts = new List<string>();

        void Add(string name, double? rate)
        {
            if (Active(rate, rules))
            {
                parts.Add($"{name} {Readings.Number(rate!.Value)} KB/s");
            }
        }

        Add("swap-in", rates.SwapIn);
        Add("swap-out", rates.SwapOut);
        Add("compression", rates.Compression);
        Add("decompression", rates.Decompression);

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The four rates per entity, from aggregate readings in KB/s only.
    /// </summary>
    /// <remarks>
    /// A reading with the wrong unit or rollup is skipped rather than trusted
    /// or thrown over, for the reason <c>CpuContention</c> gives: one
    /// mislabelled counter must not cost every verdict in the cycle. The unit
    /// check is load-bearing here in a way it is not for a percentage — a rate
    /// that arrived in bytes rather than kilobytes would clear the floor on
    /// any reading at all.
    /// </remarks>
    private static Dictionary<EntityId, Rates> RatesOf(
        IReadOnlyList<Observation> observations,
        MemoryPressurePolicy rules)
    {
        var byEntity = new Dictionary<EntityId, Rates>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!value.IsAggregateInstance ||
                value.Rollup != RollupType.Average ||
                !string.Equals(value.Unit, "kiloBytesPerSecond", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!byEntity.TryGetValue(observation.Entity, out var rates))
            {
                rates = new Rates();
            }

            // The worse of two readings, if a cycle ever carries two, so the
            // answer does not depend on collection order.
            if (Readings.IsCounter(value.CounterName, rules.SwapInCounter))
            {
                rates.SwapIn = Worse(rates.SwapIn, value.Raw);
            }
            else if (Readings.IsCounter(value.CounterName, rules.SwapOutCounter))
            {
                rates.SwapOut = Worse(rates.SwapOut, value.Raw);
            }
            else if (Readings.IsCounter(value.CounterName, rules.CompressionCounter))
            {
                rates.Compression = Worse(rates.Compression, value.Raw);
            }
            else if (Readings.IsCounter(value.CounterName, rules.DecompressionCounter))
            {
                rates.Decompression = Worse(rates.Decompression, value.Raw);
            }
            else
            {
                continue;
            }

            byEntity[observation.Entity] = rates;
        }

        return byEntity;
    }

    /// <summary>
    /// The VMs the graph places on each live host, including hosts with none.
    /// </summary>
    /// <remarks>
    /// Every live host is present even without guests, unlike
    /// <c>CpuContention</c>'s grouping, because here the host has a reading of
    /// its own and a host with no known guests is exactly the "nothing to
    /// corroborate with" case the host verdict falls back on.
    /// </remarks>
    private static Dictionary<EntityId, List<EntityId>> GuestsByHost(EntityGraph graph)
    {
        // Not shared with CpuContention's: this one keeps guest-less hosts, that one must not.
        var byHost = graph.Entities.Values
            .Where(e => e.Kind == EntityKind.EsxiHost && e.ObservationState != ObservationState.Vanished)
            .ToDictionary(e => e.Id, _ => new List<EntityId>());

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.RunsOn))
        {
            if (IsLive(graph, edge.From, EntityKind.VirtualMachine) &&
                byHost.TryGetValue(edge.To, out var guests) &&
                !guests.Contains(edge.From))
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

    private static double Worse(double? existing, double value) =>
        existing is { } known ? Math.Max(known, value) : value;
}
