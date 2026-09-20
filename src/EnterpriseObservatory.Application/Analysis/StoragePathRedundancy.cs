using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which of the platform's words for a path's state mean gone, and which mean working.
/// </summary>
/// <remarks>
/// <para>
/// The state words are policy rather than constants, and for the reason
/// <see cref="StorageLayerPolicy"/> already argued about counter names: a
/// vim25 vocabulary compiled into the application layer is the first thing
/// that has to change when a second collector arrives. An array or a switch
/// collector that words its path states differently is configured in rather
/// than requiring this file to be edited.
/// </para>
/// <para>
/// Two lists rather than one, and that is the whole design. A path is not a
/// boolean. <c>active</c> and <c>standby</c> are working, <c>dead</c> is gone,
/// and <c>disabled</c>, <c>unknown</c> and an unreported state are
/// <em>neither</em> — an operator's decision and a platform's shrug
/// respectively, and both would be libel if counted as a failure. Writing
/// "working" as "not failed" would collapse that third bucket into the second
/// and have the rule report an outage every time vCenter declined to answer.
/// </para>
/// <para>
/// <see cref="StoragePath.IsDead"/> is the same judgement, already made in the
/// domain, and it is deliberately not called here: it can only answer the
/// failed half, and taking the failed half from the domain and the working
/// half from policy would spread three buckets across two layers. The two are
/// pinned to each other by a test instead, so the duplication cannot drift.
/// </para>
/// <para>
/// There is no numeric threshold in this rule and none is wanted. The one
/// number it needs — how many paths — is counted from the table itself, and
/// the only comparison made against it is with one, which is arithmetic rather
/// than a judgement somebody would later have to defend.
/// </para>
/// </remarks>
public sealed record StoragePathRedundancyPolicy
{
    /// <summary>
    /// The states meaning this route no longer exists.
    /// </summary>
    /// <remarks>
    /// Only <c>dead</c>, matching <see cref="StoragePath.IsDead"/>. It is the
    /// platform's own word for a path it tried and could not use, and it is
    /// the only one of the five that asserts a failure rather than a setting
    /// or an absence of knowledge.
    /// </remarks>
    public IReadOnlyList<string> FailedStates { get; init; } = ["dead"];

    /// <summary>
    /// The states meaning this route can still carry the device's I/O.
    /// </summary>
    /// <remarks>
    /// <c>standby</c> belongs here beside <c>active</c> and the rule is wrong
    /// in both directions without it. On an ALUA array most paths are standby
    /// at any moment — working and unused — so counting them as lost would
    /// raise this alert across the estate on day one, and counting them as
    /// absent would raise the severe verdict on devices that are fully
    /// protected.
    /// </remarks>
    public IReadOnlyList<string> WorkingStates { get; init; } = ["active", "standby"];

    public static StoragePathRedundancyPolicy Default { get; } = new();
}

/// <summary>
/// Says that a host has lost a route to a storage device while the device still works.
/// </summary>
/// <remarks>
/// <para>
/// The fault this product exists for. A device is reached over several paths
/// precisely so that losing one costs nothing, which is exactly why losing one
/// is silent: no counter moves, no latency rises, no machine complains. The
/// estate runs on one leg for as long as it takes for that leg to go too, and
/// then every machine on the volume stops at once. Nothing an operator can
/// feel changes between the first failure and the second, so nothing but a
/// monitoring tool counting paths will ever say it happened.
/// </para>
/// <para>
/// vROps classifies this as Health, Immediate. That is citable as an intent
/// about how much it matters and about nothing else — it is not a number, and
/// there is no number here to borrow it for.
/// </para>
/// <para>
/// Two verdicts, and they are different facts rather than two intensities of
/// one. <see cref="LostTitle"/> is a loss of protection with the service
/// intact; <see cref="DownTitle"/> is a loss of the device. They carry
/// different severities, different sentences and different urgencies, and
/// because the title is in the fingerprint a device that goes from the first
/// to the second raises a new alert rather than quietly relabelling the old —
/// the same choice <see cref="StorageLayerSplit"/> makes, for the same reason:
/// an alert history claiming the device had been down all along would be a lie
/// told by an optimisation.
/// </para>
/// <para>
/// A third case was considered and deliberately produces nothing: a device
/// that only ever had one path. It has not lost redundancy, it never had any,
/// and the two are not the same finding. Every host carries local disks, boot
/// devices and USB media with exactly one path by design, so reporting them
/// would put a permanent alert on every host in the estate that no operator
/// can ever clear — the noise the fourth principle calls the operator's enemy.
/// It is also the wrong shape of thing: "this LUN should have two paths" is
/// §2's second question, a configuration finding with a lifetime of months
/// that a human either fixes or accepts, and §2 is explicit that a finding
/// poured into the alert box violates principle 4 on the first day. It belongs
/// to the compliance engine §9 records as not yet built, beside "yol sayısı ≥
/// 2" in §3's cross-validation column, and it is left there rather than
/// smuggled in as an alarm.
/// </para>
/// <para>
/// The exclusion costs nothing in coverage and needs no gate to implement,
/// which is the part worth noticing. Redundancy lost requires at least one
/// failed path <em>and</em> at least one working one, and one path cannot be
/// both — so the single-path device falls out of the arithmetic. A single-path
/// device whose one path dies still reports, as <see cref="DownTitle"/>,
/// because that is not a statement about redundancy at all.
/// </para>
/// <para>
/// Per host and per device, because that is where the fix is. A path is one
/// host's route to one LUN, so two hosts losing a path to the same array
/// volume are two cables, two HBAs and two visits; on the live estate a single
/// LUN is reached from ten hosts over twenty paths, and one alert saying "the
/// estate has lost paths" would name none of them. The adapter is in the
/// description for the same reason: four dead paths on one adapter is a cable
/// or an SFP, four spread across four adapters is the array.
/// </para>
/// <para>
/// How loud this is on the live estate: ten hosts carrying about thirty-two
/// devices each is roughly three hundred host-device pairs, and every one of
/// them is silent unless the platform has said <c>dead</c>. A healthy estate
/// produces nothing at all; the reference research's estimate of nought to
/// three findings is what a fabric with one flaking SFP looks like, and a
/// single failed HBA would produce as many findings as that host has devices —
/// which is correct rather than noisy, because each one is a LUN whose
/// redundancy really did halve, and they all name the same adapter.
/// </para>
/// <para>
/// Inventory, not measurement. The path table is read on the inventory rhythm
/// and judged in the inventory scope, which is also why this rule adds no
/// collection call of its own: <c>config.storageDevice.multipathInfo</c> and
/// <c>config.storageDevice.scsiLun</c> are already requested and already reach
/// the domain.
/// </para>
/// <para>
/// <strong>What is unverified.</strong> The reader that produces these rows
/// has never been pointed at a live vCenter — <see cref="StoragePath"/> says
/// so, and the one structure in that collector that was dumped from a real
/// server did not match what its name implied. So the identity chain this rule
/// stands on is measured (41 of 41 datastores carry both the VMFS UUID and the
/// LUN NAA) while the path states it reads are schema-shaped and not yet
/// observed. If <c>pathState</c> comes back with vocabulary nobody expected,
/// this rule goes quiet rather than wrong: an unrecognised state is in neither
/// list and is judged as neither.
/// </para>
/// </remarks>
public static class StoragePathRedundancy
{
    /// <summary>
    /// The category the counter map's path counters would land in too.
    /// </summary>
    /// <remarks>
    /// Distinct from "Storage layer" and "Storage array" because it names a
    /// different place to go and look: a cable, an SFP, a switch port or a
    /// zone, rather than a queue depth or a volume on the array.
    /// </remarks>
    public const string Category = "Storage path";

    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "storage-path-redundancy";

    private const string LostTitle = "Storage path redundancy lost";
    private const string DownTitle = "No working path to storage device";

    /// <summary>
    /// Used when the entity carries no collector of its own.
    /// </summary>
    /// <remarks>
    /// The fingerprint's first part must never be blank: two collectors'
    /// findings about identically named objects would otherwise collide into
    /// one alert, and one of them would silently disappear.
    /// </remarks>
    private const string Platform = "platform";

    /// <summary>
    /// Every device a host has lost a route to.
    /// </summary>
    /// <param name="entities">
    /// The estate as the graph currently holds it. Only hosts are judged, and
    /// only hosts we are currently looking at.
    /// </param>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Entity> entities,
        StoragePathRedundancyPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var rules = policy ?? StoragePathRedundancyPolicy.Default;
        var alerts = new List<AlertDefinition>();

        foreach (var host in entities.Where(Judgeable))
        {
            // Grouped by the device rather than by the adapter, because
            // redundancy is a property of the route to a thing and not of the
            // card. DeviceKey is the fallback the domain carries for exactly
            // this: when the NAA lookup failed, piling every unnamed path in
            // the host onto one empty key would report a single enormous
            // device that does not exist — and a failed lookup is
            // disproportionately the case where something is already wrong, so
            // it is the last case in which to stop counting.
            var devices = host.StoragePaths
                .Where(p => Identify(p).Length > 0)
                .GroupBy(Identify, StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                if (Verdict(host, device.Key, [.. device], rules) is { } found)
                {
                    alerts.Add(found);
                }
            }
        }

        return alerts;
    }

    /// <summary>
    /// An entity whose path table this rule is entitled to judge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kind is checked rather than relied upon. Nothing but a host carries
    /// paths today, but a future collector hanging a path table off an array
    /// or a switch would otherwise be folded silently into a rule whose every
    /// sentence is written about a host.
    /// </para>
    /// <para>
    /// Only <see cref="ObservationState.Active"/>. A vanished host's table is
    /// the last one we saw rather than the current one, and re-reporting it
    /// would have the product making claims about an estate it can no longer
    /// see — principle 1's failure exactly. A host in maintenance is excluded
    /// for the opposite reason: its paths may well be down and that is
    /// expected, because maintenance is when somebody is moving the cables.
    /// </para>
    /// </remarks>
    private static bool Judgeable(Entity entity) =>
        entity.Kind == EntityKind.EsxiHost &&
        entity.ObservationState == ObservationState.Active &&
        entity.StoragePaths.Count > 0;

    /// <summary>
    /// What to call the device a path leads to, or nothing when it cannot be named.
    /// </summary>
    /// <remarks>
    /// The NAA when the second table resolved it, because that is what the
    /// datastore is marked with and what makes this alert joinable to the
    /// volume and the machines on it. The platform's own key otherwise, which
    /// names nothing outside this host but still counts correctly. Empty when
    /// neither was readable, and then the path is dropped rather than heaped
    /// with the others.
    /// </remarks>
    private static string Identify(StoragePath path) =>
        path.StorageDeviceId.Length > 0 ? path.StorageDeviceId : path.DeviceKey;

    /// <summary>
    /// What this host's routes to one device add up to.
    /// </summary>
    /// <remarks>
    /// Failed is tried first and the two branches are exclusive, so a device
    /// produces at most one verdict. Both need at least one failed path: a
    /// device with no failure reported is not this rule's business whatever
    /// its paths say, which is what keeps every correctly configured
    /// single-path device and every device whose states came back unknown
    /// entirely silent.
    /// </remarks>
    private static AlertDefinition? Verdict(
        Entity host,
        string device,
        IReadOnlyList<StoragePath> paths,
        StoragePathRedundancyPolicy rules)
    {
        var failed = paths.Where(p => Matches(p, rules.FailedStates)).ToList();

        if (failed.Count == 0)
        {
            return null;
        }

        var working = paths.Count(p => Matches(p, rules.WorkingStates));

        return working == 0
            ? Alert(host, device, DownTitle, AlertSeverity.Critical, DescribeDown(device, failed))
            : Alert(
                host, device, LostTitle, AlertSeverity.Warning,
                DescribeLost(device, failed, working, paths.Count));
    }

    private static bool Matches(StoragePath path, IReadOnlyList<string> states) =>
        states.Contains(path.State, StringComparer.OrdinalIgnoreCase);

    private static AlertDefinition Alert(
        Entity host, string device, string title, AlertSeverity severity, string description) =>
        new()
        {
            // Host and device together. The device alone would merge ten
            // hosts' separate cables into one alert; the host alone would
            // merge its thirty-two LUNs, and then an operator could not
            // acknowledge the one being decommissioned without silencing the
            // one that matters.
            Fingerprint = AlertFingerprint.Create(
                Attribution(host), title, Category,
                $"{host.Id.Value}/{device}", RuleId),
            Severity = severity,
            Title = title,
            Description = description,
            Category = Category,
            Source = Attribution(host),
            Entity = host.Id,

            // Not derived, unlike CpuContention's verdicts and the blind spot.
            // Those are conclusions the product reached about numbers nobody
            // reported; this is the platform's own word for the state of its
            // own path, counted rather than inferred. The practical
            // consequence is the one that decides it: derived alerts are
            // excluded from flap tracking, and a path that dies and revives
            // across cycles is a marginal SFP — exactly the thing an operator
            // most wants told it is unstable.
        };

    /// <summary>
    /// The collector that reported this host, or the platform when none did.
    /// </summary>
    private static string Attribution(Entity host) =>
        string.IsNullOrWhiteSpace(host.SourceInstanceId) ? Platform : host.SourceInstanceId;

    private static string Adapters(List<StoragePath> failed)
    {
        var named = failed
            .Select(p => p.Adapter)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return named.Count > 0 ? string.Join(", ", named) : "not reported";
    }

    private static string Names(List<StoragePath> failed) =>
        string.Join(", ", failed.Select(p => p.Name).Order(StringComparer.Ordinal));

    private static string DescribeLost(
        string device, List<StoragePath> failed, int working, int total) =>
        $"This host has {failed.Count} of {total} path(s) to device {device} reported dead, and " +
        $"{working} still working. Nothing is broken for the machines on this device right now " +
        "and nothing will look broken: the surviving path carries the load, no latency counter " +
        "moves, and the estate stays exactly like this until the last working path goes too — at " +
        "which point every machine whose disks live here stops at once. This is the warning " +
        $"before that. The dead path(s) are {Names(failed)}, leaving by adapter(s) " +
        $"{Adapters(failed)}. Several dead paths on one adapter point at that card, its cable or " +
        "its SFP; dead paths spread across adapters point at the fabric or the array. Check the " +
        "switch port and the zoning for each, and restore the path rather than waiting for the " +
        "survivor to prove the point.";

    private static string DescribeDown(string device, List<StoragePath> failed) =>
        $"Every path this host has to device {device} is reported dead — {failed.Count} of them, " +
        $"{Names(failed)}, by adapter(s) {Adapters(failed)}. This is not a loss of redundancy but " +
        "a loss of the device: any machine on this host whose disks live here has already stopped " +
        "or is about to. No path is left to fail over to, so nothing will recover this by itself. " +
        "Check whether the device has been removed or unmasked on the array deliberately, and " +
        "whether other hosts still reach it — if they do, this host's adapters, cabling or zoning " +
        "are the place to look; if they do not, it is the array.";
}
