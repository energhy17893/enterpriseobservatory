using System.Text.RegularExpressions;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The check-id → rule-id map that migration 14 and every alert raised before
/// it rely on (ADR-0026 design note §4).
/// </summary>
public partial class RuleCheckIdsTests
{
    private static AlertFingerprint Fingerprint(string checkId) =>
        AlertFingerprint.Create("platform", "t", "c", "o", checkId);

    [Fact]
    public void Every_registered_rule_owns_at_least_one_check_id()
    {
        var owners = RuleCheckIds.Exact.Values.Concat(RuleCheckIds.Prefixes.Values).ToHashSet(StringComparer.Ordinal);

        Assert.All(AnalysisRules.All, rule => Assert.Contains(rule.RuleId, owners));
    }

    [Fact]
    public void Every_mapped_rule_is_registered()
    {
        var registered = AnalysisRules.All.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

        Assert.All(
            RuleCheckIds.Exact.Values.Concat(RuleCheckIds.Prefixes.Values),
            owner => Assert.Contains(owner, registered));
    }

    [Fact]
    public void A_direct_producers_check_id_maps_to_no_rule()
    {
        // Two-valued at N = 1: what every alert was before the migration.
        Assert.Null(RuleCheckIds.RuleOf(Fingerprint(GuardedRule.FailedCheckId)));
        Assert.Null(RuleCheckIds.RuleOf(Fingerprint("collector-unreachable:metrics")));
        Assert.Null(RuleCheckIds.RuleOf(Fingerprint("store-write-failed")));
    }

    [Fact]
    public void An_event_alert_and_a_retired_continuity_alarm_keep_their_owner()
    {
        Assert.Equal(EventAlerts.RuleId, RuleCheckIds.RuleOf(Fingerprint("vcenter-events:ha-host-failed")));
        Assert.Equal(
            MovedContinuityRules.HighAvailability,
            RuleCheckIds.RuleOf(Fingerprint("cluster-ha-scorecard-ha-disabled")));
    }

    /// <summary>
    /// Reads every <c>AlertFingerprint.Create</c> in the analysis rules' own
    /// source and checks its check id maps to the rule in that file.
    /// </summary>
    /// <remarks>
    /// Read from the source because the check ids are literals inside each
    /// rule and no rule can be made to emit every one of them from a fixture.
    /// A new check id a rule starts emitting without an entry here fails this
    /// test, rather than silently becoming a two-valued alert.
    /// </remarks>
    [Fact]
    public void Every_check_id_a_rule_can_emit_maps_to_that_rule()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "EnterpriseObservatory.Application", "Analysis");
        var checked_ = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(path);

            if (RuleIdConstant().Match(source) is not { Success: true } ruleMatch)
            {
                continue;
            }

            var ruleId = ruleMatch.Groups[1].Value;
            var constants = StringConstant().Matches(source)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

            foreach (var argument in CheckIdArguments(source))
            {
                var checkId = argument switch
                {
                    _ when argument.StartsWith('"') => argument.Trim('"'),
                    "$\"{RuleId}:{condition.Id}\"" => ruleId + ":some-condition",
                    _ when constants.TryGetValue(argument, out var value) => value,
                    _ => throw new InvalidOperationException(
                        $"{Path.GetFileName(path)}: cannot resolve the check id argument '{argument}'."),
                };

                Assert.True(
                    RuleCheckIds.RuleOf(Fingerprint(checkId)) == ruleId,
                    $"{Path.GetFileName(path)} emits check id '{checkId}', which RuleCheckIds does not map to '{ruleId}'.");

                checked_++;
            }
        }

        // Every rule file was actually read: fourteen rules, more check ids.
        Assert.True(checked_ >= 20, $"Only {checked_} check ids were found; the scan is not reading the rules.");
    }

    /// <summary>The last argument of every <c>AlertFingerprint.Create(...)</c> call.</summary>
    private static IEnumerable<string> CheckIdArguments(string source)
    {
        const string call = "AlertFingerprint.Create(";

        for (var at = source.IndexOf(call, StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf(call, at + 1, StringComparison.Ordinal))
        {
            var depth = 0;
            var last = at + call.Length;
            var inString = false;

            for (var i = at + call.Length; i < source.Length; i++)
            {
                var c = source[i];

                if (c == '"' && source[i - 1] != '\\')
                {
                    inString = !inString;
                }

                if (inString)
                {
                    continue;
                }

                if (c is '(' or '{')
                {
                    depth++;
                }
                else if (c is ')' or '}' && depth > 0)
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    last = i + 1;
                }
                else if (c == ')' && depth == 0)
                {
                    yield return source[last..i].Trim();
                    break;
                }
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseObservatory.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }

    [GeneratedRegex("""public const string RuleId = "([^"]+)";""")]
    private static partial Regex RuleIdConstant();

    [GeneratedRegex("""const string (\w+) = "([^"]+)";""")]
    private static partial Regex StringConstant();
}
