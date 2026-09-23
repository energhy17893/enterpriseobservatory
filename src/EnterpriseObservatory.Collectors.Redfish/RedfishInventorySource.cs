using System.Globalization;
using System.Text.Json;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Redfish;

/// <summary>
/// What the Redfish source needs to know about the ESXi hosts vSphere owns,
/// to fold an iLO onto one (ADR-0027 §4). Answered by the host from the
/// graph; the collector never reads it (ADR-0025).
/// </summary>
public interface IRedfishFoldingDirectory
{
    /// <summary>The ESXi host marked with this hardware UUID (<c>hardware.systemInfo.uuid</c>), any case, or null.</summary>
    EntityId? HostByHardwareUuid(string uuid);

    /// <summary>The ESXi host marked with this serial number, or null.</summary>
    EntityId? HostBySerialNumber(string serial);
}

/// <summary>
/// One HPE iLO 5/6 over Redfish, folded onto the ESXi host it manages as
/// <c>redfish.*</c> annotations (ADR-0027). No entity of its own; inventory
/// only.
/// </summary>
/// <remarks>
/// <para>
/// Every cycle, three GETs in parallel (§10.7, measured 0.3–1.0 s each):
/// <c>Systems/1</c> (identity and <c>Oem.Hpe.AggregateHealthStatus</c>,
/// present on iLO 5 too), <c>Chassis/1/Power</c>, <c>Chassis/1/Thermal</c>.
/// The deep walk — storage controllers and drives, the DIMM and firmware
/// collections, and the IML since the last <c>Created</c> watermark — runs
/// on the first read, once a day, and at once when the aggregate health
/// leaves OK. Its result is kept in this object and re-emitted every cycle,
/// so a drive alert stays raised between walks. Nothing is persisted: a
/// restart walks again on its first read.
/// </para>
/// <para>
/// Fold (ADR-0027, M6.2 identity decision): <c>Systems/1.UUID</c> against
/// the host's <c>hardware.systemInfo.uuid</c>, as reported (case-insensitive)
/// or with its first three groups byte-reversed (SMBIOS 2.6+ encodes those
/// little-endian, and tools disagree on which form they print); the serial
/// number only when neither matches. The rule that matched is annotated as
/// <c>redfish.fold_rule</c>. No match is a "could not fold" failure; the
/// hardware alerts are still raised, on the iLO rather than on a host.
/// </para>
/// <para>
/// A field an alert rests on that the iLO did not answer is Unknown
/// (ADR-0026): counted as coverage, annotated as absent, never OK and never
/// an alert.
/// </para>
/// </remarks>
public sealed class RedfishInventorySource(
    string instanceId,
    RedfishChannel channel,
    IRedfishFoldingDirectory directory,
    IClock clock) : IInventorySource
{
    /// <summary>The annotation namespace (ADR-0027).</summary>
    public const string Namespace = "redfish";

    /// <summary>How long a deep walk's result stands while the aggregate health stays OK.</summary>
    public static readonly TimeSpan DeepWalkInterval = TimeSpan.FromDays(1);

    public const string SystemPath = "/redfish/v1/Systems/1";
    public const string PowerPath = "/redfish/v1/Chassis/1/Power";
    public const string ThermalPath = "/redfish/v1/Chassis/1/Thermal";
    public const string ImlPath = "/redfish/v1/Systems/1/LogServices/IML/Entries";

    private readonly RedfishChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    private readonly IRedfishFoldingDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The last deep walk, replaced whole — an abandoned read can never leave it half-written.</summary>
    private volatile Deep? _deep;

    private volatile string? _lastAggregate;

    public string InstanceId { get; } = instanceId ?? throw new ArgumentNullException(nameof(instanceId));

    /// <summary>
    /// When the last deep walk ran. Compared against a read's <c>ReadAtUtc</c>
    /// by a caller that wants to know whether that particular read triggered
    /// one, rather than re-emitting a walk from a previous cycle.
    /// </summary>
    public DateTimeOffset? LastDeepWalkAtUtc => _deep?.At;

    /// <summary>
    /// What a read produced, for a log line (HostLog.RedfishRead) — never the
    /// UUID or serial number the fold matched on, only the rule's name.
    /// </summary>
    public readonly record struct ReadSummary(string Host, string FoldRule, int Alerts, bool DeepWalk);

    /// <summary>Summarizes a snapshot this source just returned from <see cref="ReadAsync"/>.</summary>
    public ReadSummary Summarize(InventorySnapshot snapshot)
    {
        var annotation = snapshot.Annotations.Count > 0 ? snapshot.Annotations[0] : null;

        return new ReadSummary(
            annotation?.Entity.Value ?? "not folded",
            annotation?.Settings.GetValueOrDefault($"{Namespace}.fold_rule") ?? "none",
            snapshot.Alerts.Count,
            LastDeepWalkAtUtc == snapshot.ReadAtUtc);
    }

    public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var reads = new[] { SystemPath, PowerPath, ThermalPath }.Select(p => GetAsync(p, cancellationToken)).ToArray();
        var documents = await Task.WhenAll(reads).ConfigureAwait(false);
        var (system, power, thermal) = (documents[0], documents[1], documents[2]);
        var now = _clock.UtcNow;

        var aggregate = Text(system, "Oem", "Hpe", "AggregateHealthStatus", "AggregateServerHealth");
        var previous = _lastAggregate;
        _lastAggregate = aggregate;

        var deep = _deep;
        if (deep is null || now - deep.At >= DeepWalkInterval || (previous == "OK" && aggregate != "OK"))
        {
            deep = await DeepWalkAsync(deep, now, cancellationToken).ConfigureAwait(false);
            _deep = deep;
        }

        var read = new Read(this);
        read.Fold(Text(system, "UUID"), Text(system, "SerialNumber"));
        read.System(system, aggregate);
        read.Power(power);
        read.Thermal(system, thermal);
        read.FromDeepWalk(deep);

        return new InventorySnapshot
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Annotations = read.Annotation() is { } annotation ? [annotation] : [],
            Alerts = read.Alerts,
            Failures = [.. read.Failures, .. deep.Failures],
            Coverage = [.. read.Tally.Coverage(), .. deep.Coverage],
            ViewsHeld = 0,
        };
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await _channel.GetAsync(path, cancellationToken).ConfigureAwait(false);

        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Clone()
            : throw new RedfishApiException(CollectionFailureKind.ProtocolError, $"GET {path} answered something that is not an object.");
    }

    /// <summary>Storage and drives, the DIMM and firmware collections, and the IML since the watermark.</summary>
    /// <remarks>
    /// 20 calls on Kibar (2 controllers, 14 drives), bounded by the
    /// channel's gate. A resource that fails is a named partial failure; the
    /// rest of the walk still counts. ponytail: DIMMs and firmware are
    /// counted from their collections, not walked member by member (60 more
    /// calls); AggregateHealthStatus.Memory covers DIMM health until a rule
    /// needs one DIMM's state.
    /// </remarks>
    private async Task<Deep> DeepWalkAsync(Deep? previous, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failures = new List<CollectionFailure>();
        var tally = new Tally();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);

        async Task<JsonElement?> TryGetAsync(string path)
        {
            try
            {
                return await GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (RedfishApiException ex)
            {
                lock (failures)
                {
                    failures.Add(new CollectionFailure { Kind = ex.Kind, Target = $"iLO '{InstanceId}' {path}", Detail = ex.Message });
                }

                return null;
            }
        }

        var storage = await TryGetAsync("/redfish/v1/Systems/1/Storage").ConfigureAwait(false);
        var controllers = (await Task.WhenAll(Links(storage, "Members").Select(TryGetAsync)).ConfigureAwait(false))
            .Where(c => c is not null).Select(c => c!.Value).ToList();
        var drives = (await Task.WhenAll(controllers.SelectMany(c => Links(c, "Drives")).Select(TryGetAsync)).ConfigureAwait(false))
            .Where(d => d is not null).Select(d => d!.Value).ToList();

        var predicted = new List<(string Id, string Name)>();
        foreach (var drive in drives)
        {
            if (tally.Judged("Drive", "FailurePredicted", Text(drive, "FailurePredicted")) == "true")
            {
                predicted.Add((Text(drive, "@odata.id") ?? "?", Text(drive, "Name") ?? Text(drive, "Id") ?? "?"));
            }
        }

        if (storage is not null)
        {
            settings["storage.controllers"] = Number(controllers.Count);
            settings["storage.drives"] = Number(drives.Count);
            settings["storage.failure_predicted"] = Number(predicted.Count);
        }

        settings["memory.dimms"] = Members(await TryGetAsync("/redfish/v1/Systems/1/Memory").ConfigureAwait(false));
        settings["firmware.components"] = Members(await TryGetAsync("/redfish/v1/UpdateService/FirmwareInventory").ConfigureAwait(false));

        // Incremental: only entries Created after the newest one already
        // seen (iLO 5 honours $filter, measured 200 in 3.4 s). ponytail: an
        // entry later marked Repaired keeps its Created and is not re-read,
        // and a cleared IML is not noticed until a restart; both matter only
        // once IML entries become findings (M6.4).
        var ids = previous?.ImlIds ?? [];
        var watermark = previous?.ImlWatermark ?? DateTimeOffset.UnixEpoch;
        var filter = Uri.EscapeDataString(string.Create(
            CultureInfo.InvariantCulture, $"Created gt '{watermark.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'"));

        if (await TryGetAsync($"{ImlPath}?$filter={filter}").ConfigureAwait(false) is { } iml)
        {
            ids = [.. ids];
            foreach (var entry in Array(iml, "Members"))
            {
                if (Text(entry, "Id") is { } id)
                {
                    ids.Add(id);
                }

                if (DateTimeOffset.TryParse(Text(entry, "Created"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created) && created > watermark)
                {
                    watermark = created;
                }
            }
        }

        if (ids.Count > 0)
        {
            settings["iml.entries"] = Number(ids.Count);
            settings["iml.newest_utc"] = watermark.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        settings["deep_walk_utc"] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        return new Deep(now, settings, predicted, failures, tally.Coverage(), ids, watermark);
    }

    /// <summary>
    /// <c>00112233-4455-6677-…</c> → <c>33221100-5544-7766-…</c>: the SMBIOS
    /// UUID's first three fields in the other byte order.
    /// </summary>
    public static string? ByteSwapped(string uuid)
    {
        var parts = uuid.Trim().Split('-');
        if (parts is not [{ Length: 8 }, { Length: 4 }, { Length: 4 }, { Length: 4 }, { Length: 12 }])
        {
            return null;
        }

        static string Reverse(string hex) =>
            string.Concat(Enumerable.Range(0, hex.Length / 2).Reverse().Select(i => hex.Substring(i * 2, 2)));

        return string.Join('-', Reverse(parts[0]), Reverse(parts[1]), Reverse(parts[2]), parts[3], parts[4]);
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string? Members(JsonElement? collection) =>
        collection is { } c && c.TryGetProperty("Members@odata.count", out var count) && count.ValueKind == JsonValueKind.Number
            ? count.GetRawText()
            : collection is { } d && d.TryGetProperty("Members", out var m) && m.ValueKind == JsonValueKind.Array
                ? Number(m.GetArrayLength())
                : null;

    private static JsonElement? At(JsonElement? element, params string[] path)
    {
        if (element is not { } e)
        {
            return null;
        }

        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e))
            {
                return null;
            }
        }

        return e;
    }

    private static string? Text(JsonElement? element, params string[] path) =>
        At(element, path) is { } v
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    private static double? Double(JsonElement element, string name) =>
        At(element, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

    private static JsonElement[] Array(JsonElement? element, string name) =>
        At(element, name) is { ValueKind: JsonValueKind.Array } a ? [.. a.EnumerateArray()] : [];

    private static IEnumerable<string> Links(JsonElement? element, string name) =>
        Array(element, name).Select(l => Text(l, "@odata.id")).OfType<string>();

    private static string? Count(JsonElement element, string name) =>
        At(element, name) is { ValueKind: JsonValueKind.Array } a ? Number(a.GetArrayLength()) : null;

    /// <summary>One deep walk's result, re-emitted every cycle until the next one.</summary>
    private sealed record Deep(
        DateTimeOffset At,
        Dictionary<string, string?> Settings,
        List<(string Id, string Name)> FailurePredicted,
        List<CollectionFailure> Failures,
        List<PropertyCoverage> Coverage,
        HashSet<string> ImlIds,
        DateTimeOffset ImlWatermark);

    /// <summary>Per (object type, field): objects judged, and how many carried the field (ADR-0026).</summary>
    private sealed class Tally
    {
        private readonly Dictionary<(string Type, string Field), (int Asked, int Answered)> _read = [];

        public string? Judged(string type, string field, string? value)
        {
            var (asked, answered) = _read.GetValueOrDefault((type, field));
            _read[(type, field)] = (asked + 1, answered + (value is null ? 0 : 1));
            return value;
        }

        public List<PropertyCoverage> Coverage() =>
        [
            .. _read.OrderBy(r => r.Key.Type, StringComparer.Ordinal).ThenBy(r => r.Key.Field, StringComparer.Ordinal)
                .Select(r => new PropertyCoverage
                {
                    ObjectType = r.Key.Type,
                    Property = r.Key.Field,
                    Asked = r.Value.Asked,
                    Answered = r.Value.Answered,
                }),
        ];
    }

    /// <summary>One cycle's fold, annotation values, alerts and failures.</summary>
    private sealed class Read(RedfishInventorySource source)
    {
        private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal);
        private EntityId? _host;

        public Tally Tally { get; } = new();

        public List<AlertDefinition> Alerts { get; } = [];

        public List<CollectionFailure> Failures { get; } = [];

        public void Fold(string? uuid, string? serial)
        {
            var directory = source._directory;

            if (uuid is { Length: > 0 } && directory.HostByHardwareUuid(uuid) is { } direct)
            {
                (_host, _settings["fold_rule"]) = (direct, "uuid");
            }
            else if (uuid is { Length: > 0 } && ByteSwapped(uuid) is { } swapped && directory.HostByHardwareUuid(swapped) is { } bySwap)
            {
                (_host, _settings["fold_rule"]) = (bySwap, "uuid-byte-swapped");
            }
            else if (serial is { Length: > 0 } && directory.HostBySerialNumber(serial) is { } bySerial)
            {
                (_host, _settings["fold_rule"]) = (bySerial, "serial");
            }
            else
            {
                // NotConfigured, as SimpliVity's: the iLO answered in full;
                // what is missing is a vSphere host this product reads.
                Failures.Add(new CollectionFailure
                {
                    Kind = CollectionFailureKind.NotConfigured,
                    Target = $"iLO '{source.InstanceId}'",
                    Detail = "could not fold onto a vSphere host: no ESXi host is marked with " +
                             $"UUID '{uuid}' (as reported or SMBIOS byte-swapped) or serial '{serial}'",
                });
            }
        }

        public void System(JsonElement system, string? aggregate)
        {
            _settings["model"] = Text(system, "Model");
            _settings["serial_number"] = Text(system, "SerialNumber");
            _settings["bios_version"] = Text(system, "BiosVersion");
            _settings["power_state"] = Text(system, "PowerState");
            _settings["health"] = Text(system, "Status", "Health");
            _settings[Key(InventoryVerdictKeys.RedfishAggregateHealth)] = Tally.Judged("System", "AggregateServerHealth", aggregate);
            _settings["ams"] = Text(system, "Oem", "Hpe", "AggregateHealthStatus", "AgentlessManagementService");
        }

        public void Power(JsonElement power)
        {
            // Redundancy[] per group; any group not OK is lost redundancy,
            // any group without a Health is Unknown for all of it.
            var groups = Array(power, "Redundancy").Select(g => Text(g, "Status", "Health")).ToList();
            var redundancy = groups.Count == 0 || groups.Any(h => h is null)
                ? null
                : groups.FirstOrDefault(h => h != "OK") ?? "OK";

            _settings["psu.count"] = Count(power, "PowerSupplies");
            _settings[Key(InventoryVerdictKeys.RedfishPsuRedundancy)] = Tally.Judged("Power", "Redundancy.Status.Health", redundancy);

            if (redundancy is not (null or "OK"))
            {
                Raise(AlertSeverity.Warning, "Power supply redundancy lost", "redfish-psu-redundancy",
                    $"The iLO reports power supply redundancy as {redundancy}.");
            }
        }

        public void Thermal(JsonElement system, JsonElement thermal)
        {
            // iLO 5's Thermal has no Redundancy[] (measured); the aggregate
            // status carries fan redundancy on both generations.
            var fans = Tally.Judged("System", "FanRedundancy", Text(system, "Oem", "Hpe", "AggregateHealthStatus", "FanRedundancy"));
            _settings["fan.count"] = Count(thermal, "Fans");
            _settings[Key(InventoryVerdictKeys.RedfishFanRedundancy)] = fans;

            if (fans is not (null or "Redundant"))
            {
                Raise(AlertSeverity.Warning, "Fan redundancy lost", "redfish-fan-redundancy",
                    $"The iLO reports fan redundancy as {fans}.");
            }

            var atCritical = 0;
            foreach (var sensor in Array(thermal, "Temperatures").Where(t => Text(t, "Status", "State") == "Enabled"))
            {
                var reading = Double(sensor, "ReadingCelsius");
                var critical = Double(sensor, "UpperThresholdCritical");
                Tally.Judged("Temperature", "ReadingCelsius", reading?.ToString(CultureInfo.InvariantCulture));
                Tally.Judged("Temperature", "UpperThresholdCritical", critical?.ToString(CultureInfo.InvariantCulture));

                if (reading >= critical)
                {
                    atCritical++;
                    var name = Text(sensor, "Name") ?? Text(sensor, "MemberId") ?? "?";
                    Raise(AlertSeverity.Critical, "Temperature at critical threshold", $"redfish-temperature:{name}",
                        $"Sensor '{name}' reads {reading} °C, at or above its critical threshold of {critical} °C.");
                }
            }

            _settings["temperature.sensors"] = Count(thermal, "Temperatures");
            _settings["temperature.at_critical"] = Number(atCritical);
        }

        public void FromDeepWalk(Deep deep)
        {
            foreach (var (key, value) in deep.Settings)
            {
                _settings[key] = value;
            }

            foreach (var (id, name) in deep.FailurePredicted)
            {
                Raise(AlertSeverity.Warning, "Drive failure predicted", $"redfish-drive-failure-predicted:{id}",
                    $"The iLO reports FailurePredicted for drive '{name}' ({id}).");
            }
        }

        public EntityAnnotation? Annotation() => _host is { } host
            ? new EntityAnnotation
            {
                Entity = host,
                Namespace = Namespace,
                Settings = _settings
                    .Where(s => s.Value is not null)
                    .ToDictionary(s => $"{Namespace}.{s.Key}", s => s.Value!, StringComparer.OrdinalIgnoreCase),
            }
            : null;

        private static string Key(string key) => key[(Namespace.Length + 1)..];

        /// <summary>On the folded host; on the iLO itself when the fold failed, so no hardware alarm is lost.</summary>
        private void Raise(AlertSeverity severity, string title, string check, string description) =>
            Alerts.Add(new AlertDefinition
            {
                Fingerprint = AlertFingerprint.Create(source.InstanceId, title, "Availability", _host?.Value ?? source.InstanceId, check),
                Severity = severity,
                Title = title,
                Description = description,
                Category = "Availability",
                Source = source.InstanceId,
                Entity = _host,
            });
    }
}
