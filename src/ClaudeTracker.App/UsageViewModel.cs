using System.Globalization;
using ClaudeTracker.Core;

namespace ClaudeTracker.App;

/// <summary>
/// All per-account runtime state. The view model keeps one per account id, so a fetch that
/// captured its account at the start always lands its result in the right bucket — even if the
/// user switched accounts meanwhile.
/// </summary>
internal sealed class AccountState
{
    public UsageResponse? Usage;
    public string? Error;
    public DateTimeOffset? LastUpdated;
    public AccountInfo? AccountInfo;
    /// <summary>When <c>/api/account</c> was last requested; throttles the retry while it is missing.</summary>
    public DateTimeOffset? AccountInfoAttemptedAt;
    /// <summary>The parsed <c>resets_at</c> per window key; <see cref="Resets.DetectResets"/> reads and rewrites it.</summary>
    public Dictionary<string, DateTimeOffset> PreviousResetsAt = new(StringComparer.Ordinal);
    /// <summary>Rolling 5-minute utilization history per window key, used by <see cref="PaceMath.ComputePace"/>.</summary>
    public Dictionary<string, List<(DateTimeOffset Time, double Value)>> UtilizationHistory = new(StringComparer.Ordinal);
    /// <summary>Throttles chart snapshots to one every 5 minutes.</summary>
    public DateTimeOffset? LastHistoryTimestamp;
    public List<UsageDataPoint> UsageHistory = [];
    /// <summary>Increments on every failed fetch; drives the poll backoff.</summary>
    public int ConsecutiveErrors;
    /// <summary>
    /// Counts only consecutive 401s — kept apart from <see cref="ConsecutiveErrors"/> so a
    /// network error right before the first 401 can't make it look like the second.
    /// </summary>
    public int Consecutive401s;
    /// <summary>True after a 401 retry confirmed the session is no longer valid.</summary>
    public bool SessionExpired;
}

/// <summary>
/// Central state for the app: the account roster, API polling, preferences, and the values the
/// tray icon and the popover show. The Windows twin of the Mac app's <c>UsageViewModel</c>;
/// decisions are delegated to the pure functions in ClaudeTracker.Core.
///
/// Lives on the UI thread. Views subscribe to <see cref="Changed"/> and redraw from scratch.
/// </summary>
internal sealed class UsageViewModel(SettingsStore settings, AccountStore accountStore)
{
    private static string PlaceholderLabel => L.T("Claude account");

    private readonly Dictionary<Guid, AccountState> states = [];
    private ClaudeApiClient? client;
    private CancellationTokenSource? fetchCancellation;
    private CancellationTokenSource? sessionCancellation;
    private CancellationTokenSource? timerCancellation;
    private (string Line, DateTimeOffset At)? lastPollLog;
    /// <summary>
    /// The account that was active before <see cref="AddAccount"/> switched to a fresh
    /// placeholder, so cancelling that sign-in returns there.
    /// </summary>
    private Guid? accountBeforePendingAdd;

    /// <summary>Raised after any state the views show has changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<Account> Accounts { get; private set; } = [];

    public Guid? ActiveAccountId { get; private set; }

    public ClaudeApiClient? Client => client;

    /// <summary>
    /// Set when the saved accounts could not be read at startup. The app then shows this
    /// instead of the signed-out state, and changes nothing on disk.
    /// </summary>
    public string? StorageProblem { get; private set; }

    // MARK: - Preferences

    /// <summary>Which window's utilization the tray icon tracks.</summary>
    public MenuBarDisplay MenuBarDisplay
    {
        get => MenuBarDisplays.FromRawValue(settings.GetString(PrefKey.MenuBarWindow)) ?? MenuBarDisplay.FiveHour;
        set { settings.Set(PrefKey.MenuBarWindow, value.RawValue()); Notify(); }
    }

    /// <summary>Whether per-model rows (legacy Sonnet, model-scoped limits) show in the popover.</summary>
    public bool ShowModelWindows
    {
        get => settings.GetBool(PrefKey.ShowModelWindows) ?? true;
        set { settings.Set(PrefKey.ShowModelWindows, value); Notify(); }
    }

    /// <summary>Whether absolute reset times render as 24-hour (true) or AM/PM (false).</summary>
    public bool Use24HourTime
    {
        get
        {
            if (settings.GetBool(PrefKey.Use24HourTime) is { } saved) return saved;
            // First run: seed from the system convention and persist it, so a later change of
            // the Windows format does not silently flip the app's setting.
            var seed = TimeText.Prefers24HourClock(CultureInfo.CurrentCulture);
            settings.Set(PrefKey.Use24HourTime, seed);
            return seed;
        }
        set { settings.Set(PrefKey.Use24HourTime, value); Notify(); }
    }

    /// <summary>Whether the pace and outlook lines show under each usage row.</summary>
    public bool ShowPace
    {
        get => settings.GetBool(PrefKey.ShowPace) ?? true;
        set { settings.Set(PrefKey.ShowPace, value); Notify(); }
    }

    /// <summary>
    /// Whether the tray icon's tooltip adds the pace of the window it tracks. The Mac app shows
    /// that pace as a badge in the menu bar; the preference has its name.
    /// </summary>
    public bool ShowPaceMenuBar
    {
        get => settings.GetBool(PrefKey.ShowPaceMenuBar) ?? true;
        set { settings.Set(PrefKey.ShowPaceMenuBar, value); Notify(); }
    }

    /// <summary>The time unit of every rate shown. The rate itself is always computed per hour.</summary>
    public PaceRateUnit PaceRateUnit
    {
        get => PaceRateUnits.FromRawValue(settings.GetString(PrefKey.PaceRateUnit)) ?? PaceRateUnit.PerHour;
        set { settings.Set(PrefKey.PaceRateUnit, value.RawValue()); Notify(); }
    }

    /// <summary>The popover's size, as a factor on its natural size.</summary>
    public double PopupScale
    {
        get => Core.PopupScale.Normalized(settings.GetDouble(PrefKey.PopupScale));
        set { settings.Set(PrefKey.PopupScale, Core.PopupScale.Normalized(value)); Notify(); }
    }

    // MARK: - Active account accessors

    private AccountState? ActiveState => ActiveAccountId is { } id && states.TryGetValue(id, out var state) ? state : null;

    public UsageResponse? Usage => ActiveState?.Usage;

    public string? Error => ActiveState?.Error;

    public DateTimeOffset? LastUpdated => ActiveState?.LastUpdated;

    public IReadOnlyList<UsageDataPoint> UsageHistory => ActiveState?.UsageHistory ?? [];

    public Account? ActiveAccount => Accounts.FirstOrDefault(a => a.Id == ActiveAccountId);

    /// <summary>The active account's plan badge: the live value, else the roster's saved copy.</summary>
    public string? ActiveSubscriptionLabel => ActiveState?.AccountInfo?.SubscriptionLabel ?? ActiveAccount?.SubscriptionLabel;

    /// <summary>
    /// True when an account is active and its session is healthy. False when no accounts exist,
    /// when the active id is missing from the roster, or when a 401 marked the session expired.
    /// </summary>
    public bool IsAuthenticated => ActiveAccount is not null && ActiveState?.SessionExpired != true;

    /// <summary>
    /// True from the first 401 (one silent retry still pending) until a new session is
    /// captured. Drives every "Sign in again", which signs the same account back in.
    /// </summary>
    public bool SessionNeedsSignIn => ActiveState is { } state && (state.SessionExpired || state.Consecutive401s > 0);

    /// <summary>
    /// True when the stored usage was fetched before a window's reset time that has now passed:
    /// the numbers belong to the previous cycle. Clears once a fresh fetch succeeds.
    /// </summary>
    public bool IsDataStale
    {
        get
        {
            if (Usage is not { } usage || LastUpdated is not { } updated) return false;
            var now = DateTimeOffset.UtcNow;
            return usage.AllWindows.Any(w => Resets.WindowIsStale(w.Window.ResetsAtDate, updated, now));
        }
    }

    public bool IsWindowStale(UsageWindow window) => Resets.WindowIsStale(window.ResetsAtDate, LastUpdated, DateTimeOffset.UtcNow);

    /// <summary>The rows the popover shows: per-model rows only under <see cref="ShowModelWindows"/>.</summary>
    public IReadOnlyList<TrackedWindow> ShownWindows =>
        Usage?.TrackedWindows.Where(w => ShowModelWindows || !w.IsModelScoped).ToList() ?? [];

    /// <summary>The window the tray icon tracks (a hidden per-model row can't drive it).</summary>
    public TrackedWindow? DisplayedTrackedWindow => Usage is null ? null : MenuBarDisplays.TrackedWindowFor(MenuBarDisplay, ShownWindows);

    public double DisplayedUtilization => DisplayedTrackedWindow?.Window.Utilization ?? 0;

    /// <summary>What the tray icon reads: "–" signed out, "!" failing with no data, "…" loading or stale, else the percentage.</summary>
    public string StatusText
    {
        get
        {
            if (!IsAuthenticated) return "–";
            if (Usage is null) return Error is not null ? "!" : "…";
            if (IsDataStale) return "…";
            return ((int)DisplayedUtilization).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Null while there is no live number to colour.</summary>
    public double? StatusUrgency =>
        IsAuthenticated && Usage is not null && !IsDataStale ? Math.Max(DisplayedUtilization / 100.0, DisplayedPaceUrgency) : null;

    private double DisplayedPaceUrgency
    {
        get
        {
            if (DisplayedTrackedWindow is not { } tracked || Pace(tracked.Key) is not (_, { } projected) || projected <= 0
                || tracked.Window.ResetsAtDate is not { } reset) return 0;
            return PaceMath.PaceUrgency(projected, (reset - DateTimeOffset.UtcNow).TotalHours);
        }
    }

    /// <summary>The tray icon's tooltip, which names the window the icon itself can't.</summary>
    public string StatusDescription
    {
        get
        {
            if (!IsAuthenticated) return L.T("Claude Tracker, signed out");
            if (Usage is null && Error is not null) return L.T("Claude Tracker, usage unavailable");
            if (Usage is null || IsDataStale) return L.T("Claude Tracker, updating");
            var title = DisplayedTrackedWindow?.Title ?? MenuBarDisplay.Label();
            var text = L.F("Claude Tracker, %@ at %@", title, StatusText + "%");
            return TrayPace is { } pace ? L.F("%@, pace %@", text, PaceRateUnit.Format(pace.Rate, prefix: true)) : text;
        }
    }

    /// <summary>
    /// The pace the tooltip adds: only with the setting on, live numbers, and the tracked window
    /// under 100% — the conditions of the Mac app's menu bar badge.
    /// </summary>
    private (double Rate, double? ProjectedHours)? TrayPace
    {
        get
        {
            if (!ShowPaceMenuBar || !IsAuthenticated || Usage is null || IsDataStale || DisplayedUtilization >= 100) return null;
            return DisplayedTrackedWindow is { } tracked ? Pace(tracked.Key) : null;
        }
    }

    /// <summary>The current consumption rate and projected time to full for a window of the active account.</summary>
    public (double Rate, double? ProjectedHours)? Pace(string key) =>
        ActiveState is { } state && state.UtilizationHistory.TryGetValue(key, out var history) ? PaceMath.ComputePace(history) : null;

    private AccountState State(Guid id)
    {
        if (!states.TryGetValue(id, out var state)) states[id] = state = new AccountState();
        return state;
    }

    /// <summary>
    /// Tells the views to redraw. A view that fails to must not take the caller down with it:
    /// the poll loop notifies before it arms the next poll.
    /// </summary>
    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { AppLogger.Shared.Error($"a view failed to redraw: {e}"); }
    }

    // MARK: - Startup

    /// <summary>
    /// Loads the roster and starts polling the active account. Kept out of the constructor so
    /// the object can exist without touching the network.
    /// </summary>
    public void Start()
    {
        var loaded = accountStore.LoadAccounts().ToList();
        if (accountStore.RosterIsUnreadable)
        {
            // The accounts are most likely intact on disk, just locked right now. Offering
            // "Add a Claude account" here would start a second roster over them, and the
            // clean-ups below would treat every saved session as abandoned.
            StorageProblem = L.T("Couldn't read your saved accounts. Quit Claude Tracker and open it again.");
            AppLogger.Shared.Error("startup stopped: the account roster could not be read");
            Notify();
            return;
        }
        // Reclaim placeholders abandoned by a quit or crash while the sign-in window was open:
        // their profiles hold no session — a surviving row could only fail.
        var abandoned = loaded.Where(a => a.Pending == true).ToList();
        if (abandoned.Count > 0)
        {
            loaded.RemoveAll(a => a.Pending == true);
            accountStore.SaveAccounts(loaded);
            foreach (var account in abandoned)
            {
                // Its browser profile goes with the sweep below.
                accountStore.DeleteHistory(account.Id);
                AppLogger.Shared.Info($"reclaimed abandoned pending account {Short(account.Id)}");
            }
        }
        Accounts = loaded;
        SweepOrphanedProfiles();
        foreach (var account in Accounts)
        {
            State(account.Id).UsageHistory = accountStore.LoadHistory(account.Id);
        }

        var savedActive = Core.Accounts.LoadActiveId(settings);
        // A roster exists but the stored active id is invalid — fall back to the first.
        if ((Accounts.FirstOrDefault(a => a.Id == savedActive) ?? Accounts.FirstOrDefault()) is { } active)
        {
            Activate(active);
        }
        else if (savedActive is not null)
        {
            // Every row was reclaimed, or the roster failed to decode: forget the stale
            // selection so the popover offers sign-in instead of an endless "Loading…".
            Core.Accounts.SaveActiveId(settings, null);
        }
        Notify();
    }

    /// <summary>
    /// Deletes every account profile on disk that no roster row owns: those of the placeholders
    /// reclaimed at launch, and any left behind when the app quit, or the delete failed,
    /// between removing an account and WebView2 finishing the job. Run once the roster is final.
    /// </summary>
    private void SweepOrphanedProfiles()
    {
        // A roster that failed to parse was set aside and loads as empty: every profile would
        // look orphaned, and deleting them would leave a recovery of that file by hand
        // without its sessions.
        if (accountStore.HasCorruptRoster) return;
        foreach (var profile in Core.Accounts.OrphanedProfiles(WebViewHost.ExistingProfileNames(), Accounts))
        {
            AppLogger.Shared.Info($"deleting orphaned browser profile {profile}");
            _ = WebViewHost.DeleteProfileAsync(profile);
        }
    }

    /// <summary>Stops polling and closes the hidden browser. Called when the app quits.</summary>
    public void Shutdown()
    {
        CancelInFlightWork();
        client?.TearDown();
        client = null;
    }

    private void Activate(Account account)
    {
        ActiveAccountId = account.Id;
        Core.Accounts.SaveActiveId(settings, account.Id);
        client?.TearDown();
        client = new ClaudeApiClient(account);
        StartSession();
    }

    private void CancelInFlightWork()
    {
        fetchCancellation?.Cancel();
        fetchCancellation = null;
        sessionCancellation?.Cancel();
        sessionCancellation = null;
        timerCancellation?.Cancel();
        timerCancellation = null;
    }

    // MARK: - Session

    /// <summary>
    /// Loads account info for the active account, then starts polling. The client and account
    /// are captured now, not when the work runs: an account switch mid-wait must neither
    /// misattribute the result nor start a second poll loop.
    /// </summary>
    private void StartSession()
    {
        if (client is not { } svc || ActiveAccountId is not { } id) return;
        sessionCancellation?.Cancel();
        var cancellation = sessionCancellation = new CancellationTokenSource();
        _ = RunAsync();

        async Task RunAsync()
        {
            await RefreshAccountInfoAsync(id, svc, cancellation.Token);
            if (cancellation.IsCancellationRequested || id != ActiveAccountId) return;
            StartPolling();
        }
    }

    /// <summary>
    /// Fetches <c>/api/account</c> into the account's bucket and roster row. A failure is logged
    /// and left to <see cref="RetryAccountInfoIfMissing"/>.
    /// </summary>
    private async Task RefreshAccountInfoAsync(Guid id, ClaudeApiClient svc, CancellationToken cancellation)
    {
        State(id).AccountInfoAttemptedAt = DateTimeOffset.UtcNow;
        try
        {
            var info = await svc.FetchAccountInfoAsync(cancellation);
            if (cancellation.IsCancellationRequested) return;
            State(id).AccountInfo = info;
            ApplyAccountInfoToRoster(id, info);
            Notify();
        }
        catch (Exception e)
        {
            // Everything is caught: the session start awaits this before it starts polling, and
            // this call is the first to create the web view — a WebView2 failure escaping here
            // would leave the popover on "Loading…" for good.
            // A switch cancels the session (and tears the client down): not a failure.
            if (!cancellation.IsCancellationRequested) AppLogger.Shared.Error($"account info fetch failed: {e.Message}");
        }
    }

    /// <summary>Fetches account info again after a successful poll while it is still missing — at most every 5 minutes.</summary>
    private void RetryAccountInfoIfMissing(Guid id, ClaudeApiClient svc)
    {
        var state = State(id);
        if (state.AccountInfo is not null) return;
        if ((DateTimeOffset.UtcNow - (state.AccountInfoAttemptedAt ?? DateTimeOffset.MinValue)).TotalSeconds <= 300) return;
        _ = RefreshAccountInfoAsync(id, svc, CancellationToken.None);
    }

    // MARK: - Polling

    /// <summary>Cancels any pending poll and starts a fresh adaptive polling cycle for the active account.</summary>
    public void StartPolling()
    {
        timerCancellation?.Cancel();
        timerCancellation = null;
        if (ActiveAccountId is not { } id) return;
        State(id).ConsecutiveErrors = 0;
        if (!IsAuthenticated) return;
        FetchUsage();
    }

    /// <summary>
    /// Fetches the latest usage for the active account and schedules the next adaptive poll.
    /// The account id is captured at the start, so a mid-fetch switch deposits the response in
    /// the right bucket.
    /// </summary>
    public void FetchUsage()
    {
        if (!IsAuthenticated || ActiveAccountId is not { } id || client is not { } svc || svc.IsLoginInProgress) return;
        if (IsDataStale) AppLogger.Shared.Info("fetchUsage: refreshing stale data (a reset time passed since the last fetch)");
        fetchCancellation?.Cancel();
        var cancellation = fetchCancellation = new CancellationTokenSource();
        _ = FetchUsageAsync(id, svc, cancellation.Token);
    }

    private async Task FetchUsageAsync(Guid id, ClaudeApiClient svc, CancellationToken cancellation)
    {
        var shouldSchedule = false;
        var state = State(id);
        try
        {
            var response = await svc.FetchUsageAsync(cancellation);
            if (cancellation.IsCancellationRequested) return;
            var oldUsage = state.Usage;
            LogSignatureTransitions(oldUsage, response);
            CheckForResets(id, response);
            RecordHistory(id, response);
            AppendDataPoint(id, response);
            state.Usage = response;
            state.Error = null;
            state.LastUpdated = DateTimeOffset.UtcNow;
            state.ConsecutiveErrors = 0;
            state.Consecutive401s = 0;
            state.SessionExpired = false;
            if (svc.CachedOrgName is { } orgName) ApplyOrgNameToRoster(id, orgName);
            RetryAccountInfoIfMissing(id, svc);
            shouldSchedule = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Only this fetch's own cancellation ends it quietly. Someone else's (a WebView2
            // call that was cancelled underneath) is a failure like any other, below.
            return;
        }
        catch (ApiException e)
        {
            if (cancellation.IsCancellationRequested) return;
            RecordFetchFailure(id, "APIError", e.Message, e.Message);
            if (e.Kind == ApiErrorKind.Unauthorized)
            {
                // Counted apart from ConsecutiveErrors: a transient error on the previous
                // poll must not make the first 401 look like a second.
                state.Consecutive401s++;
                if (state.Consecutive401s > 1)
                {
                    state.SessionExpired = true;
                    timerCancellation?.Cancel();
                    timerCancellation = null;
                }
                else
                {
                    // First 401: the client marked the page for a real reload; the next poll
                    // loads claude.ai afresh and retries by itself.
                    shouldSchedule = true;
                }
            }
            else
            {
                state.Consecutive401s = 0;
                shouldSchedule = true;
            }
        }
        catch (ApiDecodeException e)
        {
            if (cancellation.IsCancellationRequested) return;
            // The raw decode error means nothing to the user; the payload head is already in the log.
            RecordFetchFailure(id, "decode error", e.Message, L.T("claude.ai API format changed — check for app updates"));
            state.Consecutive401s = 0;
            shouldSchedule = true;
        }
        catch (Exception e)
        {
            // Anything else (a WebView2 failure, an unparseable reply) must still schedule the
            // next poll: an escaped exception here would end polling without a trace.
            if (cancellation.IsCancellationRequested) return;
            RecordFetchFailure(id, "unexpected error", e.ToString(), e.Message);
            state.Consecutive401s = 0;
            shouldSchedule = true;
        }
        Notify();
        if (shouldSchedule && id == ActiveAccountId) ScheduleNextPoll();
    }

    /// <summary>
    /// Bumps the account's consecutive-error count, logs the failure, and surfaces
    /// <paramref name="message"/> in the popover. Leaves the 401 count alone on purpose.
    /// </summary>
    private void RecordFetchFailure(Guid id, string kind, string detail, string message)
    {
        var state = State(id);
        state.ConsecutiveErrors++;
        AppLogger.Shared.Error($"fetchUsage {kind} (#{state.ConsecutiveErrors}): {detail}");
        state.Error = message;
    }

    /// <summary>
    /// Logs API fields that are decoded to be learned from, once per change — never on every
    /// poll. The signatures are the pure functions in ClaudeTracker.Core.
    /// </summary>
    private static void LogSignatureTransitions(UsageResponse? old, UsageResponse current)
    {
        var severities = UsageSignatures.AbnormalSeverities(current.Limits);
        if (severities.Length > 0 && severities != UsageSignatures.AbnormalSeverities(old?.Limits))
        {
            AppLogger.Shared.Info($"limit severity: {severities}");
        }
        var diagnostics = UsageSignatures.UsageDiagnostics(current);
        if (diagnostics.Length > 0 && diagnostics != (old is null ? null : UsageSignatures.UsageDiagnostics(old)))
        {
            AppLogger.Shared.Info($"usage diagnostics: {diagnostics}");
        }
        var breakdown = UsageSignatures.BreakdownSignature(current);
        if (breakdown.Length > 0 && breakdown != (old is null ? null : UsageSignatures.BreakdownSignature(old)))
        {
            AppLogger.Shared.Info($"usage breakdown: {breakdown}");
        }
    }

    /// <summary>Sleeps for the adaptive interval (plus error backoff), then fetches again.</summary>
    private void ScheduleNextPoll()
    {
        if (ActiveAccountId is not { } id) return;
        timerCancellation?.Cancel();
        var state = State(id);
        var now = DateTimeOffset.UtcNow;
        var baseInterval = Usage is { } usage
            ? Polling.AdaptivePollInterval(usage.TrackedWindows, key => Pace(key)?.ProjectedHours * 60, now)
            : 10;
        var interval = baseInterval + Polling.ErrorBackoff(state.ConsecutiveErrors);
        if (state.ConsecutiveErrors > 0)
        {
            var stem = (state.Error ?? "Error").Split(" (retry in ")[0];
            state.Error = L.F("%@ (retry in %ds)", stem, (int)interval);
            Notify();
        }
        var maxUtilization = Usage?.TrackedWindows.Select(w => w.Window.Utilization).DefaultIfEmpty(0).Max() ?? 0;
        var pollLine = string.Create(CultureInfo.InvariantCulture, $"poll: next in {interval:0.0}s (base={baseInterval:0.0}s util={maxUtilization:0}%)");
        if (Polling.ShouldLogPoll(pollLine, lastPollLog, now))
        {
            AppLogger.Shared.Info(pollLine);
            lastPollLog = (pollLine, now);
        }
        var cancellation = timerCancellation = new CancellationTokenSource();
        _ = WaitAsync();

        async Task WaitAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token); }
            catch (OperationCanceledException) { return; }
            FetchUsage();
        }
    }

    // MARK: - Resets, pace history, chart history

    /// <summary>
    /// Runs the reset bookkeeping on every poll, so a passed reset can never linger in the
    /// store. Announcing a reset (toast, sound) arrives with the notifications spec; until then
    /// a detected reset is logged.
    /// </summary>
    private void CheckForResets(Guid id, UsageResponse response)
    {
        var state = State(id);
        var (resetKeys, stored) = Resets.DetectResets(state.PreviousResetsAt, response.TrackedWindows, DateTimeOffset.UtcNow);
        state.PreviousResetsAt = stored;
        if (state.Usage is not null && resetKeys.Count > 0)
        {
            AppLogger.Shared.Info($"window reset detected: {string.Join(", ", resetKeys)}");
        }
    }

    /// <summary>
    /// Appends the current readings to each window's rolling 5-minute history. The history is
    /// cleared first when <see cref="PaceMath.ShouldResetPaceHistory"/> says the window reset.
    /// </summary>
    private void RecordHistory(Guid id, UsageResponse response)
    {
        var state = State(id);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-5);
        foreach (var tracked in response.TrackedWindows)
        {
            if (!state.UtilizationHistory.TryGetValue(tracked.Key, out var history)) history = [];
            if (history.Count > 0 && PaceMath.ShouldResetPaceHistory(history[^1].Value, tracked.Window.Utilization)) history = [];
            history.Add((now, tracked.Window.Utilization));
            history.RemoveAll(sample => sample.Time < cutoff);
            state.UtilizationHistory[tracked.Key] = history;
        }
    }

    /// <summary>
    /// Appends a chart snapshot, under the sampling contract in <see cref="Charts.AppendPrunedDataPoint"/>.
    /// Per-model windows are recorded regardless of what the popover shows: enabling a series
    /// later must not present an empty chart.
    /// </summary>
    private void AppendDataPoint(Guid id, UsageResponse response)
    {
        var state = State(id);
        double? Rate(string key) => state.UtilizationHistory.TryGetValue(key, out var history) ? PaceMath.ComputePace(history)?.Rate : null;

        var models = new Dictionary<string, double>(StringComparer.Ordinal);
        var modelPaces = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tracked in response.TrackedWindows.Where(w => w.IsModelScoped))
        {
            models[tracked.Key] = tracked.Window.Utilization;
            if (Rate(tracked.Key) is { } rate) modelPaces[tracked.Key] = rate;
        }
        var point = new UsageDataPoint(
            Charts.SampleTime(DateTimeOffset.UtcNow), response.FiveHour?.Utilization, response.SevenDay?.Utilization,
            Rate("five_hour"), Rate("seven_day"),
            // An empty dictionary would cost bytes in every point of an account without model limits.
            models.Count == 0 ? null : models, modelPaces.Count == 0 ? null : modelPaces);
        if (Charts.AppendPrunedDataPoint(point, state.UsageHistory, state.LastHistoryTimestamp) is not { } history) return;
        state.LastHistoryTimestamp = point.Timestamp;
        state.UsageHistory = history;
        accountStore.SaveHistory(id, history);
    }

    // MARK: - Accounts

    /// <summary>Called by the sign-in window once a new session cookie exists for the active account.</summary>
    public void HandleSessionFound()
    {
        if (ActiveAccountId is not { } id) return;
        var state = State(id);
        state.Error = null;
        state.SessionExpired = false;
        // A new session starts the 401 count over.
        state.Consecutive401s = 0;
        accountBeforePendingAdd = null;
        // The account is real now — clear the pending flag so a later launch doesn't reclaim it.
        ReplaceAccount(id, account => account.Pending == true ? account with { Pending = null } : account);
        if (client is { } svc) svc.IsLoginInProgress = false;
        StartSession();
        Notify();
    }

    /// <summary>
    /// Adds a placeholder account, makes it active, and opens the sign-in window on its fresh
    /// profile. Closing the window without signing in rolls the placeholder back.
    /// </summary>
    public void OpenLoginForNewAccount()
    {
        var account = AddAccount();
        if (client is not { } svc) return;
        LoginWindow.Open(svc, HandleSessionFound, () => CancelPendingAdd(account));
    }

    /// <summary>
    /// Reopens sign-in on the active account's own profile, so a rejected session signs back
    /// into the same account (same roster row, same chart history).
    /// </summary>
    public void SignInAgain()
    {
        if (client is not { } svc || ActiveAccountId is not { } id) return;
        LoginWindow.Open(svc, HandleSessionFound, () =>
        {
            if (ActiveAccountId == id) StartPolling();
        });
    }

    /// <summary>
    /// Makes another account the active one: its numbers show at once if it was polled before,
    /// and a fresh fetch starts.
    /// </summary>
    public void SwitchAccount(Guid id)
    {
        if (id == ActiveAccountId || Accounts.All(a => a.Id != id)) return;
        // Abandon a sign-in in progress first: activating tears down the client whose profile
        // the sign-in window is on. Closing it runs its rollback at once, which may itself
        // return to the account asked for here.
        LoginWindow.CloseCurrent();
        if (id == ActiveAccountId || Accounts.FirstOrDefault(a => a.Id == id) is not { } account) return;
        CancelInFlightWork();
        AppLogger.Shared.Info($"switched active account to {Short(id)}");
        Activate(account);
        Notify();
    }

    /// <summary>Gives an account another name. The name is trimmed; an empty one changes nothing.</summary>
    public void RenameAccount(Guid id, string newLabel)
    {
        var trimmed = newLabel.Trim();
        if (trimmed.Length == 0) return;
        ReplaceAccount(id, account => account with { Label = trimmed });
        Notify();
    }

    private Account AddAccount()
    {
        var account = new Account { Label = PlaceholderLabel, Pending = true };
        Accounts = [.. Accounts, account];
        states[account.Id] = new AccountState();
        accountStore.SaveAccounts(Accounts);
        CancelInFlightWork();
        // A second "Add account" while a placeholder is active keeps the original to return to.
        if (ActiveAccount?.Pending != true) accountBeforePendingAdd = ActiveAccountId;
        ActiveAccountId = account.Id;
        Core.Accounts.SaveActiveId(settings, account.Id);
        client?.TearDown();
        client = new ClaudeApiClient(account);
        Notify();
        return account;
    }

    /// <summary>Removes a partially added account if sign-in was cancelled before a session was captured.</summary>
    private void CancelPendingAdd(Account account)
    {
        if (Accounts.All(a => a.Id != account.Id)) return;
        if (states.TryGetValue(account.Id, out var state) && (state.Usage is not null || state.AccountInfo is not null)) return;
        // Only the active placeholder's rollback consumes the saved account.
        var wasActive = ActiveAccountId == account.Id;
        RemoveAccount(account.Id, accountBeforePendingAdd);
        if (wasActive) accountBeforePendingAdd = null;
    }

    /// <summary>
    /// Removes an account: its browser profile, its chart history and its roster row. Switches
    /// to <paramref name="preferredNext"/> when it still exists, otherwise to the first account,
    /// or to the signed-out state when none remains.
    /// </summary>
    public void RemoveAccount(Guid id, Guid? preferredNext = null)
    {
        // A sign-in window open on this account goes first: its page lives in the profile
        // about to be deleted, and would be left open on nothing. For a placeholder, closing
        // the window is the whole removal — its rollback does the rest, and the check below
        // then finds nothing left to do.
        if (ActiveAccountId == id && client is { IsLoginInProgress: true }) LoginWindow.CloseCurrent();
        if (Accounts.FirstOrDefault(a => a.Id == id) is not { } account) return;
        var wasActive = ActiveAccountId == id;
        ClaudeApiClient doomed;
        if (wasActive)
        {
            CancelInFlightWork();
            doomed = client ?? new ClaudeApiClient(account);
            client = null;
        }
        else
        {
            doomed = new ClaudeApiClient(account);
        }
        _ = doomed.DeleteProfileAsync();
        Accounts = Accounts.Where(a => a.Id != id).ToList();
        states.Remove(id);
        accountStore.SaveAccounts(Accounts);
        accountStore.DeleteHistory(id);
        if (wasActive)
        {
            if ((Accounts.FirstOrDefault(a => a.Id == preferredNext) ?? Accounts.FirstOrDefault()) is { } next)
            {
                Activate(next);
            }
            else
            {
                ActiveAccountId = null;
                Core.Accounts.SaveActiveId(settings, null);
            }
        }
        Notify();
    }

    /// <summary>
    /// Writes API-derived account info back into the roster: the display name while the label
    /// is still the placeholder, plus email and plan.
    /// </summary>
    private void ApplyAccountInfoToRoster(Guid id, AccountInfo info) =>
        ReplaceAccount(id, account => account with
        {
            Label = account.Label == PlaceholderLabel || account.Label.Length == 0 ? info.DisplayName : account.Label,
            Email = info.EmailAddress,
            SubscriptionLabel = info.SubscriptionLabel,
        });

    private void ApplyOrgNameToRoster(Guid id, string orgName) =>
        ReplaceAccount(id, account => account.OrgName == orgName ? account : account with { OrgName = orgName });

    /// <summary>Replaces one roster row (the roster is immutable: a change is always a new list) and saves if it changed.</summary>
    private void ReplaceAccount(Guid id, Func<Account, Account> change)
    {
        var changed = false;
        var updated = Accounts.Select(account =>
        {
            if (account.Id != id) return account;
            var next = change(account);
            changed |= next != account;
            return next;
        }).ToList();
        if (!changed) return;
        Accounts = updated;
        accountStore.SaveAccounts(Accounts);
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
}
