using System.Globalization;

namespace ClaudeTracker.Core;

// The pure logic of the macOS app's Models.swift: urgency colors, pace, polling tiers, reset
// detection, and display formatting. Every function here has a Swift twin of the same name
// and is covered by the same test cases — change them together.

// MARK: - Windows and display choice

/// <summary>One of the two built-in rate-limit windows. Its raw value is the window's API key.</summary>
public enum MenuBarWindow { FiveHour, SevenDay }

public static class MenuBarWindows
{
    public static IReadOnlyList<MenuBarWindow> All { get; } = [MenuBarWindow.FiveHour, MenuBarWindow.SevenDay];

    public static string RawValue(this MenuBarWindow window) => window == MenuBarWindow.FiveHour ? "five_hour" : "seven_day";

    /// <summary>How a key is recognized as built-in — so this enum must not gain non-window cases.</summary>
    public static MenuBarWindow? FromRawValue(string? raw) => raw switch
    {
        "five_hour" => MenuBarWindow.FiveHour,
        "seven_day" => MenuBarWindow.SevenDay,
        _ => null,
    };

    public static string Label(this MenuBarWindow window) =>
        window == MenuBarWindow.FiveHour ? L.T("5-Hour Window") : L.T("7-Day Window");

    /// <summary>Compact title for the Charts tab sections, where the "Window" suffix is noise.</summary>
    public static string ShortLabel(this MenuBarWindow window) =>
        window == MenuBarWindow.FiveHour ? L.T("5-Hour") : L.T("7-Day");
}

/// <summary>
/// The Settings choice for what the tray icon tracks. Raw values match <see cref="MenuBarWindow"/>
/// so both apps store the preference the same way.
/// </summary>
public enum MenuBarDisplay
{
    FiveHour,
    SevenDay,
    /// <summary>Whichever shown window is most utilized — a weekly model limit is often the one that runs out first.</summary>
    Highest,
}

public static class MenuBarDisplays
{
    public static IReadOnlyList<MenuBarDisplay> All { get; } = [MenuBarDisplay.FiveHour, MenuBarDisplay.SevenDay, MenuBarDisplay.Highest];

    public static string RawValue(this MenuBarDisplay display) => display switch
    {
        MenuBarDisplay.FiveHour => "five_hour",
        MenuBarDisplay.SevenDay => "seven_day",
        _ => "highest",
    };

    public static MenuBarDisplay? FromRawValue(string? raw) => raw switch
    {
        "five_hour" => MenuBarDisplay.FiveHour,
        "seven_day" => MenuBarDisplay.SevenDay,
        "highest" => MenuBarDisplay.Highest,
        _ => null,
    };

    public static string Label(this MenuBarDisplay display) =>
        MenuBarWindows.FromRawValue(display.RawValue())?.Label() ?? L.T("Highest usage");

    /// <summary>
    /// The window the tray icon tracks for <paramref name="display"/>, from <paramref name="windows"/>
    /// in display order: the chosen built-in window, or for <see cref="MenuBarDisplay.Highest"/> the
    /// most utilized one — the first on a tie, so the pick can't flip between equal windows.
    /// </summary>
    public static TrackedWindow? TrackedWindowFor(MenuBarDisplay display, IReadOnlyList<TrackedWindow> windows)
    {
        if (display != MenuBarDisplay.Highest) return windows.FirstOrDefault(w => w.Key == display.RawValue());
        TrackedWindow? best = null;
        foreach (var next in windows)
        {
            if (best is null || !(best.Window.Utilization >= next.Window.Utilization)) best = next;
        }
        return best;
    }
}

// MARK: - Urgency colors

/// <summary>An opaque sRGB color with components in 0…1.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public byte R8 => ToByte(R);
    public byte G8 => ToByte(G);
    public byte B8 => ToByte(B);

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    public static Rgb Gray(byte value) => new(value / 255.0, value / 255.0, value / 255.0);
}

public static class Urgency
{
    /// <summary>
    /// Maps a normalized urgency (0 = calm, 1 = critical) to a color. Hue interpolates
    /// continuously: green (0) → yellow → orange → red (1).
    /// </summary>
    public static Rgb Color(double urgency)
    {
        var t = Math.Clamp(urgency, 0, 1);
        return FromHsb(0.33 * (1 - t), 0.85, 0.9);
    }

    public static Rgb FromHsb(double hue, double saturation, double brightness)
    {
        if (saturation <= 0) return new Rgb(brightness, brightness, brightness);
        var h = (hue - Math.Floor(hue)) * 6;
        var sector = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);
        var p = brightness * (1 - saturation);
        var q = brightness * (1 - saturation * f);
        var t = brightness * (1 - saturation * (1 - f));
        return sector switch
        {
            0 => new Rgb(brightness, t, p),
            1 => new Rgb(q, brightness, p),
            2 => new Rgb(p, brightness, t),
            3 => new Rgb(p, q, brightness),
            4 => new Rgb(t, p, brightness),
            _ => new Rgb(brightness, p, q),
        };
    }

    /// <summary>Reference backgrounds for text contrast: the Mac popover's light and dark material.</summary>
    public static Rgb PopoverBackground(bool isDark) => Rgb.Gray(isDark ? (byte)0x2B : (byte)0xEC);

    /// <summary>WCAG 2 contrast ratio between two opaque colors, from 1 to 21.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Luminance(Rgb c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// <see cref="Color"/> for text: mixed toward black on a light background, or white on a dark
    /// one, just far enough to reach WCAG AA (4.5:1) against it. The mix keeps the hue. Bars,
    /// charts, and the tray icon keep the raw gradient.
    /// </summary>
    public static Rgb TextColor(double urgency, bool isDark) => TextColor(urgency, isDark, PopoverBackground(isDark));

    public static Rgb TextColor(double urgency, bool isDark, Rgb background)
    {
        var baseColor = Color(urgency);
        var target = isDark ? 1.0 : 0.0;
        var color = baseColor;
        for (var step = 0; step <= 50; step++)
        {
            var f = step / 50.0;
            color = new Rgb(baseColor.R + (target - baseColor.R) * f,
                            baseColor.G + (target - baseColor.G) * f,
                            baseColor.B + (target - baseColor.B) * f);
            if (ContrastRatio(color, background) >= 4.5) break;
        }
        return color;
    }
}

// MARK: - Pace

/// <summary>
/// The shared 3-band classification for all pace UI (rate text color, outlook message, chart
/// projection line). Keeps the 0.8× threshold in exactly one place.
/// </summary>
public enum PaceBand
{
    /// <summary>Projected fill time is at or beyond the reset — consumption is sustainable.</summary>
    Safe,
    /// <summary>Projected fill lands within 80–100% of the time to reset — cutting it close.</summary>
    Close,
    /// <summary>Projected to fill before the window resets.</summary>
    Over,
}

/// <summary>
/// One poll's outcome for a window's pace alert: fire a new alert, dismiss its toast, and
/// whether the window stays warned afterwards.
/// </summary>
public readonly record struct PaceAlertStep(bool Fire, bool Dismiss, bool Warned);

public static class PaceMath
{
    public static PaceBand Band(double projectedHours, double hoursToReset)
    {
        if (!(projectedHours > 0) || !(hoursToReset > 0)) return PaceBand.Safe;
        if (projectedHours >= hoursToReset) return PaceBand.Safe;
        return projectedHours >= hoursToReset * 0.8 ? PaceBand.Close : PaceBand.Over;
    }

    /// <summary>
    /// Pace state as an urgency value on the same 0…1 scale as utilization, so the tray icon can
    /// take <c>max(utilization, pace)</c> and still agree with the popover's band:
    /// safe → 0, close → 0.7, over → 1.0. The single source for every pace color.
    /// </summary>
    public static double PaceUrgency(double projectedHours, double hoursToReset) => Band(projectedHours, hoursToReset) switch
    {
        PaceBand.Safe => 0,
        PaceBand.Close => 0.7,
        _ => 1.0,
    };

    /// <summary>
    /// Pace urgency for a window's row and its charts: 0 (neutral) unless there is a projection,
    /// a reset to measure it against, and the window is still live. One helper so the rate text,
    /// the pace chart, and the forecast line can never disagree on the band.
    /// </summary>
    public static double AccentUrgency(double? projectedHours, DateTimeOffset? resetsAt, bool isStale, DateTimeOffset now)
    {
        if (isStale || projectedHours is not { } projected || resetsAt is not { } reset) return 0;
        return PaceUrgency(projected, (reset - now).TotalHours);
    }

    /// <summary>
    /// Computes consumption rate (%/hr) and projected hours-to-full from a utilization history.
    ///
    /// Uses exponentially-weighted linear regression so recent consumption dominates the slope:
    /// weight w = exp(λ · t), where t ∈ [0, 1] runs from oldest to newest. At the default λ = 2
    /// the newest point weighs about 7× the oldest.
    ///
    /// Needs at least 2 points spanning 15 seconds. Null when the rate is negligible
    /// (≤ 0.1 %/hr) or data is insufficient.
    /// </summary>
    public static (double Rate, double? ProjectedHours)? ComputePace(IReadOnlyList<(DateTimeOffset Time, double Value)> history, double lambda = 2.0)
    {
        if (history.Count < 2) return null;
        var oldest = history[0];
        var newest = history[^1];
        var elapsedSeconds = (newest.Time - oldest.Time).TotalSeconds;
        if (!(elapsedSeconds >= 15.0)) return null;

        double w = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (time, value) in history)
        {
            var seconds = (time - oldest.Time).TotalSeconds;
            var xi = seconds / 3600.0;            // hours from oldest
            var norm = seconds / elapsedSeconds;  // [0, 1]
            var wi = Math.Exp(lambda * norm);
            w += wi;
            sx += wi * xi;
            sy += wi * value;
            sxx += wi * xi * xi;
            sxy += wi * xi * value;
        }

        var denominator = w * sxx - sx * sx;
        if (!(Math.Abs(denominator) > 1e-12)) return null;
        var rate = (w * sxy - sx * sy) / denominator; // %/hr

        if (!(rate > 0.1)) return null;
        var remaining = 100.0 - newest.Value;
        return (rate, remaining > 0 ? remaining / rate : null);
    }

    /// <summary>
    /// True when the rolling pace history must be cleared because the window reset: a drop of
    /// more than 20 points, or utilization falling below 5% from at or above 5% — the latter
    /// catches resets from low utilization that the drop threshold would miss.
    /// </summary>
    public static bool ShouldResetPaceHistory(double last, double current) =>
        last - current > 20 || (current < 5 && last >= 5);

    /// <summary>
    /// Index into a rotating message list that stays fixed for a whole window cycle.
    /// Resets land on 10-minute marks, but <c>resets_at</c> arrives a fraction of a second
    /// either side of the mark, so this counts whole 10-minute slots (rounded).
    /// </summary>
    public static int OutlookMessageIndex(DateTimeOffset reset, int count)
    {
        if (count <= 0) return 0;
        var slots = (long)Math.Round((reset - DateTimeOffset.UnixEpoch).TotalSeconds / 600, MidpointRounding.AwayFromZero);
        return (int)(Math.Abs(slots) % count);
    }

    /// <summary>
    /// How far ahead of its reset a window projected to fill early runs out. Truncated to whole
    /// minutes, never rounded up, and at least one minute so the line never reads "~0m".
    /// </summary>
    public static (int Hours, int Minutes) EarlyFillLead(double hoursEarly)
    {
        var totalMinutes = Math.Max(1, (int)(hoursEarly * 60));
        return (totalMinutes / 60, totalMinutes % 60);
    }

    /// <summary>
    /// The pace-alert state machine for one window. It warns at most once per concerning episode
    /// (projected to fill within <paramref name="threshold"/> minutes). Once the projection
    /// clears the threshold the toast goes, but the window re-arms only past 1.25× the threshold,
    /// or when the projection disappears: the regression jitters, and re-arming at the exact
    /// boundary would re-fire every few polls. An unwatched window is cleared outright.
    /// </summary>
    public static PaceAlertStep AlertStep(bool watched, bool warned, double? projectedMinutes, double threshold)
    {
        if (!watched) return new PaceAlertStep(Fire: false, Dismiss: true, Warned: false);
        if (projectedMinutes is { } minutes && minutes < threshold)
        {
            return new PaceAlertStep(Fire: !warned, Dismiss: false, Warned: true);
        }
        if (!warned) return new PaceAlertStep(Fire: false, Dismiss: false, Warned: false);
        return new PaceAlertStep(Fire: false, Dismiss: true, Warned: (projectedMinutes ?? double.PositiveInfinity) <= threshold * 1.25);
    }
}

// MARK: - Polling

public static class Polling
{
    /// <summary>
    /// The adaptive polling interval, in seconds, for a single usage window:
    /// stale window (reset already passed) → 2; at 100% → 10–300 by time until reset (30 if
    /// unknown); active with pace → 1–10 by projected minutes to full; otherwise 3–10 by utilization.
    /// </summary>
    public static double PollInterval(double utilization, DateTimeOffset? resetsAt, double? projectedMinutes, DateTimeOffset now)
    {
        // Stale: reset already passed — poll aggressively to catch the new window.
        if (resetsAt is { } passed && passed < now) return 2;

        // At 100%: only need to catch the upcoming reset.
        if (utilization >= 99.9)
        {
            if (resetsAt is not { } reset) return 30;
            var seconds = Math.Max(0, (reset - now).TotalSeconds);
            if (seconds >= 1800) return 300;
            if (seconds >= 600) return 120;
            if (seconds >= 120) return 30;
            return 10;
        }

        if (projectedMinutes is { } minutes) return PollIntervalForProjectedMinutes(minutes);

        // No pace signal yet: step by utilization.
        if (utilization >= 95) return 3;
        if (utilization >= 80) return 5;
        if (utilization >= 50) return 8;
        return 10;
    }

    /// <summary>Poll interval for a window that is actively filling, from projected minutes to full.</summary>
    public static double PollIntervalForProjectedMinutes(double projectedMinutes)
    {
        if (projectedMinutes >= 60) return 10;
        if (projectedMinutes >= 30) return 8;
        if (projectedMinutes >= 15) return 5;
        if (projectedMinutes >= 5) return 3;
        if (projectedMinutes >= 2) return 2;
        return 1;
    }

    /// <summary>
    /// The poll interval for a whole response: the most urgent tier among its tracked windows
    /// (10 s when there are none). Model-scoped limits count, since a weekly model limit is often
    /// the binding one.
    /// </summary>
    public static double AdaptivePollInterval(IReadOnlyList<TrackedWindow> windows, Func<string, double?> projectedMinutes, DateTimeOffset now) =>
        windows.Count == 0
            ? 10
            : windows.Min(w => PollInterval(w.Window.Utilization, w.Window.ResetsAtDate, projectedMinutes(w.Key), now));

    /// <summary>
    /// Whether a poll's "next in …" line is worth logging: it differs from the last logged one,
    /// or <paramref name="heartbeat"/> seconds have passed. Logged on every poll it would be ~99% of the log.
    /// </summary>
    public static bool ShouldLogPoll(string line, (string Line, DateTimeOffset At)? last, DateTimeOffset now, double heartbeat = 600) =>
        last is not { } previous || line != previous.Line || (now - previous.At).TotalSeconds >= heartbeat;

    /// <summary>Additional poll delay after consecutive fetch errors: 10 s per error, capped at 60 s.</summary>
    public static double ErrorBackoff(int consecutiveErrors) =>
        consecutiveErrors > 0 ? Math.Min(consecutiveErrors * 10.0, 60) : 0;
}

// MARK: - Resets

public static class Resets
{
    /// <summary>
    /// True when a window's reset timestamp jumped forward by more than an hour AND utilization
    /// dropped below 5% — the signature of a real reset, not a rolling-expiry timestamp refresh.
    /// </summary>
    public static bool IsWindowReset(DateTimeOffset previous, DateTimeOffset next, double utilization) =>
        (next - previous).TotalSeconds > 3600 && utilization < 5;

    /// <summary>
    /// One poll's reset bookkeeping: the keys of the windows that reset since
    /// <paramref name="stored"/> (the last known reset time per window key), in
    /// <paramref name="windows"/> order, and the reset times to keep.
    ///
    /// A window whose <c>resets_at</c> comes back null, or that is missing from the response,
    /// has reset once its stored time has passed at under 5% — the forward jump only arrives once
    /// usage resumes, possibly hours later. Its entry is then dropped, so that later date reads
    /// as a baseline instead of a second reset.
    /// </summary>
    public static (IReadOnlyList<string> Reset, Dictionary<string, DateTimeOffset> Stored) DetectResets(
        IReadOnlyDictionary<string, DateTimeOffset> stored, IReadOnlyList<TrackedWindow> windows, DateTimeOffset now)
    {
        var reset = new List<string>();
        var next = new Dictionary<string, DateTimeOffset>(stored, StringComparer.Ordinal);
        foreach (var tracked in windows)
        {
            var utilization = tracked.Window.Utilization;
            if (tracked.Window.ResetsAtDate is { } date)
            {
                if (stored.TryGetValue(tracked.Key, out var previous) && IsWindowReset(previous, date, utilization))
                {
                    reset.Add(tracked.Key);
                }
                next[tracked.Key] = date;
            }
            else if (stored.TryGetValue(tracked.Key, out var previous) && previous <= now && utilization < 5)
            {
                reset.Add(tracked.Key);
                next.Remove(tracked.Key);
            }
        }
        var present = windows.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, previous) in stored.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (present.Contains(key) || previous > now) continue;
            reset.Add(key);
            next.Remove(key);
        }
        return (reset, next);
    }

    /// <summary>
    /// True when stored usage belongs to a window cycle that has since reset: the reset time has
    /// passed AND the last fetch predates it, so the displayed utilization is definitively stale.
    /// </summary>
    public static bool WindowIsStale(DateTimeOffset? resetsAt, DateTimeOffset? lastUpdated, DateTimeOffset now) =>
        resetsAt is { } reset && lastUpdated is { } updated && reset < now && updated < reset;
}

// MARK: - Time display

public static class TimeText
{
    /// <summary>
    /// The culture dates are written in. Windows keeps two: the display language, and the
    /// regional format, which may be another language's. A date written in the regional
    /// format's language inside a sentence in the display language reads as a mistake
    /// ("Se reinicia en 2 días · Thu, Oct 8 at 15:50"), so the regional format is used only
    /// while it speaks the display language. The Mac app has one locale for both.
    /// </summary>
    public static CultureInfo DisplayCulture(CultureInfo regional, CultureInfo display) =>
        regional.TwoLetterISOLanguageName == display.TwoLetterISOLanguageName ? regional : display;

    /// <summary><see cref="DisplayCulture(CultureInfo, CultureInfo)"/> for this thread's two cultures.</summary>
    public static CultureInfo DisplayCulture() => DisplayCulture(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

    /// <summary>Whether a culture conventionally uses a 24-hour clock — seeds the Time format setting on first launch.</summary>
    public static bool Prefers24HourClock(CultureInfo culture) =>
        culture.DateTimeFormat.ShortTimePattern.Contains('H');

    /// <summary>A clock time in the user's 12/24-hour choice: "5:30 PM" or "17:30".</summary>
    public static string Clock(DateTimeOffset time, bool use24Hour, CultureInfo culture) =>
        time.ToString(use24Hour ? "HH:mm" : "h:mm tt", culture);

    /// <summary>
    /// The absolute wall-clock time of a window reset, shown next to the countdown.
    ///
    /// The user's 12/24-hour choice wins over the culture's convention, while weekday and month
    /// names stay localized. When the reset is not on the same calendar day as
    /// <paramref name="now"/>, an abbreviated weekday is prepended; <paramref name="includeDate"/>
    /// additionally adds the abbreviated month and day (used by the 7-day windows — a weekday
    /// alone reads ambiguous when the reset lands on today's weekday next week).
    /// </summary>
    public static string ResetTimeText(DateTimeOffset reset, DateTimeOffset now, bool use24Hour, bool includeDate = false,
                                       TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        // resets_at lands a fraction of a second either side of the real boundary from poll to
        // poll — snap to the nearest minute (before the same-day check) so the shown time
        // doesn't flip between e.g. 17:59 and 18:00.
        var minutes = Math.Round((double)reset.UtcTicks / TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero);
        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset((long)minutes * TimeSpan.TicksPerMinute, TimeSpan.Zero), timeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        var time = Clock(local, use24Hour, culture);
        if (local.Date == localNow.Date) return time;

        var weekday = local.ToString("ddd", culture).TrimEnd('.');
        var spanish = culture.TwoLetterISOLanguageName == "es";
        if (!includeDate) return spanish ? $"{weekday}, {time}" : $"{weekday} {time}";

        var month = local.ToString("MMM", culture).TrimEnd('.');
        var day = local.Day.ToString(CultureInfo.InvariantCulture);
        return spanish ? $"{weekday}, {day} {month}, {time}" : $"{weekday}, {month} {day} at {time}";
    }
}

// MARK: - Pace rate unit

/// <summary>The time unit used to display the consumption rate in the UI.</summary>
public enum PaceRateUnit { PerHour, PerMinute, PerSecond }

public static class PaceRateUnits
{
    public static IReadOnlyList<PaceRateUnit> All { get; } = [PaceRateUnit.PerHour, PaceRateUnit.PerMinute, PaceRateUnit.PerSecond];

    public static string RawValue(this PaceRateUnit unit) => unit switch
    {
        PaceRateUnit.PerHour => "perHour",
        PaceRateUnit.PerMinute => "perMinute",
        _ => "perSecond",
    };

    public static PaceRateUnit? FromRawValue(string? raw) => raw switch
    {
        "perHour" => PaceRateUnit.PerHour,
        "perMinute" => PaceRateUnit.PerMinute,
        "perSecond" => PaceRateUnit.PerSecond,
        _ => null,
    };

    public static string Label(this PaceRateUnit unit) => unit switch
    {
        PaceRateUnit.PerHour => L.T("Per Hour"),
        PaceRateUnit.PerMinute => L.T("Per Minute"),
        _ => L.T("Per Second"),
    };

    /// <summary>Formats a rate (always computed internally as %/hr) for display.</summary>
    /// <param name="prefix">Prepends "+" (for live pace lines and the tray tooltip).</param>
    /// <param name="short">Uses single-character time abbreviations.</param>
    public static string Format(this PaceRateUnit unit, double ratePerHour, bool prefix = false, bool @short = false)
    {
        var sign = prefix ? "+" : "";
        switch (unit)
        {
            case PaceRateUnit.PerHour:
                var hourUnit = @short ? "/h" : "/hr";
                return ratePerHour < 10
                    ? $"{sign}{Fixed(ratePerHour, 1)}%{hourUnit}"
                    : $"{sign}{(long)Math.Round(ratePerHour, MidpointRounding.AwayFromZero)}%{hourUnit}";
            case PaceRateUnit.PerMinute:
                var minuteUnit = @short ? "/m" : "/min";
                var perMinute = ratePerHour / 60.0;
                return $"{sign}{Fixed(perMinute, perMinute < 1 ? 3 : 2)}%{minuteUnit}";
            default:
                return $"{sign}{Fixed(ratePerHour / 3600.0, 4)}%/s";
        }
    }

    /// <summary>
    /// Chart y-axis label: the rate in this unit, at most two significant digits, no unit — the
    /// pace chart's stats row states it, and a unit made the labels far wider than "100%".
    /// </summary>
    public static string AxisLabel(this PaceRateUnit unit, double ratePerHour)
    {
        var value = ratePerHour / unit switch { PaceRateUnit.PerHour => 1, PaceRateUnit.PerMinute => 60, _ => 3600 };
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) is < 1e-20 or > 1e20) return "0";
        var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        // Rounded as the decimal number a person would read (1.15, not 1.149999…), half to
        // even — which is how the Mac's number formatter rounds to significant digits.
        var scale = (decimal)Math.Pow(10, magnitude - 1);
        var rounded = Math.Round((decimal)value / scale, MidpointRounding.ToEven) * scale;
        return rounded.ToString("0." + new string('#', Math.Max(0, 1 - magnitude)), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// printf-style fixed notation with a "." decimal point, like the Mac app's <c>%.1f</c>.
    /// .NET's "F" format rounds the exact binary value, as C does, so 0.15 (really 0.1499…)
    /// prints "0.1" and the exact tie 0.25 prints "0.2". Do not round beforehand:
    /// <c>Math.Round(value, digits)</c> treats 0.15 as a tie and gives "0.2".
    /// </summary>
    private static string Fixed(double value, int decimals) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
