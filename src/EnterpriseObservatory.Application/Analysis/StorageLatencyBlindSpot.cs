using System.Globalization;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// What has to be true before the product admits it cannot see a volume's latency.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is a resolution rather than a threshold, and that is why
/// none of them is zero. The condition this rule detects is "the counter read
/// zero", but a policy field defaulted to <c>0</c> says nothing about the
/// platform and cannot be tightened or loosened by an operator on a platform
/// that reports differently. Stating the smallest value the platform is
/// capable of reporting says the same thing, says why, and moves when the
/// platform does.
/// </para>
/// <para>
/// The counter names follow <see cref="StorageLayerPolicy"/>'s concession and
/// not its full extent. The latency counters and the load counters are found
/// by shape — a duration from a vantage point, a rate from a vantage point —
/// exactly as <see cref="PeerOutliers"/> and <see cref="SharedVolumeLatency"/>
/// find them, so they need no configuration. Only
/// <see cref="SiocCounter"/> is named, because no shape can express what it is:
/// it is not a performance number at all, it is the evidence for whether
/// another counter's number means anything, and nothing on
/// <see cref="CounterValue"/> says "this counter validates that one". Naming it
/// in a literal would put a vim25 string in the application layer; naming it in
/// policy lets a collector with its own equivalent be configured in.
/// </para>
/// </remarks>
public sealed record StorageLatencyBlindSpotPolicy
{
    /// <summary>
    /// The counter saying whether the sub-millisecond measurement channel is running.
    /// </summary>
    /// <remarks>
    /// Storage I/O Control's share of the interval. On vSphere it is the only
    /// thing that can distinguish "this volume is faster than a millisecond"
    /// from "this volume's latency was never measured", because
    /// <c>datastore.datastoreVMObservedLatency.latest</c> — the one counter
    /// reported in microseconds — reports only while SIOC is running. Counter
    /// map §5b records that this counter is collected for this rule and for
    /// nothing else.
    /// </remarks>
    public string SiocCounter { get; init; } = "datastore.siocActiveTimePercentage.average";

    /// <summary>
    /// The smallest latency the platform is able to report at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One millisecond. vSphere reports <c>datastore.total{Read,Write}Latency</c>
    /// in whole milliseconds, so every request that completes faster than this
    /// truncates to zero and the product prints "0 ms" over a measurement it
    /// never had. Counter map §5b measured it: across 178,180 readings, 99.6%
    /// are zero, and the observed values are integers throughout.
    /// </para>
    /// <para>
    /// Raising this is how an operator would describe a platform with coarser
    /// reporting. Lowering it below the platform's resolution turns the rule
    /// off, because then nothing is ever unmeasurable.
    /// </para>
    /// </remarks>
    public double MinimumMeasurableMilliseconds { get; init; } = 1d;

    /// <summary>
    /// The smallest share of the interval that counts as SIOC actually running.
    /// </summary>
    /// <remarks>
    /// One percent, for the same reason as the millisecond above: the counter
    /// is reported in whole percent, so one is its resolution and not a
    /// judgement about how much throttling matters. Any reading at all means
    /// the channel exists and this rule has nothing to offer.
    /// </remarks>
    public double MinimumActiveSiocPercentage { get; init; } = 1d;

    /// <summary>
    /// The load that makes a zero a truncation rather than an honest answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One operation a second, summed over read and write and over every host
    /// that mounts the volume. Deliberately far below
    /// <see cref="SharedVolumePolicy.MinimumOperationsPerSecond"/>'s ten, and
    /// the difference is the difference between the two rules. That one is
    /// about to average a latency and needs enough requests for the average to
    /// be a picture of the volume rather than the fate of a handful. This one
    /// averages nothing. It asks a single question — did any I/O happen whose
    /// latency should have shown up — and one request answers it as well as a
    /// thousand do.
    /// </para>
    /// <para>
    /// It is not a significance gate and must not be read as one. It is what
    /// separates a truncated zero from an idle volume's honest zero, and an
    /// idle volume is the one case where "0 ms" is the truth.
    /// </para>
    /// </remarks>
    public double MinimumOperationsPerSecond { get; init; } = 1d;

    /// <summary>
    /// How many latency readings must agree before the silence is a pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three, matching <see cref="PeerOutlierPolicy.MinimumVantagePoints"/> and
    /// spending the same currency. A rule here sees one sample per series per
    /// cycle and cannot count samples over time, so the only evidence it can
    /// accumulate is breadth across the hosts that mount the volume. One host
    /// reporting zero once is a sample; the read and write counters on three
    /// hosts all reporting zero while the volume is working is a property of
    /// the measurement.
    /// </para>
    /// <para>
    /// Readings rather than hosts, because a single host mounting the volume
    /// still contributes two independent latency counters and a second opinion
    /// about the platform's resolution is not the same thing as a second
    /// opinion about a volume's health. This rule accuses no host, so it does
    /// not need the three-vantage-point majority <see cref="PeerOutliers"/>
    /// needs — it needs enough numbers that a stray zero cannot carry it.
    /// </para>
    /// </remarks>
    public int MinimumLatencyReadings { get; init; } = 3;

    /// <summary>
    /// K: how many judged cycles the verdict is taken over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window-ratio hysteresis rather than a count of consecutive cycles.
    /// Measured on the live estate: 58 % of 262 historical clears were genuine
    /// alternation — SIOC off, sub-millisecond latency clipped to zero, so a
    /// working volume reads 1 ms and 0 ms by turns and the per-cycle rule
    /// followed it faithfully. M-of-N consecutive would still flap at that
    /// rate; a share over a window does not.
    /// </para>
    /// <para>
    /// Only judged cycles enter the window — measured or blind. A quiet cycle,
    /// too few readings or a missing counter stays Unknown as before and does
    /// not move it. Until the window holds K judged cycles the rule says
    /// NotJudgeable. One is the per-cycle rule.
    /// </para>
    /// <para>
    /// <b>Placeholder.</b> 20 until the replay
    /// (<c>docs/measurements/blind-spot-hysteresis-replay.sql</c>) is run on
    /// the live estate and K is chosen from it.
    /// </para>
    /// </remarks>
    public int WindowCycles { get; init; } = 20;

    /// <summary>
    /// P: the share of the window's judged cycles that must be measurable for
    /// the measurement to count as working, in percent.
    /// </summary>
    /// <remarks>
    /// At or above it the volume is Absent; below it, Present. <b>Placeholder.</b>
    /// 50 until the replay is run and P is chosen from it.
    /// </remarks>
    public double MinimumMeasurablePercent { get; init; } = 50d;

    public static StorageLatencyBlindSpotPolicy Default { get; } = new();
}

/// <summary>
/// Says that on this volume the product's storage latency is not a measurement.
/// </summary>
/// <remarks>
/// <para>
/// A different kind of rule from every other one here, and the difference is
/// the point. <see cref="PeerOutliers"/>, <see cref="SharedVolumeLatency"/> and
/// <see cref="StorageLayerSplit"/> judge the estate. This judges the product's
/// own ability to see, which puts it beside <c>GuardedRule</c>'s "Analysis rule
/// failed" rather than beside them — same category, same severity, same
/// <c>platform</c> attribution, and for the same argument: the finding is that
/// we have stopped looking at part of the estate, not that the estate is
/// broken.
/// </para>
/// <para>
/// It says nothing whatever about whether storage is slow, and the wording is
/// built to make that impossible to misread. A volume here may be perfectly
/// healthy; the product does not know, and that is the entire alert. Counter
/// map §5b calls a zero worse than silence because a zero looks like a
/// measurement — an operator reads "storage latency 0 ms", clears storage and
/// moves on, having been told nothing. This rule is the sentence that would
/// have stopped them.
/// </para>
/// <para>
/// Measured, not supposed. A probe run against the live estate on 20 September
/// 2026 read 302 samples of each datastore latency counter and found every one
/// of them zero, while <c>numberReadAveraged</c> was non-zero in 24 of 302 and
/// <c>numberWriteAveraged</c> in 76 of 302, peaking at 1638 and 260 operations.
/// There is load, and the latency reads zero. The probe's own words: a latency
/// counter reading zero while the IOPS counters do not is not a fast volume.
/// </para>
/// <para>
/// The three gates are all necessary and none implies another, which is worth
/// stating because it looks like it ought to collapse. Load says nothing about
/// the other two. Zero latency does not imply SIOC is off — SIOC that is
/// enabled but not congested reports zero active time while it is running.
/// SIOC being inactive does not imply zero latency either: the whole-millisecond
/// counters report regardless of SIOC, so a genuinely slow volume with SIOC off
/// reads 5 ms, this rule stays silent, and <see cref="SharedVolumeLatency"/>
/// speaks instead. That silence is structural rather than coordinated — where
/// there is a latency to judge, there is no blind spot to report.
/// </para>
/// <para>
/// Because SIOC's active time cannot distinguish "off" from "on and quiet", the
/// alert does not claim SIOC is disabled. It reports what the counter said and
/// asks the operator to check. The claim it does make — that the measurement
/// channel was not reporting — holds in both cases, because an enabled but idle
/// SIOC does not produce a
/// <c>datastoreVMObservedLatency</c> reading either.
/// </para>
/// <para>
/// Datastores only. Counter map §5b records the same truncation on the
/// <c>disk.*</c> device triple that <see cref="StorageLayerSplit"/> reads, and
/// this rule deliberately says nothing about it: SIOC is a datastore feature
/// and does nothing for a device's latency, so there the product would be
/// raising an alert whose fix does not exist. Naming a problem with no action
/// is the noise this product's fourth principle forbids. That debt stays open
/// and stays recorded.
/// </para>
/// </remarks>
public static class StorageLatencyBlindSpot
{
    /// <summary>
    /// <c>GuardedRule</c>'s category rather than a storage one, deliberately.
    /// </summary>
    /// <remarks>
    /// The three storage categories — "Storage path", "Storage array", "Storage
    /// layer" — each name a place to go and look. This alert names none,
    /// because it is not about the storage. It is about a setting, and about
    /// the product, which is what "Configuration" already covers.
    /// </remarks>
    public const string Category = "Configuration";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "storage-latency-blind-spot";

    /// <summary>
    /// Who this is attributed to.
    /// </summary>
    /// <remarks>
    /// The platform and not the vCenter, following <c>CpuContention</c>. No
    /// collector reported this; the product concluded it, and it concluded it
    /// from an absence rather than from a reading.
    /// </remarks>
    private const string Platform = "platform";

    private const string Title = "Storage latency cannot be measured here";

    /// <summary>
    /// Every volume whose latency this cycle's samples cannot speak for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One alert per datastore, and the estate-wide proportion carried as a
    /// sentence inside each one. That is the answer to the question this rule
    /// was commissioned with — 41 datastores with SIOC off, one alert or
    /// forty-one — and it is a synthesis rather than a compromise.
    /// </para>
    /// <para>
    /// Forty-one, because an alert names a fix and this fix is per datastore.
    /// SIOC is enabled on a datastore, so a single estate-wide alert would be a
    /// progress bar: the operator enables it on eight volumes and the alert
    /// does not move, because thirty-three are left. It cannot be acknowledged
    /// for the one volume where SIOC was deliberately left off — acknowledging
    /// it silences the other forty — and it has no <see cref="EntityId"/>, so
    /// nothing downstream can hang it off the volume it is about. Every other
    /// rule in this product reports per object and would raise forty-one alerts
    /// for forty-one sick volumes; nobody calls that noise, that is the product
    /// working.
    /// </para>
    /// <para>
    /// The reference research into vROps is the argument against, and it is
    /// about a different thing. Its twelve near-identical contention alerts
    /// expressed about four ideas about the <em>same</em> object, and the
    /// conclusion recorded against it — express the proportion as a field on
    /// one alert, not as four alerts — was about one idea being said four
    /// times. Here it is one idea about forty-one different objects, each with
    /// its own switch to flick. Collapsing those is not de-duplication, it is
    /// losing the answer to "which".
    /// </para>
    /// <para>
    /// But the proportion is the real finding, and it is honoured where it
    /// belongs. Forty-one of forty-one is not forty-one volumes with a
    /// misconfiguration, it is an estate that was never configured, and an
    /// operator who reads one alert in isolation would go and fix one volume.
    /// So each alert carries the count: how many of the estate's volumes are in
    /// this state. One volume of forty-one says check that volume; forty-one of
    /// forty-one says stop and set a policy. That is the vROps lesson applied —
    /// the proportion is a field on the alert — without spending the identity
    /// that makes the alert actionable.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations,
        StorageLatencyBlindSpotPolicy? policy = null) =>
        [.. Judge(observations, policy, DateTimeOffset.MinValue).OfType<ConditionPresent>().SelectMany(p => p.Alerts)];

    /// <summary>
    /// Every volume this cycle's samples carried latency for, judged in three
    /// values (ADR-0026).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Present: blind, as <see cref="Evaluate"/> has always said. Absent: the
    /// measurement works — a latency at or above the platform's resolution, or
    /// SIOC active. Unknown otherwise, and this is the rule's whole conversion:
    /// </para>
    /// <list type="bullet">
    /// <item><b>A quiet cycle</b> — load below
    /// <see cref="StorageLatencyBlindSpotPolicy.MinimumOperationsPerSecond"/> —
    /// is <see cref="UnknownReason.NotJudgeable"/>. At idle a zero is an honest
    /// zero, so the cycle cannot tell whether the measurement works; it is not
    /// evidence that it does. Measured: every one of the 399 Cleared→Returned
    /// pairs this rule left in fifteen hours before 22 September 2026 was a
    /// quiet cycle resolving the alert and the next busy one raising it again
    /// (design note §3.1, §7).</item>
    /// <item>Fewer latency readings than
    /// <see cref="StorageLatencyBlindSpotPolicy.MinimumLatencyReadings"/>:
    /// <see cref="UnknownReason.NotJudgeable"/>.</item>
    /// <item>Load or SIOC counter missing:
    /// <see cref="UnknownReason.InputNotCollected"/>.</item>
    /// </list>
    /// <para>
    /// A volume with no latency reading at all gets no verdict: nothing in the
    /// cycle names it, so an alert on it is "not reported".
    /// </para>
    /// </remarks>
    /// <param name="observations">This cycle's samples.</param>
    /// <param name="policy">The resolutions; <see cref="StorageLatencyBlindSpotPolicy.Default"/> when null.</param>
    /// <param name="evidenceAtUtc">When those samples were taken.</param>
    public static IReadOnlyList<SubjectVerdict> Judge(
        IReadOnlyList<Observation> observations,
        StorageLatencyBlindSpotPolicy? policy,
        DateTimeOffset evidenceAtUtc) =>
        Judge(observations, policy, evidenceAtUtc, static (_, cycle) => cycle);

    /// <summary>
    /// The same, with each volume's verdict taken over its last
    /// <see cref="StorageLatencyBlindSpotPolicy.WindowCycles"/> judged cycles
    /// rather than this one alone.
    /// </summary>
    /// <remarks>
    /// This cycle's judgement is computed exactly as above. A measured or blind
    /// cycle is added to the volume's window; anything Unknown is returned as
    /// it is and leaves the window alone. With the window full the volume is
    /// Absent when at least
    /// <see cref="StorageLatencyBlindSpotPolicy.MinimumMeasurablePercent"/> of
    /// it was measurable and Present otherwise; not yet full, it is
    /// <see cref="UnknownReason.NotJudgeable"/>.
    /// </remarks>
    /// <param name="observations">This cycle's samples.</param>
    /// <param name="policy">The resolutions and the window; <see cref="StorageLatencyBlindSpotPolicy.Default"/> when null.</param>
    /// <param name="evidenceAtUtc">When those samples were taken.</param>
    /// <param name="window">Each volume's recent judged cycles; updated in place.</param>
    public static IReadOnlyList<SubjectVerdict> Judge(
        IReadOnlyList<Observation> observations,
        StorageLatencyBlindSpotPolicy? policy,
        DateTimeOffset evidenceAtUtc,
        StorageLatencyBlindSpotWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var rules = policy ?? StorageLatencyBlindSpotPolicy.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(rules.WindowCycles, 1, nameof(policy));

        return Judge(observations, rules, evidenceAtUtc, (volume, cycle) =>
        {
            if (cycle.Kind == JudgementKind.Unknown)
            {
                return cycle;
            }

            var (measurable, held) = window.Record(volume, cycle.Kind == JudgementKind.Measured, rules.WindowCycles);

            if (held < rules.WindowCycles)
            {
                return new(JudgementKind.Unknown, UnknownReason.NotJudgeable, string.Create(CultureInfo.InvariantCulture,
                    $"{held} of the {rules.WindowCycles} judged cycles the verdict is taken over have been seen; " +
                    $"{measurable} of them measurable"));
            }

            var working = measurable * 100d >= rules.MinimumMeasurablePercent * held;

            return new(working ? JudgementKind.Measured : JudgementKind.Blind, Measurable: measurable, Window: held);
        });
    }

    private static IReadOnlyList<SubjectVerdict> Judge(
        IReadOnlyList<Observation> observations,
        StorageLatencyBlindSpotPolicy? policy,
        DateTimeOffset evidenceAtUtc,
        Func<EntityId, Judgement, Judgement> overWindow)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var rules = policy ?? StorageLatencyBlindSpotPolicy.Default;

        // Two passes, because each alert reports the estate's proportion and
        // the proportion is not known until every volume has been judged. The
        // denominator is volumes this rule was able to consider at all — ones
        // that reported latency counters — rather than every entity in the
        // cycle, so a cluster of hosts does not dilute it into meaninglessness.
        var judged = new List<(EntityId Volume, Judgement Judgement)>();

        foreach (var volume in observations.GroupBy(o => o.Entity))
        {
            var latency = volume.Where(o => IsLatency(o.Value)).ToList();

            if (latency.Count == 0)
            {
                continue;
            }

            judged.Add((volume.Key, overWindow(volume.Key, Unmeasurable(volume, latency, rules))));
        }

        var blind = judged.Count(j => j.Judgement.Kind == JudgementKind.Blind);

        return
        [
            .. judged.Select(j =>
            {
                IReadOnlyList<AlertFingerprint> covers = [FingerprintOf(j.Volume)];

                return j.Judgement.Kind switch
                {
                    JudgementKind.Blind => (SubjectVerdict)new ConditionPresent
                    {
                        Covers = covers,
                        Alerts = [Alert(j.Volume, blind, judged.Count, j.Judgement)],
                        Entity = j.Volume,
                        EvidenceAtUtc = evidenceAtUtc,
                    },
                    JudgementKind.Measured => new ConditionAbsent
                    {
                        Covers = covers,
                        Entity = j.Volume,
                        EvidenceAtUtc = evidenceAtUtc,
                    },
                    _ => new Unknown
                    {
                        Covers = covers,
                        Entity = j.Volume,
                        Reason = j.Judgement.Reason,
                        Detail = j.Judgement.Detail,
                    },
                };
            }),
        ];
    }

    /// <summary>The fingerprint of the alert about one volume.</summary>
    public static AlertFingerprint FingerprintOf(EntityId volume) =>
        // The volume and nothing else. There is no counter to name because
        // the finding is about all of them, and no host to name because the
        // finding is that every host agrees. A fingerprint carrying either
        // would invent a protagonist the alert is specifically denying —
        // the same choice SharedVolumeLatency makes, from the same place.
        AlertFingerprint.Create(Platform, Title, Category, volume.Value, "storage-latency-blind-spot");

    private enum JudgementKind
    {
        Blind,
        Measured,
        Unknown,
    }

    /// <summary>
    /// What was concluded; <see cref="Window"/> is zero for one cycle's
    /// judgement and the number of judged cycles it was taken over otherwise.
    /// </summary>
    private readonly record struct Judgement(
        JudgementKind Kind,
        UnknownReason Reason = default,
        string Detail = "",
        int Measurable = 0,
        int Window = 0);

    /// <summary>What can be said about this volume's measurement this cycle.</summary>
    private static Judgement Unmeasurable(
        IEnumerable<Observation> volume,
        List<Observation> latency,
        StorageLatencyBlindSpotPolicy rules)
    {
        // Enough readings that one stray zero cannot carry the alert. The
        // count is also what keeps a volume mounted by a single host from
        // being judged on one number.
        if (latency.Count < rules.MinimumLatencyReadings)
        {
            return new(JudgementKind.Unknown, UnknownReason.NotJudgeable, string.Create(CultureInfo.InvariantCulture,
                $"{latency.Count} latency reading(s) this cycle; {rules.MinimumLatencyReadings} are needed to judge the measurement"));
        }

        // Any reading at or above the platform's resolution means the
        // measurement works. This is what makes the rule silent wherever the
        // other three storage rules can speak, rather than by agreement with
        // them.
        if (latency.Any(o => o.Value.Raw >= rules.MinimumMeasurableMilliseconds))
        {
            return new(JudgementKind.Measured);
        }

        // SIOC running answers the question on its own, whatever the load: the
        // channel this rule says is missing is there.
        var sioc = Sioc(volume, rules);

        if (sioc is { } running && running >= rules.MinimumActiveSiocPercentage)
        {
            return new(JudgementKind.Measured);
        }

        // Not looking is not the same as looking and finding nothing. Without
        // the load counters the product cannot tell a truncated zero from an
        // idle volume's honest one, and guessing which is exactly the confident
        // wrong answer this codebase treats as worse than silence.
        var load = Load(volume);

        if (load is not { } operations)
        {
            return new(JudgementKind.Unknown, UnknownReason.InputNotCollected,
                "no load counter (operations a second from a mounting host) arrived for this volume this cycle");
        }

        // A quiet cycle: a zero at idle is an honest zero, so whether the
        // measurement works cannot be told. Not evidence that it does.
        if (operations < rules.MinimumOperationsPerSecond)
        {
            return new(JudgementKind.Unknown, UnknownReason.NotJudgeable, string.Create(CultureInfo.InvariantCulture,
                $"a quiet cycle: {operations:0.##} operations a second, below the {rules.MinimumOperationsPerSecond:0.##} that makes a zero a truncation rather than an honest idle zero"));
        }

        // The same argument again for the evidence counter. A collector that
        // does not report SIOC leaves the product unable to say why the zeros
        // are there, and an alert that cannot name its cause cannot name its
        // fix.
        if (sioc is null)
        {
            return new(JudgementKind.Unknown, UnknownReason.InputNotCollected,
                $"'{rules.SiocCounter}' did not arrive for this volume this cycle");
        }

        return new(JudgementKind.Blind);
    }

    /// <summary>
    /// A latency reading this rule is entitled to judge.
    /// </summary>
    /// <remarks>
    /// Found by shape and not by name, exactly as <see cref="PeerOutliers"/>
    /// finds them: a duration reported from a vantage point is a shared
    /// resource's service time as seen from somewhere. That restricts this rule
    /// to datastores without the application layer ever naming one, and keeps
    /// a host's own devices — whose instance is a device rather than an
    /// observer — out of a rule whose only fix would not apply to them.
    /// </remarks>
    private static bool IsLatency(CounterValue value) =>
        value.InstanceIsVantagePoint &&
        Readings.IsMilliseconds(value.Unit);

    /// <summary>
    /// How much work this volume was asked for, or <c>null</c> if nobody looked.
    /// </summary>
    /// <remarks>
    /// The shape <see cref="SharedVolumeLatency"/> already established for a
    /// demand counter — a rate, averaged, from a vantage point, not a fault —
    /// summed across reads, writes and hosts. Null and zero are kept apart
    /// deliberately: zero is an idle volume and the answer is silence, null is
    /// an uncollected counter and the answer is also silence, but for a
    /// different reason that a future reader needs to be able to tell apart.
    /// </remarks>
    private static double? Load(IEnumerable<Observation> volume)
    {
        double? total = null;

        foreach (var observation in volume)
        {
            var value = observation.Value;

            if (!value.InstanceIsVantagePoint ||
                value.Rollup != RollupType.Average ||
                value.IsFaultCount ||
                !string.Equals(value.Unit, "number", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total = (total ?? 0d) + value.Raw;
        }

        return total;
    }

    /// <summary>
    /// The highest SIOC active time any host saw, or <c>null</c> if nobody looked.
    /// </summary>
    /// <remarks>
    /// The maximum rather than the sum or the mean, because the question is
    /// whether the channel ran at all. SIOC is a property of the datastore, so
    /// one host seeing it active is enough to say the product is not blind
    /// here — and a mean across hosts would let one busy host's throttling be
    /// averaged away by nine quiet ones into a false blind spot.
    /// </remarks>
    private static double? Sioc(
        IEnumerable<Observation> volume, StorageLatencyBlindSpotPolicy rules)
    {
        double? highest = null;

        foreach (var observation in volume)
        {
            if (Readings.IsCounter(observation.Value.CounterName, rules.SiocCounter))
            {
                highest = Math.Max(highest ?? double.MinValue, observation.Value.Raw);
            }
        }

        return highest;
    }

    private static AlertDefinition Alert(EntityId volume, int blind, int considered, Judgement judgement) =>
        new()
        {
            Fingerprint = FingerprintOf(volume),

            // Warning, and the precedent is GuardedRule's rather than a storage
            // rule's: we do not know the estate is broken, only that we have
            // stopped looking at part of it. Critical would claim this volume
            // is in trouble, which is the one thing this rule must never imply
            // — it may be the fastest volume in the estate.
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description = Describe(blind, considered, judgement),
            Category = Category,
            Source = Platform,
            Entity = volume,

            // Concluded by the product from an absence, not observed by a
            // collector. It also keeps the blind spot out of flap tracking,
            // which is right: a volume that drops below the load floor on a
            // quiet cycle would otherwise have the product reporting that its
            // own uncertainty is unstable, and there is nothing an operator can
            // do with that.
            IsDerived = true,
        };

    private static string Describe(int blind, int considered, Judgement judgement)
    {
        // Over a window the claim is a share, not "every counter reads zero":
        // some of those cycles may have measured, just too few of them.
        var opening = judgement.Window > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"This volume is carrying I/O and its latency counters keep reading zero —a latency was measurable in only {judgement.Measurable} of the last {judgement.Window} busy cycles — while ")
            : "This volume is carrying I/O and every latency counter on it reads zero, while ";

        // How much of the estate is in this state, and therefore whether the
        // operator is looking at one volume's setting or at an estate that was
        // never configured. The whole argument for forty-one alerts instead of
        // one rests on this sentence being in each of them.
        var scale = blind >= considered && considered > 1
            ? $"Every one of the {considered} volumes measured this cycle is in this state, " +
              "which points at an estate-wide default rather than at this volume: set Storage " +
              "I/O Control once, as policy, rather than volume by volume."
            : $"{blind} of the {considered} volume(s) measured this cycle are in this state.";

        return
            opening +
            "Storage I/O Control reports no active time. That is not a fast volume: the " +
            "platform reports these counters in whole milliseconds, so anything below one " +
            "millisecond truncates to zero, and the one counter reported finely enough to " +
            "tell the difference only reports while Storage I/O Control is running. This " +
            "volume's latency may be excellent or may be poor — the product does not know, " +
            "and it is saying so rather than showing you a zero that looks like a " +
            $"measurement. {scale} Check whether Storage I/O Control is enabled on this " +
            "datastore and enable it if not. Until it is, the three storage rules this " +
            "product ships — slow from one host, slow from every host, and which layer is " +
            "the bottleneck — are structurally unable to fire on this volume, because all " +
            "three need a latency above zero to have anything to judge.";
    }
}

/// <summary>
/// Each volume's last judged cycles for <see cref="StorageLatencyBlindSpot"/>:
/// whether the measurement worked in each.
/// </summary>
/// <remarks>
/// <para>
/// In memory, held by the rule's adapter, which lives as long as the process
/// (<see cref="AnalysisRules.All"/>). It does not survive a restart, and that
/// is accepted rather than engineered around: after a restart every volume's
/// window is empty and the rule says NotJudgeable until K judged cycles have
/// been seen again — ten minutes at K = 20 and one busy cycle every 30 s. An
/// alert already open stays open (and stale) meanwhile; nothing is resolved
/// or raised on an empty window.
/// </para>
/// <para>
/// Bounded twice: each volume keeps at most K entries, and past
/// <see cref="MaxVolumes"/> volumes the whole thing is emptied rather than
/// managed, which costs one refill and nothing else.
/// </para>
/// </remarks>
public sealed class StorageLatencyBlindSpotWindow
{
    private readonly Dictionary<EntityId, Queue<bool>> _volumes = [];
    private readonly Lock _gate = new();

    public StorageLatencyBlindSpotWindow(int maxVolumes = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVolumes, 1);
        MaxVolumes = maxVolumes;
    }

    public int MaxVolumes { get; }

    /// <summary>How many volumes have a window.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _volumes.Count;
            }
        }
    }

    /// <summary>
    /// Adds one judged cycle to a volume's window and returns what the window
    /// now holds.
    /// </summary>
    /// <param name="volume">The volume judged.</param>
    /// <param name="measurable">Whether the measurement worked this cycle.</param>
    /// <param name="capacity">K; older entries beyond it are dropped.</param>
    /// <returns>How many of the held cycles were measurable, and how many are held.</returns>
    public (int Measurable, int Held) Record(EntityId volume, bool measurable, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        lock (_gate)
        {
            if (!_volumes.TryGetValue(volume, out var cycles))
            {
                if (_volumes.Count >= MaxVolumes)
                {
                    _volumes.Clear();
                }

                cycles = new Queue<bool>(capacity);
                _volumes[volume] = cycles;
            }

            cycles.Enqueue(measurable);

            while (cycles.Count > capacity)
            {
                cycles.Dequeue();
            }

            return (cycles.Count(c => c), cycles.Count);
        }
    }
}
