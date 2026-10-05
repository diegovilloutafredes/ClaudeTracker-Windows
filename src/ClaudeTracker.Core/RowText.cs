using System.Globalization;

namespace ClaudeTracker.Core;

/// <summary>
/// The words of a usage row's two pace lines. The Mac app builds them inside the row's view
/// (<c>UsageWindowView.paceLine</c> and <c>paceOutlook</c>); here they are pure functions, so
/// the wording and its thresholds are tested rather than looked at.
/// </summary>
public static class PaceText
{
    private static readonly string[] SafeMessages =
    [
        "On track — resets before limit",
        "You're good — resets in time",
        "All clear — window resets first",
        "Safe — usage resets before full",
        "No rush — plenty of time left",
    ];

    private static readonly string[] CloseMessages =
    [
        "Getting close — may hit limit",
        "Pace is high — watch your usage",
        "Caution — cutting it close",
        "Almost at the edge — ease up",
        "Trending toward the limit",
    ];

    private static readonly string[] OverMessages =
    [
        "Will hit limit %@ before reset",
        "Runs out %@ before reset",
        "On pace to fill %@ early",
        "Full %@ before window resets",
    ];

    /// <summary>
    /// The pace line: the rate in the chosen unit, then how long until the window is full when
    /// that is under a day — "+8.4%/hr · full in 2h 10m".
    /// </summary>
    public static string Line(double ratePerHour, double? projectedHours, PaceRateUnit unit)
    {
        var rate = unit.Format(ratePerHour, prefix: true);
        if (projectedHours is not { } hours || !(hours < 24)) return rate;
        if (hours < 1) return rate + " " + L.F("· full in %dm", Math.Max(1, (int)(hours * 60)));
        var whole = (int)hours;
        var minutes = (int)((hours - whole) * 60);
        return rate + " " + (minutes > 0 ? L.F("· full in %dh %dm", whole, minutes) : L.F("· full in %dh", whole));
    }

    /// <summary>
    /// The outlook line under the pace line, with the band that colours it. Null when there is
    /// no projection, or no reset still ahead to measure it against.
    ///
    /// The phrase is picked by <see cref="PaceMath.OutlookMessageIndex"/>, so it stays the same
    /// for a whole window cycle. In the "over" band it says how much earlier than its reset the
    /// window runs out, through <see cref="PaceMath.EarlyFillLead"/>: never overstated, and
    /// with minutes only while they matter (under three hours).
    /// </summary>
    public static (PaceBand Band, string Message)? Outlook(double? projectedHours, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (projectedHours is not { } projected || !(projected > 0) || resetsAt is not { } reset) return null;
        var hoursToReset = (reset - now).TotalHours;
        if (!(hoursToReset > 0)) return null;

        string Pick(string[] messages) => messages[PaceMath.OutlookMessageIndex(reset, messages.Length)];
        switch (PaceMath.Band(projected, hoursToReset))
        {
            case PaceBand.Safe:
                return (PaceBand.Safe, L.T(Pick(SafeMessages)));
            case PaceBand.Close:
                return (PaceBand.Close, L.T(Pick(CloseMessages)));
            default:
                var (hours, minutes) = PaceMath.EarlyFillLead(hoursToReset - projected);
                var lead = hours == 0 ? L.F("~%dm", minutes)
                    : hours >= 3 || minutes == 0 ? L.F("~%dh", hours)
                    : L.F("~%dh %dm", hours, minutes);
                return (PaceBand.Over, L.F(Pick(OverMessages), lead));
        }
    }
}

/// <summary>Money as the popover shows it.</summary>
public static class Money
{
    /// <summary>
    /// A US-dollar amount written the way <paramref name="culture"/> writes money — its digit
    /// grouping, decimal mark and symbol placement — always with two decimals, and with the
    /// symbol "US$" wherever a bare "$" would be read as the local currency.
    /// </summary>
    public static string Usd(double amount, CultureInfo culture)
    {
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = UsesTheUsDollar(culture) ? "$" : "US$";
        format.CurrencyDecimalDigits = 2;
        return amount.ToString("C", format);
    }

    private static bool UsesTheUsDollar(CultureInfo culture)
    {
        // A language without a country ("es"), or no culture at all, has no currency to ask about.
        if (culture.IsNeutralCulture || culture.Name.Length == 0) return false;
        try { return new RegionInfo(culture.Name).ISOCurrencySymbol == "USD"; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The extra usage line: spent of the monthly limit, "$12.34 / $50.00".</summary>
    public static string SpentOfLimit(double used, double limit, CultureInfo culture) =>
        Usd(used, culture) + " / " + Usd(limit, culture);
}

/// <summary>
/// The popup size setting's range, as a factor on the popover's natural size. Registered in the
/// workspace's <c>BUSINESS_RULES.md</c>; the Mac app's slider has the same three numbers.
/// </summary>
public static class PopupScale
{
    public const double Minimum = 0.75;
    public const double Maximum = 1.5;
    public const double Step = 0.05;
    public const double Default = 1.0;

    /// <summary>A stored value brought into range; anything unusable becomes the default.</summary>
    public static double Normalized(double? stored) =>
        stored is { } value && value > 0 && !double.IsNaN(value) ? Math.Clamp(value, Minimum, Maximum) : Default;
}
