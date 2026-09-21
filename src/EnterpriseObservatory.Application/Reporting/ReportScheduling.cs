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

        var candidateLocal = new DateTimeOffset(candidateDate, localNow.Offset)
            .AddHours(schedule.HourLocal);

        // The offset captured above is today's; a candidate that lands on a
        // different side of a DST change needs its own offset, so it is
        // reconstructed from wall-clock components through the zone rather
        // than trusted as an arithmetic shift.
        candidateLocal = new DateTimeOffset(
            DateTime.SpecifyKind(candidateLocal.DateTime, DateTimeKind.Unspecified),
            zone.GetUtcOffset(candidateLocal.DateTime));

        if (candidateLocal > localNow)
        {
            // The computed slot for "today" (or this week) has not happened
            // yet; the one that is due is a full cycle earlier.
            candidateLocal = schedule.Frequency == ReportFrequency.Weekly
                ? candidateLocal.AddDays(-7)
                : candidateLocal.AddDays(-1);
        }

        return candidateLocal.ToUniversalTime();
    }

    /// <summary>Whether the schedule owes a run that has not yet been claimed.</summary>
    public static bool IsDue(ReportSchedule schedule, DateTimeOffset? lastClaimedUtc, DateTimeOffset nowUtc)
    {
        var occurrence = LastOccurrenceUtc(schedule, nowUtc);

        return lastClaimedUtc is null || lastClaimedUtc.Value < occurrence;
    }
}
