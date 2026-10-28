using System.Globalization;

namespace ClaudeTracker.Core;

// The plan's next billing date (CT-010): which date the popover's footer shows, how it is
// written, and when the subscription details are read again. The Mac's twins are the free
// functions of the same names in Models.swift; its footer words the line itself.

/// <summary>What happens to the plan next: it renews, or it runs out.</summary>
public enum PlanEventKind { Renews, Ends }

/// <summary>The plan's next billing event, and when it happens.</summary>
public sealed record PlanEvent(PlanEventKind Kind, DateTimeOffset Date);

public static class Billing
{
    /// <summary>How often the subscription details are read again: six hours (BUSINESS_RULES <c>subscription-refresh</c>).</summary>
    public const double SubscriptionRefreshSeconds = 21600;

    /// <summary>
    /// The event the footer shows, or null when there is nothing ahead to show. A plan that is
    /// ending says when it ends (<c>plan_ending_at</c>); any other, its next charge
    /// (<c>next_charge_at</c>). A time that has passed is never shown: the next reading brings
    /// the next one.
    /// </summary>
    public static PlanEvent? UpcomingPlanEvent(SubscriptionDetails? details, DateTimeOffset now)
    {
        if (details?.PlanEndingAtDate is { } end && end > now) return new PlanEvent(PlanEventKind.Ends, end);
        if (details?.NextChargeAtDate is { } next && next > now) return new PlanEvent(PlanEventKind.Renews, next);
        return null;
    }

    /// <summary>
    /// The day of a billing event as the footer writes it: abbreviated month and day in the
    /// reader's time zone — "Nov 19", "19 nov" — with the year when it is not this year's
    /// ("Jan 5, 2027"), as an annual plan's can be. The local day, as claude.ai's billing page
    /// writes it: a charge at 01:12 UTC falls on the day before in the Americas.
    /// </summary>
    public static string PlanDateText(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(date, timeZone);
        var month = local.ToString("MMM", culture).TrimEnd('.');
        var day = local.Day.ToString(CultureInfo.InvariantCulture);
        // English puts the month first; Spanish, like most languages, the day.
        var english = culture.TwoLetterISOLanguageName == "en";
        var text = english ? $"{month} {day}" : $"{day} {month}";
        if (local.Year == TimeZoneInfo.ConvertTime(now, timeZone).Year) return text;
        var year = local.Year.ToString(CultureInfo.InvariantCulture);
        return english ? $"{text}, {year}" : $"{text} {year}";
    }

    /// <summary>
    /// The footer's line, in the app's language: "Renews Nov 19", or "Ends Nov 19" for a plan
    /// that will not renew. Null when there is nothing ahead to show.
    /// </summary>
    public static string? FooterLine(SubscriptionDetails? details, DateTimeOffset now, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        if (UpcomingPlanEvent(details, now) is not { } next) return null;
        var date = PlanDateText(next.Date, now, timeZone, culture);
        return next.Kind == PlanEventKind.Ends ? L.F("Ends %@", date) : L.F("Renews %@", date);
    }

    /// <summary>
    /// Whether the subscription details are due to be read: never yet for this account in this
    /// session, or the last attempt is <see cref="SubscriptionRefreshSeconds"/> old. An attempt
    /// counts whether it worked or not, so a member refused the endpoint is asked again six
    /// hours later, not at every poll.
    /// </summary>
    public static bool SubscriptionRefreshDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is not { } last || (now - last).TotalSeconds >= SubscriptionRefreshSeconds;
}
