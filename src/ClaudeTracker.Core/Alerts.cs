namespace ClaudeTracker.Core;

/// <summary>Joins names the way a language lists things.</summary>
public static class TextList
{
    /// <summary>
    /// "A", "A and B", "A, B, and C" in English (as Swift's list format writes it, which is
    /// what the Mac app shows); "A y B", "A, B y C" in Spanish.
    /// </summary>
    public static string And(IReadOnlyList<string> items) => And(items, language: null);

    /// <summary>The same in a given language; null is the app's current one.</summary>
    internal static string And(IReadOnlyList<string> items, string? language)
    {
        string Join(string key, string head, string last) =>
            language is null ? L.F(key, head, last) : L.Format(language, key, head, last);

        return items.Count switch
        {
            0 => "",
            1 => items[0],
            2 => Join("%@ and %@", items[0], items[1]),
            _ => Join("%@, and %@", string.Join(", ", items.Take(items.Count - 1)), items[^1]),
        };
    }
}

/// <summary>
/// The words of the two alerts, and the rule for which windows raise them. The Mac app builds
/// these inside its view model (<c>dispatchNotifications</c>, <c>dispatchPaceAlert</c>,
/// <c>isWatched</c>); here they are pure, so they are tested.
/// </summary>
public static class AlertText
{
    public static string ResetTitle => L.T("Claude Usage Reset");

    /// <summary>
    /// "5-Hour Window reset — you're good to go!", or for several windows in one poll
    /// "5-Hour Window and 7-Day Window have reset — you're good to go!". Two keys, not one:
    /// Spanish inflects the verb.
    /// </summary>
    public static string ResetBody(IReadOnlyList<string> windows) =>
        windows.Count > 1
            ? L.F("%@ have reset — you're good to go!", TextList.And(windows))
            : L.F("%@ reset — you're good to go!", TextList.And(windows));

    public static string PaceTitle => L.T("Approaching usage limit");

    /// <summary>"5-Hour Window fills in 18 min at 42%/hr".</summary>
    public static string PaceBody(string window, int minutesLeft, double ratePerHour, PaceRateUnit unit) =>
        L.F("%@ fills in %d min at %@", window, minutesLeft, unit.Format(ratePerHour));
}

/// <summary>
/// The alert settings' ranges and starting values — the Mac app's, registered in the
/// workspace's <c>BUSINESS_RULES.md</c> — and the rule for which windows are watched.
/// </summary>
public static class AlertSettings
{
    /// <summary>
    /// A slider's value as a screen reader says it: "30 minutes", not the "30m" beside the
    /// slider, which is read out as a number and a letter.
    /// </summary>
    public static string MinutesInWords(int minutes) => minutes == 1 ? L.T("1 minute") : L.F("%d minutes", minutes);

    /// <inheritdoc cref="MinutesInWords"/>
    public static string SecondsInWords(int seconds) => seconds == 1 ? L.T("1 second") : L.F("%d seconds", seconds);

    /// <summary>Seconds a toast stays, as the "Duration" slider allows.</summary>
    public const double ToastSecondsMinimum = 1;
    public const double ToastSecondsMaximum = 30;
    public const double ResetToastSecondsDefault = 3;
    public const double PaceToastSecondsDefault = 5;

    /// <summary>Minutes to full below which a pace alert fires, as "Warn with less than" allows.</summary>
    public const double WarningMinutesMinimum = 5;
    public const double WarningMinutesMaximum = 60;
    public const double WarningMinutesStep = 5;
    public const double WarningMinutesDefault = 30;

    /// <summary>A stored toast duration brought into range; anything unusable becomes <paramref name="fallback"/>.</summary>
    public static double ToastSeconds(double? stored, double fallback) =>
        stored is { } value && value > 0 && !double.IsNaN(value) ? Math.Clamp(value, ToastSecondsMinimum, ToastSecondsMaximum) : fallback;

    /// <summary>A stored warning threshold brought into range; anything unusable becomes the default.</summary>
    public static double WarningMinutes(double? stored) =>
        stored is { } value && value > 0 && !double.IsNaN(value) ? Math.Clamp(value, WarningMinutesMinimum, WarningMinutesMaximum) : WarningMinutesDefault;

    /// <summary>
    /// Whether a window's resets and pace are alerted on. The two built-in windows follow their
    /// own switches. A per-model window rides the 7-Day switch and also needs "Show per-model
    /// usage": a window worth warning about should announce its reset too, and one that is
    /// hidden should do neither.
    /// </summary>
    public static bool IsWatched(TrackedWindow window, bool notify5Hour, bool notify7Day, bool showModelWindows)
    {
        if (window.IsModelScoped) return notify7Day && showModelWindows;
        return window.Key == MenuBarWindow.FiveHour.RawValue() ? notify5Hour : notify7Day;
    }
}
