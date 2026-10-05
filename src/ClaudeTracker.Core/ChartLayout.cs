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

    /// <summary>The figures above a chart: the latest value, the highest, and the average. Null without samples.</summary>
    public static (double Now, double Peak, double Average)? Stats(IReadOnlyList<(DateTimeOffset Time, double Value)> pairs)
    {
        if (pairs.Count == 0) return null;
        double peak = double.NegativeInfinity, sum = 0;
        foreach (var (_, value) in pairs)
        {
            peak = Math.Max(peak, value);
            sum += value;
        }
        return (pairs[^1].Value, peak, sum / pairs.Count);
    }

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
