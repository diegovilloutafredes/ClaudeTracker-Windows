using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// Rules that exist only on Windows, or that Windows states more strictly than the Mac app, so
/// they have no Swift test to be a port of. Each says why it exists.
/// </summary>
public class WindowsOnlyTests
{
    // MARK: - Roster file

    /// <summary>
    /// A saved row without its profile id must fail the load, not get a fresh random one: a new
    /// id on every launch would detach the account from its real session, and the launch sweep
    /// would then see that session's profile as unowned.
    /// </summary>
    [Theory]
    [InlineData("""[{"label":"X","profileId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","addedAt":"2026-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[{"id":"11111111-2222-3333-4444-555555555555","profileId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","addedAt":"2026-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[{"id":"11111111-2222-3333-4444-555555555555","label":"X","addedAt":"2026-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[{"id":"11111111-2222-3333-4444-555555555555","label":"X","profileId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}]""")]
    [InlineData("[null]")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ARosterRowMissingARequiredFieldIsPreservedNotRepaired(string json)
    {
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var store = new AccountStore(directory);
            File.WriteAllText(store.AccountsPath, json);
            Assert.Empty(store.LoadAccounts());
            Assert.True(store.HasCorruptRoster);
            Assert.Equal(json, File.ReadAllText(store.CorruptAccountsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnAccountSurvivesTheRosterFileExactly()
    {
        // AddedAt defaults to the clock at full resolution; the file must not round it.
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var account = new Account { Label = "One", AddedAt = new DateTimeOffset(2026, 10, 5, 10, 18, 43, TimeSpan.Zero).AddTicks(9892488) };
            var store = new AccountStore(directory);
            store.SaveAccounts([account]);
            Assert.Equal(account, Assert.Single(store.LoadAccounts()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A roster that cannot be READ (locked by a backup or sync tool) is not an empty roster.
    /// It loads as empty, but the file must survive: a first version copied nothing aside
    /// (the copy hit the same lock) and the next save wrote "[]" over every account.
    /// </summary>
    [Fact]
    public void ALockedRosterIsNeverOverwritten()
    {
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var store = new AccountStore(directory);
            store.SaveAccounts([new Account { Label = "Kept" }]);
            var saved = File.ReadAllText(store.AccountsPath);

            using (new FileStream(store.AccountsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(store.LoadAccounts());
            }
            Assert.True(store.RosterIsUnreadable);
            Assert.False(store.HasCorruptRoster);
            store.SaveAccounts([]);
            Assert.Equal(saved, File.ReadAllText(store.AccountsPath));

            // Readable again: it loads, and saving works again.
            Assert.Equal("Kept", Assert.Single(store.LoadAccounts()).Label);
            Assert.False(store.RosterIsUnreadable);
            store.SaveAccounts([]);
            Assert.Empty(store.LoadAccounts());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LockedSettingsAreNeverOverwritten()
    {
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "settings.json");
            new SettingsStore(path).Set(PrefKey.PopupScale, 1.25);
            var saved = File.ReadAllText(path);

            SettingsStore locked;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                locked = new SettingsStore(path);
            }
            Assert.True(locked.IsReadOnly);
            Assert.Null(locked.GetDouble(PrefKey.PopupScale));
            locked.Set(PrefKey.ShowPace, false);
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.Equal(1.25, new SettingsStore(path).GetDouble(PrefKey.PopupScale));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptSettingsArePreservedEvenWithNoLoggerAttached()
    {
        // The copy once sat inside the log call, so it never ran when nothing was listening.
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ not json");
            var settings = new SettingsStore(path);
            Assert.False(settings.IsReadOnly);
            Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
            settings.Set(PrefKey.ShowPace, true);
            Assert.True(new SettingsStore(path).GetBool(PrefKey.ShowPace));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: - Chart history file

    [Fact]
    public void AHistoryRowWithoutItsTimestampIsPreservedNotLoaded()
    {
        // Without this a row with no "t" loaded as a sample dated year 1.
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var store = new AccountStore(directory);
            var id = Guid.NewGuid();
            File.WriteAllText(store.HistoryPath(id), """[{"h5":40,"d7":55}]""");
            Assert.Empty(store.LoadHistory(id));
            Assert.True(File.Exists(store.HistoryPath(id) + ".corrupt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AChartSampleSurvivesItsFileWhenItsTimeIsASampleTime()
    {
        // The history file keeps milliseconds. A point stamped with Charts.SampleTime reads
        // back equal; one stamped with the raw clock would not.
        var directory = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;
        try
        {
            var raw = new DateTimeOffset(2026, 10, 5, 10, 18, 43, TimeSpan.Zero).AddTicks(1234567);
            var point = new UsageDataPoint(Charts.SampleTime(raw), 12.5, 3, 4.25, null,
                                           new Dictionary<string, double> { ["scoped.Fable"] = 7 });
            var store = new AccountStore(directory);
            var id = Guid.NewGuid();
            store.SaveHistory(id, [point]);
            var loaded = Assert.Single(store.LoadHistory(id));
            Assert.Equal(point.Timestamp, loaded.Timestamp);
            Assert.Equal(TimeSpan.FromTicks(4567), raw - loaded.Timestamp);
            Assert.Equal(12.5, loaded.FiveHour);
            Assert.Equal(7, loaded.Utilization("scoped.Fable"));
            Assert.Null(loaded.ModelPaces);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: - Rate text

    /// <summary>
    /// The Mac app formats rates with C's <c>%.1f</c>, which rounds the exact binary value. The
    /// Mac suite's vectors never land near a tie; these do. 0.15 is really 0.1499…, so it must
    /// print "0.1" — a first version here rounded it as a tie and printed "0.2".
    /// </summary>
    [Theory]
    [InlineData(0.05, "0.1%/hr")]
    [InlineData(0.15, "0.1%/hr")]
    [InlineData(0.25, "0.2%/hr")]
    [InlineData(0.35, "0.3%/hr")]
    [InlineData(0.45, "0.5%/hr")]
    [InlineData(0.65, "0.7%/hr")]
    [InlineData(0.95, "0.9%/hr")]
    [InlineData(1.05, "1.1%/hr")]
    [InlineData(1.15, "1.1%/hr")]
    [InlineData(9.95, "9.9%/hr")]
    public void PerHourRatesRoundLikePrintf(double rate, string expected) =>
        Assert.Equal(expected, PaceRateUnit.PerHour.Format(rate));

    /// <summary>
    /// Axis labels round to two significant digits the way the Mac's number formatter does: on
    /// the decimal number as written, half to even. The Mac suite's vectors avoid ties.
    /// </summary>
    [Theory]
    [InlineData(1.15, "1.2")]
    [InlineData(1.25, "1.2")]
    [InlineData(9.95, "10")]
    [InlineData(0.417, "0.42")]
    [InlineData(12.3, "12")]
    [InlineData(155.0, "160")]
    [InlineData(0.0072, "0.0072")]
    [InlineData(0.0, "0")]
    public void AxisLabelsRoundTheDecimalNumberHalfToEven(double ratePerHour, string expected) =>
        Assert.Equal(expected, PaceRateUnit.PerHour.AxisLabel(ratePerHour));

    // MARK: - Release tags

    [Fact]
    public void ParseReleasesFindsAPlainVersionBeneathATagThatIsNotOne()
    {
        // A newest tag that is not a version must not hide the real release under it.
        var json = """
        [
          {"tag_name": "windows-v9.9.9", "html_url": "https://github.com/x/y/releases/tag/windows-v9.9.9",
           "published_at": "2026-06-02T00:00:00Z", "assets": []},
          {"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
           "published_at": "2026-06-01T00:00:00Z", "assets": []}
        ]
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("9.0.0", update?.Version);
        Assert.Equal(2, dates.Count);
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.1", "1.2", true)]
    [InlineData("2.0", "10.0", false)]
    public void VersionsCompareByNumberNotByText(string remote, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewerVersion(remote, current));

    // MARK: - Network failure text

    [Theory]
    // Chromium's wording for "the request never left" and for the 30 s abort. The Mac app
    // shows WebKit's own text for these; Chromium's is not fit to show.
    [InlineData("TypeError: Failed to fetch", "Couldn't reach claude.ai")]
    [InlineData("Uncaught (in promise) TypeError: Failed to fetch", "Couldn't reach claude.ai")]
    [InlineData("TimeoutError: signal timed out", "claude.ai took too long to answer")]
    // Anything unexpected stays as thrown: the raw text is what makes it diagnosable.
    [InlineData("SyntaxError: Unexpected token '<', \"<!DOCTYPE \"... is not valid JSON", "SyntaxError: Unexpected token '<', \"<!DOCTYPE \"... is not valid JSON")]
    [InlineData("script failed", "script failed")]
    public void NetworkFailuresGetPlainWordsOnlyWhereChromiumsAreCryptic(string thrown, string shown)
    {
        Assert.Equal(FetchFailure.Network, FetchFailures.Classify(thrown));
        Assert.Equal(shown, FetchFailures.NetworkDetail(thrown));
    }

    [Theory]
    [InlineData("Couldn't reach claude.ai")]
    [InlineData("claude.ai took too long to answer")]
    public void NetworkFailureTextIsTranslated(string key) => Assert.NotEqual(key, L.Lookup("es", key));

    // MARK: - Pace lines
    //
    // The Mac app words these inside its row view, where no test reaches them. The values
    // asserted here are what that view produces for the same inputs.

    [Theory]
    [InlineData(8.4, 2.17, "+8.4%/hr · full in 2h 10m")]
    [InlineData(8.4, 2.999, "+8.4%/hr · full in 2h 59m")]  // minutes are cut, never rounded up
    [InlineData(8.4, 2.0, "+8.4%/hr · full in 2h")]
    [InlineData(8.4, 0.75, "+8.4%/hr · full in 45m")]
    [InlineData(8.4, 0.005, "+8.4%/hr · full in 1m")]      // never "full in 0m"
    [InlineData(8.4, 23.99, "+8.4%/hr · full in 23h 59m")]
    [InlineData(8.4, 24.0, "+8.4%/hr")]                    // a day or more away says nothing
    [InlineData(12.6, 30.0, "+13%/hr")]
    public void ThePaceLineSaysWhenTheWindowFillsOnlyWithinADay(double rate, double projectedHours, string expected) =>
        Assert.Equal(expected, PaceText.Line(rate, projectedHours, PaceRateUnit.PerHour));

    [Fact]
    public void ThePaceLineIsTheRateAloneWithoutAProjection()
    {
        Assert.Equal("+8.4%/hr", PaceText.Line(8.4, null, PaceRateUnit.PerHour));
        Assert.Equal("+0.500%/min · full in 45m", PaceText.Line(30, 0.75, PaceRateUnit.PerMinute));
    }

    /// <summary>A reset time whose outlook phrase is number <paramref name="slot"/> in a list of that many.</summary>
    private static DateTimeOffset ResetInSlot(int slot, int count) =>
        DateTimeOffset.UnixEpoch.AddSeconds(600.0 * (3_000_000L * count + slot));

    [Theory]
    [InlineData(0, "On track — resets before limit")]
    [InlineData(1, "You're good — resets in time")]
    [InlineData(4, "No rush — plenty of time left")]
    public void ASafePaceReadsTheSamePhraseForAWholeCycle(int slot, string expected)
    {
        var reset = ResetInSlot(slot, 5);
        // Fills in 10 h, resets in 5 h — and again half an hour later, with the reset a second early.
        Assert.Equal((PaceBand.Safe, expected), PaceText.Outlook(10, reset, reset.AddHours(-5)));
        Assert.Equal((PaceBand.Safe, expected), PaceText.Outlook(10, reset.AddSeconds(-1), reset.AddHours(-4.5)));
    }

    [Fact]
    public void AClosePaceWarnsWithoutATime()
    {
        var reset = ResetInSlot(2, 5);
        // 4.5 h to fill against 5 h to the reset: inside the last fifth.
        Assert.Equal((PaceBand.Close, "Caution — cutting it close"), PaceText.Outlook(4.5, reset, reset.AddHours(-5)));
    }

    [Theory]
    // hours to the reset, hours to full, slot, expected
    [InlineData(5.0, 2.0, 0, "Will hit limit ~3h before reset")]      // three hours or more: no minutes
    [InlineData(5.0, 3.5, 1, "Runs out ~1h 30m before reset")]
    [InlineData(5.0, 3.0, 2, "On pace to fill ~2h early")]            // a whole number of hours
    [InlineData(1.0, 0.5, 3, "Full ~30m before window resets")]
    [InlineData(5.0, 0.25, 0, "Will hit limit ~4h before reset")]     // 4 h 45 m is never rounded up to 5
    public void AnOverPaceSaysHowEarlyTheWindowRunsOut(double hoursToReset, double hoursToFull, int slot, string expected)
    {
        var reset = ResetInSlot(slot, 4);
        Assert.Equal((PaceBand.Over, expected), PaceText.Outlook(hoursToFull, reset, reset.AddHours(-hoursToReset)));
    }

    [Fact]
    public void ThereIsNoOutlookWithoutAProjectionOrAResetAhead()
    {
        var reset = ResetInSlot(0, 5);
        Assert.Null(PaceText.Outlook(null, reset, reset.AddHours(-5)));
        Assert.Null(PaceText.Outlook(0, reset, reset.AddHours(-5)));
        Assert.Null(PaceText.Outlook(2, null, reset.AddHours(-5)));
        Assert.Null(PaceText.Outlook(2, reset, reset));              // the reset is now
        Assert.Null(PaceText.Outlook(2, reset, reset.AddMinutes(1))); // the reset has passed
    }

    // MARK: - An account that is already there (CT-003)
    //
    // Windows has this rule first; the Mac app's twin and its test come with its side of CT-003.

    private static Account Row(string label, string? email, string plan = "Pro") =>
        new() { Label = label, Email = email, SubscriptionLabel = email is null ? null : plan };

    [Fact]
    public void SigningInAnAccountThatIsAlreadyThereKeepsItsRowAndGivesItTheNewSession()
    {
        var work = Row("Work", "ana@company.com", "Team");
        var home = Row("Home", "ana@example.com");
        var added = Row("Claude account", null);
        var roster = new[] { work, home, added };

        var merge = Accounts.MergeDuplicate(roster, added.Id, "  Ana@Example.com ");

        Assert.NotNull(merge);
        // Its own name, id, plan and place in the list — only the session is the new one.
        Assert.Equal(home with { ProfileId = added.ProfileId }, merge.Kept);
        Assert.Equal([work, merge.Kept], merge.Roster);
        Assert.Equal(home.ProfileName, merge.AbandonedProfile);
        Assert.NotEqual(merge.Kept.ProfileName, merge.AbandonedProfile);
    }

    [Fact]
    public void ANewAccountIsNotMergedIntoAnything()
    {
        var home = Row("Home", "ana@example.com");
        var added = Row("Claude account", null);
        Assert.Null(Accounts.MergeDuplicate([home, added], added.Id, "bruno@example.com"));
        Assert.Null(Accounts.MergeDuplicate([added], added.Id, "ana@example.com"));
    }

    [Fact]
    public void SigningAnAccountBackInIsNotAMerge()
    {
        // Two rows that already share an email (made before the rule existed): signing one of
        // them in again must not make the other swallow it.
        var first = Row("First", "ana@example.com");
        var second = Row("Second", "ana@example.com");
        Assert.Null(Accounts.MergeDuplicate([first, second], second.Id, "ana@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAnEmailThereIsNothingToCompare(string? email)
    {
        var home = Row("Home", "ana@example.com");
        var added = Row("Claude account", null);
        Assert.Null(Accounts.MergeDuplicate([home, added], added.Id, email));
    }

    [Fact]
    public void ARowThatIsNotInTheRosterMergesNothing()
    {
        var home = Row("Home", "ana@example.com");
        Assert.Null(Accounts.MergeDuplicate([home], Guid.NewGuid(), "ana@example.com"));
    }

    // MARK: - Alerts (CT-004)
    //
    // The Mac app words its alerts and decides which windows raise them inside its view model.
    // The values here are what it produces for the same inputs.

    [Fact]
    public void NamesAreListedTheWayTheLanguageListsThem()
    {
        Assert.Equal("", TextList.And([]));
        Assert.Equal("5-Hour Window", TextList.And(["5-Hour Window"]));
        Assert.Equal("5-Hour Window and 7-Day Window", TextList.And(["5-Hour Window", "7-Day Window"]));
        Assert.Equal("A, B, and C", TextList.And(["A", "B", "C"]));
        Assert.Equal("A y B", TextList.And(["A", "B"], "es"));
        Assert.Equal("A, B y C", TextList.And(["A", "B", "C"], "es"));
    }

    [Fact]
    public void AResetAlertNamesOneWindowOrSeveral()
    {
        Assert.Equal("5-Hour Window reset — you're good to go!", AlertText.ResetBody(["5-Hour Window"]));
        Assert.Equal("5-Hour Window and 7-Day Window have reset — you're good to go!",
                     AlertText.ResetBody(["5-Hour Window", "7-Day Window"]));
    }

    [Fact]
    public void APaceAlertSaysWhichWindowHowSoonAndHowFast()
    {
        Assert.Equal("5-Hour Window fills in 25 min at 45%/hr", AlertText.PaceBody("5-Hour Window", 25, 45.0, PaceRateUnit.PerHour));
        Assert.Equal("7-Day Fable fills in 3 min at 0.750%/min", AlertText.PaceBody("7-Day Fable", 3, 45.0, PaceRateUnit.PerMinute));
    }

    [Theory]
    // key, per-model, 5-hour switch, 7-day switch, per-model rows shown -> watched
    [InlineData("five_hour", false, true, false, false, true)]
    [InlineData("five_hour", false, false, true, true, false)]
    [InlineData("seven_day", false, true, false, true, false)]
    [InlineData("seven_day", false, false, true, false, true)]
    // A per-model window rides the 7-day switch, and only while its row is shown.
    [InlineData("scoped.Fable", true, true, true, true, true)]
    [InlineData("scoped.Fable", true, true, true, false, false)]
    [InlineData("scoped.Fable", true, true, false, true, false)]
    [InlineData("seven_day_sonnet", true, false, true, true, true)]
    public void AWindowIsWatchedByItsOwnSwitch(string key, bool perModel, bool notify5Hour, bool notify7Day, bool showModels, bool watched)
    {
        var window = new TrackedWindow(key, key, new UsageWindow(10, null), perModel);
        Assert.Equal(watched, AlertSettings.IsWatched(window, notify5Hour, notify7Day, showModels));
    }

    [Theory]
    [InlineData(null, 3.0)]
    [InlineData(0.0, 3.0)]
    [InlineData(-4.0, 3.0)]
    [InlineData(double.NaN, 3.0)]
    [InlineData(0.2, 1.0)]
    [InlineData(12.0, 12.0)]
    [InlineData(99.0, 30.0)]
    public void AStoredToastDurationIsBroughtIntoRange(double? stored, double expected) =>
        Assert.Equal(expected, AlertSettings.ToastSeconds(stored, AlertSettings.ResetToastSecondsDefault));

    [Theory]
    [InlineData(null, 30.0)]
    [InlineData(0.0, 30.0)]
    [InlineData(2.0, 5.0)]
    [InlineData(45.0, 45.0)]
    [InlineData(600.0, 60.0)]
    public void AStoredWarningThresholdIsBroughtIntoRange(double? stored, double expected) =>
        Assert.Equal(expected, AlertSettings.WarningMinutes(stored));

    // MARK: - Chart arithmetic (CT-006)
    //
    // The Mac app computes these inside its chart views. The values are what its code gives.

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static List<(DateTimeOffset Time, double Value)> Readings(params (double HoursAfterNoon, double Value)[] samples) =>
        samples.Select(s => (Noon.AddHours(s.HoursAfterNoon), s.Value)).ToList();

    [Fact]
    public void AForecastRunsFromTheWindowsStartToItsReset()
    {
        // A 5-hour window resetting at 17:00, at 40 % by 14:00, filling at 10 %/hr: full at 20:00.
        var forecast = ChartLayout.ForecastFor(Readings((1, 20), (2, 40)), Noon.AddHours(5), 5 * 3600, liveUtilization: 41, ratePerHour: 10);
        Assert.Equal(Noon, forecast.WindowStart);
        Assert.Equal(Noon.AddHours(2), forecast.LastTime);
        Assert.Equal(40, forecast.LastValue);
        Assert.Equal(Noon.AddHours(8), forecast.ProjectedFull);
        // The projection lands after the reset, so the chart runs on to it.
        Assert.Equal(Noon.AddHours(8), forecast.End);
    }

    [Fact]
    public void AForecastThatFillsBeforeTheResetStillEndsAtTheReset()
    {
        var forecast = ChartLayout.ForecastFor(Readings((1, 20), (2, 40)), Noon.AddHours(5), 5 * 3600, 40, ratePerHour: 60);
        Assert.Equal(Noon.AddHours(3), forecast.ProjectedFull);
        Assert.Equal(Noon.AddHours(5), forecast.End);
    }

    [Fact]
    public void AForecastHasNoProjectionWithoutAPaceAReadingOrRoomLeft()
    {
        var reset = Noon.AddHours(5);
        Assert.Null(ChartLayout.ForecastFor(Readings((2, 40)), reset, 5 * 3600, 40, ratePerHour: null).ProjectedFull);
        Assert.Null(ChartLayout.ForecastFor(Readings((2, 100)), reset, 5 * 3600, 100, ratePerHour: 10).ProjectedFull);
        var empty = ChartLayout.ForecastFor([], reset, 5 * 3600, liveUtilization: 12, ratePerHour: 10);
        Assert.Null(empty.ProjectedFull);
        Assert.Null(empty.LastTime);
        Assert.Equal(12, empty.LastValue);   // nothing recorded in this window yet: the live number
        Assert.Equal(reset, empty.End);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]     // before the window began
    [InlineData(0.0, 0.0)]
    [InlineData(1.25, 25.0)]
    [InlineData(5.0, 100.0)]
    [InlineData(9.0, 100.0)]    // past the reset
    public void AnEvenPaceIsAStraightLineFromNothingToFull(double hoursAfterStart, double expected) =>
        Assert.Equal(expected, ChartLayout.ExpectedPercent(Noon.AddHours(hoursAfterStart), Noon, Noon.AddHours(5)));

    [Fact]
    public void TheFiguresAreTheLatestTheHighestAndTheAverage()
    {
        Assert.Null(ChartLayout.Stats([]));
        Assert.Equal((30, 50, 33), ChartLayout.Stats(Readings((0, 20), (1, 50), (2, 30))));
        // The average is the whole number that is shown, and a half goes up as on the Mac:
        // .NET's own rounding takes 12.5 to 12, the even one.
        Assert.Equal((13, 13, 13), ChartLayout.Stats(Readings((0, 12), (1, 13))));
        // A pace chart leaves a fifth of headroom, and never collapses to nothing.
        Assert.Equal(60, ChartLayout.PaceScaleTop(Readings((0, 20), (1, 50), (2, 30))));
        Assert.Equal(1, ChartLayout.PaceScaleTop(Readings((0, 0.2))));
        Assert.Equal(1, ChartLayout.PaceScaleTop([]));
    }

    [Fact]
    public void ASeriesIsItsOwnSamplesFromACutoffOn()
    {
        var history = new[]
        {
            new UsageDataPoint(Noon, 10, 3, null, null),
            new UsageDataPoint(Noon.AddMinutes(5), null, 4, null, null),   // the 5-hour window was missing here
            new UsageDataPoint(Noon.AddMinutes(10), 12, 5, null, null),
        };
        Assert.Equal([(Noon, 10.0), (Noon.AddMinutes(10), 12.0)], ChartLayout.Pairs(history, p => p.Utilization("five_hour"), Noon));
        Assert.Equal([(Noon.AddMinutes(10), 5.0)], ChartLayout.Pairs(history, p => p.Utilization("seven_day"), Noon.AddMinutes(6)));
    }

    [Fact]
    public void ThePointerSnapsToTheMinute()
    {
        Assert.Equal(Noon.AddMinutes(7), ChartLayout.QuantizeToMinute(Noon.AddMinutes(7).AddSeconds(29)));
        Assert.Equal(Noon.AddMinutes(8), ChartLayout.QuantizeToMinute(Noon.AddMinutes(7).AddSeconds(31)));
    }

    [Theory]
    // hours shown, ending at 13:27 UTC -> the marks, as UTC clock times or days of October
    [InlineData(1, "12:30 12:45 13:00 13:15")]
    [InlineData(5, "10:00 12:00")]
    [InlineData(24, "18:00 00:00 06:00 12:00")]
    [InlineData(7 * 24, "d30 d2 d4")]
    // Weeks and fortnights fall on Mondays: 14 and 28 September 2026 are.
    [InlineData(30 * 24, "d14 d28")]
    public void ATimeAxisIsMarkedOnRoundTimes(int hours, string expected)
    {
        var upper = Noon.AddHours(1).AddMinutes(27);
        var ticks = ChartLayout.TimeTicks(upper.AddHours(-hours), upper, TimeZoneInfo.Utc);
        var shown = string.Join(" ", ticks.Select(t => hours < 25 ? t.ToString("HH:mm") : "d" + t.Day));
        Assert.Equal(expected, shown);
        Assert.InRange(ticks.Count, 2, 4);
    }

    [Fact]
    public void RoundTimesAreTheReadersOwnNotUtc()
    {
        // A zone a quarter-hour off UTC: the marks still fall on its own hours.
        var kathmandu = TimeZoneInfo.CreateCustomTimeZone("test+5:45", new TimeSpan(5, 45, 0), "test", "test");
        var ticks = ChartLayout.TimeTicks(Noon.AddHours(-5), Noon, kathmandu);
        Assert.All(ticks, tick => Assert.Equal(0, TimeZoneInfo.ConvertTime(tick, kathmandu).Minute));
        Assert.Empty(ChartLayout.TimeTicks(Noon, Noon));
    }

    /// <summary>Clocks an hour forward at midnight on 6 September and back at midnight on 5 April, as in Chile.</summary>
    private static TimeZoneInfo MidnightChanges()
    {
        var midnight = new DateTime(1, 1, 1, 0, 0, 0);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 9, 6),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 4, 5));
        return TimeZoneInfo.CreateCustomTimeZone("test-4", TimeSpan.FromHours(-4), "test", "test", "test summer", [rule]);
    }

    [Theory]
    // The clocks go back: counted in seconds, every mark after it would sit at 23:00 the day before.
    [InlineData(4, 8, "3@0 5@0 7@0")]
    // The clocks go forward at midnight: that day has no midnight, and its mark is its first moment.
    [InlineData(9, 9, "4@0 6@1 8@0")]
    // Within a day, the hour that does not exist is left out rather than drawn twice.
    [InlineData(9, 6, "5@12 5@18 6@6 6@12")]
    public void DayMarksStayOnTheirDayWhenTheClocksChange(int month, int lastDay, string expected)
    {
        var zone = MidnightChanges();
        var upper = new DateTimeOffset(2026, month, lastDay, 16, 0, 0, TimeSpan.Zero);
        var lower = lastDay == 6 ? upper.AddHours(-24) : upper.AddDays(-7);
        var shown = ChartLayout.TimeTicks(lower, upper, zone)
            .Select(tick => TimeZoneInfo.ConvertTime(tick, zone))
            .Select(local => $"{local.Day}@{local.Hour}");
        Assert.Equal(expected, string.Join(" ", shown));
    }

    /// <summary>Clocks an hour forward at 02:00 on 8 March and back at 02:00 on 1 November, as in New York in 2026.</summary>
    private static TimeZoneInfo NightChanges()
    {
        var two = new DateTime(1, 1, 1, 2, 0, 0);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(two, 3, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(two, 11, 1));
        return TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test", "test", "test summer", [rule]);
    }

    [Theory]
    // The hour before the clocks go back: its marks are summer time's. Read as winter time's,
    // 01:00 fell after the range's end, and a 1h chart had no time marks at all.
    [InlineData("04:51", 1, "1:00-4 1:15-4 1:30-4 1:45-4")]
    // Across the change the clock shows 01:30 twice, and the axis marks both.
    [InlineData("05:30", 1, "1:30-4 1:45-4 1:00-5 1:15-5 1:30-5")]
    // The hour after: winter time's, with nothing from the hour before.
    [InlineData("06:10", 1, "1:15-5 1:30-5 1:45-5 2:00-5")]
    [InlineData("02:00", 5, "22:00-4 0:00-4 2:00-5")]
    public void TheHourTheClocksGoBackOverIsMarkedAsItHappens(string fromUtc, int hours, string expected)
    {
        var zone = NightChanges();
        var lower = new DateTimeOffset(2026, 11, 1, int.Parse(fromUtc[..2]), int.Parse(fromUtc[3..]), 0, TimeSpan.Zero);
        var ticks = ChartLayout.TimeTicks(lower, lower.AddHours(hours), zone);
        Assert.Equal(expected, string.Join(" ", ticks.Select(tick => $"{tick.Hour}:{tick.Minute:00}{tick.Offset.Hours}")));
        Assert.Equal(ticks.OrderBy(tick => tick), ticks);
    }

    [Theory]
    // room for the popover, in its own units -> the tallest its list of charts may be
    [InlineData(928, 678)]      // a 1440-line screen
    // A 1080-line screen at 150 %, with the popup size at 125 % and at 150 %. Given the Mac
    // app's floor of 320, the popover here stood 32 and 122 units taller than the screen.
    [InlineData(538, 288)]
    [InlineData(448, 198)]
    public void TheChartsListLeavesRoomForWhatSurroundsIt(double available, double expected)
    {
        Assert.Equal(expected, ChartLayout.ListHeightLimit(available));
        Assert.True(ChartLayout.ListHeightLimit(available) + ChartLayout.ListSurroundings <= available);
        // The update banner and a notice stand above the list, and are room it does not have.
        Assert.Equal(expected - 60, ChartLayout.ListHeightLimit(available, alsoShowing: 60));
        // On a screen too small for all of it the list keeps enough to be scrolled.
        Assert.Equal(ChartLayout.SmallestList, ChartLayout.ListHeightLimit(300));
    }

    [Fact]
    public void AnAxisOfAnyLengthKeepsToAFewMarks()
    {
        // A forecast at a crawling pace ends when it would fill: here in over a year.
        foreach (var days in new[] { 9, 40, 100, 400, 4000 })
        {
            var ticks = ChartLayout.TimeTicks(Noon, Noon.AddDays(days), TimeZoneInfo.Utc);
            Assert.InRange(ticks.Count, 1, 5);
            Assert.All(ticks, tick => Assert.Equal(DayOfWeek.Monday, tick.DayOfWeek));
        }
    }

    [Fact]
    public void ALabelIsAClockTimeWithinADayAndADateBeyond()
    {
        var english = new System.Globalization.CultureInfo("en-US");
        var spanish = new System.Globalization.CultureInfo("es-CL");
        var moment = Noon.AddHours(5.5);    // 17:30 UTC
        Assert.Equal("17:30", ChartLayout.TimeLabel(moment, 5 * 3600, use24Hour: true, TimeZoneInfo.Utc, english));
        Assert.Equal("5:30 PM", ChartLayout.TimeLabel(moment, 24 * 3600, use24Hour: false, TimeZoneInfo.Utc, english));
        Assert.Equal("Oct 5", ChartLayout.TimeLabel(moment, 7 * 24 * 3600, use24Hour: true, TimeZoneInfo.Utc, english));
        Assert.Equal("5 oct", ChartLayout.TimeLabel(moment, 7 * 24 * 3600, use24Hour: true, TimeZoneInfo.Utc, spanish).TrimEnd('.'));
    }

    // MARK: - Extra usage and popup size

    [Theory]
    [InlineData("en-US", "$12.34 / $50.00")]
    [InlineData("en-GB", "US$12.34 / US$50.00")]     // a bare "$" would not say whose dollars
    [InlineData("es-CL", "US$12,34 / US$50,00")]     // Chile's own "$" has no decimals; these keep two
    public void ExtraUsageIsWrittenAsUsDollarsInTheReadersOwnNumberStyle(string culture, string expected) =>
        Assert.Equal(expected, Money.SpentOfLimit(12.34, 50, new System.Globalization.CultureInfo(culture)));

    [Fact]
    public void UsDollarsSurviveACultureWithNoCountry()
    {
        Assert.Equal("US$1,234.50", Money.Usd(1234.5, System.Globalization.CultureInfo.InvariantCulture));
        // Spanish without a country writes the symbol after the number.
        Assert.Contains("US$", Money.Usd(5, new System.Globalization.CultureInfo("es")));
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.25, 1.25)]
    [InlineData(9.0, 1.5)]
    public void AStoredPopupSizeIsBroughtIntoRange(double? stored, double expected) =>
        Assert.Equal(expected, PopupScale.Normalized(stored));

    // MARK: - Spanish and screen readers (CT-007)

    [Fact]
    public void ADateIsWrittenInTheLanguageOfTheSentenceAroundIt()
    {
        var english = new System.Globalization.CultureInfo("en-US");
        var chile = new System.Globalization.CultureInfo("es-CL");
        var spanish = new System.Globalization.CultureInfo("es");
        // The regional format speaks the display language: it is the one to use, with its own habits.
        Assert.Same(chile, TimeText.DisplayCulture(regional: chile, display: spanish));
        Assert.Same(english, TimeText.DisplayCulture(regional: english, display: english));
        // It does not: a Spanish sentence must not end in "Thu, Oct 8", nor an English one in "jue, 8 oct".
        Assert.Same(spanish, TimeText.DisplayCulture(regional: english, display: spanish));
        Assert.Same(english, TimeText.DisplayCulture(regional: chile, display: english));

        var reset = new DateTimeOffset(2026, 10, 8, 15, 50, 0, TimeSpan.Zero);
        var now = reset.AddDays(-3);
        var inSpanish = TimeText.ResetTimeText(reset, now, use24Hour: true, includeDate: true, TimeZoneInfo.Utc,
                                               TimeText.DisplayCulture(regional: english, display: spanish));
        Assert.DoesNotContain("Thu", inSpanish);
        Assert.Contains("oct", inSpanish);
    }

    [Theory]
    [InlineData(1, "1 minute", "1 minuto")]
    [InlineData(30, "30 minutes", "30 minutos")]
    public void ASliderSaysItsMinutesInWords(int minutes, string english, string spanish)
    {
        Assert.Equal(english, AlertSettings.MinutesInWords(minutes));
        Assert.Equal(spanish, minutes == 1 ? L.Lookup("es", "1 minute") : L.Format("es", "%d minutes", minutes));
    }

    [Theory]
    [InlineData(1, "1 second", "1 segundo")]
    [InlineData(5, "5 seconds", "5 segundos")]
    public void ASliderSaysItsSecondsInWords(int seconds, string english, string spanish)
    {
        Assert.Equal(english, AlertSettings.SecondsInWords(seconds));
        Assert.Equal(spanish, seconds == 1 ? L.Lookup("es", "1 second") : L.Format("es", "%d seconds", seconds));
    }

    // MARK: - Launch at sign-in (CT-005)

    private const string Here = @"C:\Users\me\AppData\Local\Programs\ClaudeTracker\ClaudeTracker.exe";
    private static readonly string Wanted = LoginItem.Command(Here);

    [Fact]
    public void TheSignInEntryStartsTheAppQuietly()
    {
        Assert.Equal("\"" + Here + "\" --background", Wanted);
        Assert.Equal(Here, LoginItem.ExecutablePath(Wanted));
        Assert.Equal(@"C:\apps\ClaudeTracker.exe", LoginItem.ExecutablePath(@"C:\apps\ClaudeTracker.exe --background"));
        Assert.Null(LoginItem.ExecutablePath(null));
        Assert.Null(LoginItem.ExecutablePath("   "));
        Assert.Null(LoginItem.ExecutablePath("\"unclosed"));
    }

    [Theory]
    // What Task Manager and Settings write beside an entry: even while it is allowed, odd once switched off.
    [InlineData(new byte[] { }, true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x9A, 0x55, 0x01, 0xCC, 0x35, 0xDC, 0x01 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    // Written by Windows 11's Settings on 2026-10-05: off, then on again.
    [InlineData(new byte[] { 0x01, 0, 0, 0, 0x22, 0x05, 0xCA, 0x73, 0xEF, 0x54, 0xDD, 0x01 }, false)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    public void WindowsNotesAnEntryTheUserSwitchedOff(byte[] note, bool allowed) =>
        Assert.Equal(allowed, LoginItem.IsAllowed(note));

    [Fact]
    public void AnInstalledCopyOptsInOnceAndThenFollowsWhatWasDoneElsewhere()
    {
        // The first run of an installed copy: on, and registered.
        Assert.Equal(new LoginItemStep(true, true), LoginItem.AtStart(true, null, null, allowed: true, Wanted, false));
        // ...unless an earlier install's entry was switched off: that still stands.
        Assert.Equal(new LoginItemStep(false, false), LoginItem.AtStart(true, null, Wanted, allowed: false, Wanted, true));

        // On, registered, allowed: nothing to do.
        Assert.Equal(default, LoginItem.AtStart(true, true, Wanted, allowed: true, Wanted, true));
        // Switched off in Task Manager: the switch follows, and the entry is not written again.
        Assert.Equal(new LoginItemStep(false, false), LoginItem.AtStart(true, true, Wanted, allowed: false, Wanted, true));
        // Removed by a cleanup tool: the same.
        Assert.Equal(new LoginItemStep(false, false), LoginItem.AtStart(true, true, null, allowed: true, Wanted, false));
        // Off in the app stays off: nothing registered, or still switched off in Windows.
        Assert.Equal(default, LoginItem.AtStart(true, false, null, allowed: true, Wanted, false));
        Assert.Equal(default, LoginItem.AtStart(true, false, Wanted, allowed: false, Wanted, true));
        // Switched back on in Windows' own list: Windows will start the app, so the switch
        // says so. Nothing is written — the entry is already there.
        Assert.Equal(new LoginItemStep(true, false), LoginItem.AtStart(true, false, Wanted, allowed: true, Wanted, true));
        // Another copy's entry is not this copy's business.
        Assert.Equal(default, LoginItem.AtStart(true, false, LoginItem.Command(@"D:\Other\ClaudeTracker.exe"), allowed: true, Wanted, true));
    }

    [Fact]
    public void TheEntryMovesOnlyWhenTheCopyItNamesIsGone()
    {
        var elsewhere = LoginItem.Command(@"D:\Old\ClaudeTracker.exe");
        // Installed again in another folder, the old one deleted: this copy takes the entry over.
        Assert.Equal(new LoginItemStep(null, true), LoginItem.AtStart(true, true, elsewhere, allowed: true, Wanted, registeredCopyExists: false));
        // Another copy that is still there keeps it.
        Assert.Equal(default, LoginItem.AtStart(true, true, elsewhere, allowed: true, Wanted, registeredCopyExists: true));
        // The same copy, written with other capitals, is the same copy.
        Assert.Equal(default, LoginItem.AtStart(true, true, Wanted.ToUpperInvariant().Replace("--BACKGROUND", "--background"), allowed: true, Wanted, registeredCopyExists: false));
    }

    [Fact]
    public void InstallingAgainAfterAnUninstallKeepsWhatTheUserHadChosen()
    {
        // The uninstaller took the entry with the copy before. The settings were kept, and say "on".
        Assert.Equal(new LoginItemStep(null, true), LoginItem.AtStart(true, true, null, allowed: true, Wanted, false, justInstalled: true));
        // The same missing entry on any other start was removed by someone: the switch follows.
        Assert.Equal(new LoginItemStep(false, false), LoginItem.AtStart(true, true, null, allowed: true, Wanted, false, justInstalled: false));
        // Off before the uninstall is still off after the reinstall.
        Assert.Equal(default, LoginItem.AtStart(true, false, null, allowed: true, Wanted, false, justInstalled: true));
        // And a first install ever opts in, as on any first run.
        Assert.Equal(new LoginItemStep(true, true), LoginItem.AtStart(true, null, null, allowed: true, Wanted, false, justInstalled: true));
    }

    [Fact]
    public void ACopyRunFromABuildFolderLeavesSignInAlone()
    {
        foreach (bool? preference in new bool?[] { null, true, false })
        {
            Assert.Equal(default, LoginItem.AtStart(installed: false, preference, null, allowed: true, Wanted, false));
            Assert.Equal(default, LoginItem.AtStart(installed: false, preference, Wanted, allowed: false, Wanted, true));
        }
    }

    // MARK: - Updates (CT-005)

    [Theory]
    [InlineData(4 * 3600, "Checks every ~4h — on launch and wake")]
    [InlineData(12 * 3600, "Checks every ~12h — on launch and wake")]
    [InlineData(23.4 * 3600, "Checks every ~23h — on launch and wake")]
    [InlineData(23.5 * 3600, "Checks every ~1d — on launch and wake")]
    [InlineData(24 * 3600, "Checks every ~1d — on launch and wake")]
    public void TheCheckIntervalIsSaidInHoursThenInDays(double seconds, string expected) =>
        Assert.Equal(expected, Updates.CheckIntervalLabel(seconds));

    [Fact]
    public void ASetupFileIsRunTheQuietWayAndComparedByItsOwnVersion()
    {
        // The setup script reads AppArgs and hands it to the app it starts: the two must agree.
        Assert.Contains("/VERYSILENT", Updates.SilentInstallArguments);
        Assert.Contains("/AppArgs=--background", Updates.SilentInstallArguments);
        Assert.EndsWith("--background", LoginItem.Command("x"));
        Assert.Equal("1.4.0", Updates.SetupVersion(1, 4, 0));
        Assert.True(Updates.IsNewerVersion(Updates.SetupVersion(1, 10, 0), "1.9.2"));
        Assert.False(Updates.IsNewerVersion(Updates.SetupVersion(0, 1, 0), "0.1.0"));
        Assert.Contains("/ClaudeTracker-Windows/", Updates.ReleasesUrl);
    }

    /// <summary>
    /// The whole decision about a downloaded setup, against the real release key: the fixture
    /// was signed on the maintainer's Mac, so this is the one place the "yes, run it" answer
    /// is reached without that Mac.
    /// </summary>
    [Fact]
    public void ADownloadedSetupIsRunOnlyWhenSignedAndNewer()
    {
        var file = Fixture.Bytes("signing-check.txt");
        var signature = Fixture.Bytes("signing-check.txt.sig");
        Assert.Equal(SetupVerdict.Run, Updates.JudgeSetup(file, signature, Updates.SigningPublicKey, "1.3.0", "1.2.9"));
        // Genuine, but the release's file is not the newer version it was offered as.
        Assert.Equal(SetupVerdict.NotNewer, Updates.JudgeSetup(file, signature, Updates.SigningPublicKey, "1.2.9", "1.2.9"));
        Assert.Equal(SetupVerdict.NotNewer, Updates.JudgeSetup(file, signature, Updates.SigningPublicKey, "1.2.0", "1.2.9"));

        // One byte off, a signature of something else, no signature: never run, whatever version it claims.
        var tampered = (byte[])file.Clone();
        tampered[0] ^= 1;
        Assert.Equal(SetupVerdict.SignatureInvalid, Updates.JudgeSetup(tampered, signature, Updates.SigningPublicKey, "9.9.9", "1.2.9"));
        Assert.Equal(SetupVerdict.SignatureInvalid, Updates.JudgeSetup(file, new byte[64], Updates.SigningPublicKey, "9.9.9", "1.2.9"));
        Assert.Equal(SetupVerdict.SignatureInvalid, Updates.JudgeSetup(file, [], Updates.SigningPublicKey, "9.9.9", "1.2.9"));
        // And a key that is not the embedded one does not vouch for the file either.
        Assert.Equal(SetupVerdict.SignatureInvalid, Updates.JudgeSetup(file, signature, new byte[32], "9.9.9", "1.2.9"));
    }

    [Theory]
    [InlineData("https://github.com/diegovilloutafredes/ClaudeTracker-Windows/releases/tag/v1.2.0", true)]
    [InlineData("http://127.0.0.1:8123/release.html", true)]
    // Windows runs or opens whatever it is handed: none of these may come out of a release.
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("ms-settings:startupapps", false)]
    [InlineData("javascript:alert(1)", false)]
    public void OnlyAWebAddressIsOpenedFromARelease(string address, bool opens) =>
        Assert.Equal(opens, Updates.IsWebLink(new Uri(address, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void AReleasePageThatIsNotAWebAddressIsStillNotOpened()
    {
        Assert.False(Updates.IsWebLink(null));
        var (update, _) = Updates.ParseGitHubReleases(
            """[{"tag_name":"v9.0.0","html_url":"file:///C:/Windows/System32/calc.exe","published_at":"2026-10-01T00:00:00Z","assets":[]}]""", "1.0.0");
        Assert.NotNull(update);
        Assert.False(Updates.IsWebLink(update!.ReleaseUrl));
    }
}
