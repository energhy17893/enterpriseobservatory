using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Raises an alert for any counter whose non-zero reading is itself a fault.
/// </summary>
/// <remarks>
/// <para>
/// The product's first rule that judges a <em>measurement</em> rather than a
/// collection failure. Everything alerting on before this came from inventory —
/// a host vCenter cannot reach, a datastore reported inaccessible, an alarm
/// vCenter had already raised — or from the collector being unable to read
/// something. Numbers were stored and drawn and never examined.
/// </para>
/// <para>
/// Deliberately one rule and not an engine. The analysis proposal's own advice
/// was to write two rules concretely and extract the shared shape at the third,
/// because what a rule actually needs is only learnable by writing one; an
/// engine designed before that is a guess about its own callers.
/// </para>
/// <para>
/// It names no vendor and no counter. Which counters are faults is the
/// collector's declaration, arriving on
/// <see cref="CounterValue.IsFaultCount"/> — the application layer may not
/// reference a collector, and more usefully, an iLO or Redfish collector's
/// error counters will arrive here without this file changing.
/// </para>
/// <para>
/// No threshold, and that is the point. A level like CPU at 70% invites the
/// question of where to draw the line; SCSI does not reset a bus because the
/// array is busy. One is a fault. Zero is a real answer rather than the
/// truncated kind — see the counter map §5b for the latency zeros that are
/// not.
/// </para>
/// </remarks>
public static class FaultCounters
{
    /// <summary>
    /// Every fault this batch of observations reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One alert per device, not one per host. A host with a failing cable has
    /// one bad path out of thirty-two, and an alert that said only "this host
    /// has storage errors" would leave somebody to find which — which is the
    /// whole reason the per-device series were kept.
    /// </para>
    /// <para>
    /// Aggregates are skipped. A counter kept per device is stored twice, once
    /// per device and once as a computed total across them, and alerting on
    /// both would raise a nameless second alert for every real one.
    /// </para>
    /// <para>
    /// Nothing here holds state or looks backwards. A path that resets once and
    /// stops produces an alert that opens and then closes, which is honest —
    /// and a path that does it fifty times a day is caught by flap detection,
    /// which exists precisely because such a problem leaves no instance behind
    /// and is very likely the most important thing happening.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var alerts = new List<AlertDefinition>();
        var seen = new HashSet<AlertFingerprint>();

        foreach (var observation in observations)
        {
            var value = observation.Value;

            if (!value.IsFaultCount ||
                value.Raw <= 0 ||
                value.IsAggregateInstance)
            {
                continue;
            }

            var fingerprint = AlertFingerprint.Create(
                observation.Source,
                FaultTitle(value.CounterName),
                Category,
                $"{observation.Entity.Value}/{value.Instance}",
                "fault-counter");

            if (!seen.Add(fingerprint))
            {
                continue;
            }

            alerts.Add(new AlertDefinition
            {
                Fingerprint = fingerprint,
                Severity = AlertSeverity.Warning,
                Title = FaultTitle(value.CounterName),
                Description = Describe(observation),
                Category = Category,
                Source = observation.Source,
                Entity = observation.Entity,
            });
        }

        return alerts;
    }

    /// <summary>
    /// Shown beside the alert and used to separate these from thresholds.
    /// </summary>
    public const string Category = "Fault";
    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "fault-counters";


    /// <summary>
    /// A readable title from the counter's own name.
    /// </summary>
    /// <remarks>
    /// The counter name rather than a translation table. A table would have to
    /// be extended for every collector and would quietly fall back to
    /// something vague for the one nobody added — and the raw name is what an
    /// operator will search the vendor's documentation for anyway.
    /// </remarks>
    private static string FaultTitle(string counterName) => $"Fault reported: {counterName}";

    private static string Describe(Observation observation)
    {
        var value = observation.Value;

        // The count, the device and the window it covers. A summation means
        // "this many, during this interval", and without the interval the
        // number cannot be read: three resets in twenty seconds and three in
        // five minutes are different situations.
        var count = value.Raw.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var seconds = value.Interval.TotalSeconds
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        return
            $"'{value.CounterName}' reported {count} on device '{value.Instance}' " +
            $"in the last {seconds} seconds. This counter is a fault rather than a level: " +
            "there is no acceptable non-zero reading, so no threshold applies. Check the " +
            "cable, the transceiver and the switch port for this path before looking at the " +
            "array.";
    }
}
