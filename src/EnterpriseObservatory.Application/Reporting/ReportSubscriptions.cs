namespace EnterpriseObservatory.Application.Reporting;

/// <summary>
/// What a scheduled report contains.
/// </summary>
/// <remarks>
/// One member per report the product can print (M5.1–M5.3). Stored by name,
/// so a new member is additive and does not touch a stored subscription,
/// which keeps the kind it was created with.
/// </remarks>
public enum ReportKind
{
    Alerts,

    Compliance,

    Capacity,
}

/// <summary>How often a report goes out.</summary>
public enum ReportFrequency
{
    Daily,

    /// <summary>Once a week, on <see cref="ReportSchedule.DayOfWeek"/>.</summary>
    Weekly,
}

/// <summary>
/// When a report is due.
/// </summary>
/// <remarks>
/// Expressed in a named time zone and a local hour, not a UTC instant. "Every
/// morning at 7" is what a person means by a schedule; storing only a UTC
/// instant would mean a distribution list in Istanbul who asked for a 7am
/// report reads it in the middle of the night every time the offset changes
/// under daylight saving.
/// </remarks>
public sealed record ReportSchedule
{
    public required ReportFrequency Frequency { get; init; }

    /// <summary>Which day, for <see cref="ReportFrequency.Weekly"/>. Ignored for <see cref="ReportFrequency.Daily"/>.</summary>
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Monday;

    /// <summary>The local hour it goes out, 0-23.</summary>
    public int HourLocal { get; init; } = 7;

    /// <summary>An IANA or Windows time zone id. UTC unless the subscriber said otherwise.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (HourLocal is < 0 or > 23)
        {
            problems.Add("The hour must be between 0 and 23.");
        }

        if (!TryResolveTimeZone(TimeZoneId, out _))
        {
            problems.Add($"'{TimeZoneId}' is not a time zone this machine recognises.");
        }

        return problems;
    }

    /// <summary>
    /// Resolves the schedule's time zone, without throwing.
    /// </summary>
    /// <remarks>
    /// An estate's server and its subscribers do not always share an operating
    /// system, and Windows and IANA disagree on a handful of ids — "Turkey
    /// Standard Time" versus "Europe/Istanbul". .NET on modern Windows resolves
    /// both; this only guards the case where the id is simply wrong, so the
    /// scheduler reports that plainly instead of throwing from inside a
    /// background loop.
    /// </remarks>
    public static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

/// <summary>
/// A standing request to email a report on a schedule.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LastSentUtc"/> is written before the send is attempted, not
/// after it succeeds — see <see cref="IReportSubscriptionStore.MarkDispatched"/>
/// for why. It therefore means "the last time this subscription's due window
/// was claimed", and <see cref="LastError"/> is what says whether that attempt
/// actually reached the relay.
/// </para>
/// </remarks>
public sealed record ReportSubscription
{
    public required string Id { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    public required ReportSchedule Schedule { get; init; }

    public ReportKind Kind { get; init; } = ReportKind.Alerts;

    public bool IsEnabled { get; init; } = true;

    public DateTimeOffset? LastSentUtc { get; init; }

    /// <summary>What went wrong on the last attempt. Null when it worked, or none has run yet.</summary>
    public string? LastError { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Recipients.Count == 0)
        {
            problems.Add("At least one recipient is required.");
        }

        foreach (var recipient in Recipients)
        {
            if (!MailAddresses.IsValid(recipient))
            {
                problems.Add($"'{recipient}' is not a valid email address.");
            }
        }

        problems.AddRange(Schedule.Validate());

        return problems;
    }
}

/// <summary>Standing report subscriptions, durable.</summary>
public interface IReportSubscriptionStore
{
    IReadOnlyList<ReportSubscription> All { get; }

    ReportSubscription? Find(string id);

    /// <returns>False if the id is already taken.</returns>
    bool Add(ReportSubscription subscription);

    /// <returns>False if no subscription has that id.</returns>
    bool Update(ReportSubscription subscription);

    /// <returns>False if no subscription has that id.</returns>
    bool Remove(string id);

    /// <summary>
    /// Claims a subscription's current due window, before the send is attempted.
    /// </summary>
    /// <remarks>
    /// Written ahead of the send rather than after it, which is the trade this
    /// product makes deliberately: a crash between "the mail left the relay"
    /// and "we recorded that it did" would otherwise resend on restart, and a
    /// duplicate report in a distribution list's inbox is a visible, confusing
    /// mistake that nothing here can take back. A crash between claiming the
    /// window and actually sending instead costs one skipped report, which the
    /// next scheduled occurrence quietly makes up for. See
    /// <see cref="ReportSubscription.LastSentUtc"/>.
    /// </remarks>
    void MarkDispatched(string id, DateTimeOffset atUtc);

    /// <summary>Records why the claimed attempt failed. Leaves the claim from <see cref="MarkDispatched"/> in place.</summary>
    void MarkFailed(string id, string detail);
}
