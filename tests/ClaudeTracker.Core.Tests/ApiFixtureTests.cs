using ClaudeTracker.Core;

namespace ClaudeTracker.Core.Tests;

/// <summary>
/// Fixture-based decoding tests against captured shapes of the unofficial claude.ai API — the
/// port of the macOS suite's APIFixtureTests.swift, one test for one test.
///
/// The verbatim /api/organizations/{id}/usage responses captured live are JSON files in
/// <c>Fixtures/</c> (<c>live-usage-&lt;date&gt;.json</c>), the same vectors the Mac suite reads:
/// 2026-09-10, Max 5x org; and 2026-09-21, Max 5x org — first sighting of a populated
/// <c>seven_day_breakdown</c> and of four more null window keys.
/// </summary>
public class ApiFixtureTests
{
    // MARK: - UsageResponse

    [Fact]
    public void UsageResponseDecodesFullPayload()
    {
        var json = """
        {
          "five_hour": {"utilization": 42.5, "resets_at": "2026-06-09T18:00:00.000Z"},
          "seven_day": {"utilization": 80, "resets_at": "2026-06-12T10:00:00Z"},
          "seven_day_opus": {"utilization": 12, "resets_at": null},
          "seven_day_sonnet": {"utilization": 30, "resets_at": "2026-06-12T10:00:00Z"},
          "extra_usage": {"is_enabled": true, "monthly_limit": 50, "used_credits": 12.5, "utilization": 25}
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(42.5, r.FiveHour?.Utilization);
        Assert.NotNull(r.FiveHour?.ResetsAtDate);
        Assert.Equal(80.0, r.SevenDay?.Utilization);
        Assert.Equal(12.0, r.SevenDayOpus?.Utilization);
        Assert.Null(r.SevenDayOpus?.ResetsAtDate);
        Assert.Equal(30.0, r.SevenDaySonnet?.Utilization);
        Assert.True(r.ExtraUsage?.IsEnabled);
        Assert.Equal(12.5, r.ExtraUsage?.UsedCredits);
    }

    [Fact]
    public void UsageResponseToleratesNullResetsAtAfterReset()
    {
        var json = """
        {"five_hour": {"utilization": 0.5, "resets_at": null}}
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(0.5, r.FiveHour?.Utilization);
        Assert.Null(r.FiveHour?.ResetsAtDate);
        Assert.Null(r.SevenDay);
    }

    [Fact]
    public void UsageResponseIgnoresUnknownFutureFields()
    {
        var json = """
        {
          "five_hour": {"utilization": 10, "resets_at": "2026-06-09T18:00:00Z", "new_field": 1},
          "brand_new_window": {"utilization": 5},
          "some_flag": true
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(10.0, r.FiveHour?.Utilization);
    }

    [Fact]
    public void UsageResponseSurvivesMalformedUnusedWindow()
    {
        // A malformed sub-window (null utilization) must not poison the whole response.
        var json = """
        {
          "five_hour": {"utilization": 42, "resets_at": "2026-06-09T18:00:00Z"},
          "seven_day": {"utilization": 80, "resets_at": "2026-06-12T10:00:00Z"},
          "seven_day_opus": {"utilization": null, "resets_at": null}
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(42.0, r.FiveHour?.Utilization);
        Assert.Equal(80.0, r.SevenDay?.Utilization);
        Assert.Null(r.SevenDayOpus);
    }

    [Fact]
    public void UsageResponseSurvivesWindowOfWrongType()
    {
        var json = """
        {
          "five_hour": {"utilization": 42, "resets_at": "2026-06-09T18:00:00Z"},
          "seven_day_sonnet": "unexpected-string"
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(42.0, r.FiveHour?.Utilization);
        Assert.Null(r.SevenDaySonnet);
    }

    [Fact]
    public void ExtraUsageDecodesDisabledState()
    {
        // Mirrors a live Team payload where usage credits ran out: is_enabled flips false
        // but disabled_reason/credits_ever_enabled say why. Decode-only — the popover
        // hides the section while is_enabled is false (a disabled-state row was tried
        // and deliberately removed as noise).
        var json = """
        {
          "extra_usage": {"is_enabled": false, "monthly_limit": null, "used_credits": null,
                          "utilization": null, "currency": "USD", "decimal_places": 2,
                          "disabled_reason": "out_of_credits", "user_disabled": false,
                          "spend_limit_reached": false, "credits_ever_enabled": true,
                          "daily": null, "weekly": null}
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.False(r.ExtraUsage?.IsEnabled);
        Assert.Equal("out_of_credits", r.ExtraUsage?.DisabledReason);
        Assert.False(r.ExtraUsage?.UserDisabled);
        Assert.True(r.ExtraUsage?.CreditsEverEnabled);
    }

    [Fact]
    public void ExtraUsageToleratesWrongTypedOptionalField()
    {
        // Per-field lenient decode: a shape change in a non-load-bearing field must not
        // null the whole ExtraUsage (which would silently hide the section).
        var json = """
        {
          "extra_usage": {"is_enabled": true, "monthly_limit": 50, "used_credits": 12.5,
                          "utilization": 25, "disabled_reason": {"code": 7}}
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.True(r.ExtraUsage?.IsEnabled);
        Assert.Equal(12.5, r.ExtraUsage?.UsedCredits);
        Assert.Null(r.ExtraUsage?.DisabledReason);
    }

    [Fact]
    public void UsageLimitToleratesWrongTypedOptionalField()
    {
        // A wrong-typed severity/is_active must degrade that field to null, not make the
        // per-element decode drop the whole entry (losing the model's row and pace bucket).
        var json = """
        {
          "limits": [
            {"kind": "weekly_scoped", "percent": 8, "severity": 3, "is_active": "yes",
             "resets_at": "2026-08-17T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(1, r.Limits?.Count);
        Assert.Null(r.Limits?.FirstOrDefault()?.Severity);
        Assert.Null(r.Limits?.FirstOrDefault()?.IsActive);
        Assert.Equal("Fable", r.ScopedModelWindows.FirstOrDefault()?.Label);
    }

    // MARK: - spend / locked_reason (decode-only, logged never displayed)

    [Fact]
    public void SpendAndLockedReasonDecodeFromLivePayloadShape()
    {
        // Shape observed live 2026-09-02 on a Max 5x org.
        var json = """
        {
          "five_hour": {"utilization": 12.0, "resets_at": "2026-09-02T12:10:00.294700+00:00",
                        "limit_dollars": null, "used_dollars": null, "remaining_dollars": null,
                        "locked_reason": null},
          "seven_day": {"utilization": 2.0, "resets_at": "2026-09-03T21:00:00.294739+00:00",
                        "locked_reason": "spend_limit_reached"},
          "extra_usage": {"is_enabled": false, "monthly_limit": null, "used_credits": null,
                          "utilization": null, "currency": null, "decimal_places": null,
                          "disabled_reason": null, "user_disabled": false,
                          "spend_limit_reached": false, "credits_ever_enabled": false,
                          "daily": null, "weekly": null},
          "spend": {"used": {"amount_minor": 1250, "currency": "USD", "exponent": 2},
                    "limit": null, "percent": 0, "severity": "normal", "enabled": false,
                    "disabled_reason": null, "cap": null, "balance": null, "auto_reload": null,
                    "disclaimer": "Usage credits cover you when you hit your plan limits.",
                    "can_purchase_credits": true, "can_toggle": true},
          "member_dashboard_available": false
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Null(r.FiveHour?.LockedReason);
        Assert.Equal("spend_limit_reached", r.SevenDay?.LockedReason);
        Assert.False(r.Spend?.Enabled);
        Assert.Equal(0.0, r.Spend?.Percent);
        Assert.Equal("normal", r.Spend?.Severity);
        Assert.Equal(1250L, r.Spend?.Used?.AmountMinor);
        Assert.Equal("USD", r.Spend?.Used?.Currency);
        Assert.Equal(2, r.Spend?.Used?.Exponent);
        Assert.True(r.Spend?.CanPurchaseCredits);
    }

    [Fact]
    public void SpendAndLockedReasonAbsentOnOlderPayloads()
    {
        var json = """
        {"five_hour": {"utilization": 5, "resets_at": null}}
        """;
        var r = UsageResponse.Parse(json);
        Assert.Null(r.FiveHour?.LockedReason);
        Assert.Null(r.Spend);
    }

    [Fact]
    public void WrongTypedLockedReasonDoesNotDropWindow()
    {
        var json = """
        {"five_hour": {"utilization": 5, "resets_at": null, "locked_reason": {"code": 1}}}
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(5.0, r.FiveHour?.Utilization);
        Assert.Null(r.FiveHour?.LockedReason);
    }

    [Fact]
    public void WrongTypedSpendFieldDegradesToNilNotWholeObject()
    {
        var json = """
        {"spend": {"enabled": "yes", "percent": 40, "severity": "warning", "used": 7}}
        """;
        var r = UsageResponse.Parse(json);
        Assert.NotNull(r.Spend);
        Assert.Null(r.Spend?.Enabled);
        Assert.Equal(40.0, r.Spend?.Percent);
        Assert.Equal("warning", r.Spend?.Severity);
        Assert.Null(r.Spend?.Used);
    }

    [Fact]
    public void UsageDiagnosticsEmptyForQuietLivePayload()
    {
        // Disabled spend that agrees with extra_usage, normal severity, no locks → nothing to log.
        var json = """
        {
          "five_hour": {"utilization": 12, "resets_at": null, "locked_reason": null},
          "extra_usage": {"is_enabled": false},
          "spend": {"enabled": false, "percent": 0, "severity": "normal"}
        }
        """;
        Assert.Equal("", UsageSignatures.UsageDiagnostics(UsageResponse.Parse(json)));
        Assert.Equal("", UsageSignatures.UsageDiagnostics(UsageResponse.Parse("{}")));
    }

    [Fact]
    public void UsageDiagnosticsReportsLocksSpendEnabledAndDisagreement()
    {
        var locked = """
        {"five_hour": {"utilization": 100, "resets_at": null, "locked_reason": "abuse"},
         "seven_day_sonnet": {"utilization": 1, "resets_at": null, "locked_reason": "paused"}}
        """;
        Assert.Equal("five_hour.locked=abuse seven_day_sonnet.locked=paused",
                     UsageSignatures.UsageDiagnostics(UsageResponse.Parse(locked)));

        var enabled = """
        {"extra_usage": {"is_enabled": true},
         "spend": {"enabled": true, "percent": 35.5, "severity": "normal",
                   "used": {"amount_minor": 1250, "currency": "USD", "exponent": 2}}}
        """;
        Assert.Equal("spend=enabled(pct=35.5,sev=normal,used=1250 USD e2,reason=nil)",
                     UsageSignatures.UsageDiagnostics(UsageResponse.Parse(enabled)));

        var disagree = """
        {"extra_usage": {"is_enabled": true},
         "spend": {"enabled": false, "severity": "normal", "disabled_reason": "out_of_credits"}}
        """;
        Assert.Equal("spend=disabled(pct=nil,sev=normal,used=nil,reason=out_of_credits) extra_usage.is_enabled=true",
                     UsageSignatures.UsageDiagnostics(UsageResponse.Parse(disagree)));

        var abnormal = """
        {"spend": {"enabled": false, "severity": "exceeded"}}
        """;
        Assert.Equal("spend=disabled(pct=nil,sev=exceeded,used=nil,reason=nil)",
                     UsageSignatures.UsageDiagnostics(UsageResponse.Parse(abnormal)));
    }

    // MARK: - Severity log signature

    [Fact]
    public void AbnormalSeveritiesEmptyWhenAllNormal()
    {
        var json = """
        {"limits": [
          {"kind": "session", "percent": 4, "severity": "normal", "is_active": true},
          {"kind": "weekly_all", "percent": 4}
        ]}
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal("", UsageSignatures.AbnormalSeverities(r.Limits));
        Assert.Equal("", UsageSignatures.AbnormalSeverities(null));
    }

    [Fact]
    public void AbnormalSeveritiesLabelsScopedModelsAndSortsOrderIndependently()
    {
        var jsonA = """
        {"limits": [
          {"kind": "weekly_scoped", "percent": 90, "severity": "warning", "is_active": true,
           "scope": {"model": {"display_name": "Fable"}}},
          {"kind": "session", "percent": 99, "severity": "exceeded", "is_active": false}
        ]}
        """;
        var jsonB = """
        {"limits": [
          {"kind": "session", "percent": 99, "severity": "exceeded", "is_active": false},
          {"kind": "weekly_scoped", "percent": 90, "severity": "warning", "is_active": true,
           "scope": {"model": {"display_name": "Fable"}}}
        ]}
        """;
        var a = UsageSignatures.AbnormalSeverities(UsageResponse.Parse(jsonA).Limits);
        var b = UsageSignatures.AbnormalSeverities(UsageResponse.Parse(jsonB).Limits);
        Assert.Equal("session=exceeded(active=false) weekly_scoped(Fable)=warning(active=true)", a);
        // A server-side reorder of the same entries must not read as a transition.
        Assert.Equal(a, b);
    }

    // MARK: - Limits array (model-scoped windows)

    [Fact]
    public void UsageResponseDecodesScopedModelLimits()
    {
        // Mirrors the live payload shape: the Fable-era API reports per-model usage in the
        // `limits` array (kind == "weekly_scoped") instead of the legacy seven_day_* fields.
        var json = """
        {
          "five_hour": {"utilization": 3, "resets_at": "2026-07-11T14:49:59.540253+00:00"},
          "seven_day": {"utilization": 0, "resets_at": "2026-07-16T20:59:59.540280+00:00"},
          "seven_day_sonnet": null,
          "limits": [
            {"kind": "session", "group": "session", "percent": 3, "severity": "normal",
             "resets_at": "2026-07-11T14:49:59.732320+00:00", "scope": null, "is_active": true},
            {"kind": "weekly_all", "group": "weekly", "percent": 0, "severity": "normal",
             "resets_at": "2026-07-16T20:59:59.732344+00:00", "scope": null, "is_active": false},
            {"kind": "weekly_scoped", "group": "weekly", "percent": 1, "severity": "normal",
             "resets_at": "2026-07-16T20:59:59.732614+00:00",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(3, r.Limits?.Count);
        Assert.Equal("normal", r.Limits?.FirstOrDefault()?.Severity);
        Assert.True(r.Limits?.FirstOrDefault()?.IsActive);
        Assert.False(r.Limits?.LastOrDefault()?.IsActive);
        var scoped = r.ScopedModelWindows;
        Assert.Single(scoped);
        Assert.Equal("Fable", scoped.FirstOrDefault()?.Label);
        Assert.Equal("scoped.Fable", scoped.FirstOrDefault()?.PaceKey);
        Assert.Equal(1.0, scoped.FirstOrDefault()?.Window.Utilization);
        Assert.NotNull(scoped.FirstOrDefault()?.Window.ResetsAtDate);
    }

    [Fact]
    public void UsageResponseDecodesLivePayload20260910()
    {
        // Verbatim /api/organizations/{id}/usage response captured 2026-09-10 (Max 5x org).
        // First live sighting of a non-"normal" severity and of an is_active == true entry
        // sitting on the highest-percent window; also carries the unmodeled top-level key
        // copper_kite that the parser must keep dropping silently (seven_day_breakdown is null here).
        var r = UsageResponse.Parse(Fixture.Text("live-usage-2026-09-10.json"));
        Assert.Equal(4.0, r.FiveHour?.Utilization);
        Assert.Equal(38.0, r.SevenDay?.Utilization);
        Assert.Null(r.SevenDaySonnet);
        Assert.False(r.ExtraUsage?.IsEnabled);
        Assert.Equal(3, r.Limits?.Count);
        var fable = r.ScopedModelWindows;
        Assert.Single(fable);
        Assert.Equal(75.0, fable.FirstOrDefault()?.Window.Utilization);
        Assert.Equal("warning", r.Limits?.LastOrDefault()?.Severity);
        Assert.True(r.Limits?.LastOrDefault()?.IsActive);
        Assert.Equal("weekly_scoped(Fable)=warning(active=true)", UsageSignatures.AbnormalSeverities(r.Limits));
        Assert.False(r.Spend?.Enabled);
        Assert.True(r.Spend?.CanPurchaseCredits);
        Assert.Equal("", UsageSignatures.UsageDiagnostics(r));
    }

    [Fact]
    public void UsageResponseDecodesLivePayload20260921()
    {
        // Verbatim /api/organizations/{id}/usage response captured 2026-09-21 (Max 5x org).
        // seven_day_breakdown is populated for the first time (per-surface share of the
        // weekly window: claude_code/chat/cowork/other) and four more null window keys
        // appeared (harbor_lantern, wattle_ember, cedar_ember, amber_gauge) — the latter are
        // unmodeled, so the parser must keep dropping them silently. is_active is again true
        // only on the highest-percent limit (scoped 6 > session 4 > weekly 3).
        var r = UsageResponse.Parse(Fixture.Text("live-usage-2026-09-21.json"));
        Assert.Equal(4.0, r.FiveHour?.Utilization);
        Assert.Equal(3.0, r.SevenDay?.Utilization);
        Assert.Null(r.SevenDaySonnet);
        Assert.False(r.ExtraUsage?.IsEnabled);
        Assert.Equal(3, r.Limits?.Count);
        Assert.Equal(new[] { false, false, true }, r.Limits?.Select(l => l.IsActive ?? false));
        var fable = r.ScopedModelWindows;
        Assert.Equal(new[] { "Fable" }, fable.Select(w => w.Label));
        Assert.Equal(6.0, fable.FirstOrDefault()?.Window.Utilization);
        Assert.Equal(new[] { "five_hour", "seven_day", "scoped.Fable" }, r.TrackedWindows.Select(w => w.Key));
        Assert.Equal("", UsageSignatures.AbnormalSeverities(r.Limits));
        Assert.False(r.Spend?.Enabled);
        Assert.Equal("", UsageSignatures.UsageDiagnostics(r));
        Assert.Equal(4, r.SevenDayBreakdown?.Rows?.Count);
        Assert.NotNull(r.SevenDayBreakdown?.AsOf);
        Assert.NotNull(r.SevenDayBreakdown?.WindowStartedAt);
        Assert.Equal("Claude Code", r.SevenDayBreakdown?.Rows?.FirstOrDefault()?.DisplayName);
        Assert.Equal("chat:0,claude_code:100,cowork:0,other:0", UsageSignatures.BreakdownSignature(r));
    }

    [Fact]
    public void SevenDayBreakdownNilOnOlderPayloadsAndWhenWrongTyped()
    {
        var old = UsageResponse.Parse(Fixture.Text("live-usage-2026-09-10.json"));
        Assert.Null(old.SevenDayBreakdown);
        Assert.Equal("", UsageSignatures.BreakdownSignature(old));
        Assert.Null(UsageResponse.Parse("{}").SevenDayBreakdown);

        // Wrong-typed fields degrade to null without dropping the object or the other rows.
        var json = """
        {"seven_day_breakdown": {"as_of": 12, "window_started_at": "2026-09-17T21:00:00Z",
          "rows": [{"key": "chat", "display_name": "Chats", "percent": "40"},
                   {"key": "claude_code", "percent": 60}]}}
        """;
        var r = UsageResponse.Parse(json);
        Assert.NotNull(r.SevenDayBreakdown);
        Assert.Null(r.SevenDayBreakdown?.AsOf);
        Assert.Equal("2026-09-17T21:00:00Z", r.SevenDayBreakdown?.WindowStartedAt);
        Assert.Equal(2, r.SevenDayBreakdown?.Rows?.Count);
        Assert.Null(r.SevenDayBreakdown?.Rows?.FirstOrDefault()?.Percent);
        Assert.Equal("chat:nil,claude_code:60", UsageSignatures.BreakdownSignature(r));
    }

    [Fact]
    public void ScopedModelWindowsSkipEntriesWithoutModelOrPercent()
    {
        var json = """
        {
          "limits": [
            {"kind": "weekly_scoped", "percent": 5, "scope": {"model": null}},
            {"kind": "weekly_scoped", "percent": null, "scope": {"model": {"id": null, "display_name": "Fable"}}},
            {"kind": "session", "percent": 3, "scope": null}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(3, r.Limits?.Count);
        Assert.Empty(r.ScopedModelWindows);
    }

    [Fact]
    public void ScopedModelWindowsOmitDuplicateOfLegacySonnetWindow()
    {
        // If the API ever ships both the legacy seven_day_sonnet window and a scoped
        // "Sonnet" limit, only the legacy row should render — no duplicate bars.
        var json = """
        {
          "seven_day_sonnet": {"utilization": 30, "resets_at": "2026-07-16T21:00:00Z"},
          "limits": [
            {"kind": "weekly_scoped", "percent": 30, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Sonnet"}}},
            {"kind": "weekly_scoped", "percent": 1, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(new[] { "Fable" }, r.ScopedModelWindows.Select(w => w.Label));
    }

    // MARK: - Tracked windows

    [Fact]
    public void TrackedWindowsListsBuiltInsThenScopedModelsFromLivePayload()
    {
        var r = UsageResponse.Parse(Fixture.Text("live-usage-2026-09-10.json"));
        var tracked = r.TrackedWindows;
        Assert.Equal(new[] { "five_hour", "seven_day", "scoped.Fable" }, tracked.Select(w => w.Key));
        Assert.Equal(new[] { false, false, true }, tracked.Select(w => w.IsModelScoped));
        Assert.Equal(new[] { false, true, true }, tracked.Select(w => w.IsSevenDay));
        Assert.Equal("7-Day Fable", tracked.LastOrDefault()?.Title);
        Assert.Equal(75.0, tracked.LastOrDefault()?.Window.Utilization);
        Assert.Equal(MenuBarWindow.FiveHour.Label(), tracked.FirstOrDefault()?.Title);
    }

    [Fact]
    public void TrackedWindowsKeepsLegacySonnetAndSkipsItsScopedDuplicate()
    {
        var json = """
        {
          "seven_day": {"utilization": 10, "resets_at": "2026-07-16T21:00:00Z"},
          "seven_day_sonnet": {"utilization": 30, "resets_at": "2026-07-16T21:00:00Z"},
          "limits": [
            {"kind": "weekly_scoped", "percent": 30, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Sonnet"}}},
            {"kind": "weekly_scoped", "percent": 1, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        var tracked = r.TrackedWindows;
        // No five_hour in the payload → it is simply absent, not a placeholder.
        Assert.Equal(new[] { "seven_day", "seven_day_sonnet", "scoped.Fable" }, tracked.Select(w => w.Key));
        Assert.Equal("7-Day Sonnet", tracked[1].Title);
        Assert.True(tracked[1].IsModelScoped);
        // The Charts tab's model sections come from the same list.
        Assert.Equal(new[] { "five_hour", "seven_day", "seven_day_sonnet", "scoped.Fable" },
                     Charts.SeriesFor(r).Select(s => s.Key));
    }

    // MARK: - Charts tab series

    [Fact]
    public void ChartSeriesKeepsBuiltInWindowsWithoutAResponse()
    {
        // Before the first fetch (or while one is failing) the Charts tab still renders the
        // built-in windows from persisted history — dropping them would blank real data.
        var series = Charts.SeriesFor(null);
        Assert.Equal(new[] { "five_hour", "seven_day" }, series.Select(s => s.Key));
        Assert.All(series, s => Assert.Null(s.Window));
        Assert.Equal(5 * 3600.0, series.FirstOrDefault()?.DurationSeconds);
        Assert.Equal(7 * 24 * 3600.0, series.LastOrDefault()?.DurationSeconds);
    }

    [Fact]
    public void ChartSeriesAppendsModelWindowsInDisplayOrder()
    {
        var json = """
        {
          "five_hour": {"utilization": 3, "resets_at": "2026-07-11T14:49:59Z"},
          "seven_day": {"utilization": 10, "resets_at": "2026-07-16T20:59:59Z"},
          "seven_day_sonnet": {"utilization": 30, "resets_at": "2026-07-16T21:00:00Z"},
          "limits": [
            {"kind": "weekly_scoped", "percent": 1, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}}}
          ]
        }
        """;
        var series = Charts.SeriesFor(UsageResponse.Parse(json));
        Assert.Equal(new[] { "five_hour", "seven_day", "seven_day_sonnet", "scoped.Fable" }, series.Select(s => s.Key));
        // Every section must resolve a live window, or its forecast chart never renders.
        Assert.All(series, s => Assert.NotNull(s.Window));
        Assert.Equal(1.0, series.LastOrDefault()?.Window?.Utilization);
        Assert.Equal(7 * 24 * 3600.0, series.LastOrDefault()?.DurationSeconds);
    }

    [Fact]
    public void ChartSeriesOmitsModelWindowsTheResponseDoesNotReport()
    {
        var json = """
        {"five_hour": {"utilization": 3, "resets_at": null}, "seven_day_sonnet": null, "limits": []}
        """;
        var series = Charts.SeriesFor(UsageResponse.Parse(json));
        Assert.Equal(new[] { "five_hour", "seven_day" }, series.Select(s => s.Key));
    }

    [Fact]
    public void LimitsDecodeIsPerElementFailSoft()
    {
        // One malformed entry must not wipe the whole array — the Fable row would
        // silently disappear otherwise.
        var json = """
        {
          "limits": [
            {"kind": 42},
            {"kind": "weekly_scoped", "percent": 1, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(new[] { "Fable" }, r.ScopedModelWindows.Select(w => w.Label));
    }

    [Fact]
    public void ScopedModelWindowsDedupeDuplicateLabels()
    {
        // The scope object also carries a `surface` axis, so two weekly_scoped entries
        // for the same model are plausible. Keep the max-percent one — a duplicate label
        // would collide on row identity and interleave two series into one pace bucket.
        var json = """
        {
          "limits": [
            {"kind": "weekly_scoped", "percent": 5, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": "a"}},
            {"kind": "weekly_scoped", "percent": 40, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": "b"}},
            {"kind": "weekly_scoped", "percent": 2, "resets_at": "2026-07-16T21:00:00Z",
             "scope": {"model": {"id": null, "display_name": "Haiku"}}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        var scoped = r.ScopedModelWindows;
        Assert.Equal(new[] { "Fable", "Haiku" }, scoped.Select(w => w.Label));
        Assert.Equal(40.0, scoped.FirstOrDefault()?.Window.Utilization);
    }

    [Fact]
    public void ScopedModelWindowsSkipEmptyDisplayName()
    {
        var json = """
        {
          "limits": [
            {"kind": "weekly_scoped", "percent": 5, "scope": {"model": {"id": null, "display_name": ""}}}
          ]
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Empty(r.ScopedModelWindows);
    }

    [Fact]
    public void UsageResponseSurvivesLimitsOfWrongType()
    {
        var json = """
        {
          "five_hour": {"utilization": 42, "resets_at": "2026-06-09T18:00:00Z"},
          "limits": "unexpected-string"
        }
        """;
        var r = UsageResponse.Parse(json);
        Assert.Equal(42.0, r.FiveHour?.Utilization);
        Assert.Null(r.Limits);
    }

    // MARK: - AccountInfo

    [Fact]
    public void AccountInfoDecodesMembershipsAndTier()
    {
        var json = """
        {
          "full_name": "Diego V",
          "email_address": "d@example.com",
          "memberships": [
            {"organization": {
              "uuid": "abc", "name": "Personal",
              "capabilities": ["chat", "claude_max"],
              "rate_limit_tier": "default_claude_max_5x"
            }}
          ]
        }
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("Diego V", info.DisplayName);
        Assert.Equal("d@example.com", info.EmailAddress);
        Assert.Equal("Max 5×", info.SubscriptionLabel);
    }

    [Fact]
    public void AccountInfoDerivesTeamLabelFromRavenCapability()
    {
        // Team/Enterprise orgs report the "raven" capability with raven_type
        // distinguishing the plan — mirrors a live Team-account payload.
        var json = """
        {
          "email_address": "d@company.com",
          "memberships": [
            {"organization": {
              "uuid": "abc", "name": "Trust Technologies",
              "capabilities": ["raven", "chat"],
              "rate_limit_tier": "default_raven",
              "raven_type": "team"
            }}
          ]
        }
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("Team", info.SubscriptionLabel);
    }

    [Fact]
    public void AccountInfoDerivesEnterpriseLabelFromRavenType()
    {
        var json = """
        {
          "email_address": "d@company.com",
          "memberships": [
            {"organization": {
              "capabilities": ["raven", "chat"],
              "rate_limit_tier": "default_raven",
              "raven_type": "enterprise"
            }}
          ]
        }
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("Enterprise", info.SubscriptionLabel);
    }

    [Fact]
    public void AccountInfoRavenWithoutTypeDefaultsToTeam()
    {
        var json = """
        {
          "email_address": "d@company.com",
          "memberships": [
            {"organization": {"capabilities": ["raven", "chat"], "rate_limit_tier": "default_raven"}}
          ]
        }
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("Team", info.SubscriptionLabel);
    }

    [Fact]
    public void AccountInfoDerivesFreeLabelFromBaseTier()
    {
        // Mirrors a live free-plan payload: bare "chat" capability on the base tier.
        var json = """
        {
          "email_address": "d@example.com",
          "memberships": [
            {"organization": {"capabilities": ["chat"], "rate_limit_tier": "default_claude_ai"}}
          ]
        }
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("Free", info.SubscriptionLabel);
    }

    [Fact]
    public void AccountInfoDecodesWithoutMemberships()
    {
        var json = """
        {"email_address": "d@example.com"}
        """;
        var info = AccountInfo.Parse(json);
        Assert.Equal("d@example.com", info.DisplayName);
        Assert.Null(info.SubscriptionLabel);
    }

    // MARK: - Account backward compatibility
    //
    // The Mac app keeps the roster as a Swift-encoded blob in its preferences; the Windows app
    // keeps it in accounts.json, written by AccountStore. These three tests port the Mac
    // suite's persistence rules against that file.

    [Fact]
    public void AccountDecodesLegacyRosterWithoutOptionalFields()
    {
        // Shape of a roster file written before email/subscriptionLabel/orgName existed:
        // the same row the Mac test decodes, in the Windows file's encoding (profileId for
        // the Mac's dataStoreIdentifier, addedAt as an ISO 8601 string — the same instant).
        var json = """
        [{
          "id": "11111111-2222-3333-4444-555555555555",
          "label": "Claude account",
          "profileId": "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
          "addedAt": "2023-03-08T20:26:40+00:00"
        }]
        """;
        using var dir = new TempDirectory();
        var store = new AccountStore(dir.Path);
        File.WriteAllText(store.AccountsPath, json);
        var accounts = store.LoadAccounts();
        Assert.Single(accounts);
        Assert.Equal("Claude account", accounts.FirstOrDefault()?.Label);
        Assert.Null(accounts.FirstOrDefault()?.Email);
        Assert.Null(accounts.FirstOrDefault()?.SubscriptionLabel);
        Assert.Null(accounts.FirstOrDefault()?.OrgName);
        // Legacy rows must decode with Pending == null — a non-null default would make
        // the launch-time reclamation pass delete real signed-in accounts.
        Assert.Null(accounts.FirstOrDefault()?.Pending);
    }

    [Fact]
    public void AccountPendingFlagRoundTrips()
    {
        var acct = new Account { Label = "New", Pending = true };
        using var dir = new TempDirectory();
        var store = new AccountStore(dir.Path);
        store.SaveAccounts([acct]);
        var decoded = store.LoadAccounts();
        Assert.True(decoded.FirstOrDefault()?.Pending);
    }

    [Fact]
    public void AccountRoundTripsThroughJSON()
    {
        var acct = new Account { Label = "Work", Email = "w@x.com", SubscriptionLabel = "Max 5×", OrgName = "Org" };
        using var dir = new TempDirectory();
        var store = new AccountStore(dir.Path);
        store.SaveAccounts([acct]);
        var decoded = store.LoadAccounts();
        Assert.Equal(new[] { acct }, decoded);
    }

    // MARK: - AccountStore round-trips

    [Fact]
    public void AccountStoreRoundTripsRoster()
    {
        var accounts = new[] { new Account { Label = "One" }, new Account { Label = "Two", Email = "two@x.com" } };
        using var dir = new TempDirectory();
        var store = new AccountStore(dir.Path);
        store.SaveAccounts(accounts);
        Assert.Equal(accounts, store.LoadAccounts());
    }

    [Fact]
    public void AccountStoreLoadsEmptyRosterWhenUnset()
    {
        using var dir = new TempDirectory();
        Assert.Empty(new AccountStore(dir.Path).LoadAccounts());
    }

    [Fact]
    public void AccountStoreRoundTripsActiveID()
    {
        using var dir = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var id = Guid.NewGuid();
        Accounts.SaveActiveId(settings, id);
        Assert.Equal(id, Accounts.LoadActiveId(settings));
        Accounts.SaveActiveId(settings, null);
        Assert.Null(Accounts.LoadActiveId(settings));
    }

    [Fact]
    public void AccountStoreIgnoresGarbageActiveID()
    {
        using var dir = new TempDirectory();
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Set(PrefKey.ActiveAccountId, "not-a-uuid");
        Assert.Null(Accounts.LoadActiveId(settings));
    }

    // MARK: - GitHub release parsing
    //
    // The Mac app installs from the release's .zip and its .zip.sig; the Windows app installs
    // from the asset named Updates.InstallerAssetName ("ClaudeTracker-Setup.exe") and its .sig,
    // matched by exact name. So the payloads below carry the installer where the Mac tests'
    // carry the zip. The test names are the Mac suite's: read "Zip" in them as "the installer".

    [Fact]
    public void ParseReleasesFindsNewerVersionWithZipAsset()
    {
        var json = """
        [
          {"tag_name": "v9.9.9",
           "html_url": "https://github.com/x/y/releases/tag/v9.9.9",
           "published_at": "2026-06-01T00:00:00Z",
           "assets": [
             {"name": "ClaudeTracker.dmg", "browser_download_url": "https://example.com/ClaudeTracker.dmg"},
             {"name": "ClaudeTracker-Setup.exe", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe"},
             {"name": "ClaudeTracker-Setup.exe.sig", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe.sig"}
           ]},
          {"tag_name": "v1.0.0",
           "html_url": "https://github.com/x/y/releases/tag/v1.0.0",
           "published_at": "2026-05-01T00:00:00Z",
           "assets": []}
        ]
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.20.0");
        Assert.Equal("9.9.9", update?.Version);
        Assert.Equal("https://example.com/ClaudeTracker-Setup.exe", update?.DownloadUrl?.AbsoluteUri);
        Assert.Equal("https://example.com/ClaudeTracker-Setup.exe.sig", update?.SignatureUrl?.AbsoluteUri);
        Assert.Equal("https://github.com/x/y/releases/tag/v9.9.9", update?.ReleaseUrl.AbsoluteUri);
        Assert.Equal(2, dates.Count);
    }

    [Fact]
    public void ParseReleasesReturnsNilWhenCurrentIsNewest()
    {
        var json = """
        [{"tag_name": "v1.0.0", "html_url": "https://github.com/x/y/releases/tag/v1.0.0",
          "published_at": "2026-05-01T00:00:00Z", "assets": []}]
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.20.0");
        Assert.Null(update);
        Assert.Single(dates);
    }

    [Fact]
    public void ParseReleasesSkipsPrereleaseAndDraft()
    {
        // A pre-release published for testing must never reach auto-update users.
        var json = """
        [
          {"tag_name": "v9.9.9", "html_url": "https://github.com/x/y/releases/tag/v9.9.9",
           "published_at": "2026-06-01T00:00:00Z", "prerelease": true, "assets": []},
          {"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
           "published_at": "2026-05-15T00:00:00Z", "draft": true, "assets": []},
          {"tag_name": "v2.0.0", "html_url": "https://github.com/x/y/releases/tag/v2.0.0",
           "published_at": "2026-05-01T00:00:00Z", "prerelease": false, "assets": []}
        ]
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("2.0.0", update?.Version);
        Assert.Equal(3, dates.Count);
    }

    [Fact]
    public void ParseReleasesToleratesRateLimitErrorPayload()
    {
        // GitHub returns an object (not an array) on 403 rate limits.
        var json = """
        {"message": "API rate limit exceeded", "documentation_url": "https://docs.github.com"}
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Null(update);
        Assert.Empty(dates);
    }

    [Fact]
    public void ParseReleasesHandlesUpdateWithoutZipAsset()
    {
        var json = """
        [{"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
          "published_at": "2026-05-01T00:00:00Z", "assets": []}]
        """;
        var (update, _) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("9.0.0", update?.Version);
        Assert.Null(update?.DownloadUrl);
    }

    [Fact]
    public void ParseReleasesOffersAnUnsignedZipOnlyAsAManualDownload()
    {
        // An unsigned installer can't be verified, so it is offered only as a manual download
        // (the release page), never installed in-app.
        var json = """
        [{"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
          "published_at": "2026-05-01T00:00:00Z",
          "assets": [{"name": "ClaudeTracker-Setup.exe", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe"}]}]
        """;
        var (update, _) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("9.0.0", update?.Version);
        Assert.Null(update?.DownloadUrl);
        Assert.Null(update?.SignatureUrl);
    }

    // MARK: - GitHub release parsing: rules only the Windows app has
    //
    // No Swift twins: the Mac app takes the first .zip on the newest release and reads any
    // newest tag as a version.

    [Fact]
    public void ParseReleasesDoesNotInstallFromTheMacZip()
    {
        // The installer is matched by exact name, never by extension: a release whose only
        // assets are the Mac app's .zip and .zip.sig has nothing Windows can install in-app.
        var json = """
        [{"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
          "published_at": "2026-05-01T00:00:00Z",
          "assets": [
            {"name": "ClaudeTracker.zip", "browser_download_url": "https://example.com/ClaudeTracker.zip"},
            {"name": "ClaudeTracker.zip.sig", "browser_download_url": "https://example.com/ClaudeTracker.zip.sig"}
          ]}]
        """;
        var (update, _) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("9.0.0", update?.Version);
        Assert.Null(update?.DownloadUrl);
        Assert.Null(update?.SignatureUrl);
    }

    [Fact]
    public void ParseReleasesIgnoresANewestTagThatIsNotAPlainVersion()
    {
        // Only a plain dotted version can be an update: compared as a string, a tag such as
        // "windows-v9.9.9" reads as newer than any version by its first letter.
        var json = """
        [{"tag_name": "windows-v9.9.9", "html_url": "https://github.com/x/y/releases/tag/windows-v9.9.9",
          "published_at": "2026-06-01T00:00:00Z",
          "assets": [
            {"name": "ClaudeTracker-Setup.exe", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe"},
            {"name": "ClaudeTracker-Setup.exe.sig", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe.sig"}
          ]}]
        """;
        var (update, dates) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Null(update);
        Assert.Single(dates);
    }

    [Fact]
    public void ParseReleasesNeedsTheInstallersOwnSignature()
    {
        // The signature is matched by exact name too (<installer>.sig). Beside the Mac .zip and
        // its .zip.sig, an installer without its own .sig is still unsigned: another asset's
        // signature must not make it installable in-app.
        var json = """
        [{"tag_name": "v9.0.0", "html_url": "https://github.com/x/y/releases/tag/v9.0.0",
          "published_at": "2026-05-01T00:00:00Z",
          "assets": [
            {"name": "ClaudeTracker.zip", "browser_download_url": "https://example.com/ClaudeTracker.zip"},
            {"name": "ClaudeTracker.zip.sig", "browser_download_url": "https://example.com/ClaudeTracker.zip.sig"},
            {"name": "ClaudeTracker-Setup.exe", "browser_download_url": "https://example.com/ClaudeTracker-Setup.exe"}
          ]}]
        """;
        var (update, _) = Updates.ParseGitHubReleases(json, "1.0.0");
        Assert.Equal("9.0.0", update?.Version);
        Assert.Null(update?.DownloadUrl);
        Assert.Null(update?.SignatureUrl);
    }

    // MARK: - Helpers

    /// <summary>A scratch folder for one test's AccountStore files, deleted when the test ends.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("claudetracker-tests-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
