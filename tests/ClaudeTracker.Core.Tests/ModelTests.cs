using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// The original model and helper tests — timestamp parsing, utilization clamping, plan labels,
/// version comparison, pace, the update-check cadence, and reset and stale detection. The port
/// of the macOS suite's ClaudeTrackerTests.swift, one test for one test.
///
/// Not ported: its five view-model and app-startup tests (<c>UsageViewModel</c> and the app
/// entry point have no Windows twin until the app shell is written).
/// </summary>
public class ModelTests
{
    // MARK: - ISO 8601 date parsing

    [Fact]
    public void ResetsAtDateWithFractionalSeconds()
    {
        var w = new UsageWindow(50, "2025-01-15T10:00:00.000Z");
        Assert.NotNull(w.ResetsAtDate);
    }

    [Fact]
    public void ResetsAtDateWithoutFractionalSeconds()
    {
        var w = new UsageWindow(50, "2025-01-15T10:00:00Z");
        Assert.NotNull(w.ResetsAtDate);
    }

    [Fact]
    public void ResetsAtDateInvalid()
    {
        var w = new UsageWindow(50, "not-a-date");
        Assert.Null(w.ResetsAtDate);
    }

    [Fact]
    public void ResetsAtDateParsesCorrectly()
    {
        var w = new UsageWindow(50, "2025-01-15T10:00:00Z");
        var expected = T.Utc(2025, 1, 15, 10, 0, 0);
        Assert.Equal(expected, w.ResetsAtDate);
    }

    // MARK: - Utilization fraction clamping

    [Fact]
    public void UtilizationFractionNormal() =>
        Assert.Equal(0.5, new UsageWindow(50, "").UtilizationFraction);

    [Fact]
    public void UtilizationFractionAtZero() =>
        Assert.Equal(0.0, new UsageWindow(0, "").UtilizationFraction);

    [Fact]
    public void UtilizationFractionAtHundred() =>
        Assert.Equal(1.0, new UsageWindow(100, "").UtilizationFraction);

    [Fact]
    public void UtilizationFractionClampsAboveHundred() =>
        Assert.Equal(1.0, new UsageWindow(110, "").UtilizationFraction);

    // MARK: - Subscription label inference

    [Fact]
    public void SubscriptionLabelMax20x() =>
        Assert.Equal("Max 20×", MakeInfo(["claude_max"], "default_claude_max_20x").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelMax5x() =>
        Assert.Equal("Max 5×", MakeInfo(["claude_max"], "default_claude_max_5x").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelMax() =>
        Assert.Equal("Max", MakeInfo(["claude_max"], "default_claude_max").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelProViaCaps() =>
        Assert.Equal("Pro", MakeInfo(["claude_pro"], "").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelProViaTier() =>
        Assert.Equal("Pro", MakeInfo([], "default_pro").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelTeam() =>
        Assert.Equal("Team", MakeInfo(["claude_team"], "").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelEnterprise() =>
        Assert.Equal("Enterprise", MakeInfo(["claude_enterprise"], "").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelNilForFreeAccount() =>
        Assert.Null(MakeInfo([], "").SubscriptionLabel);

    [Fact]
    public void SubscriptionLabelNilWhenNoMemberships()
    {
        var info = new AccountInfo(null, "x@x.com", null);
        Assert.Null(info.SubscriptionLabel);
    }

    [Fact]
    public void OrganizationPlanComesFromTheCanonicalLabel()
    {
        // Settings shows the org name only for organization plans. The check reads the
        // canonical persisted label, which display code localizes and must not compare.
        Assert.True(new Account { Label = "Work", SubscriptionLabel = "Team" }.IsOrganizationPlan);
        Assert.True(new Account { Label = "Work", SubscriptionLabel = "Enterprise" }.IsOrganizationPlan);
        Assert.False(new Account { Label = "Home", SubscriptionLabel = "Max 5×" }.IsOrganizationPlan);
        Assert.False(new Account { Label = "New" }.IsOrganizationPlan);
    }

    // MARK: - Version comparison

    [Fact]
    public void VersionIsNewer() => Assert.True(Updates.IsNewerVersion("1.9.0", "1.8.0"));

    [Fact]
    public void VersionIsOlder() => Assert.False(Updates.IsNewerVersion("1.7.0", "1.8.0"));

    [Fact]
    public void VersionIsSame() => Assert.False(Updates.IsNewerVersion("1.8.0", "1.8.0"));

    [Fact]
    public void VersionMajorBump() => Assert.True(Updates.IsNewerVersion("2.0.0", "1.9.9"));

    [Fact]
    public void VersionPatchLevel() => Assert.True(Updates.IsNewerVersion("1.8.1", "1.8.0"));

    [Fact]
    public void VersionTwoDigitMinor()
    {
        // Ensures numeric comparison (not lexicographic): "1.10.0" > "1.9.0"
        Assert.True(Updates.IsNewerVersion("1.10.0", "1.9.0"));
    }

    // MARK: - Pace calculation

    [Fact]
    public void PaceNilWhenHistoryEmpty() => Assert.Null(PaceMath.ComputePace([]));

    [Fact]
    public void PaceNilWhenOnlyOneEntry() => Assert.Null(PaceMath.ComputePace([(T.Now, 10.0)]));

    [Fact]
    public void PaceNilWhenElapsedTooShort()
    {
        var now = T.Now;
        // 10 seconds elapsed — below the 15s minimum
        (DateTimeOffset, double)[] history = [(now.AddSeconds(-10), 10.0), (now, 11.0)];
        Assert.Null(PaceMath.ComputePace(history));
    }

    [Fact]
    public void PaceNilWhenRateNegligible()
    {
        var now = T.Now;
        // 1 hr elapsed, 0.05% delta → rate = 0.05 %/hr ≤ 0.1 threshold
        (DateTimeOffset, double)[] history = [(now.AddSeconds(-3600), 10.0), (now, 10.05)];
        Assert.Null(PaceMath.ComputePace(history));
    }

    [Fact]
    public void PaceReturnsCorrectRate()
    {
        var now = T.Now;
        // 1 hr elapsed, 10% delta → rate = 10 %/hr
        (DateTimeOffset, double)[] history = [(now.AddSeconds(-3600), 50.0), (now, 60.0)];
        var result = PaceMath.ComputePace(history);
        Assert.NotNull(result);
        Assert.Equal(10.0, result!.Value.Rate, 0.01);
    }

    [Fact]
    public void PaceProjectedHours()
    {
        var now = T.Now;
        // 1 hr elapsed, 10% delta → rate = 10 %/hr; 40% remaining (100-60) → projected = 4 hr
        (DateTimeOffset, double)[] history = [(now.AddSeconds(-3600), 50.0), (now, 60.0)];
        var result = PaceMath.ComputePace(history);
        Assert.NotNull(result);
        Assert.Equal(4.0, result!.Value.ProjectedHours!.Value, 0.01);
    }

    [Fact]
    public void PaceProjectedHoursNilAtFull()
    {
        var now = T.Now;
        // Already at 100% → remaining = 0 → ProjectedHours = null
        (DateTimeOffset, double)[] history = [(now.AddSeconds(-3600), 90.0), (now, 100.0)];
        var result = PaceMath.ComputePace(history);
        Assert.NotNull(result);
        Assert.Null(result!.Value.ProjectedHours);
    }

    // MARK: - Adaptive update-check interval

    [Fact]
    public void AdaptiveCheckIntervalDefaultsWithoutEnoughDates()
    {
        Assert.Equal(12 * 3600.0, Updates.AdaptiveCheckInterval([]), 0.5);
        Assert.Equal(12 * 3600.0, Updates.AdaptiveCheckInterval([T.Now]), 0.5);
    }

    [Fact]
    public void AdaptiveCheckIntervalClampsToFloor()
    {
        // Two releases 8h apart → half = 4h (the floor).
        var now = T.Now;
        Assert.Equal(4 * 3600.0, Updates.AdaptiveCheckInterval([now, now.AddSeconds(-8 * 3600)]), 0.5);
    }

    [Fact]
    public void AdaptiveCheckIntervalClampsToCeiling()
    {
        // ~10 days apart → half = 5 days, clamped to the 24h ceiling.
        var now = T.Now;
        Assert.Equal(24 * 3600.0, Updates.AdaptiveCheckInterval([now, now.AddSeconds(-10 * 24 * 3600)]), 0.5);
    }

    [Fact]
    public void AdaptiveCheckIntervalAveragesGapsAndIgnoresOrder()
    {
        // Gaps of 20h and 40h → avg 30h → half = 15h. Input deliberately unsorted.
        var now = T.Now;
        DateTimeOffset[] dates = [now.AddSeconds(-60 * 3600), now, now.AddSeconds(-20 * 3600)];
        Assert.Equal(15 * 3600.0, Updates.AdaptiveCheckInterval(dates), 1);
    }

    // MARK: - Window reset detection

    [Fact]
    public void IsWindowResetTrueOnLargeForwardJumpAndLowUtil()
    {
        var prev = T.Now;
        Assert.True(Resets.IsWindowReset(prev, prev.AddSeconds(5 * 3600), 2));
    }

    [Fact]
    public void IsWindowResetFalseAtExactlyOneHour()
    {
        var prev = T.Now;
        Assert.False(Resets.IsWindowReset(prev, prev.AddSeconds(3600), 0));
    }

    [Fact]
    public void IsWindowResetFalseWhenUtilizationStillHigh()
    {
        var prev = T.Now;
        Assert.False(Resets.IsWindowReset(prev, prev.AddSeconds(5 * 3600), 6));
    }

    // MARK: - Stale data detection

    [Fact]
    public void WindowIsStaleWhenResetPassedAfterLastFetch()
    {
        var now = T.Now;
        Assert.True(Resets.WindowIsStale(now.AddSeconds(-3600), now.AddSeconds(-2 * 3600), now));
    }

    [Fact]
    public void WindowIsStaleFalseWhenFetchedAfterReset()
    {
        var now = T.Now;
        Assert.False(Resets.WindowIsStale(now.AddSeconds(-3600), now.AddSeconds(-600), now));
    }

    [Fact]
    public void WindowIsStaleFalseWhenResetInFuture()
    {
        var now = T.Now;
        Assert.False(Resets.WindowIsStale(now.AddSeconds(3600), now.AddSeconds(-600), now));
    }

    [Fact]
    public void WindowIsStaleFalseWhenNilInputs()
    {
        var now = T.Now;
        Assert.False(Resets.WindowIsStale(null, now, now));
        Assert.False(Resets.WindowIsStale(now, null, now));
    }

    // MARK: - Helpers

    private static AccountInfo MakeInfo(IReadOnlyList<string> caps, string tier)
    {
        var org = new AccountOrganization(caps, tier, null);
        return new AccountInfo(null, "t@t.com", [new AccountMembership(org)]);
    }
}
