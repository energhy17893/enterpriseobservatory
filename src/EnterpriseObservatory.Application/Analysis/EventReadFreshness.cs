using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Turns an event rule's "absent" into "unknown" for a source whose events
/// were not read up to the time the rule needs (ADR-0026 §5.7, F note §6).
/// </summary>
/// <remarks>
/// <para>
/// An event rule re-derives every open condition from stored events, so for
/// it "no report and no clear in the store" means absent only if the store
/// holds that source's stream up to about now. For a source whose read failed,
/// was cut off, was not attempted or stopped short, the clear — or a new report
/// — may be sitting unread in vCenter, and the honest verdict is
/// <see cref="UnknownReason.SourceSilent"/>: the alert stays open, marked, and
/// nothing is resolved on a read that did not happen.
/// </para>
/// <para>
/// Only absences change. What a rule raised was read, and stays present; the
/// watermark says what may be missing, not that what is there is wrong.
/// </para>
/// </remarks>
public static class EventReadFreshness
{
    /// <summary>
    /// How many inventory intervals a watermark may lag before it is stale.
    /// <strong>This product's choice</strong>, not a measured value.
    /// </summary>
    /// <remarks>
    /// Events are read after each inventory cycle, so the rule always sees the
    /// previous cycle's read: a fresh watermark is up to one interval old. One
    /// read that did not land makes it about two. One and a half sits between
    /// the two, with room for the cycle's own duration and timer jitter.
    /// </remarks>
    public const double AllowedIntervals = 1.5;

    /// <summary>The time a source's events must have been read through for an absence to count.</summary>
    public static DateTimeOffset RequiredThrough(DateTimeOffset nowUtc, TimeSpan inventoryInterval) =>
        nowUtc - (inventoryInterval * AllowedIntervals);

    /// <summary>
    /// The verdicts, with every absence about a source not read through
    /// <paramref name="requiredThroughUtc"/> replaced by an unknown.
    /// </summary>
    /// <remarks>
    /// The source is read off the verdict's entity (<c>source:moref</c>). An
    /// absence with no entity — an alert filed against the vCenter itself —
    /// cannot be attributed, so it stands only when every source is fresh. The
    /// watermarks are asked for only when there is an absence to check.
    /// </remarks>
    public static IReadOnlyList<SubjectVerdict> Hold(
        IReadOnlyList<SubjectVerdict> verdicts,
        IEventReader events,
        DateTimeOffset requiredThroughUtc)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(events);

        if (!verdicts.Any(v => v is ConditionAbsent))
        {
            return verdicts;
        }

        var watermarks = events.ReadWatermarks;

        return
        [
            .. verdicts.Select(verdict => verdict is ConditionAbsent absent &&
                                          Stale(absent.Entity, watermarks, requiredThroughUtc) is { } why
                ? new Unknown
                {
                    Covers = absent.Covers,
                    Entity = absent.Entity,
                    Reason = UnknownReason.SourceSilent,
                    Detail = why,
                }
                : verdict),
        ];
    }

    /// <summary>Why the events behind an absence are not known to be read, or null when they are.</summary>
    private static string? Stale(
        EntityId? entity, IReadOnlyList<EventReadWatermark> watermarks, DateTimeOffset required)
    {
        if (entity is not { } id)
        {
            if (watermarks.Count == 0)
            {
                return $"events not read up to {required:u}: no source's event read is recorded";
            }

            return watermarks
                .Select(w => StaleOne(w.SourceInstanceId, w, required))
                .FirstOrDefault(why => why is not null);
        }

        // The owner is everything before the separator the entity id was
        // composed with, not whichever source id the value starts with:
        // "vc-1" must not answer for "vc-10:host-1".
        var owner = watermarks
            .Where(w => id.Value.StartsWith(w.SourceInstanceId + EntityId.Separator, StringComparison.Ordinal))
            .MaxBy(w => w.SourceInstanceId.Length);

        var source = owner?.SourceInstanceId ?? SourceOf(id);

        return StaleOne(source, owner, required);
    }

    private static string? StaleOne(string source, EventReadWatermark? watermark, DateTimeOffset required) =>
        watermark switch
        {
            null => $"events not read up to {required:u}: no event read is recorded for '{source}'",
            { ReadThroughUtc: { } through } when through >= required => null,
            { ReadThroughUtc: { } through } =>
                $"events not read up to {required:u}: '{source}' was last read completely at {through:u}",
            _ => $"events not read up to {required:u}: for '{source}', {watermark.Detail ?? "no complete read is recorded"}",
        };

    private static string SourceOf(EntityId id)
    {
        var at = id.Value.IndexOf(EntityId.Separator, StringComparison.Ordinal);

        return at < 0 ? id.Value : id.Value[..at];
    }
}
