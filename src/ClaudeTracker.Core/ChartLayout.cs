using System.Globalization;

namespace ClaudeTracker.Core;

/// <summary>
/// The arithmetic of the Charts tab that can be wrong without looking wrong: where a forecast
/// starts and ends, what "expected" is at a moment, the scale of a pace chart, what the axis
/// says. The Mac app computes these inside its chart views (<c>MenuBarChartsView</c>); here
/// they are pure, so they are tested. Drawing is the app project's.
/// </summary>
public static class ChartLayout
{
    /// <summary>One series out of the history: its samples from <paramref name="from"/> on, in time order.</summary>
    public static List<(DateTimeOffset Time, double Value)> Pairs(
        IReadOnlyList<UsageDataPoint> history, Func<UsageDataPoint, double?> value, DateTimeOffset from)
    {
        var pairs = new List<(DateTimeOffset, double)>();
        foreach (var point in history)
        {
            if (point.Timestamp >= from && value(point) is { } sample) pairs.Add((point.Timestamp, sample));
        }
        return pairs;
    }

    /// <summary>
    /// The figures above a chart: the latest value, the highest, and the average, which is
    /// rounded here as it is shown — a half goes up, as on the Mac. Null without samples.
    /// </summary>
    public static (double Now, double Peak, double Average)? Stats(IReadOnlyList<(DateTimeOffset Time, double Value)> pairs)
    {
        if (pairs.Count == 0) return null;
        double peak = double.NegativeInfinity, sum = 0;
        foreach (var (_, value) in pairs)
        {
            peak = Math.Max(peak, value);
            sum += value;
        }
        return (pairs[^1].Value, peak, Math.Round(sum / pairs.Count, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// What stands around the charts' list in the popover, in the popover's own units: header,
    /// tabs, range picker, footer, padding. Measured at 205; the Mac app allows the same 250,
    /// and what is over leaves room for the gap between the popover and the screen's edge.
    /// </summary>
    public const double ListSurroundings = 250;

    /// <summary>
    /// The least the charts' list is given, on a screen too small for more: enough to show
    /// that there is a list, and to scroll it. The Mac app's floor is 320, taller than the
    /// room left on a 1080-line screen at 150 %, where the popover then stood off the top.
    /// </summary>
    public const double SmallestList = 80;

    /// <summary>
    /// The tallest the charts' scrolling list may be, so the popover never outgrows the screen:
    /// the room there is, less what surrounds the list and whatever else is showing above it
    /// (the update banner, a notice). All in the popover's own units.
    /// </summary>
    public static double ListHeightLimit(double available, double alsoShowing = 0) =>
        Math.Max(available - ListSurroundings - alsoShowing, SmallestList);

    /// <summary>The top of a pace chart's scale: a fifth above the highest rate, and never under 1.</summary>
    public static double PaceScaleTop(IReadOnlyList<(DateTimeOffset Time, double Value)> pairs)
    {
        var highest = 0.0;
        foreach (var (_, value) in pairs) highest = Math.Max(highest, value);
        return Math.Max(highest * 1.2, 1);
    }

    /// <summary>The frame of a forecast chart.</summary>
    /// <param name="WindowStart">When the window in progress began: its reset, less its length.</param>
    /// <param name="ProjectedFull">When the current pace reaches 100 %; null without a pace, a last reading, or room left.</param>
    /// <param name="End">The right edge: the reset, or the projection when that is later.</param>
    /// <param name="LastValue">The last reading in the window, or the live utilization when there is none.</param>
    public sealed record Forecast(DateTimeOffset WindowStart, DateTimeOffset Reset, DateTimeOffset? ProjectedFull,
                                  DateTimeOffset End, DateTimeOffset? LastTime, double LastValue);

    /// <summary>
    /// Frames the forecast of a live window from its readings (those since the window began)
    /// and its current pace.
    /// </summary>
    public static Forecast ForecastFor(IReadOnlyList<(DateTimeOffset Time, double Value)> pairs, DateTimeOffset reset,
                                       double windowSeconds, double liveUtilization, double? ratePerHour)
    {
        var start = reset.AddSeconds(-windowSeconds);
        DateTimeOffset? lastTime = pairs.Count > 0 ? pairs[^1].Time : null;
        var lastValue = pairs.Count > 0 ? pairs[^1].Value : liveUtilization;
        DateTimeOffset? projected = lastTime is { } time && ratePerHour is { } rate && rate > 0 && lastValue < 100
            ? time.AddHours((100 - lastValue) / rate)
            : null;
        var end = projected is { } full && full > reset ? full : reset;
        return new Forecast(start, reset, projected, end, lastTime, lastValue);
    }

    /// <summary>
    /// What an even pace would have used by a moment: 0 at the window's start, 100 at its
    /// reset, and never outside that.
    /// </summary>
    public static double ExpectedPercent(DateTimeOffset moment, DateTimeOffset windowStart, DateTimeOffset reset)
    {
        var length = (reset - windowStart).TotalSeconds;
        if (!(length > 0)) return 0;
        return Math.Clamp((moment - windowStart).TotalSeconds / length * 100, 0, 100);
    }

    /// <summary>
    /// The pointer's moment, to the nearest minute. The history holds a sample every five
    /// minutes, so finer than this buys nothing — and every distinct value redraws every chart.
    /// </summary>
    public static DateTimeOffset QuantizeToMinute(DateTimeOffset moment)
    {
        var minutes = Math.Round((moment - DateTimeOffset.UnixEpoch).TotalSeconds / 60, MidpointRounding.AwayFromZero);
        return DateTimeOffset.UnixEpoch.AddMinutes(minutes).ToOffset(moment.Offset);
    }

    /// <summary>The steps a time axis may count in, in seconds: 5 minutes up to two weeks.</summary>
    private static readonly double[] TimeSteps =
        [300, 600, 900, 1800, 3600, 2 * 3600, 3 * 3600, 6 * 3600, 12 * 3600, 86400, 2 * 86400, 7 * 86400, 14 * 86400];

    /// <summary>A Monday to count several-day steps from, so a week's marks fall on Mondays.</summary>
    private static readonly DateTime FirstMonday = new(1970, 1, 5);

    /// <summary>
    /// Where a time axis puts its marks: on round times of the reader's own clock — the hour,
    /// the quarter, midnight — at the finest step that leaves no more than four of them. An
    /// axis marked at 09:21 and 11:01 is harder to read than one marked at 10:00 and 12:00.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> TimeTicks(DateTimeOffset lower, DateTimeOffset upper, TimeZoneInfo? timeZone = null)
    {
        var span = (upper - lower).TotalSeconds;
        if (!(span > 0)) return [];
        var step = TimeSteps.FirstOrDefault(candidate => span / candidate <= 4);
        if (step == 0)
        {
            // Past eight weeks — a forecast at a crawling pace runs that far: fortnights, doubled.
            for (step = TimeSteps[^1]; span / step > 4; step *= 2) { }
        }
        timeZone ??= TimeZoneInfo.Local;

        // Counted on the wall clock from midnight, not in seconds from the first mark: the
        // marks then stay on round times whatever the offset from UTC, and when the clocks change.
        var wallLower = TimeZoneInfo.ConvertTime(lower, timeZone).DateTime;
        var wallUpper = TimeZoneInfo.ConvertTime(upper, timeZone).DateTime;
        var origin = wallLower.Date;
        if (step > 86400)
        {
            // Or the marks would start wherever the range happens to.
            var days = (long)(step / 86400);
            var sinceMonday = (long)(origin - FirstMonday).TotalDays;
            origin = origin.AddDays(-(((sinceMonday % days) + days) % days));
        }
        // Every mark the wall clock shows anywhere near the range, each turned into the moment
        // (or the two moments) it stands for, and kept if that falls inside. Where the clocks
        // go back the wall clock at the end of the range can read earlier than at its start,
        // so the walk begins a step and two hours before the earlier of the two readings.
        var reach = Math.Max(step, 2 * 3600);
        var earliest = (wallLower < wallUpper ? wallLower : wallUpper).AddSeconds(-reach);
        var latest = (wallLower > wallUpper ? wallLower : wallUpper).AddSeconds(reach);
        var ticks = new SortedSet<DateTimeOffset>();
        for (var count = (long)Math.Floor((earliest - origin).TotalSeconds / step); ; count++)
        {
            var mark = origin.AddSeconds(count * step);
            if (mark > latest) break;
            if (timeZone.IsInvalidTime(mark))
            {
                // The clocks skip this moment. A day's mark moves to the first moment that day
                // has (where they change at midnight, there is no midnight); an hour's is left out.
                if (step < 86400) continue;
                mark = mark.AddHours(1);
            }
            if (timeZone.IsAmbiguousTime(mark))
            {
                // The hour the clocks go back over happens twice, and so does its mark.
                foreach (var offset in timeZone.GetAmbiguousTimeOffsets(mark)) Keep(new DateTimeOffset(mark, offset));
            }
            else
            {
                Keep(new DateTimeOffset(mark, timeZone.GetUtcOffset(mark)));
            }
        }
        return [.. ticks];

        void Keep(DateTimeOffset tick)
        {
            if (tick >= lower && tick <= upper) ticks.Add(tick);
        }
    }

    /// <summary>
    /// An axis or pointer label: the clock time for a span under about a day (in the user's
    /// 12/24-hour choice, like the reset line), the month and day beyond.
    /// </summary>
    public static string TimeLabel(DateTimeOffset moment, double spanSeconds, bool use24Hour,
                                   TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(moment, timeZone ?? TimeZoneInfo.Local);
        if (spanSeconds < 25 * 3600) return TimeText.Clock(local, use24Hour, culture);
        // English puts the month first; Spanish, like most languages, the day.
        return local.ToString(culture.TwoLetterISOLanguageName == "en" ? "MMM d" : "d MMM", culture);
    }
}
