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
/// Presents are judged the same way (ADR-0026 §3: stale means "we could not
/// re-check"). An event cannot be re-read; its evidence is the event, and it
/// holds while its source's stream is read through. So a verdict on a fresh
/// stream is dated at the read-through time — "no clear as of the last
/// complete read" — rather than at the event, which the scope's age clamp
/// would otherwise call stale one window after it happened. On a stale stream
/// it is <see cref="UnknownReason.SourceSilent"/>. The re-dating is for the
/// clamp only: the time to live was already judged on the event's own time.
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
    /// The verdicts, with every present or absent verdict about a source not
    /// read through <paramref name="requiredThroughUtc"/> replaced by an
    /// unknown, and every other one dated no earlier than its source's read.
    /// </summary>
    /// <remarks>
    /// A present verdict's source is its alert's <see cref="AlertDefinition.Source"/>,
    /// the vCenter the event came from. An absence carries no alert, so its
    /// source is read off its entity (<c>source:moref</c>). One with neither —
    /// an alert filed against the vCenter itself — cannot be attributed, so it
    /// stands only when every source is fresh.
    /// </remarks>
    public static IReadOnlyList<SubjectVerdict> Hold(
        IReadOnlyList<SubjectVerdict> verdicts,
        IEventReader events,
        DateTimeOffset requiredThroughUtc)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(events);

        if (!verdicts.Any(v => v is ConditionAbsent or ConditionPresent))
        {
            return verdicts;
        }

        var watermarks = events.ReadWatermarks;

        SubjectVerdict Judge(SubjectVerdict verdict, string? source, DateTimeOffset evidence, Func<DateTimeOffset, SubjectVerdict> redate)
        {
            var (why, through) = Read(source, verdict.Entity, watermarks, requiredThroughUtc);

            return why is not null
                ? new Unknown
                {
                    Covers = verdict.Covers,
                    Entity = verdict.Entity,
                    Reason = UnknownReason.SourceSilent,
                    Detail = why,
                }
                : through > evidence ? redate(through) : verdict;
        }

        return
        [
            .. verdicts.Select(verdict => verdict switch
            {
                ConditionPresent present => Judge(
                    present,
                    present.Alerts.Select(a => a.Source).FirstOrDefault(s => s.Length > 0),
                    present.EvidenceAtUtc,
                    at => present with { EvidenceAtUtc = at }),
                ConditionAbsent absent => Judge(
                    absent, null, absent.EvidenceAtUtc, at => absent with { EvidenceAtUtc = at }),
                _ => verdict,
            }),
        ];
    }

    /// <summary>
    /// Why the events behind a verdict are not known to be read (null when
    /// they are), and the time they are read through.
    /// </summary>
    private static (string? Why, DateTimeOffset Through) Read(
        string? source, EntityId? entity, IReadOnlyList<EventReadWatermark> watermarks, DateTimeOffset required)
    {
        if (source is null && entity is not { })
        {
            if (watermarks.Count == 0)
            {
                return ($"events not read up to {required:u}: no source's event read is recorded", default);
            }

            return (
                watermarks
                    .Select(w => StaleOne(w.SourceInstanceId, w, required))
                    .FirstOrDefault(why => why is not null),
                watermarks.Min(w => w.ReadThroughUtc ?? DateTimeOffset.MinValue));
        }

        // The owner of an entity is everything before the separator the
        // entity id was composed with, not whichever source id the value
        // starts with: "vc-1" must not answer for "vc-10:host-1".
        var owner = source is not null
            ? watermarks.FirstOrDefault(w => string.Equals(w.SourceInstanceId, source, StringComparison.Ordinal))
            : watermarks
                .Where(w => entity!.Value.Value.StartsWith(w.SourceInstanceId + EntityId.Separator, StringComparison.Ordinal))
                .MaxBy(w => w.SourceInstanceId.Length);

        var name = source ?? owner?.SourceInstanceId ?? SourceOf(entity!.Value);

        return (StaleOne(name, owner, required), owner?.ReadThroughUtc ?? default);
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
