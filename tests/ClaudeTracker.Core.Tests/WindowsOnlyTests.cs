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
}
