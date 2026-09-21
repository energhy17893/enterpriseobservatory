namespace EnterpriseObservatory.Application.Reporting;

/// <summary>
/// Decides whether a schedule is due, given the clock. Pure, so a minute-tick
/// worker has no calendar logic of its own to get wrong.
/// </summary>
/// <remarks>
/// The rule: find the most recent moment the schedule names that is not after
/// now; the subscription is due if it has never been claimed for that moment.
/// Comparing against the occurrence itself, rather than against "has an hour
/// passed since last time", is what makes a five-minute service restart not
/// skip a report and a permanently-disabled subscription not fire a backlog
/// the moment it is re-enabled: only the single most recent occurrence is ever
/// owed.
/// </remarks>
public static class ReportScheduling
{
    /// <summary>The most recent moment, at or before <paramref name="nowUtc"/>, that the schedule names.</summary>
    public static DateTimeOffset LastOccurrenceUtc(ReportSchedule schedule, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var zone = ReportSchedule.TryResolveTimeZone(schedule.TimeZoneId, out var resolved)
            ? resolved
            : TimeZoneInfo.Utc;

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);

        var candidateDate = localNow.Date;

        if (schedule.Frequency == ReportFrequency.Weekly)
        {
            // Walk back to the most recent matching weekday, which is today
            // when today matches.
            var back = ((int)candidateDate.DayOfWeek - (int)schedule.DayOfWeek + 7) % 7;
            candidateDate = candidateDate.AddDays(-back);
        }

        var candidateWallClock = candidateDate.AddHours(schedule.HourLocal);

        // Reconstructed from wall-clock components through the zone rather
        // than trusted as an arithmetic shift on an offset carried over from
        // somewhere else: the offset is only ever valid for the date it was
        // read for.
        var candidateLocal = ResolveLocal(candidateWallClock, zone);

        if (candidateLocal > localNow)
        {
            // The computed slot for "today" (or this week) has not happened
            // yet; the one that is due is a full cycle earlier. Stepped back
            // on the wall clock and re-resolved through the zone -- a
            // candidate a day or a week away does not necessarily share
            // today's offset, and reusing it is exactly what let a schedule
            // whose local slot has not yet happened today land on the wrong
            // side of a DST change and appear due a second time.
            candidateWallClock = schedule.Frequency == ReportFrequency.Weekly
                ? candidateWallClock.AddDays(-7)
                : candidateWallClock.AddDays(-1);

            candidateLocal = ResolveLocal(candidateWallClock, zone);
        }

        return candidateLocal.ToUniversalTime();
    }

    /// <summary>
    /// Pairs a wall-clock date and time with the zone's offset for it.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> is a pure function of
    /// its input, so an invalid time in a spring-forward gap or an ambiguous
    /// one in a fall-back overlap always resolves the same way for the same
    /// wall clock -- deterministic, even though which side of the transition
    /// it lands on is the zone's rule to make, not this method's.
    /// </remarks>
    private static DateTimeOffset ResolveLocal(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>Whether the schedule owes a run that has not yet been claimed.</summary>
    public static bool IsDue(ReportSchedule schedule, DateTimeOffset? lastClaimedUtc, DateTimeOffset nowUtc)
    {
        var occurrence = LastOccurrenceUtc(schedule, nowUtc);

        return lastClaimedUtc is null || lastClaimedUtc.Value < occurrence;
    }
}
