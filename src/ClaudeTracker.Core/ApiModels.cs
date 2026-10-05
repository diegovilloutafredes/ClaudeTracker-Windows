using System.Globalization;
using System.Text.Json;

namespace ClaudeTracker.Core;

// Types for the claude.ai API payloads (/api/organizations/{id}/usage, /api/account,
// /api/organizations), plus the tracked-window enumeration and the log signatures derived
// from them. A port of the macOS app's APIModels.swift: the decoding rules below match its
// Codable implementations field for field, and the same fixture files test both.

/// <summary>A payload that cannot be decoded at all (the Mac app's <c>DecodingError</c>).</summary>
public sealed class ApiDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parses the ISO 8601 timestamps the API sends, with or without fractional seconds.</summary>
public static class IsoDate
{
    /// <summary>Null for null input or anything that is not a full date-time with a zone.</summary>
    /// <param name="allowFraction">False rejects fractional seconds, for sources that never send them.</param>
    public static DateTimeOffset? Parse(string? text, bool allowFraction = true)
    {
        if (text is null || text.Length < 20) return null;
        if (!allowFraction && text.Contains('.')) return null;
        // yyyy-MM-ddTHH:mm:ss[.fraction](Z|±HH:mm|±HHmm)
        static bool Digits(string s, int start, int count, out int value)
        {
            value = 0;
            if (start + count > s.Length) return false;
            for (var i = start; i < start + count; i++)
            {
                if (!char.IsAsciiDigit(s[i])) return false;
                value = value * 10 + (s[i] - '0');
            }
            return true;
        }
        if (!Digits(text, 0, 4, out var year) || text[4] != '-' || !Digits(text, 5, 2, out var month) || text[7] != '-'
            || !Digits(text, 8, 2, out var day) || text[10] is not ('T' or 't')
            || !Digits(text, 11, 2, out var hour) || text[13] != ':' || !Digits(text, 14, 2, out var minute)
            || text[16] != ':' || !Digits(text, 17, 2, out var second)) return null;

        var index = 19;
        long fractionTicks = 0;
        if (index < text.Length && text[index] == '.')
        {
            index++;
            var start = index;
            long scale = TimeSpan.TicksPerSecond;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                scale /= 10;
                fractionTicks += (text[index] - '0') * scale; // digits past 100 ns add nothing
                index++;
            }
            if (index == start) return null;
        }

        TimeSpan offset;
        if (index == text.Length - 1 && text[index] is 'Z' or 'z')
        {
            offset = TimeSpan.Zero;
        }
        else if (index < text.Length && text[index] is '+' or '-')
        {
            var sign = text[index] == '-' ? -1 : 1;
            if (!Digits(text, index + 1, 2, out var offsetHours)) return null;
            var minutesAt = index + 3;
            if (minutesAt < text.Length && text[minutesAt] == ':') minutesAt++;
            if (!Digits(text, minutesAt, 2, out var offsetMinutes) || minutesAt + 2 != text.Length) return null;
            offset = new TimeSpan(sign * offsetHours, sign * offsetMinutes, 0);
        }
        else
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset).AddTicks(fractionTicks);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Lenient field readers: a missing, null, or wrong-typed value reads as null.</summary>
internal static class Lenient
{
    public static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool? Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? Object(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
}

/// <summary>A single rate-limit window returned by the usage API.</summary>
/// <param name="Utilization">Percentage, 0–100+ (may slightly exceed 100 when overages are permitted).</param>
/// <param name="ResetsAt">ISO 8601 time of the next reset. Null right after a reset, while the server computes the new window.</param>
/// <param name="LockedReason">Why the window is locked, if the API says so. Logged, never displayed.</param>
public sealed record UsageWindow(double Utilization, string? ResetsAt, string? LockedReason = null)
{
    public DateTimeOffset? ResetsAtDate => IsoDate.Parse(ResetsAt);

    /// <summary>Utilization clamped to [0, 1] for progress bars.</summary>
    public double UtilizationFraction => Math.Min(Utilization / 100.0, 1.0);

    /// <summary>
    /// <c>utilization</c> and <c>resets_at</c> are strict (a wrong type drops the window);
    /// <c>locked_reason</c> is lenient so it can never cost the window its row.
    /// </summary>
    internal static UsageWindow? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number) return null;
        string? resetsAt = null;
        if (e.TryGetProperty("resets_at", out var resets) && resets.ValueKind != JsonValueKind.Null)
        {
            if (resets.ValueKind != JsonValueKind.String) return null;
            resetsAt = resets.GetString();
        }
        return new UsageWindow(utilization.GetDouble(), resetsAt, Lenient.String(e, "locked_reason"));
    }
}

/// <summary>The model a scoped limit applies to.</summary>
public sealed record UsageLimitModel(string? DisplayName);

/// <summary>The <c>scope</c> object of a <c>weekly_scoped</c> limit entry.</summary>
public sealed record UsageLimitScope(UsageLimitModel? Model)
{
    internal static UsageLimitScope? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("model", out var model) || model.ValueKind == JsonValueKind.Null) return new UsageLimitScope(Model: null);
        if (model.ValueKind != JsonValueKind.Object) return null;
        if (!model.TryGetProperty("display_name", out var name) || name.ValueKind == JsonValueKind.Null)
        {
            return new UsageLimitScope(new UsageLimitModel(null));
        }
        return name.ValueKind == JsonValueKind.String ? new UsageLimitScope(new UsageLimitModel(name.GetString())) : null;
    }
}

/// <summary>A single entry in the usage response's <c>limits</c> array.</summary>
/// <param name="Severity">Server-side urgency ("normal", "warning", "critical"). Logged only.</param>
/// <param name="IsActive">Semantics unclear — it is not "currently binding". Logged only.</param>
public sealed record UsageLimit(string Kind, double? Percent = null, string? ResetsAt = null, UsageLimitScope? Scope = null,
                                string? Severity = null, bool? IsActive = null)
{
    /// <summary>Only <c>kind</c> is load-bearing; any other wrong-typed field degrades to null.</summary>
    internal static UsageLimit? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
        return new UsageLimit(
            kind.GetString()!,
            Lenient.Number(e, "percent"),
            Lenient.String(e, "resets_at"),
            e.TryGetProperty("scope", out var scope) ? UsageLimitScope.TryParse(scope) : null,
            Lenient.String(e, "severity"),
            Lenient.Bool(e, "is_active"));
    }
}

/// <summary>A money amount as the <c>spend</c> object reports it: <c>amount_minor</c> scaled by 10^exponent.</summary>
public sealed record SpendAmount(long AmountMinor, string Currency, int Exponent)
{
    internal static SpendAmount? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("amount_minor", out var amount) || WholeNumber(amount) is not { } minor) return null;
        if (!e.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String) return null;
        if (!e.TryGetProperty("exponent", out var exponent) || WholeNumber(exponent) is not { } exp || exp is < int.MinValue or > int.MaxValue) return null;
        return new SpendAmount(minor, currency.GetString()!, (int)exp);
    }

    /// <summary>A JSON number with a whole value — written as <c>1250</c> or as <c>1250.0</c>.</summary>
    private static long? WholeNumber(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Number) return null;
        if (e.TryGetInt64(out var whole)) return whole;
        var value = e.GetDouble();
        return value == Math.Floor(value) && Math.Abs(value) < 9e15 ? (long)value : null;
    }
}

/// <summary>The top-level <c>spend</c> object: credit spend in minor currency units. Every field is lenient.</summary>
public sealed record Spend(bool? Enabled = null, double? Percent = null, string? Severity = null, string? DisabledReason = null,
                           SpendAmount? Used = null, bool? CanPurchaseCredits = null)
{
    internal static Spend? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return new Spend(
            Lenient.Bool(e, "enabled"),
            Lenient.Number(e, "percent"),
            Lenient.String(e, "severity"),
            Lenient.String(e, "disabled_reason"),
            e.TryGetProperty("used", out var used) ? SpendAmount.TryParse(used) : null,
            Lenient.Bool(e, "can_purchase_credits"));
    }
}

/// <summary>One surface row of <c>seven_day_breakdown</c>.</summary>
public sealed record BreakdownRow(string? Key, string? DisplayName, double? Percent);

/// <summary>
/// The top-level <c>seven_day_breakdown</c> object: how the weekly window's usage splits across
/// surfaces. Reads as a share of usage, not a utilization. Every field is lenient.
/// </summary>
public sealed record SevenDayBreakdown(string? AsOf, string? WindowStartedAt, IReadOnlyList<BreakdownRow>? Rows)
{
    internal static SevenDayBreakdown? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        List<BreakdownRow>? rows = null;
        if (e.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            rows = [];
            foreach (var row in rowsElement.EnumerateArray())
            {
                // One row that is not an object fails the whole array, as it does in the Mac app.
                if (row.ValueKind != JsonValueKind.Object) { rows = null; break; }
                rows.Add(new BreakdownRow(Lenient.String(row, "key"), Lenient.String(row, "display_name"), Lenient.Number(row, "percent")));
            }
        }
        return new SevenDayBreakdown(Lenient.String(e, "as_of"), Lenient.String(e, "window_started_at"), rows);
    }
}

/// <summary>Pay-as-you-go credit usage, present when the account has extra usage enabled.</summary>
public sealed record ExtraUsage(bool IsEnabled, double? MonthlyLimit = null, double? UsedCredits = null, double? Utilization = null,
                                string? DisabledReason = null, bool? UserDisabled = null, bool? CreditsEverEnabled = null)
{
    /// <summary>Only <c>is_enabled</c> is load-bearing; any other wrong-typed field degrades to null.</summary>
    internal static ExtraUsage? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Lenient.Bool(e, "is_enabled") is not { } isEnabled) return null;
        return new ExtraUsage(
            isEnabled,
            Lenient.Number(e, "monthly_limit"),
            Lenient.Number(e, "used_credits"),
            Lenient.Number(e, "utilization"),
            Lenient.String(e, "disabled_reason"),
            Lenient.Bool(e, "user_disabled"),
            Lenient.Bool(e, "credits_ever_enabled"));
    }
}

/// <summary>A model-scoped weekly limit surfaced as a displayable usage window.</summary>
/// <param name="Label">The model's display name as reported by the API (e.g. "Fable").</param>
public sealed record ScopedModelWindow(string Label, UsageWindow Window)
{
    /// <summary>History-bucket key for pace tracking, distinct from the legacy window keys.</summary>
    public string PaceKey => "scoped." + Label;
}

/// <summary>
/// One rate-limit window the app tracks: the key its pace/history buckets use, its localized
/// row title, and the window itself. The single enumeration behind reset detection, pace
/// history, pace alerts, chart snapshots, and the popover rows.
/// </summary>
/// <param name="Key">"five_hour", "seven_day", "seven_day_sonnet", or "scoped.&lt;model&gt;".</param>
/// <param name="IsModelScoped">True for the legacy Sonnet sub-window and model-scoped limits.</param>
public sealed record TrackedWindow(string Key, string Title, UsageWindow Window, bool IsModelScoped)
{
    /// <summary>Every window but the 5-hour one spans a week.</summary>
    public bool IsSevenDay => Key != MenuBarWindow.FiveHour.RawValue();

    /// <summary>The localized row title for a window key.</summary>
    public static string TitleForKey(string key)
    {
        if (MenuBarWindows.FromRawValue(key) is { } builtIn) return builtIn.Label();
        if (key == "seven_day_sonnet") return L.T("7-Day Sonnet");
        var model = key.StartsWith("scoped.", StringComparison.Ordinal) ? key["scoped.".Length..] : key;
        return L.F("7-Day %@", model);
    }

    /// <summary>
    /// A window that left the response, rebuilt from its key so its reset can still be titled
    /// and gated like the live one.
    /// </summary>
    public static TrackedWindow Vanished(string key) =>
        new(key, TitleForKey(key), new UsageWindow(0, null), MenuBarWindows.FromRawValue(key) is null);
}

/// <summary>Response payload from the <c>/api/organizations/{id}/usage</c> endpoint.</summary>
/// <param name="Limits">The newer, generalized limit list. Model-scoped weekly limits (e.g. Fable) only appear here.</param>
/// <param name="Spend">Mirrors <paramref name="ExtraUsage"/>. Decoded so it can be learned from the log — never displayed.</param>
/// <param name="SevenDayBreakdown">Per-surface split of the weekly window. Logged, never displayed.</param>
public sealed record UsageResponse(UsageWindow? FiveHour, UsageWindow? SevenDay, UsageWindow? SevenDayOpus, UsageWindow? SevenDaySonnet,
                                   ExtraUsage? ExtraUsage, IReadOnlyList<UsageLimit>? Limits = null, Spend? Spend = null,
                                   SevenDayBreakdown? SevenDayBreakdown = null)
{
    /// <summary>
    /// Fail-soft decoding: a malformed sub-window must not poison the whole response, so each
    /// one decodes independently and a failed one becomes null. <c>limits</c> entries are
    /// additionally fail-soft per element.
    /// </summary>
    /// <exception cref="ApiDecodeException">The payload is not a JSON object.</exception>
    public static UsageResponse Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new ApiDecodeException("usage payload is not valid JSON", e); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ApiDecodeException("usage payload is not a JSON object");

            UsageWindow? Window(string name) => root.TryGetProperty(name, out var e) ? UsageWindow.TryParse(e) : null;
            List<UsageLimit>? limits = null;
            if (root.TryGetProperty("limits", out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Array)
            {
                limits = [];
                foreach (var entry in limitsElement.EnumerateArray())
                {
                    if (UsageLimit.TryParse(entry) is { } limit) limits.Add(limit);
                }
            }
            return new UsageResponse(
                Window("five_hour"), Window("seven_day"), Window("seven_day_opus"), Window("seven_day_sonnet"),
                root.TryGetProperty("extra_usage", out var extra) ? ExtraUsage.TryParse(extra) : null,
                limits,
                root.TryGetProperty("spend", out var spend) ? Spend.TryParse(spend) : null,
                root.TryGetProperty("seven_day_breakdown", out var breakdown) ? SevenDayBreakdown.TryParse(breakdown) : null);
        }
    }

    /// <summary>The two built-in windows (5-hour, 7-day) that are present, in display order.</summary>
    public IReadOnlyList<(MenuBarWindow Kind, UsageWindow Window)> AllWindows
    {
        get
        {
            var result = new List<(MenuBarWindow, UsageWindow)>(2);
            if (FiveHour is { } five) result.Add((MenuBarWindow.FiveHour, five));
            if (SevenDay is { } seven) result.Add((MenuBarWindow.SevenDay, seven));
            return result;
        }
    }

    /// <summary>
    /// Every window the app tracks, in display order: the built-in 5-hour and 7-day windows,
    /// the legacy Sonnet sub-window, then each model-scoped weekly limit.
    /// </summary>
    public IReadOnlyList<TrackedWindow> TrackedWindows
    {
        get
        {
            var result = AllWindows.Select(w => new TrackedWindow(w.Kind.RawValue(), w.Kind.Label(), w.Window, false)).ToList();
            if (SevenDaySonnet is { } sonnet)
            {
                result.Add(new TrackedWindow("seven_day_sonnet", TrackedWindow.TitleForKey("seven_day_sonnet"), sonnet, true));
            }
            foreach (var scoped in ScopedModelWindows)
            {
                result.Add(new TrackedWindow(scoped.PaceKey, TrackedWindow.TitleForKey(scoped.PaceKey), scoped.Window, true));
            }
            return result;
        }
    }

    /// <summary>
    /// Model-scoped weekly limits (e.g. "Fable") from the <c>limits</c> array, as displayable
    /// windows. A model that already renders via its legacy sub-window (Sonnet) is skipped, and
    /// duplicate labels collapse to the max-percent entry — a duplicate would interleave two
    /// series into one pace-history bucket.
    /// </summary>
    public IReadOnlyList<ScopedModelWindow> ScopedModelWindows
    {
        get
        {
            var result = new List<ScopedModelWindow>();
            var indexByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var limit in Limits ?? [])
            {
                if (limit.Kind != "weekly_scoped") continue;
                if (limit.Scope?.Model?.DisplayName is not { Length: > 0 } label || limit.Percent is not { } percent) continue;
                if (SevenDaySonnet is not null && label == "Sonnet") continue;
                var window = new ScopedModelWindow(label, new UsageWindow(percent, limit.ResetsAt));
                if (indexByLabel.TryGetValue(label, out var index))
                {
                    if (percent > result[index].Window.Utilization) result[index] = window;
                }
                else
                {
                    indexByLabel[label] = result.Count;
                    result.Add(window);
                }
            }
            return result;
        }
    }
}

/// <summary>A claude.ai organization, used only to extract the UUID for usage API calls.</summary>
public sealed record Organization(string Uuid, string Name)
{
    /// <exception cref="ApiDecodeException">The payload is not an array of objects with string <c>uuid</c> and <c>name</c>.</exception>
    public static IReadOnlyList<Organization> ParseList(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new ApiDecodeException("organizations payload is not valid JSON", e); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ApiDecodeException("organizations payload is not an array");
            var result = new List<Organization>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Lenient.String(e, "uuid") is not { } uuid || Lenient.String(e, "name") is not { } name)
                {
                    throw new ApiDecodeException("organization entry lacks a string uuid or name");
                }
                result.Add(new Organization(uuid, name));
            }
            return result;
        }
    }
}

/// <summary>Organization-level fields used to infer subscription tier.</summary>
/// <param name="RavenType">Plan flavor for "raven" (Team/Enterprise) orgs, e.g. "team".</param>
public sealed record AccountOrganization(IReadOnlyList<string>? Capabilities, string? RateLimitTier, string? RavenType = null);

public sealed record AccountMembership(AccountOrganization Organization);

/// <summary>Account profile returned by <c>/api/account</c>.</summary>
/// <param name="Memberships">Organization memberships; only the first entry determines the subscription tier.</param>
public sealed record AccountInfo(string? FullName, string EmailAddress, IReadOnlyList<AccountMembership>? Memberships)
{
    public string DisplayName => FullName ?? EmailAddress;

    /// <summary>
    /// Human-readable plan label derived from <c>capabilities</c> and <c>rate_limit_tier</c> —
    /// the API exposes no dedicated tier field. Null for unrecognised accounts. This is the
    /// canonical English value: the roster persists it and code compares it, so only display
    /// code localizes it.
    /// </summary>
    public string? SubscriptionLabel
    {
        get
        {
            if (Memberships is not { Count: > 0 } memberships) return null;
            var org = memberships[0].Organization;
            var caps = new HashSet<string>(org.Capabilities ?? [], StringComparer.Ordinal);
            var tier = org.RateLimitTier ?? "";
            if (caps.Contains("claude_max"))
            {
                if (tier.Contains("20x", StringComparison.Ordinal)) return "Max 20×";
                if (tier.Contains("5x", StringComparison.Ordinal)) return "Max 5×";
                return "Max";
            }
            if (caps.Contains("claude_pro") || tier.Contains("_pro", StringComparison.Ordinal)) return "Pro";
            if (caps.Contains("claude_team")) return "Team";
            if (caps.Contains("claude_enterprise")) return "Enterprise";
            // Team/Enterprise orgs report the "raven" capability; raven_type distinguishes the plan.
            if (caps.Contains("raven"))
            {
                return org.RavenType?.Contains("enterprise", StringComparison.Ordinal) == true ? "Enterprise" : "Team";
            }
            // Free accounts: "chat" capability on the base tier.
            if (caps.Contains("chat") && tier == "default_claude_ai") return "Free";
            return null;
        }
    }

    /// <exception cref="ApiDecodeException">A required field is missing, or a present field has the wrong type.</exception>
    public static AccountInfo Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new ApiDecodeException("account payload is not valid JSON", e); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ApiDecodeException("account payload is not a JSON object");
            if (Lenient.String(root, "email_address") is not { } email) throw new ApiDecodeException("account payload lacks email_address");

            List<AccountMembership>? memberships = null;
            if (Present(root, "memberships", out var membershipsElement))
            {
                if (membershipsElement.ValueKind != JsonValueKind.Array) throw new ApiDecodeException("memberships is not an array");
                memberships = [];
                foreach (var m in membershipsElement.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || Lenient.Object(m, "organization") is not { } org)
                    {
                        throw new ApiDecodeException("membership lacks an organization object");
                    }
                    List<string>? capabilities = null;
                    if (Present(org, "capabilities", out var caps))
                    {
                        if (caps.ValueKind != JsonValueKind.Array) throw new ApiDecodeException("capabilities is not an array");
                        capabilities = [];
                        foreach (var c in caps.EnumerateArray())
                        {
                            if (c.ValueKind != JsonValueKind.String) throw new ApiDecodeException("capability is not a string");
                            capabilities.Add(c.GetString()!);
                        }
                    }
                    memberships.Add(new AccountMembership(new AccountOrganization(
                        capabilities, OptionalString(org, "rate_limit_tier"), OptionalString(org, "raven_type"))));
                }
            }
            return new AccountInfo(OptionalString(root, "full_name"), email, memberships);
        }
    }

    private static bool Present(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>Absent or null reads as null; a present value of another type is an error.</summary>
    private static string? OptionalString(JsonElement obj, string name)
    {
        if (!Present(obj, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ApiDecodeException($"{name} is not a string");
    }
}

/// <summary>Log signatures for API fields that are decoded to be learned from, not displayed.</summary>
public static class UsageSignatures
{
    /// <summary>
    /// Signature of the non-"normal" entries in a <c>limits</c> array, e.g.
    /// <c>weekly_scoped(Fable)=warning(active=true)</c>. Sorted so a server-side reorder never
    /// looks like a transition. Empty when every entry is "normal".
    /// </summary>
    public static string AbnormalSeverities(IReadOnlyList<UsageLimit>? limits) =>
        string.Join(" ", (limits ?? [])
            .Where(l => (l.Severity ?? "normal") != "normal")
            .Select(l =>
            {
                var model = l.Scope?.Model?.DisplayName ?? "";
                var kind = model.Length == 0 ? l.Kind : $"{l.Kind}({model})";
                return $"{kind}={l.Severity ?? ""}(active={Bool(l.IsActive ?? false)})";
            })
            .Order(StringComparer.Ordinal));

    /// <summary>
    /// Signature of the not-yet-understood fields: any window with a non-null
    /// <c>locked_reason</c>, and the <c>spend</c> object whenever it is enabled, carries a
    /// non-"normal" severity, or disagrees with <c>extra_usage.is_enabled</c>. Empty for the
    /// quiet shape observed live, so the log stays silent until something changes.
    /// </summary>
    public static string UsageDiagnostics(UsageResponse r)
    {
        var parts = new List<string>();
        (string, UsageWindow?)[] windows =
        [
            ("five_hour", r.FiveHour), ("seven_day", r.SevenDay),
            ("seven_day_opus", r.SevenDayOpus), ("seven_day_sonnet", r.SevenDaySonnet),
        ];
        foreach (var (key, window) in windows)
        {
            if (window?.LockedReason is { } reason) parts.Add($"{key}.locked={reason}");
        }
        if (r.Spend is { } spend)
        {
            var enabled = spend.Enabled ?? false;
            var extraEnabled = r.ExtraUsage?.IsEnabled;
            var abnormal = (spend.Severity ?? "normal") != "normal";
            var disagrees = extraEnabled is { } extra && extra != enabled;
            if (enabled || abnormal || disagrees)
            {
                var used = spend.Used is { } u ? $"{u.AmountMinor} {u.Currency} e{u.Exponent}" : "nil";
                parts.Add($"spend={(enabled ? "enabled" : "disabled")}"
                          + $"(pct={(spend.Percent is { } p ? SwiftDouble(p) : "nil")}"
                          + $",sev={spend.Severity ?? "nil"}"
                          + $",used={used}"
                          + $",reason={spend.DisabledReason ?? "nil"})");
                if (disagrees) parts.Add($"extra_usage.is_enabled={Bool(extraEnabled ?? false)}");
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Log signature of <c>seven_day_breakdown</c>: <c>key:percent</c> per row, sorted by key.
    /// <c>as_of</c> is excluded (it changes on every response). Empty when absent.
    /// </summary>
    public static string BreakdownSignature(UsageResponse r) =>
        string.Join(",", (r.SevenDayBreakdown?.Rows ?? [])
            .Where(row => row.Key is not null)
            .Select(row => $"{row.Key}:{(row.Percent is { } p ? p.ToString("F0", CultureInfo.InvariantCulture) : "nil")}")
            .Order(StringComparer.Ordinal));

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// A double as Swift prints it, so both apps write the same log line: whole values keep
    /// ".0", and the exponent of very large or small values is lower-case ("1e-05").
    /// </summary>
    private static string SwiftDouble(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e16
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
}
