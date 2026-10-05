using System.Globalization;
using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// Tests for the pure tuning/maintenance helpers: the port of the macOS suite's
/// PureLogicTests.swift, one test per Swift test of the same name (API fixture decoding has
/// its own suite).
///
/// Four Swift tests have no twin here because their subject exists only on the Mac:
/// UrgencyNSColorMatchesSwiftUIGradient (AppKit/SwiftUI color parity),
/// JSExceptionMessageReadsTheThrownErrorFromUserInfo and JSExceptionMessageFallsBackToTheDescription
/// (WebKit's NSError), and PinnedHourCycleLocaleOverridesBase (DIVERGENCES 11).
/// </summary>
public class PureLogicTests
{
    // MARK: - pollInterval: stale window

    [Fact]
    public void PollIntervalStaleWindowPollsAggressively()
    {
        var now = T.Now;
        // Reset passed 10s ago — 2s regardless of utilization.
        Assert.Equal(2, Polling.PollInterval(utilization: 40, resetsAt: now.AddSeconds(-10), projectedMinutes: null, now: now));
        Assert.Equal(2, Polling.PollInterval(utilization: 100, resetsAt: now.AddSeconds(-10), projectedMinutes: null, now: now));
    }

    // MARK: - pollInterval: at 100%

    [Fact]
    public void PollIntervalAtFullWithUnknownReset()
    {
        Assert.Equal(30, Polling.PollInterval(utilization: 100, resetsAt: null, projectedMinutes: null, now: T.Now));
    }

    [Fact]
    public void PollIntervalAtFullTiersByTimeToReset()
    {
        var now = T.Now;
        Assert.Equal(300, Polling.PollInterval(utilization: 100, resetsAt: now.AddSeconds(3600), projectedMinutes: null, now: now));
        Assert.Equal(120, Polling.PollInterval(utilization: 100, resetsAt: now.AddSeconds(700), projectedMinutes: null, now: now));
        Assert.Equal(30, Polling.PollInterval(utilization: 100, resetsAt: now.AddSeconds(200), projectedMinutes: null, now: now));
        Assert.Equal(10, Polling.PollInterval(utilization: 100, resetsAt: now.AddSeconds(60), projectedMinutes: null, now: now));
    }

    [Fact]
    public void PollIntervalJustBelowFullThresholdUsesFallback()
    {
        var now = T.Now;
        // 99.8% is below the 99.9 threshold → falls through to utilization tiers (95... → 3)
        Assert.Equal(3, Polling.PollInterval(utilization: 99.8, resetsAt: now.AddSeconds(3600), projectedMinutes: null, now: now));
    }

    // MARK: - pollInterval: pace-driven

    [Fact]
    public void PollIntervalUsesProjectedMinutesWhenAvailable()
    {
        var now = T.Now;
        var reset = now.AddSeconds(4 * 3600);
        Assert.Equal(10, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 120, now: now));
        Assert.Equal(8, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 45, now: now));
        Assert.Equal(5, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 20, now: now));
        Assert.Equal(3, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 10, now: now));
        Assert.Equal(2, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 3, now: now));
        Assert.Equal(1, Polling.PollInterval(utilization: 50, resetsAt: reset, projectedMinutes: 1, now: now));
    }

    // MARK: - pollInterval: utilization fallback

    [Fact]
    public void PollIntervalFallbackTiersByUtilization()
    {
        var now = T.Now;
        var reset = now.AddSeconds(4 * 3600);
        Assert.Equal(3, Polling.PollInterval(utilization: 96, resetsAt: reset, projectedMinutes: null, now: now));
        Assert.Equal(5, Polling.PollInterval(utilization: 85, resetsAt: reset, projectedMinutes: null, now: now));
        Assert.Equal(8, Polling.PollInterval(utilization: 60, resetsAt: reset, projectedMinutes: null, now: now));
        Assert.Equal(10, Polling.PollInterval(utilization: 10, resetsAt: reset, projectedMinutes: null, now: now));
        Assert.Equal(10, Polling.PollInterval(utilization: 10, resetsAt: null, projectedMinutes: null, now: now));
    }

    [Fact]
    public void PollIntervalForProjectedMinutesMatchesTiers()
    {
        Assert.Equal(10, Polling.PollIntervalForProjectedMinutes(60));
        Assert.Equal(8, Polling.PollIntervalForProjectedMinutes(30));
        Assert.Equal(5, Polling.PollIntervalForProjectedMinutes(15));
        Assert.Equal(3, Polling.PollIntervalForProjectedMinutes(5));
        Assert.Equal(2, Polling.PollIntervalForProjectedMinutes(2));
        Assert.Equal(1, Polling.PollIntervalForProjectedMinutes(1));
    }

    // MARK: - adaptivePollInterval

    private static TrackedWindow Window(string key, double utilization, double resetsIn, DateTimeOffset now, bool scoped = false)
    {
        var iso = T.Iso(now.AddSeconds(resetsIn));
        return new TrackedWindow(key, Title: key, new UsageWindow(utilization, iso), IsModelScoped: scoped);
    }

    [Fact]
    public void AdaptivePollIntervalFollowsAFillingModelLimit()
    {
        // A weekly model limit is often the binding one: its pace must drive the cadence
        // even while the built-in windows are calm.
        var now = T.Now;
        TrackedWindow[] windows =
        [
            Window("five_hour", 10, resetsIn: 3 * 3600, now),
            Window("seven_day", 30, resetsIn: 3 * 86400, now),
            Window("scoped.Fable", 90, resetsIn: 3 * 86400, now, scoped: true),
        ];
        var interval = Polling.AdaptivePollInterval(windows, key => key == "scoped.Fable" ? 10 : null, now);
        Assert.Equal(Polling.PollIntervalForProjectedMinutes(10), interval);
    }

    [Fact]
    public void AdaptivePollIntervalWithoutWindowsIsTenSeconds()
    {
        Assert.Equal(10, Polling.AdaptivePollInterval([], _ => null, T.Now));
    }

    // MARK: - Menu bar window choice

    private static List<TrackedWindow> MenuBarCandidates(double fiveHour, double sevenDay, double fable)
    {
        var now = T.Now;
        return
        [
            Window("five_hour", fiveHour, resetsIn: 3600, now),
            Window("seven_day", sevenDay, resetsIn: 86400, now),
            Window("scoped.Fable", fable, resetsIn: 86400, now, scoped: true),
        ];
    }

    [Fact]
    public void MenuBarShowsTheChosenBuiltInWindow()
    {
        var windows = MenuBarCandidates(fiveHour: 10, sevenDay: 30, fable: 90);
        Assert.Equal("five_hour", MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.FiveHour, windows)?.Key);
        Assert.Equal("seven_day", MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.SevenDay, windows)?.Key);
    }

    [Fact]
    public void MenuBarHighestFollowsTheMostUtilizedWindowIncludingModelLimits()
    {
        Assert.Equal("scoped.Fable",
                     MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.Highest, MenuBarCandidates(fiveHour: 10, sevenDay: 30, fable: 90))?.Key);
        Assert.Equal("five_hour",
                     MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.Highest, MenuBarCandidates(fiveHour: 70, sevenDay: 30, fable: 60))?.Key);
    }

    [Fact]
    public void MenuBarHighestPrefersDisplayOrderOnATie()
    {
        // A stable pick: the pace badge and the screen-reader label follow the chosen window, so a
        // tie must not flip between windows from poll to poll.
        Assert.Equal("five_hour",
                     MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.Highest, MenuBarCandidates(fiveHour: 40, sevenDay: 40, fable: 40))?.Key);
    }

    [Fact]
    public void MenuBarHasNoWindowWhenTheChoiceIsMissing()
    {
        Assert.Null(MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.Highest, []));
        // Team orgs drop an idle `five_hour`.
        Assert.Null(MenuBarDisplays.TrackedWindowFor(MenuBarDisplay.FiveHour,
                                                     MenuBarCandidates(fiveHour: 0, sevenDay: 30, fable: 5).Skip(1).ToList()));
    }

    [Fact]
    public void MenuBarDisplayKeepsThePersistedWindowValues()
    {
        // Stored under the same key as the old MenuBarWindow setting: existing choices must load.
        Assert.Equal(MenuBarDisplay.FiveHour, MenuBarDisplays.FromRawValue(MenuBarWindow.FiveHour.RawValue()));
        Assert.Equal(MenuBarDisplay.SevenDay, MenuBarDisplays.FromRawValue(MenuBarWindow.SevenDay.RawValue()));
    }

    // MARK: - Poll log throttle

    [Fact]
    public void PollLogSkipsARepeatedLineUntilTheHeartbeat()
    {
        var now = T.Now;
        const string line = "poll: next in 10.0s (base=10.0s util=13%)";
        Assert.True(Polling.ShouldLogPoll(line, last: null, now));
        Assert.False(Polling.ShouldLogPoll(line, last: (line, now.AddSeconds(-60)), now));
        Assert.True(Polling.ShouldLogPoll(line, last: (line, now.AddSeconds(-600)), now));
    }

    [Fact]
    public void PollLogKeepsEveryChange()
    {
        var now = T.Now;
        Assert.True(Polling.ShouldLogPoll("poll: next in 5.0s (base=5.0s util=81%)",
                                          last: ("poll: next in 8.0s (base=8.0s util=79%)", now.AddSeconds(-3)), now));
    }

    // MARK: - Error backoff

    [Fact]
    public void ErrorBackoffScalesAndCaps()
    {
        Assert.Equal(0, Polling.ErrorBackoff(consecutiveErrors: 0));
        Assert.Equal(10, Polling.ErrorBackoff(consecutiveErrors: 1));
        Assert.Equal(30, Polling.ErrorBackoff(consecutiveErrors: 3));
        Assert.Equal(60, Polling.ErrorBackoff(consecutiveErrors: 6));
        Assert.Equal(60, Polling.ErrorBackoff(consecutiveErrors: 10));
    }

    // MARK: - Pace history reset guard

    [Fact]
    public void PaceHistoryResetsOnLargeDrop()
    {
        Assert.True(PaceMath.ShouldResetPaceHistory(last: 30, current: 9));
    }

    [Fact]
    public void PaceHistoryKeepsOnDropOfExactlyTwenty()
    {
        Assert.False(PaceMath.ShouldResetPaceHistory(last: 30, current: 10));
    }

    [Fact]
    public void PaceHistoryResetsOnLowUtilizationCrossingFive()
    {
        // 6% → 4%: small drop, but crossing below 5% from ≥5% signals a reset.
        Assert.True(PaceMath.ShouldResetPaceHistory(last: 6, current: 4));
    }

    [Fact]
    public void PaceHistoryKeepsWhenAlreadyBelowFive()
    {
        Assert.False(PaceMath.ShouldResetPaceHistory(last: 4, current: 3));
    }

    [Fact]
    public void PaceHistoryKeepsOnIncrease()
    {
        Assert.False(PaceMath.ShouldResetPaceHistory(last: 4, current: 6));
        Assert.False(PaceMath.ShouldResetPaceHistory(last: 50, current: 60));
    }

    [Fact]
    public void PaceHistoryKeepsOnModerateDrop()
    {
        Assert.False(PaceMath.ShouldResetPaceHistory(last: 50, current: 40));
    }

    // MARK: - Chart downsampling

    /// <summary>30 days of 5-minute samples: a sawtooth that climbs to a peak and drops at each reset.</summary>
    private static List<(DateTimeOffset Time, double Value)> MonthOfSamples(DateTimeOffset end) =>
        Enumerable.Range(0, 8640).Select(i =>
        {
            var time = end.AddSeconds(-(double)(8639 - i) * 300);
            var value = (double)(i % 2016) / 2016 * 97;
            return (Time: time, Value: i == 5000 ? value + 3 : value);
        }).ToList();

    [Fact]
    public void DownsampleCapsTheMarksAChartDraws()
    {
        var end = T.Now;
        var pairs = MonthOfSamples(end);
        var plotted = Charts.Downsample(pairs, buckets: 150, lower: pairs[0].Time, upper: end);
        Assert.True(plotted.Count <= 2 * 150 + 2, $"{plotted.Count} marks");
        Assert.Equal(plotted.Select(p => p.Time).Order(), plotted.Select(p => p.Time));
    }

    [Fact]
    public void DownsampleKeepsPeaksDropsAndEndpoints()
    {
        var end = T.Now;
        var pairs = MonthOfSamples(end);
        var plotted = Charts.Downsample(pairs, buckets: 150, lower: pairs[0].Time, upper: end);
        Assert.Equal(pairs.Max(p => p.Value), plotted.Max(p => p.Value));
        Assert.Equal(pairs.Min(p => p.Value), plotted.Min(p => p.Value));
        Assert.Equal(pairs[0].Time, plotted[0].Time);
        Assert.Equal(pairs[^1].Time, plotted[^1].Time);
    }

    [Fact]
    public void DownsampleLeavesShortSeriesUntouched()
    {
        var end = T.Now;
        var pairs = Enumerable.Range(0, 60).Select(i => (Time: end.AddSeconds(-(double)(59 - i) * 300), Value: (double)i)).ToList();
        var plotted = Charts.Downsample(pairs, buckets: 150, lower: pairs[0].Time, upper: end);
        Assert.Equal(pairs.Select(p => p.Time), plotted.Select(p => p.Time));
    }

    // MARK: - Chart history append/prune/cap

    private static UsageDataPoint Point(DateTimeOffset date, double v = 50) =>
        new(Timestamp: date, FiveHour: v, SevenDay: v, FiveHourPace: null, SevenDayPace: null);

    [Fact]
    public void AppendDataPointThrottledWithinFiveMinutes()
    {
        var now = T.Now;
        UsageDataPoint[] history = [Point(now.AddSeconds(-100))];
        Assert.Null(Charts.AppendPrunedDataPoint(Point(now), history, lastTimestamp: now.AddSeconds(-100)));
    }

    [Fact]
    public void AppendDataPointAppendsAfterThrottleWindow()
    {
        var now = T.Now;
        UsageDataPoint[] history = [Point(now.AddSeconds(-400))];
        var result = Charts.AppendPrunedDataPoint(Point(now), history, lastTimestamp: now.AddSeconds(-400));
        Assert.Equal(2, result?.Count);
        Assert.Equal(now, result?[^1].Timestamp);
    }

    [Fact]
    public void AppendDataPointAppendsWhenNoPriorTimestamp()
    {
        var now = T.Now;
        var result = Charts.AppendPrunedDataPoint(Point(now), [], lastTimestamp: null);
        Assert.Equal(1, result?.Count);
    }

    [Fact]
    public void AppendDataPointPrunesEntriesOlderThanThirtyDays()
    {
        var now = T.Now;
        var old = Point(now.AddSeconds(-31 * 24 * 3600));
        var recent = Point(now.AddSeconds(-3600));
        var result = Charts.AppendPrunedDataPoint(Point(now), [old, recent], lastTimestamp: now.AddSeconds(-3600));
        Assert.Equal(2, result?.Count);
        Assert.Equal(recent.Timestamp, result?[0].Timestamp);
    }

    [Fact]
    public void AppendDataPointCapsAtLimitKeepingNewest()
    {
        var now = T.Now;
        // Chronological order, oldest first — matching how history is stored.
        var history = Enumerable.Range(1, 5).Reverse().Select(i => Point(now.AddSeconds(-i * 400))).ToList();
        var result = Charts.AppendPrunedDataPoint(Point(now), history, lastTimestamp: history[^1].Timestamp, cap: 5);
        Assert.Equal(5, result?.Count);
        Assert.Equal(now, result?[^1].Timestamp);
        // The oldest entry was dropped to respect the cap.
        Assert.DoesNotContain(result!, p => p.Timestamp == now.AddSeconds(-2000));
    }

    // MARK: - Chart series sample lookup

    [Fact]
    public void DataPointRoutesBuiltInWindowsToTheirOwnFields()
    {
        var dp = new UsageDataPoint(Timestamp: T.Now, FiveHour: 12, SevenDay: 34,
                                    FiveHourPace: 1.5, SevenDayPace: 2.5);
        Assert.Equal(12, dp.Utilization("five_hour"));
        Assert.Equal(34, dp.Utilization("seven_day"));
        Assert.Equal(1.5, dp.PaceRate("five_hour"));
        Assert.Equal(2.5, dp.PaceRate("seven_day"));
    }

    [Fact]
    public void DataPointRoutesModelWindowsToTheDictionaries()
    {
        var dp = new UsageDataPoint(Timestamp: T.Now, FiveHour: 12, SevenDay: 34,
                                    FiveHourPace: null, SevenDayPace: null,
                                    Models: new Dictionary<string, double> { ["scoped.Fable"] = 7, ["seven_day_sonnet"] = 21 },
                                    ModelPaces: new Dictionary<string, double> { ["scoped.Fable"] = 0.5 });
        Assert.Equal(7, dp.Utilization("scoped.Fable"));
        Assert.Equal(21, dp.Utilization("seven_day_sonnet"));
        Assert.Equal(0.5, dp.PaceRate("scoped.Fable"));
        // A model with no pace bucket yet must read as "no sample", not zero.
        Assert.Null(dp.PaceRate("seven_day_sonnet"));
        Assert.Null(dp.Utilization("scoped.Nonexistent"));
    }

    [Fact]
    public void DataPointDecodesHistoryStoredBeforeModelChartsExisted()
    {
        // A literal payload without the model dictionaries — a round-trip of the current record
        // could not catch a regression here, since the encoder writes whatever we decode.
        // Windows never wrote the Mac's pre-`models` format, but it stores the same shape for
        // every account without model-scoped limits. The payload is in the history file's own
        // encoding (DIVERGENCES 7: short names, Unix seconds — the Mac literal's 774000000
        // seconds since 2001 is this instant), and is read the way the app reads it.
        const string json = """
            [{"t": 1752307200, "h5": 40, "d7": 55, "h5p": 3, "d7p": 1}]
            """;
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            // A payload that fails to decode fails the test with the store's own message.
            var store = new AccountStore(directory, logError: error => Assert.Fail(error));
            var id = Guid.NewGuid();
            File.WriteAllText(store.HistoryPath(id), json);
            var dp = store.LoadHistory(id)[0];
            Assert.Equal(40, dp.FiveHour);
            Assert.Equal(55, dp.SevenDay);
            Assert.Null(dp.Models);
            Assert.Null(dp.ModelPaces);
            Assert.Null(dp.Utilization("scoped.Fable"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: - Chart content filter persistence

    [Fact]
    public void HiddenKeysRoundTripAndSortStably()
    {
        var keys = new HashSet<string> { "seven_day", "scoped.Fable" };
        var raw = Charts.EncodeHiddenKeys(keys);
        Assert.Equal("scoped.Fable\nseven_day", raw);
        Assert.Equal(keys, Charts.DecodeHiddenKeys(raw));
        // Same set, different construction order — the stored string must not churn.
        Assert.Equal(raw, Charts.EncodeHiddenKeys(new HashSet<string> { "scoped.Fable", "seven_day" }));
    }

    [Fact]
    public void HiddenKeysEmptyStringDecodesToEmptySet()
    {
        Assert.Empty(Charts.DecodeHiddenKeys(""));
        Assert.Equal("", Charts.EncodeHiddenKeys(new HashSet<string>()));
    }

    [Fact]
    public void HiddenKeysPreserveModelNamesContainingSpacesAndCommas()
    {
        // Keys embed API-supplied display names; a comma separator would split them.
        var keys = new HashSet<string> { "scoped.Claude 3.5, Sonnet" };
        Assert.Equal(keys, Charts.DecodeHiddenKeys(Charts.EncodeHiddenKeys(keys)));
    }

    // MARK: - Pace alert state machine

    [Fact]
    public void PaceAlertFiresOncePerConcerningEpisode()
    {
        var first = PaceMath.AlertStep(watched: true, warned: false, projectedMinutes: 20, threshold: 30);
        Assert.Equal(new PaceAlertStep(Fire: true, Dismiss: false, Warned: true), first);
        var second = PaceMath.AlertStep(watched: true, warned: true, projectedMinutes: 15, threshold: 30);
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: false, Warned: true), second);
    }

    [Fact]
    public void PaceAlertDismissesButStaysWarnedInsideTheHysteresisBand()
    {
        // 35 min clears the 30-min threshold but not 1.25× (37.5): re-arming here would
        // re-fire toast and sound every few polls as the regression jitters.
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: true, Warned: true),
                     PaceMath.AlertStep(watched: true, warned: true, projectedMinutes: 35, threshold: 30));
    }

    [Fact]
    public void PaceAlertRearmsPastTheHysteresisBandOrWhenPaceVanishes()
    {
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: true, Warned: false),
                     PaceMath.AlertStep(watched: true, warned: true, projectedMinutes: 40, threshold: 30));
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: true, Warned: false),
                     PaceMath.AlertStep(watched: true, warned: true, projectedMinutes: null, threshold: 30));
    }

    [Fact]
    public void PaceAlertClearsAnUnwatchedWindow()
    {
        // Also how a window that left the response is cleared, so it can warn if it returns.
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: true, Warned: false),
                     PaceMath.AlertStep(watched: false, warned: true, projectedMinutes: 10, threshold: 30));
    }

    [Fact]
    public void PaceAlertStaysQuietBelowTheThreshold()
    {
        Assert.Equal(new PaceAlertStep(Fire: false, Dismiss: false, Warned: false),
                     PaceMath.AlertStep(watched: true, warned: false, projectedMinutes: 35, threshold: 30));
    }

    // MARK: - PaceBand

    [Fact]
    public void PaceBandSafeWhenProjectionBeyondReset()
    {
        Assert.Equal(PaceBand.Safe, PaceMath.Band(projectedHours: 5, hoursToReset: 4));
        Assert.Equal(PaceBand.Safe, PaceMath.Band(projectedHours: 4, hoursToReset: 4));
    }

    [Fact]
    public void PaceBandCloseWithinEightyPercentOfReset()
    {
        Assert.Equal(PaceBand.Close, PaceMath.Band(projectedHours: 3.5, hoursToReset: 4));
        Assert.Equal(PaceBand.Close, PaceMath.Band(projectedHours: 3.2, hoursToReset: 4));
    }

    [Fact]
    public void PaceBandOverWhenFillingWellBeforeReset()
    {
        Assert.Equal(PaceBand.Over, PaceMath.Band(projectedHours: 2, hoursToReset: 4));
    }

    [Fact]
    public void PaceBandSafeOnInvalidInputs()
    {
        Assert.Equal(PaceBand.Safe, PaceMath.Band(projectedHours: 0, hoursToReset: 4));
        Assert.Equal(PaceBand.Safe, PaceMath.Band(projectedHours: -1, hoursToReset: 4));
        Assert.Equal(PaceBand.Safe, PaceMath.Band(projectedHours: 2, hoursToReset: 0));
    }

    // MARK: - Urgency colors

    [Fact]
    public void UrgencyColorClampsOutOfRangeInputs()
    {
        Assert.Equal(Urgency.Color(0), Urgency.Color(-1));
        Assert.Equal(Urgency.Color(1), Urgency.Color(2));
    }

    [Fact]
    public void PaceUrgencyColorBands()
    {
        // The C# pace helpers return the urgency number and the view picks the color: 0 is the
        // Mac's neutral `.secondary`, 0.7 and 1.0 are Urgency.Color(0.7) and Urgency.Color(1.0).
        // safe → neutral
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 5, hoursToReset: 4));
        // close (≥ 0.8×) → Urgency.Color(0.7)
        Assert.Equal(0.7, PaceMath.PaceUrgency(projectedHours: 3.5, hoursToReset: 4));
        // over → Urgency.Color(1.0)
        Assert.Equal(1.0, PaceMath.PaceUrgency(projectedHours: 2, hoursToReset: 4));
        // invalid inputs → neutral
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 0, hoursToReset: 4));
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 2, hoursToReset: 0));
    }

    [Fact]
    public void PaceUrgencyMatchesPaceBands()
    {
        // The tray icon and the popover rows must agree on pace state: a projection that
        // lands after the reset is "safe" everywhere, so its urgency must be 0, not a
        // continuous ratio that reads as near-critical at 1.2× the time to reset.
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 4.8, hoursToReset: 4));
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 4, hoursToReset: 4));
        Assert.Equal(0.7, PaceMath.PaceUrgency(projectedHours: 3.5, hoursToReset: 4));
        Assert.Equal(1.0, PaceMath.PaceUrgency(projectedHours: 2, hoursToReset: 4));
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 0, hoursToReset: 4));
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 2, hoursToReset: 0));
        // The band colors derive from the same urgency values.
        Assert.Equal(Urgency.Color(0.7), Urgency.Color(PaceMath.PaceUrgency(projectedHours: 3.5, hoursToReset: 4)));
    }

    [Fact]
    public void PaceAccentColorSharedByRowAndCharts()
    {
        var now = T.Now;
        var reset = now.AddSeconds(4 * 3600);
        Assert.Equal(1.0, PaceMath.AccentUrgency(projectedHours: 2, resetsAt: reset, isStale: false, now: now));
        Assert.Equal(0, PaceMath.AccentUrgency(projectedHours: 5, resetsAt: reset, isStale: false, now: now));
        // No projection, no reset date, or a stale window → neutral, like the row.
        Assert.Equal(0, PaceMath.AccentUrgency(projectedHours: null, resetsAt: reset, isStale: false, now: now));
        Assert.Equal(0, PaceMath.AccentUrgency(projectedHours: 2, resetsAt: null, isStale: false, now: now));
        Assert.Equal(0, PaceMath.AccentUrgency(projectedHours: 2, resetsAt: reset, isStale: true, now: now));
    }

    [Fact]
    public void ContrastRatioSpansBlackOnWhite()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(1, 1, 1);
        Assert.Equal(21, Urgency.ContrastRatio(black, white), tolerance: 0.01);
        Assert.Equal(1, Urgency.ContrastRatio(white, white), tolerance: 0.01);
    }

    [Fact]
    public void UrgencyTextColorIsLegibleOnBothPopoverBackgrounds()
    {
        // The raw gradient is 1.2–2.4:1 on the light popover from green to orange, and
        // red is ~3:1 on the dark one; text needs WCAG AA (4.5:1) across the whole range.
        for (var i = 0; i <= 20; i++)
        {
            var t = i / 20.0;
            foreach (var isDark in new[] { false, true })
            {
                var ratio = Urgency.ContrastRatio(Urgency.TextColor(t, isDark), Urgency.PopoverBackground(isDark));
                Assert.True(ratio >= 4.5, $"t={t} dark={isDark}");
            }
        }
    }

    /// <summary>HSB hue in 0…1 (0 for a gray) — what the Swift test reads from NSColor's hueComponent.</summary>
    private static double Hue(Rgb color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var delta = max - Math.Min(color.R, Math.Min(color.G, color.B));
        if (delta <= 0) return 0;
        double sixths;
        if (max == color.R) sixths = (color.G - color.B) / delta;
        else if (max == color.G) sixths = 2 + (color.B - color.R) / delta;
        else sixths = 4 + (color.R - color.G) / delta;
        return (sixths < 0 ? sixths + 6 : sixths) / 6;
    }

    [Fact]
    public void UrgencyTextColorKeepsTheGradientHue()
    {
        foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var baseColor = Urgency.Color(t);
            foreach (var isDark in new[] { false, true })
            {
                var text = Urgency.TextColor(t, isDark);
                Assert.Equal(Hue(baseColor), Hue(text), tolerance: 0.01);
            }
        }
    }

    [Fact]
    public void PaceTextColorsUseTheLegibleVariant()
    {
        // The Mac's pace helpers take the color scheme and return the text color themselves.
        // Here they return the urgency number, so the legible variant is Urgency.TextColor
        // over that number — the composition the view makes.
        var now = T.Now;
        var reset = now.AddSeconds(4 * 3600);
        Assert.Equal(Urgency.TextColor(0.7, isDark: false),
                     Urgency.TextColor(PaceMath.PaceUrgency(projectedHours: 3.5, hoursToReset: 4), isDark: false));
        Assert.Equal(Urgency.TextColor(1.0, isDark: true),
                     Urgency.TextColor(PaceMath.AccentUrgency(projectedHours: 2, resetsAt: reset, isStale: false, now: now), isDark: true));
        // A safe pace stays neutral as text too.
        Assert.Equal(0, PaceMath.PaceUrgency(projectedHours: 5, hoursToReset: 4));
    }

    // MARK: - Window reset detection (backward jump)

    [Fact]
    public void IsWindowResetFalseOnBackwardTimestampJump()
    {
        var prev = T.Now;
        Assert.False(Resets.IsWindowReset(previous: prev, next: prev.AddSeconds(-5 * 3600), utilization: 2));
    }

    // MARK: - detectResets

    private static TrackedWindow Tracked(string key, double utilization, DateTimeOffset? resetsAt)
    {
        var iso = resetsAt is { } date ? T.Iso(date) : null;
        return new TrackedWindow(key, Title: key, new UsageWindow(utilization, iso), IsModelScoped: false);
    }

    /// <summary>The kept reset time for a window key, or null — Swift's optional dictionary subscript.</summary>
    private static DateTimeOffset? StoredReset(Dictionary<string, DateTimeOffset> stored, string key) =>
        stored.TryGetValue(key, out var date) ? date : null;

    [Fact]
    public void DetectResetsFiresWhenResetsAtGoesNullAfterStoredResetPassed()
    {
        var now = T.Now;
        var stored = new Dictionary<string, DateTimeOffset> { ["five_hour"] = now.AddSeconds(-30) };
        var result = Resets.DetectResets(stored, [Tracked("five_hour", 0, null)], now);
        Assert.Equal(["five_hour"], result.Reset);
        // Dropped, so the next window's first date reads as a baseline, not a second reset.
        Assert.Null(StoredReset(result.Stored, "five_hour"));
    }

    [Fact]
    public void DetectResetsWaitsWhileNullResetsAtPrecedesStoredReset()
    {
        var now = T.Now;
        var reset = now.AddSeconds(600);
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["five_hour"] = reset },
                                         [Tracked("five_hour", 0, null)], now);
        Assert.Empty(result.Reset);
        Assert.Equal(reset, StoredReset(result.Stored, "five_hour"));
    }

    [Fact]
    public void DetectResetsIgnoresNullResetsAtWhileUtilizationIsHigh()
    {
        var now = T.Now;
        var reset = now.AddSeconds(-30);
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["five_hour"] = reset },
                                         [Tracked("five_hour", 40, null)], now);
        Assert.Empty(result.Reset);
        Assert.Equal(reset, StoredReset(result.Stored, "five_hour"));
    }

    [Fact]
    public void DetectResetsBaselinesTheNextWindowAfterANullReset()
    {
        var now = T.Now;
        var first = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["five_hour"] = now.AddSeconds(-30) },
                                        [Tracked("five_hour", 0, null)], now);
        var nextReset = now.AddSeconds(5 * 3600);
        var second = Resets.DetectResets(first.Stored, [Tracked("five_hour", 1, nextReset)], now);
        Assert.Empty(second.Reset);
        Assert.Equal((nextReset - DateTimeOffset.UnixEpoch).TotalSeconds,
                     (StoredReset(second.Stored, "five_hour") - DateTimeOffset.UnixEpoch)?.TotalSeconds ?? 0, tolerance: 1);
    }

    [Fact]
    public void DetectResetsFiresWhenAWindowVanishesAfterItsReset()
    {
        // Team orgs drop an idle `five_hour` from the response altogether.
        var now = T.Now;
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["five_hour"] = now.AddSeconds(-30) }, [], now);
        Assert.Equal(["five_hour"], result.Reset);
        Assert.Null(StoredReset(result.Stored, "five_hour"));
    }

    [Fact]
    public void DetectResetsKeepsAVanishedWindowUntilItsResetPasses()
    {
        var now = T.Now;
        var reset = now.AddSeconds(600);
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["five_hour"] = reset }, [], now);
        Assert.Empty(result.Reset);
        Assert.Equal(reset, StoredReset(result.Stored, "five_hour"));
    }

    [Fact]
    public void DetectResetsKeepsTheForwardJumpRule()
    {
        var now = T.Now;
        var old = now.AddSeconds(-30);
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset> { ["seven_day"] = old },
                                         [Tracked("seven_day", 1, now.AddSeconds(7 * 86400))], now);
        Assert.Equal(["seven_day"], result.Reset);
        Assert.NotNull(StoredReset(result.Stored, "seven_day"));
    }

    [Fact]
    public void VanishedWindowIdentityMatchesTheLiveWindow()
    {
        // A window can leave the response a poll or more before its reset passes, so its
        // reset toast can't rely on the previous response for a title.
        var fiveHour = TrackedWindow.Vanished("five_hour");
        Assert.Equal(MenuBarWindow.FiveHour.Label(), fiveHour.Title);
        Assert.False(fiveHour.IsModelScoped);
        var fable = TrackedWindow.Vanished("scoped.Fable");
        Assert.Equal(L.F("7-Day %@", "Fable"), fable.Title);
        Assert.True(fable.IsModelScoped);
        Assert.Equal(L.T("7-Day Sonnet"), TrackedWindow.Vanished("seven_day_sonnet").Title);
    }

    [Fact]
    public void DetectResetsBaselinesWindowsWithoutAStoredReset()
    {
        var now = T.Now;
        var result = Resets.DetectResets(new Dictionary<string, DateTimeOffset>(),
                                         [Tracked("five_hour", 0, now.AddSeconds(3600)), Tracked("seven_day", 0, null)], now);
        Assert.Empty(result.Reset);
        Assert.NotNull(StoredReset(result.Stored, "five_hour"));
        Assert.Null(StoredReset(result.Stored, "seven_day"));
    }

    // MARK: - PaceRateUnit formatting

    /// <summary>
    /// Pace chart y-axis labels carry no unit (the stats row above states it), so they are
    /// as narrow as the utilization charts' and the stacked plots line up.
    /// </summary>
    [Fact]
    public void PaceAxisLabelIsTheCompactConvertedNumber()
    {
        Assert.Equal("26", PaceRateUnit.PerHour.AxisLabel(26));
        Assert.Equal("0.42", PaceRateUnit.PerMinute.AxisLabel(25.02));
        Assert.Equal("0.0072", PaceRateUnit.PerSecond.AxisLabel(26));
        Assert.Equal("0", PaceRateUnit.PerMinute.AxisLabel(0));
    }

    [Fact]
    public void PerHourFormatUsesOneDecimalBelowTen()
    {
        Assert.Equal("9.5%/hr", PaceRateUnit.PerHour.Format(9.5));
    }

    [Fact]
    public void PerHourFormatRoundsToIntegerAtTenOrMore()
    {
        Assert.Equal("45%/hr", PaceRateUnit.PerHour.Format(45.4));
    }

    [Fact]
    public void PerHourFormatShortWithPrefix()
    {
        Assert.Equal("+45%/h", PaceRateUnit.PerHour.Format(45, prefix: true, @short: true));
    }

    [Fact]
    public void PerMinuteFormatBelowOne()
    {
        // 30 %/hr = 0.5 %/min → three decimals
        Assert.Equal("0.500%/min", PaceRateUnit.PerMinute.Format(30));
    }

    [Fact]
    public void PerMinuteFormatAtOneOrMore()
    {
        // 90 %/hr = 1.5 %/min → two decimals
        Assert.Equal("1.50%/min", PaceRateUnit.PerMinute.Format(90));
    }

    [Fact]
    public void PerSecondFormat()
    {
        // 36 %/hr = 0.01 %/s
        Assert.Equal("0.0100%/s", PaceRateUnit.PerSecond.Format(36));
    }

    // MARK: - Pace outlook wording

    /// <summary>
    /// resets_at lands a fraction of a second either side of its 10-minute mark from poll to
    /// poll; the outlook sentence used to switch phrasing between polls because of it.
    /// </summary>
    [Fact]
    public void OutlookMessageIndexIsStableAcrossResetJitter()
    {
        var mark = DateTimeOffset.FromUnixTimeSeconds(1_790_197_200); // 2026-09-23 21:00 UTC, a 10-minute mark
        Assert.Equal(PaceMath.OutlookMessageIndex(reset: mark.AddSeconds(0.108), count: 5),
                     PaceMath.OutlookMessageIndex(reset: mark.AddSeconds(-0.454), count: 5));
        // …while still varying between windows (seconds-based picks were always index 0).
        Assert.NotEqual(PaceMath.OutlookMessageIndex(reset: mark.AddSeconds(600), count: 5),
                        PaceMath.OutlookMessageIndex(reset: mark, count: 5));
    }

    /// <summary>
    /// The "over" line must never claim more lead than there is: a 1.53 h lead rounded to
    /// "~2h" sat directly under "Resets in 1 hr, 34 min".
    /// </summary>
    [Fact]
    public void EarlyFillLeadNeverOverstatesTheLead()
    {
        var lead = PaceMath.EarlyFillLead(hoursEarly: 1.53);
        Assert.Equal(1, lead.Hours);
        Assert.Equal(31, lead.Minutes);
        Assert.Equal(2, PaceMath.EarlyFillLead(hoursEarly: 2).Hours);
        Assert.Equal(0, PaceMath.EarlyFillLead(hoursEarly: 2).Minutes);
        Assert.Equal(1, PaceMath.EarlyFillLead(hoursEarly: 0.001).Minutes); // never "~0m"
    }

    // MARK: - Orphaned data stores

    /// <summary>
    /// The launch sweep deletes browser profiles whose account is gone (the app died between
    /// removing an account and deleting its profile, a cancelled add) and must never touch one
    /// the roster owns — including a pending placeholder's, whose sign-in may still be in
    /// progress. The Mac sweeps WebKit data stores by UUID; here they are WebView2 profile names.
    /// </summary>
    [Fact]
    public void OrphanedDataStoreIDsKeepsEveryStoreTheRosterOwns()
    {
        var owned = new Account { Label = "Work" };
        var pending = new Account { Label = "Claude account", Pending = true };
        var orphan = Accounts.ProfilePrefix + Guid.NewGuid().ToString("N");
        string[] existing = [owned.ProfileName, orphan, pending.ProfileName];
        Assert.Equal([orphan], Accounts.OrphanedProfiles(existing, roster: [owned, pending]));
        Assert.Empty(Accounts.OrphanedProfiles([], roster: [owned]));
    }

    // MARK: - FetchFailure

    /// <summary>
    /// A Cloudflare-challenged fetch must never read as an auth failure: two of them in a
    /// row would mark a valid session expired. The tokens arrive as the thrown error's
    /// message, so classification is by substring.
    /// </summary>
    [Fact]
    public void FetchFailureClassifiesTheThrownToken()
    {
        Assert.Equal(FetchFailure.Challenge, FetchFailures.Classify("Error: CF_CHALLENGE"));
        Assert.Equal(FetchFailure.Unauthorized, FetchFailures.Classify("Error: HTTP_401"));
        Assert.Equal(FetchFailure.Unauthorized, FetchFailures.Classify("Error: HTTP_403"));
        Assert.Equal(FetchFailure.RateLimited, FetchFailures.Classify("Error: HTTP_429"));
        Assert.Equal(FetchFailure.NotFound, FetchFailures.Classify("Error: HTTP_404"));
        Assert.Equal(FetchFailure.Http, FetchFailures.Classify("Error: HTTP_500"));
        Assert.Equal(FetchFailure.Network, FetchFailures.Classify("TypeError: Load failed"));
        // A message wrapped in other text still classifies (the description fallback).
        Assert.Equal(FetchFailure.Unauthorized, FetchFailures.Classify("A JavaScript exception occurred: Error: HTTP_401"));
    }

    // MARK: - resetTimeText

    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// ICU inserts a narrow no-break space before AM/PM on recent OSes; normalize for comparison,
    /// as the Swift tests do.
    /// </summary>
    private static string NormalizedTime(string s) => s.Replace(' ', ' ').Replace(' ', ' ');

    [Fact]
    public void ResetTimeTextSameDay12Hour()
    {
        var now = T.Utc(2026, 7, 1, 10, 0);
        var reset = T.Utc(2026, 7, 1, 17, 30);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: false, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("5:30 PM", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextSameDay24Hour()
    {
        var now = T.Utc(2026, 7, 1, 10, 0);
        var reset = T.Utc(2026, 7, 1, 17, 30);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("17:30", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextNextDayAddsWeekday()
    {
        // 2026-07-01 is a Wednesday; reset lands Thursday 2026-07-02.
        var now = T.Utc(2026, 7, 1, 23, 0);
        var reset = T.Utc(2026, 7, 2, 17, 30);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: false, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("Thu 5:30 PM", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextSixDaysOutAddsWeekday24Hour()
    {
        // Reset lands Tuesday 2026-07-07.
        var now = T.Utc(2026, 7, 1, 10, 0);
        var reset = T.Utc(2026, 7, 7, 9, 5);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        // The hour is zero-padded in 24-hour mode ("09:05", digital-clock style), as ICU does on the Mac.
        Assert.Equal("Tue 09:05", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextWithDateAddsMonthDay24Hour()
    {
        // 7-day-style horizon: reset lands Tuesday 2026-07-07.
        var now = T.Utc(2026, 7, 1, 10, 0);
        var reset = T.Utc(2026, 7, 7, 9, 5);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: true, includeDate: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        // A full date and time are joined with the locale's connector ("at" in English), as ICU does on the Mac.
        Assert.Equal("Tue, Jul 7 at 09:05", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextWithDateAddsMonthDay12Hour()
    {
        // Reset lands Thursday 2026-07-02, the day after `now`.
        var now = T.Utc(2026, 7, 1, 23, 0);
        var reset = T.Utc(2026, 7, 2, 17, 30);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: false, includeDate: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("Thu, Jul 2 at 5:30 PM", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextWithDateSameDayShowsTimeOnly()
    {
        // A 7-day window resetting today: the countdown already covers it — no date.
        var now = T.Utc(2026, 7, 1, 10, 0);
        var reset = T.Utc(2026, 7, 1, 17, 30);
        var text = TimeText.ResetTimeText(reset, now, use24Hour: true, includeDate: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("17:30", NormalizedTime(text));
    }

    [Fact]
    public void ResetTimeTextRoundsJitteredResetToNearestMinute()
    {
        var now = T.Utc(2026, 7, 1, 10, 0);
        var boundary = T.Utc(2026, 7, 1, 18, 0);
        // Live resets_at lands a fraction of a second either side of the boundary from poll to
        // poll (20:59:59.546 vs 21:00:00.108 observed 2026-09-23); both must read as the same minute.
        var early = TimeText.ResetTimeText(boundary.AddSeconds(-0.454), now, use24Hour: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        var late = TimeText.ResetTimeText(boundary.AddSeconds(0.108), now, use24Hour: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("18:00", NormalizedTime(early));
        Assert.Equal("18:00", NormalizedTime(late));
    }

    [Fact]
    public void ResetTimeTextRoundsBeforeTheSameDayCheck()
    {
        // A reset jittered to just before midnight is tomorrow's 00:00 — it needs the weekday.
        var now = T.Utc(2026, 7, 1, 10, 0);
        var midnight = T.Utc(2026, 7, 2, 0, 0);
        var text = TimeText.ResetTimeText(midnight.AddSeconds(-0.4), now, use24Hour: true, timeZone: TimeZoneInfo.Utc, culture: EnUs);
        Assert.Equal("Thu 00:00", NormalizedTime(text));
    }

    [Fact]
    public void Prefers24HourClockByLocale()
    {
        Assert.False(TimeText.Prefers24HourClock(CultureInfo.GetCultureInfo("en-US")));  // h12
        Assert.True(TimeText.Prefers24HourClock(CultureInfo.GetCultureInfo("de-DE")));   // h23
        Assert.True(TimeText.Prefers24HourClock(CultureInfo.GetCultureInfo("fr-FR")));   // h23
    }
}
