using System.Text.Json.Serialization;

namespace ClaudeTracker.Core;

// Chart history and the pure helpers of the Charts tab — the Windows twins of
// UsageDataPoint / appendPrunedDataPoint / chartSeries / downsample in the macOS app.

/// <summary>
/// A single timestamped utilization snapshot, stored persistently for the Charts tab.
///
/// Sampled at most once every 5 minutes regardless of poll rate; capped at 8640 entries
/// (30 days at this resolution) and pruned to a 30-day window. The JSON names are short on
/// purpose: the whole history file is rewritten at every sample.
/// </summary>
/// <param name="Timestamp">
/// Stored to the millisecond. Create samples with <see cref="Charts.SampleTime"/>, so a point
/// in memory equals the same point read back from its file.
/// </param>
/// <param name="FiveHourPace">Consumption rate in %/hr at snapshot time; null when history was insufficient.</param>
/// <param name="Models">
/// Per-model utilization keyed by the window's history key ("seven_day_sonnet",
/// "scoped.&lt;Model&gt;"). Null on accounts whose response carries no model-scoped limits.
/// </param>
/// <param name="ModelPaces">Per-model consumption rate in %/hr, keyed like <paramref name="Models"/>.</param>
public sealed record UsageDataPoint(
    [property: JsonPropertyName("t"), JsonConverter(typeof(UnixSecondsConverter)), JsonRequired] DateTimeOffset Timestamp,
    [property: JsonPropertyName("h5")] double? FiveHour,
    [property: JsonPropertyName("d7")] double? SevenDay,
    [property: JsonPropertyName("h5p")] double? FiveHourPace,
    [property: JsonPropertyName("d7p")] double? SevenDayPace,
    [property: JsonPropertyName("m")] IReadOnlyDictionary<string, double>? Models = null,
    [property: JsonPropertyName("mp")] IReadOnlyDictionary<string, double>? ModelPaces = null)
{
    /// <summary>
    /// Utilization for a chart series, routing the two built-in windows to their dedicated
    /// fields and every model-scoped window to the dictionary.
    /// </summary>
    public double? Utilization(string key) => key switch
    {
        "five_hour" => FiveHour,
        "seven_day" => SevenDay,
        _ => Models is not null && Models.TryGetValue(key, out var value) ? value : null,
    };

    /// <summary>Consumption rate in %/hr for a chart series, keyed like <see cref="Utilization"/>.</summary>
    public double? PaceRate(string key) => key switch
    {
        "five_hour" => FiveHourPace,
        "seven_day" => SevenDayPace,
        _ => ModelPaces is not null && ModelPaces.TryGetValue(key, out var value) ? value : null,
    };
}

/// <summary>
/// One section of the Charts tab: a usage window plus the key its history samples are stored
/// under (shared with the pace buckets).
/// </summary>
/// <param name="Title">Already-localized section title, e.g. "5-Hour" or "7-Day Fable".</param>
/// <param name="Window">The live window, used for the forecast chart. Null before the first fetch.</param>
/// <param name="DurationSeconds">Window length, used to anchor the forecast chart's start.</param>
public sealed record ChartSeries(string Key, string Title, UsageWindow? Window, double DurationSeconds);

/// <summary>How much history the Charts tab displays.</summary>
public enum ChartTimeRange { OneHour, FiveHours, OneDay, SevenDays, ThirtyDays }

public static class ChartTimeRanges
{
    public static IReadOnlyList<ChartTimeRange> All { get; } =
        [ChartTimeRange.OneHour, ChartTimeRange.FiveHours, ChartTimeRange.OneDay, ChartTimeRange.SevenDays, ChartTimeRange.ThirtyDays];

    /// <summary>Both the stored value and the label on the picker.</summary>
    public static string RawValue(this ChartTimeRange range) => range switch
    {
        ChartTimeRange.OneHour => "1h",
        ChartTimeRange.FiveHours => "5h",
        ChartTimeRange.OneDay => "24h",
        ChartTimeRange.SevenDays => "7d",
        _ => "30d",
    };

    public static ChartTimeRange? FromRawValue(string? raw) => raw switch
    {
        "1h" => ChartTimeRange.OneHour,
        "5h" => ChartTimeRange.FiveHours,
        "24h" => ChartTimeRange.OneDay,
        "7d" => ChartTimeRange.SevenDays,
        "30d" => ChartTimeRange.ThirtyDays,
        _ => null,
    };

    public static double Hours(this ChartTimeRange range) => range switch
    {
        ChartTimeRange.OneHour => 1,
        ChartTimeRange.FiveHours => 5,
        ChartTimeRange.OneDay => 24,
        ChartTimeRange.SevenDays => 168,
        _ => 720,
    };
}

public static class Charts
{
    /// <summary>Time buckets per chart for <see cref="Downsample"/>: about one per horizontal point of a chart.</summary>
    public const int Buckets = 150;

    /// <summary>An instant cut to the millisecond, which is all a chart sample's file keeps.</summary>
    public static DateTimeOffset SampleTime(DateTimeOffset instant) =>
        new(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMillisecond, instant.Offset);

    /// <summary>
    /// Appends a chart snapshot to persistent history, enforcing the sampling contract: at most
    /// one point per <paramref name="minInterval"/> seconds (null when throttled — the caller
    /// keeps the old history), entries older than <paramref name="maxAge"/> seconds pruned, and
    /// the result capped to the newest <paramref name="cap"/>.
    /// </summary>
    public static List<UsageDataPoint>? AppendPrunedDataPoint(UsageDataPoint point, IReadOnlyList<UsageDataPoint> history,
                                                              DateTimeOffset? lastTimestamp, double minInterval = 300,
                                                              double maxAge = 30 * 24 * 3600, int cap = 8640)
    {
        if (lastTimestamp is { } last && (point.Timestamp - last).TotalSeconds < minInterval) return null;
        var cutoff = point.Timestamp.AddSeconds(-maxAge);
        var result = history.Where(p => p.Timestamp >= cutoff).ToList();
        result.Add(point);
        if (result.Count > cap) result.RemoveRange(0, result.Count - cap);
        return result;
    }

    /// <summary>
    /// The chart sections available for a usage response, in display order.
    ///
    /// The two built-in windows are always listed, with or without a response: the Charts tab
    /// renders them from persisted history, so dropping them when there is no response (fresh
    /// launch, failing fetch) would blank charts that have 30 days of data behind them.
    /// Model-scoped series exist only while the response reports them.
    /// </summary>
    public static IReadOnlyList<ChartSeries> SeriesFor(UsageResponse? response)
    {
        const double week = 7 * 24 * 3600;
        var result = new List<ChartSeries>
        {
            new(MenuBarWindow.FiveHour.RawValue(), MenuBarWindow.FiveHour.ShortLabel(), response?.FiveHour, 5 * 3600),
            new(MenuBarWindow.SevenDay.RawValue(), MenuBarWindow.SevenDay.ShortLabel(), response?.SevenDay, week),
        };
        foreach (var tracked in response?.TrackedWindows ?? [])
        {
            if (tracked.IsModelScoped) result.Add(new ChartSeries(tracked.Key, tracked.Title, tracked.Window, week));
        }
        return result;
    }

    /// <summary>
    /// Serializes the Charts tab's hidden-series set. Newline-separated because keys embed
    /// API-supplied model names, and sorted so rebuilding an unchanged set can't churn the
    /// stored string.
    /// </summary>
    public static string EncodeHiddenKeys(IEnumerable<string> keys) =>
        string.Join("\n", keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    public static HashSet<string> DecodeHiddenKeys(string? raw) =>
        new((raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>
    /// At most two samples per time bucket over [<paramref name="lower"/>, <paramref name="upper"/>]
    /// (each bucket's lowest and highest, in time order) plus the first and last sample. A 30-day
    /// chart then draws ~300 marks instead of 8640, while peaks and reset drops still show.
    /// Expects <paramref name="pairs"/> in time order, as the history is stored.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Time, double Value)> Downsample(
        IReadOnlyList<(DateTimeOffset Time, double Value)> pairs, int buckets, DateTimeOffset lower, DateTimeOffset upper)
    {
        if (buckets <= 0) return pairs;
        var width = (upper - lower).TotalSeconds / buckets;
        if (!(width > 0) || pairs.Count <= 2 * buckets + 2) return pairs;

        int Bucket(int i) => Math.Min(buckets - 1, (int)((pairs[i].Time - lower).TotalSeconds / width));
        var keep = new List<int> { 0 };
        var index = 0;
        while (index < pairs.Count)
        {
            var bucket = Bucket(index);
            int lo = index, hi = index, j = index + 1;
            while (j < pairs.Count && Bucket(j) == bucket)
            {
                if (pairs[j].Value < pairs[lo].Value) lo = j;
                if (pairs[j].Value > pairs[hi].Value) hi = j;
                j++;
            }
            keep.Add(Math.Min(lo, hi));
            if (lo != hi) keep.Add(Math.Max(lo, hi));
            index = j;
        }
        keep.Add(pairs.Count - 1);

        var result = new List<(DateTimeOffset, double)>(keep.Count);
        var previous = -1;
        foreach (var k in keep)
        {
            if (k == previous) continue;
            result.Add(pairs[k]);
            previous = k;
        }
        return result;
    }

    /// <summary>
    /// The sample nearest the hovered instant, or null when none is close enough: only a sample
    /// within 5% of the visible span (min 10 min) counts — inside a data gap (PC asleep) there is
    /// no reading, and snapping to one hours away would present it as belonging to the hovered instant.
    /// </summary>
    public static (DateTimeOffset Time, double Value)? NearestSample(
        IReadOnlyList<(DateTimeOffset Time, double Value)> pairs, DateTimeOffset t, double spanSeconds)
    {
        if (pairs.Count == 0) return null;
        var nearest = pairs[0];
        var best = Math.Abs((nearest.Time - t).TotalSeconds);
        for (var i = 1; i < pairs.Count; i++)
        {
            var distance = Math.Abs((pairs[i].Time - t).TotalSeconds);
            if (distance < best) { best = distance; nearest = pairs[i]; }
        }
        return best <= Math.Max(spanSeconds * 0.05, 600) ? nearest : null;
    }
}
