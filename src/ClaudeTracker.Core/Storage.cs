using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTracker.Core;

/// <summary>
/// Keys for every persisted preference. Never write a key as a string literal — a single
/// namespace keeps a typo from silently desyncing the write and read sides. The names match the
/// macOS app's <c>PrefKey</c>, so the two apps describe a setting the same way.
/// </summary>
public static class PrefKey
{
    /// <summary>Stores <see cref="MenuBarDisplay"/>; the key name predates the "Highest usage" option.</summary>
    public const string MenuBarWindow = "menuBarWindow";
    public const string Notify5Hour = "notify5Hour";
    public const string Notify7Day = "notify7Day";
    public const string NotifyToast = "notifyToast";
    public const string NotifySound = "notifySound";
    public const string ToastDuration = "toastDuration";
    public const string ToastPermanent = "toastPermanent";
    public const string ShowPace = "showPace";
    public const string ShowPaceMenuBar = "showPaceMenuBar";
    public const string PaceRateUnit = "paceRateUnit";
    public const string NotifyPace = "notifyPace";
    public const string PaceWarningMinutes = "paceWarningMinutes";
    public const string PaceToastEnabled = "paceToastEnabled";
    public const string PaceToastDuration = "paceToastDuration";
    public const string PaceToastPermanent = "paceToastPermanent";
    public const string PaceSoundEnabled = "paceSoundEnabled";
    public const string PopupScale = "popupScale";
    public const string ShowChartsTab = "showChartsTab";
    public const string SelectedTab = "selectedTab";
    public const string ChartTimeRange = "chartTimeRange";
    /// <summary>
    /// Charts tab content filter. Series visibility is stored as the <em>hidden</em> set so a
    /// model-scoped window the API starts reporting shows up without the user opting in.
    /// </summary>
    public const string ChartHiddenSeries = "chartHiddenSeries";
    public const string ChartShowUtilization = "chartShowUtilization";
    public const string ChartShowPace = "chartShowPace";
    public const string ChartShowForecast = "chartShowForecast";
    /// <summary>
    /// Gates the per-model rows of the Usage tab and their reset/pace alerts. The stored name
    /// predates the scoped rows; it is kept so both apps use the same key.
    /// </summary>
    public const string ShowModelWindows = "showSonnetWindow";
    public const string Use24HourTime = "use24HourTime";
    public const string AutoUpdate = "autoUpdate";
    public const string LaunchAtLogin = "launchAtLogin";
    public const string LastNotifiedUpdateVersion = "lastNotifiedUpdateVersion";
    public const string UpdateCheckInterval = "updateCheckInterval";
    /// <summary>The release whose install last failed, and how many times in a row (auto-install retry cap).</summary>
    public const string FailedInstallVersion = "failedInstallVersion";
    public const string FailedInstallCount = "failedInstallCount";
    public const string ActiveAccountId = "activeAccountID";
}

/// <summary>Writes a file so a crash mid-write leaves either the old content or the new, never a torn file.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var staging = path + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, path, overwrite: true); // same-volume rename: the commit point
    }
}

/// <summary>
/// The two ways a saved file can fail to load are different, and must be handled differently:
/// a file that reads but does not parse is corrupt — it is copied to a <c>.corrupt</c> sibling
/// and the app starts over; a file that cannot be read at all (locked by a backup or sync tool,
/// access denied) is probably fine — it must be left alone, and above all never overwritten.
/// </summary>
internal static class StorageFile
{
    /// <summary>Reads a file, riding out a lock that lasts a moment.</summary>
    /// <exception cref="IOException">Still unreadable after a few tries.</exception>
    /// <exception cref="UnauthorizedAccessException">Access denied — not something a retry fixes.</exception>
    public static byte[] ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
        }
    }

    /// <summary>Copies an undecodable file beside itself as <c>.corrupt</c>. False when even that failed.</summary>
    public static bool PreserveCorrupt(string path)
    {
        try
        {
            File.Copy(path, path + ".corrupt", overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Outcome(string path, bool preserved) =>
        preserved ? $"file preserved as {Path.GetFileName(path)}.corrupt" : "and it could NOT be copied aside";
}

/// <summary>
/// Preferences in one JSON file of flat keys (the Windows stand-in for <c>UserDefaults</c>).
/// Values are booleans, numbers, or strings. A file that fails to parse is preserved beside
/// itself as <c>.corrupt</c> before the first save can overwrite it; a file that cannot be read
/// makes the store run on defaults without ever saving (<see cref="IsReadOnly"/>).
/// </summary>
public sealed class SettingsStore
{
    private readonly string path;
    private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>True when the settings file exists but could not be read: nothing is saved, so it stays as it is.</summary>
    public bool IsReadOnly { get; }

    public SettingsStore(string path, Action<string>? logError = null)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        byte[] bytes;
        try
        {
            bytes = StorageFile.ReadWithRetry(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            IsReadOnly = true;
            logError?.Invoke($"settings could not be read — running on defaults and saving nothing, so the file is left untouched: {e.Message}");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("root is not an object");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.True: values[property.Name] = true; break;
                    case JsonValueKind.False: values[property.Name] = false; break;
                    case JsonValueKind.Number: values[property.Name] = property.Value.GetDouble(); break;
                    case JsonValueKind.String: values[property.Name] = property.Value.GetString()!; break;
                }
            }
        }
        catch (JsonException e)
        {
            values.Clear();
            var preserved = StorageFile.PreserveCorrupt(path);
            logError?.Invoke($"settings decode failed — {StorageFile.Outcome(path, preserved)}: {e.Message}");
        }
    }

    public bool Contains(string key) { lock (gate) return values.ContainsKey(key); }

    public bool? GetBool(string key) { lock (gate) return values.TryGetValue(key, out var v) && v is bool b ? b : null; }

    public double? GetDouble(string key) { lock (gate) return values.TryGetValue(key, out var v) && v is double d ? d : null; }

    public int? GetInt(string key) => GetDouble(key) is { } d ? (int)d : null;

    public string? GetString(string key) { lock (gate) return values.TryGetValue(key, out var v) ? v as string : null; }

    public void Set(string key, bool value) => Store(key, value);

    public void Set(string key, double value) => Store(key, value);

    public void Set(string key, int value) => Store(key, (double)value);

    public void Set(string key, string value) => Store(key, value);

    public void Remove(string key)
    {
        lock (gate)
        {
            if (values.Remove(key)) Save();
        }
    }

    private void Store(string key, object value)
    {
        lock (gate)
        {
            if (values.TryGetValue(key, out var existing) && existing.Equals(value)) return;
            values[key] = value;
            Save();
        }
    }

    private void Save()
    {
        if (IsReadOnly) return;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                switch (value)
                {
                    case bool b: writer.WriteBoolean(key, b); break;
                    case double d: writer.WriteNumber(key, d); break;
                    case string s: writer.WriteString(key, s); break;
                }
            }
            writer.WriteEndObject();
        }
        AtomicFile.WriteAllBytes(path, stream.ToArray());
    }
}

/// <summary>
/// A locally tracked Claude account. Each account is backed by its own WebView2 profile, so
/// cookies (including <c>sessionKey</c>) never collide.
///
/// <see cref="Label"/>, <see cref="Email"/>, and <see cref="SubscriptionLabel"/> are filled from
/// <c>/api/account</c> after the first successful fetch. New persisted fields must be optional:
/// an older roster has to keep loading.
///
/// The four fields marked required are the ones a saved row cannot do without. A row missing
/// one makes the whole roster fail to load (and be preserved as corrupt) — the defaults below
/// are for new accounts only, and silently minting a fresh id or profile for a damaged row
/// would detach it from its real session and history on every launch.
/// </summary>
public sealed record Account
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public string Label { get; init; } = "";
    public string? Email { get; init; }
    public string? SubscriptionLabel { get; init; }
    public string? OrgName { get; init; }
    /// <summary>Names the WebView2 profile holding this account's session (the Mac app's <c>dataStoreIdentifier</c>).</summary>
    [JsonRequired]
    public Guid ProfileId { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// True while the account is a placeholder awaiting its first sign-in. Rows still true at
    /// launch were abandoned by a quit or crash with the sign-in window open, and are reclaimed.
    /// </summary>
    public bool? Pending { get; init; }

    /// <summary>
    /// Team or Enterprise, whose org name Settings shows. Reads the canonical English label, so
    /// views must not compare the (localized) label themselves.
    /// </summary>
    [JsonIgnore]
    public bool IsOrganizationPlan => SubscriptionLabel is "Team" or "Enterprise";

    /// <summary>The WebView2 profile name for this account (letters, digits and "-" only, well under the 64-character limit).</summary>
    [JsonIgnore]
    public string ProfileName => Accounts.ProfilePrefix + ProfileId.ToString("N");
}

/// <summary>
/// What a roster becomes when a sign-in turns out to be an account it already had.
/// </summary>
/// <param name="Roster">The roster without the row that was added for the sign-in.</param>
/// <param name="Kept">The row that was already there, now holding the new session's profile.</param>
/// <param name="AbandonedProfile">The profile that row had before — the session it gave up — to be deleted.</param>
public sealed record DuplicateMerge(IReadOnlyList<Account> Roster, Account Kept, string AbandonedProfile);

public static class Accounts
{
    public const string ProfilePrefix = "acct-";

    /// <summary>
    /// The account profiles in <paramref name="existingProfileNames"/> that no roster entry owns,
    /// in that order — left behind if the app died between removing an account and deleting its
    /// profile. A pending placeholder is in the roster, so its profile is kept. Profiles the app
    /// did not create are never listed.
    /// </summary>
    public static IReadOnlyList<string> OrphanedProfiles(IEnumerable<string> existingProfileNames, IEnumerable<Account> roster)
    {
        var owned = roster.Select(a => a.ProfileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return existingProfileNames
            .Where(name => name.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase) && !owned.Contains(name))
            .ToList();
    }

    /// <summary>
    /// Finds out whether the row that just signed in is an account the roster already had, and
    /// if so what the roster becomes. Null when it is not: nothing to do.
    ///
    /// Two rows are the same account when their email is the same, whatever its letter case —
    /// which is all a sign-in is told apart by. The question is asked once per row, when its
    /// email is first learned (<paramref name="email"/> comes from the account profile fetched
    /// after sign-in); a row that already knew its email has merely been signed in again.
    ///
    /// The row that was already there wins. It keeps its id, and with it its name and its chart
    /// history, and takes over the profile of the row that just signed in: the session made a
    /// moment ago, certainly valid, where its own may have expired. The new row disappears.
    /// </summary>
    public static DuplicateMerge? MergeDuplicate(IReadOnlyList<Account> roster, Guid signedInId, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        if (roster.FirstOrDefault(a => a.Id == signedInId) is not { Email: null } signedIn) return null;
        var existing = roster.FirstOrDefault(a =>
            a.Id != signedInId && string.Equals(a.Email?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;
        var kept = existing with { ProfileId = signedIn.ProfileId };
        return new DuplicateMerge(
            roster.Where(a => a.Id != signedInId).Select(a => a.Id == existing.Id ? kept : a).ToList(),
            kept,
            existing.ProfileName);
    }

    /// <summary>The saved active account, or null when none is saved or the saved value is not an id.</summary>
    public static Guid? LoadActiveId(SettingsStore settings) =>
        Guid.TryParse(settings.GetString(PrefKey.ActiveAccountId), out var id) ? id : null;

    /// <summary>Saves the active account; null forgets it.</summary>
    public static void SaveActiveId(SettingsStore settings, Guid? id)
    {
        if (id is { } value) settings.Set(PrefKey.ActiveAccountId, value.ToString("D"));
        else settings.Remove(PrefKey.ActiveAccountId);
    }
}

/// <summary>
/// File persistence for the account roster and each account's chart history, under one folder.
///
/// A roster or history file that fails to decode is preserved as a <c>.corrupt</c> sibling
/// before the empty value's first save can overwrite it — corruption must stay recoverable,
/// never silently fatal.
/// </summary>
public sealed class AccountStore(string directory, Action<string>? logError = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string AccountsPath => Path.Combine(directory, "accounts.json");

    /// <summary>Where an undecodable roster is preserved for manual recovery.</summary>
    public string CorruptAccountsPath => AccountsPath + ".corrupt";

    public bool HasCorruptRoster => File.Exists(CorruptAccountsPath);

    /// <summary>
    /// True when the roster file exists but could not be read at the last load. The roster then
    /// loads as empty, but that says nothing about the accounts on disk: the file is not
    /// overwritten for the rest of the session, and callers must not clean up as if it were empty.
    /// </summary>
    public bool RosterIsUnreadable => unreadable.Contains(AccountsPath);

    /// <summary>Files that exist but could not be read; never written while that holds.</summary>
    private readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);

    public string HistoryPath(Guid accountId) => Path.Combine(directory, $"history-{accountId:D}.json");

    public IReadOnlyList<Account> LoadAccounts() =>
        Load<List<Account>>(AccountsPath, "accounts", roster => roster.TrueForAll(account => account is not null)) ?? [];

    public void SaveAccounts(IReadOnlyList<Account> accounts) => Save(AccountsPath, accounts, "accounts");

    public List<UsageDataPoint> LoadHistory(Guid accountId) =>
        Load<List<UsageDataPoint>>(HistoryPath(accountId), "usage history", history => history.TrueForAll(point => point is not null)) ?? [];

    public void SaveHistory(Guid accountId, IReadOnlyList<UsageDataPoint> history) => Save(HistoryPath(accountId), history, "usage history");

    public void DeleteHistory(Guid accountId)
    {
        try { File.Delete(HistoryPath(accountId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logError?.Invoke($"usage history delete failed: {e.Message}");
        }
    }

    private T? Load<T>(string path, string what, Func<T, bool> isValid) where T : class
    {
        if (!File.Exists(path))
        {
            unreadable.Remove(path);
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = StorageFile.ReadWithRetry(path);
            unreadable.Remove(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not corrupt — unreadable. Loading as empty and then saving would replace real
            // data with nothing, so the file is fenced off from writes instead.
            unreadable.Add(path);
            logError?.Invoke($"{what} could not be read — it will not be overwritten this session: {e.Message}");
            return null;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, Options);
            // A file that parses but holds something unusable ("null", or a list with a null
            // row) is as corrupt as one that does not parse.
            if (value is null || !isValid(value)) throw new JsonException("the file holds null where a value is required");
            return value;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // Copied aside first, as its own statement: inside the log call it would be
            // skipped whenever no logger is attached.
            var preserved = StorageFile.PreserveCorrupt(path);
            logError?.Invoke($"{what} decode failed — {StorageFile.Outcome(path, preserved)}: {e.Message}");
            return null;
        }
    }

    private void Save<T>(string path, T value, string what)
    {
        if (unreadable.Contains(path))
        {
            logError?.Invoke($"{what} not saved: the file could not be read earlier, so it is left as it is");
            return;
        }
        try
        {
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logError?.Invoke($"{what} save failed — not persisted: {e.Message}");
        }
    }
}

/// <summary>
/// A chart sample's timestamp as Unix seconds to the millisecond: a third the size of an ISO
/// string, in a history file that holds up to 8640 of them and is rewritten at every sample.
/// Only for <see cref="UsageDataPoint.Timestamp"/> — the roster keeps full-precision ISO
/// timestamps, so an account read back equals the one saved.
/// </summary>
internal sealed class UnixSecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(reader.GetDouble() * 1000))
            : IsoDate.Parse(reader.GetString()) ?? throw new JsonException("timestamp is neither Unix seconds nor ISO 8601");

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteRawValue((value.ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
}
