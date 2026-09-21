using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The judgements that read a host's typed configuration rather than one
/// advanced setting: services, time, standard switch security and lockdown.
/// </summary>
/// <remarks>
/// <para>
/// Each one reads the baseline from the catalogue where the guide words one
/// the product can compare with — <c>Stopped, Start and stop manually</c>,
/// <c>lockdownNormal</c>, <c>Reject</c> — so a re-issued guide that changes it
/// changes the verdict without a code change, and a baseline this product
/// cannot read is said to be unreadable rather than guessed at.
/// </para>
/// <para>
/// Every part the host did not report is
/// <see cref="ComplianceVerdict.NotEvaluated"/>, never failing: null on the
/// entity is "the product could not see", not "the host has none".
/// </para>
/// </remarks>
public static partial class SettingChecks
{
    private const string NtpService = "ntpd";
    private const string PtpService = "ptpd";

    private const string CollectionGap =
        "That is a collection gap, not a pass or a failure; see Collectors for what could be read.";

    private static SettingJudgement JudgeConfiguration(
        SettingCheck check, ComplianceControl control, Entity host) => check.Kind switch
        {
            SettingCheckKind.ServiceMatchesBaseline => JudgeService(check.Setting, control, host),
            SettingCheckKind.TimeServiceMatchesBaseline => JudgeTimeService(control, host),
            SettingCheckKind.TimeSourcesConfigured => JudgeTimeSources(control, host),
            SettingCheckKind.TimeSynchronized => JudgeTimeSynchronized(control, host),
            SettingCheckKind.LockdownAtLeastBaseline => JudgeLockdown(control, host),
            SettingCheckKind.NetworkRejects => JudgeNetwork(check.Setting, control, host),
            _ => NotEvaluated(control.BaselineValue, $"No judgement is defined for '{check.Kind}'."),
        };

    // --- services ----------------------------------------------------------------

    /// <summary>A service's state and start policy, in the platform's policy words.</summary>
    private sealed record ServiceState(bool Running, string Policy);

    private static SettingJudgement JudgeService(string key, ComplianceControl control, Entity host)
    {
        if (ParseServiceBaseline(control.BaselineValue) is not { } wanted)
        {
            return UnreadableServiceBaseline(control);
        }

        var expected = $"{key}: {Describe(wanted)}";

        if (host.Services is null)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its services, so '{key}' was not read. {CollectionGap}");
        }

        var service = Find(host.Services, key);

        if (service is null)
        {
            return NotEvaluated(
                expected,
                $"The host reported its services, but none with the key '{key}', so there is nothing to judge.");
        }

        if (Unread(service) is { } missing)
        {
            return missing with { Expected = expected };
        }

        return Verdict(Meets(service, wanted), expected) with { Observed = Describe(service) };
    }

    /// <summary>
    /// The service that keeps time, in the state the baseline words.
    /// </summary>
    /// <remarks>
    /// A host that says it keeps time with PTP is judged on <c>ptpd</c>, one
    /// that says NTP on <c>ntpd</c>. One that does not say — older than
    /// vSphere 7.0 Update 3 — passes on either, because the guide's own
    /// discussion accepts "NTP and/or PTP" and inferring the protocol from the
    /// version would be this product guessing.
    /// </remarks>
    private static SettingJudgement JudgeTimeService(ComplianceControl control, Entity host)
    {
        if (ParseServiceBaseline(control.BaselineValue) is not { } wanted)
        {
            return UnreadableServiceBaseline(control);
        }

        var keys = TimeServiceKeys(host.TimeConfiguration);
        var expected = keys.Count == 1
            ? $"{keys[0]}: {Describe(wanted)}"
            : $"{NtpService}, or {PtpService} on a host that uses PTP: {Describe(wanted)}";

        if (host.Services is null)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its services, so its time service was not read. {CollectionGap}");
        }

        var services = host.Services;
        var found = keys.Select(k => (Key: k, Service: Find(services, k))).ToList();
        var reported = found.Where(f => f.Service is not null).Select(f => f.Service!).ToList();
        var observed = string.Join("; ", reported.Select(Describe));

        if (reported.Any(s => Meets(s, wanted)))
        {
            return Verdict(true, expected) with { Observed = observed };
        }

        if (found[0].Service is null)
        {
            return NotEvaluated(
                expected,
                $"The host reported its services, but none with the key '{found[0].Key}', so there " +
                "is nothing to judge.");
        }

        if (reported.Select(Unread).FirstOrDefault(u => u is not null) is { } missing)
        {
            return missing with { Expected = expected };
        }

        return Verdict(false, expected) with { Observed = observed };
    }

    /// <summary>
    /// The guide's service baseline — a state and a start policy in the words
    /// the vSphere Client uses — or null when it is not both.
    /// </summary>
    private static ServiceState? ParseServiceBaseline(string baseline)
    {
        bool? running = null;
        string? policy = null;

        foreach (var part in baseline.Split([',', '\n', '\r'], Tidy))
        {
            if (part.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
            {
                running = false;
            }
            else if (part.Equals("Running", StringComparison.OrdinalIgnoreCase))
            {
                running = true;
            }
            else if (PolicyFromWords(part) is { } words)
            {
                policy = words;
            }
            else
            {
                return null;
            }
        }

        return running is { } r && policy is not null ? new ServiceState(r, policy) : null;
    }

    /// <summary>The platform's policy value for the vSphere Client's words.</summary>
    private static string? PolicyFromWords(string words) => words.ToUpperInvariant() switch
    {
        "START AND STOP MANUALLY" => "off",
        "START AND STOP WITH HOST" => "on",
        "START AND STOP WITH PORT USAGE" => "automatic",
        _ => null,
    };

    private static string WordsForPolicy(string policy) => policy.ToUpperInvariant() switch
    {
        "OFF" => "Start and stop manually",
        "ON" => "Start and stop with host",
        "AUTOMATIC" => "Start and stop with port usage",
        _ => "an unrecognised start policy",
    };

    private static bool Meets(HostService service, ServiceState wanted) =>
        service.Running == wanted.Running &&
        string.Equals(service.Policy, wanted.Policy, StringComparison.OrdinalIgnoreCase);

    private static HostService? Find(IReadOnlyList<HostService> services, string key) =>
        services.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A not-evaluated judgement when the service's state or policy is missing.</summary>
    private static SettingJudgement? Unread(HostService service)
    {
        var missing = (service.Running, service.Policy) switch
        {
            (null, null) => "its state or its start policy",
            (null, _) => "its state",
            (_, null) => "its start policy",
            _ => null,
        };

        return missing is null
            ? null
            : NotEvaluated(
                string.Empty,
                $"The host reported '{service.Key}' without {missing}, so it was not judged. {CollectionGap}");
    }

    private static string Describe(ServiceState state) =>
        $"{(state.Running ? "Running" : "Stopped")}, {WordsForPolicy(state.Policy)} (policy '{state.Policy}')";

    private static string Describe(HostService service)
    {
        var state = service.Running switch
        {
            true => "Running",
            false => "Stopped",
            null => "state not reported",
        };

        var policy = service.Policy is null
            ? "start policy not reported"
            : $"{WordsForPolicy(service.Policy)} (policy '{service.Policy}')";

        return $"{service.Key}: {state}, {policy}";
    }

    private static SettingJudgement UnreadableServiceBaseline(ComplianceControl control) => NotEvaluated(
        control.BaselineValue,
        $"The guide's baseline '{Flat(control.BaselineValue)}' is not a service state and start " +
        "policy this product can compare with.");

    // --- time --------------------------------------------------------------------

    /// <summary>
    /// A time source: an NTP server, or PTP on a host that keeps time with PTP.
    /// </summary>
    /// <remarks>
    /// The guide's baseline is site-specific (or the public pool), so this
    /// asks only that there is a source; which servers are the organisation's
    /// is not something the guide lets a product decide.
    /// </remarks>
    private static SettingJudgement JudgeTimeSources(ComplianceControl control, Entity host)
    {
        var expected =
            "at least one NTP server, or PTP configured on a host that uses PTP " +
            $"(baseline: {Flat(control.BaselineValue)})";

        if (host.TimeConfiguration is not { } time)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its time configuration, so its sources were not read. {CollectionGap}");
        }

        var observed = Describe(time);

        if (UsesPtp(time))
        {
            return Verdict(time.Ptp is not null, expected) with
            {
                Observed = observed,

                // Said in the guide's own discussion of this control.
                Reason = time.Ptp is not null && time.NtpServers.Count == 0
                    ? "The host keeps time with PTP. The guide suggests NTP as a backup source for PTP, " +
                      "and none is configured."
                    : null,
            };
        }

        return Verdict(time.NtpServers.Count > 0, expected) with { Observed = observed };
    }

    /// <summary>
    /// A time source and a running time service that starts with the host.
    /// </summary>
    /// <remarks>
    /// The 9.1 control describes NTP only. A host that says it keeps time with
    /// PTP is judged on its PTP configuration and <c>ptpd</c> instead, and the
    /// finding says so: failing it for lacking NTP would be the product being
    /// confidently wrong about a host that is configured correctly.
    /// </remarks>
    private static SettingJudgement JudgeTimeSynchronized(ComplianceControl control, Entity host)
    {
        var wanted = new ServiceState(Running: true, Policy: "on");
        var expected =
            $"NTP servers configured and {NtpService} {Describe(wanted)}; on a host that uses PTP, " +
            $"PTP configured and {PtpService} likewise (baseline: {Flat(control.BaselineValue)})";

        if (host.TimeConfiguration is not { } time)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its time configuration, so it was not read. {CollectionGap}");
        }

        if (host.Services is null)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its services, so its time service was not read. {CollectionGap}");
        }

        var ptp = UsesPtp(time);
        var key = ptp ? PtpService : NtpService;
        var hasSource = ptp ? time.Ptp is not null : time.NtpServers.Count > 0;
        var service = Find(host.Services, key);
        var note = ptp
            ? $"The host keeps time with PTP, so it was judged on its PTP configuration and {PtpService}; " +
              "the guide's control describes NTP."
            : null;

        if (!hasSource)
        {
            // No source is a failure whatever the service is doing: a running
            // daemon with nothing to synchronise against keeps no time.
            return Verdict(false, expected) with
            {
                Observed = Describe(time) + "; " + (service is null ? $"{key}: not reported" : Describe(service)),
                Reason = note,
            };
        }

        if (service is null)
        {
            return NotEvaluated(
                expected,
                $"The host reported its services, but none with the key '{key}', so there is nothing to judge.");
        }

        if (Unread(service) is { } missing)
        {
            return missing with { Expected = expected };
        }

        return Verdict(Meets(service, wanted), expected) with
        {
            Observed = Describe(time) + "; " + Describe(service),
            Reason = note,
        };
    }

    /// <summary>The service keys that keep time on this host, the one it names first.</summary>
    private static IReadOnlyList<string> TimeServiceKeys(TimeConfiguration? time) => time?.Protocol switch
    {
        { } p when p.Equals("ptp", StringComparison.OrdinalIgnoreCase) => [PtpService],
        { } p when p.Equals("ntp", StringComparison.OrdinalIgnoreCase) => [NtpService],
        _ => [NtpService, PtpService],
    };

    private static bool UsesPtp(TimeConfiguration time) =>
        string.Equals(time.Protocol, "ptp", StringComparison.OrdinalIgnoreCase);

    private static string Describe(TimeConfiguration time)
    {
        var parts = new List<string>
        {
            time.Protocol is null ? "protocol not reported" : $"protocol {time.Protocol}",
        };

        if (time.Ptp is { } ptp)
        {
            var devices = ptp.PortDevices.Count == 0 ? "no port device" : string.Join(", ", ptp.PortDevices);
            parts.Add(ptp.Domain is { } domain ? $"PTP on {devices} (domain {domain})" : $"PTP on {devices}");
        }

        parts.Add(time.NtpServers.Count == 0
            ? "NTP servers: none"
            : $"NTP servers: {string.Join(", ", time.NtpServers)}");

        return string.Join("; ", parts);
    }

    // --- lockdown ----------------------------------------------------------------

    /// <summary>
    /// Lockdown mode at least as strict as the baseline.
    /// </summary>
    /// <remarks>
    /// Strict passes a <c>lockdownNormal</c> baseline. The guide recommends
    /// normal over strict for operability — a strict host that loses vCenter
    /// cannot be managed at all — not because strict is weaker, and failing
    /// the stricter host would ask its operator for an exception to be more
    /// secure. The finding still says it differs from the baseline.
    /// </remarks>
    private static SettingJudgement JudgeLockdown(ComplianceControl control, Entity host)
    {
        var baseline = control.BaselineValue.Trim();
        var wanted = LockdownRank(baseline);

        if (wanted < 0)
        {
            return NotEvaluated(
                control.BaselineValue,
                $"The guide's baseline '{Flat(control.BaselineValue)}' is not a lockdown mode.");
        }

        var expected = wanted == LockdownRank("lockdownStrict") ? baseline : $"{baseline} or lockdownStrict";

        if (host.LockdownMode is not { } observed)
        {
            return NotEvaluated(
                expected,
                $"The host did not report its lockdown mode, so it was not read. {CollectionGap}");
        }

        var rank = LockdownRank(observed);

        if (rank < 0)
        {
            return NotEvaluated(
                expected,
                $"The host reported '{observed}', which is not a lockdown mode this product knows.");
        }

        return Verdict(rank >= wanted, expected) with
        {
            Observed = observed,
            Reason = rank > wanted
                ? $"Stricter than the baseline '{baseline}', which the guide chose for operability rather than security."
                : null,
        };
    }

    private static int LockdownRank(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "LOCKDOWNDISABLED" => 0,
        "LOCKDOWNNORMAL" => 1,
        "LOCKDOWNSTRICT" => 2,
        _ => -1,
    };

    // --- standard switch security ---------------------------------------------------

    /// <summary>
    /// Every standard vSwitch and port group rejects the named behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged on the effective policy: a port group that sets nothing inherits
    /// its switch's <c>Accept</c>, and one that overrides a rejecting switch
    /// with <c>Accept</c> is the case the control exists for. Both are named in
    /// the evidence, so the operator does not have to find them again.
    /// </para>
    /// <para>
    /// Some workloads need forged transmits or MAC changes; the guide's answer
    /// is a separate port group for them, and this product's answer is an
    /// exception with an owner and an end date, not a port group quietly left
    /// out of the check.
    /// </para>
    /// </remarks>
    private static SettingJudgement JudgeNetwork(string flag, ComplianceControl control, Entity host)
    {
        Func<SecurityPolicyFlags, bool?>? read = flag switch
        {
            "ForgedTransmits" => f => f.ForgedTransmits,
            "MacChanges" => f => f.MacChanges,
            "AllowPromiscuous" => f => f.AllowPromiscuous,
            _ => null,
        };

        if (read is null)
        {
            return NotEvaluated(control.BaselineValue, $"No switch security policy is called '{flag}'.");
        }

        if (!control.BaselineValue.Trim().Equals("Reject", StringComparison.OrdinalIgnoreCase))
        {
            return NotEvaluated(
                control.BaselineValue,
                $"The guide's baseline '{Flat(control.BaselineValue)}' is not Reject, and Reject is the " +
                "only policy this product compares with.");
        }

        var expected = $"{flag}: Reject on every standard vSwitch and port group (effective policy)";

        var missing = new List<string>();
        if (host.VirtualSwitchSecurity is null)
        {
            missing.Add("standard vSwitches");
        }

        if (host.PortGroupSecurity is null)
        {
            missing.Add("port groups");
        }

        if (missing.Count > 0)
        {
            return NotEvaluated(
                expected,
                $"The host did not report the security policy of its {string.Join(" or ", missing)}, " +
                $"so {flag} was not read. {CollectionGap}");
        }

        var policies = host.VirtualSwitchSecurity!.Concat(host.PortGroupSecurity!).ToList();

        if (policies.Count == 0)
        {
            return Verdict(true, expected) with { Observed = "no standard vSwitches or port groups" };
        }

        var accepting = new List<string>();
        var unread = new List<string>();

        foreach (var policy in policies)
        {
            switch (policy.Effective is null ? null : read(policy.Effective))
            {
                case true:
                    accepting.Add(Name(policy));
                    break;
                case null:
                    unread.Add(Name(policy));
                    break;
            }
        }

        if (accepting.Count > 0)
        {
            // A known Accept is a verdict even when another port group was not read.
            return Verdict(false, expected) with
            {
                Observed = $"Accept on {string.Join(", ", accepting)}",
                Reason =
                    "The guide allows a separate port group that accepts this, with only authorized VMs on " +
                    "it, for workloads that rely on it; record that as an exception rather than a pass." +
                    (unread.Count > 0 ? $" Not reported for {string.Join(", ", unread)}." : string.Empty),
            };
        }

        if (unread.Count > 0)
        {
            return NotEvaluated(
                expected,
                $"The effective {flag} policy was not reported for {string.Join(", ", unread)}, so the " +
                $"host was not judged. {CollectionGap}");
        }

        var switches = policies.Count(p => p.Scope == NetworkPolicyScope.VirtualSwitch);

        return Verdict(true, expected) with
        {
            Observed = $"Reject on all {switches} vSwitches and {policies.Count - switches} port groups",
        };
    }

    private static string Name(NetworkSecurityPolicy policy) => policy.Scope switch
    {
        NetworkPolicyScope.PortGroup when policy.VirtualSwitchName is { } vswitch =>
            $"port group '{policy.Name}' on {vswitch}",
        NetworkPolicyScope.PortGroup => $"port group '{policy.Name}'",
        _ => $"vSwitch '{policy.Name}'",
    };

    // --- shared ------------------------------------------------------------------

    private const StringSplitOptions Tidy =
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

    /// <summary>The guide's multi-line cells on one line, for evidence text.</summary>
    private static string Flat(string text) => string.Join(' ', text.Split(['\r', '\n', ' ', '\t'], Tidy));
}
