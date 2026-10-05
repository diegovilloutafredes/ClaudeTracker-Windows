using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using ClaudeTracker.Core;
using Microsoft.Web.WebView2.Core;

namespace ClaudeTracker.App;

/// <summary>
/// The one WebView2 environment the app uses: a single browser process group and user-data
/// folder, with one profile per account so sessions never share cookies.
/// </summary>
internal static class WebViewHost
{
    private static Task<CoreWebView2Environment>? environment;
    private static Window? hiddenWindow;

    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        // A failed creation is not kept, or one failure would last until the app restarts.
        if (environment is { IsCompleted: true, IsCompletedSuccessfully: false }) environment = null;
        return environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebView, null);
    }

    /// <summary>
    /// Makes the next web view start from a new environment. This is what WebView2 advises
    /// after a web view could not be created: the runtime the environment was bound to may
    /// have been replaced by an update. Views already open keep the environment they have.
    /// </summary>
    public static void ForgetEnvironment() => environment = null;

    /// <summary>What the app says where WebView2 itself is not installed (possible on Windows 10).</summary>
    public static string MissingRuntimeMessage =>
        L.T("Microsoft Edge WebView2 is missing on this PC. Install it from Microsoft and try again.");

    /// <summary>A window that is never shown: the parent every hidden web view needs.</summary>
    public static IntPtr HiddenParent
    {
        get
        {
            hiddenWindow ??= new Window { ShowInTaskbar = false, WindowStyle = WindowStyle.None, Width = 1, Height = 1 };
            return new WindowInteropHelper(hiddenWindow).EnsureHandle();
        }
    }

    public static Task<CoreWebView2ControllerOptions> OptionsForAsync(Account account) => OptionsForAsync(account.ProfileName);

    private static async Task<CoreWebView2ControllerOptions> OptionsForAsync(string profileName)
    {
        var options = (await EnvironmentAsync()).CreateCoreWebView2ControllerOptions();
        options.ProfileName = profileName;
        return options;
    }

    /// <summary>The names of the browser profiles that have a folder on disk.</summary>
    public static IReadOnlyList<string> ExistingProfileNames()
    {
        // WebView2 keeps each profile in "EBWebView\WV2Profile_<name>" under the user-data folder.
        const string folderPrefix = "WV2Profile_";
        try
        {
            var root = Path.Combine(AppPaths.WebView, "EBWebView");
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root, folderPrefix + "*").Select(path => Path.GetFileName(path)[folderPrefix.Length..]).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes a profile that no web view is open on. WebView2 has no call for that, so this
    /// opens one hidden view on the profile and deletes through it. The folder goes when the
    /// browser process exits, or at its next start if something still holds it then.
    /// </summary>
    public static async Task DeleteProfileAsync(string profileName)
    {
        try
        {
            var controller = await (await EnvironmentAsync()).CreateCoreWebView2ControllerAsync(HiddenParent, await OptionsForAsync(profileName));
            controller.CoreWebView2.Profile.Delete();
        }
        catch (Exception e)
        {
            AppLogger.Shared.Error($"profile delete failed for {profileName}: {e.Message}");
        }
    }

    /// <summary>The claude.ai <c>sessionKey</c> cookie in a web view's profile, if any.</summary>
    public static async Task<string?> SessionKeyAsync(CoreWebView2 webView)
    {
        var cookies = await webView.CookieManager.GetCookiesAsync(ClaudeApiClient.Origin);
        return cookies.FirstOrDefault(c => c.Name == "sessionKey" && IsClaudeDomain(c.Domain))?.Value;
    }

    /// <summary>The single cookie-domain predicate: the claude.ai and anthropic.com session domains.</summary>
    public static bool IsClaudeDomain(string domain) =>
        domain.Contains("claude.ai", StringComparison.OrdinalIgnoreCase) || domain.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fetches usage and account data from the unofficial claude.ai web API.
///
/// Plain HTTP requests to claude.ai are blocked by Cloudflare's bot detection. This client
/// keeps a hidden WebView2 on a claude.ai page and runs every API call as <c>fetch()</c> inside
/// it, so requests come from a real browser context with the account's cookies — exactly as the
/// web app's do. The Windows twin of the Mac app's <c>ClaudeAPIService</c>.
///
/// Everything here runs on the UI thread, which is where WebView2 lives.
/// </summary>
internal sealed class ClaudeApiClient(Account account)
{
    public const string Origin = "https://claude.ai";

    /// <summary>
    /// A same-origin page that costs a fraction of the web app's memory. Fetches from it carry
    /// the same cookies. It cannot answer a Cloudflare challenge, so the first challenged fetch
    /// switches this client to the full page for the rest of its life.
    /// </summary>
    private const string LightHostPage = Origin + "/robots.txt";

    /// <summary>
    /// Development aid (<c>--full-host-page</c>): every client starts on the full page, as if
    /// Cloudflare had already challenged it. For comparing the two host pages.
    /// </summary>
    public static bool StartOnFullPage { get; set; }

    /// <summary>
    /// Development aid (<c>--challenge-once</c>): the next fetch fails as if Cloudflare had
    /// challenged it. A real challenge cannot be provoked, and the switch to the full page
    /// must still be seen working.
    /// </summary>
    public static bool SimulateChallengeOnce { get; set; }

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the hidden page is asked to collect its garbage. Every poll leaves a few
    /// kilobytes behind, and a page this quiet gives the browser engine no reason to collect:
    /// left alone, its process grew by 11 MB an hour, and one forced collection took back
    /// everything the page held (measured 2026-10-05, with <c>--page-heap</c>).
    /// </summary>
    private static readonly TimeSpan CollectionInterval = TimeSpan.FromMinutes(30);
    private const int FetchTimeoutMilliseconds = 30000;

    private CoreWebView2Controller? controller;
    private Task<CoreWebView2>? creating;
    /// <summary>Counts creations, so one that was given up on can tell it is no longer wanted.</summary>
    private int creation;
    private Task? loading;
    private bool isPageReady;
    /// <summary>
    /// Set when the loaded document can't be trusted for the next fetch: after a 401/403, a
    /// Cloudflare-challenged fetch, a readiness timeout, or a failed navigation. The next
    /// <see cref="EnsureReadyAsync"/> then does a real top-level load.
    /// </summary>
    private bool needsReload;
    private bool useFullPage = StartOnFullPage;
    private string? cachedOrgId;
    private bool tornDown;
    private DateTimeOffset collectedAt = DateTimeOffset.UtcNow;

    public string? CachedOrgName { get; private set; }

    /// <summary>
    /// True while the sign-in window is open on this account. Usage fetches wait: until the
    /// new session exists they could only fail, and count toward expiring the old one.
    /// </summary>
    public bool IsLoginInProgress { get; set; }

    public Account Account { get; } = account;

    // MARK: - Web view

    private Task<CoreWebView2> WebViewAsync()
    {
        // A failed creation is not kept, or one failure would last until the app restarts.
        if (creating is { IsCompleted: true, IsCompletedSuccessfully: false }) creating = null;
        return creating ??= CreateAsync(++creation);
    }

    private async Task<CoreWebView2> CreateAsync(int attempt)
    {
        CoreWebView2Controller created;
        try
        {
            var environment = await WebViewHost.EnvironmentAsync();
            created = await environment.CreateCoreWebView2ControllerAsync(WebViewHost.HiddenParent, await WebViewHost.OptionsForAsync(Account));
        }
        catch (WebView2RuntimeNotFoundException e)
        {
            WebViewHost.ForgetEnvironment();
            throw new InvalidOperationException(WebViewHost.MissingRuntimeMessage, e);
        }
        catch
        {
            WebViewHost.ForgetEnvironment();
            throw;
        }
        // Torn down meanwhile, or given up on after a readiness timeout and replaced.
        if (tornDown || attempt != creation)
        {
            created.Close();
            throw new ApiException(ApiErrorKind.NetworkError, "torn down");
        }
        controller = created;
        created.IsVisible = false;
        var webView = created.CoreWebView2;
        webView.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.AreDefaultContextMenusEnabled = false;
        webView.NavigationCompleted += (_, e) =>
        {
            // The page can navigate by itself (the web app does). If that fails, or leaves
            // claude.ai, the document can no longer make the fetches: load afresh next time.
            // A load in progress overrules this when it finishes.
            if (e.IsSuccess && webView.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
            isPageReady = false;
            needsReload = true;
        };
        webView.ProcessFailed += (_, e) =>
        {
            // Without this a dead browser process leaves the page "ready" and every later
            // fetch fails the same way until something else happens to clear the flag.
            AppLogger.Shared.Error($"web view process failed ({e.ProcessFailedKind}) — reloading claude.ai");
            isPageReady = false;
            needsReload = true;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                controller = null;
                creating = null;
            }
        };
        return webView;
    }

    /// <summary>Closes the hidden web view. Call before dropping the client.</summary>
    public void TearDown()
    {
        tornDown = true;
        isPageReady = false;
        try { controller?.Close(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { /* already gone */ }
        controller = null;
        creating = null;
    }

    /// <summary>
    /// Deletes this account's browser profile — cookies, storage, everything. WebView2 closes
    /// any web view still on the profile and removes its folder; if the folder is held open it
    /// retries at the next browser start.
    /// </summary>
    public async Task DeleteProfileAsync()
    {
        try
        {
            tornDown = false;
            var webView = await WebViewAsync();
            webView.Profile.Delete();
        }
        catch (Exception e)
        {
            // Nobody awaits this call. A profile it could not delete is found again by the
            // sweep at the next launch.
            AppLogger.Shared.Error($"profile delete failed for {Account.ProfileName}: {e.Message}");
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>
    /// Asks the page to collect its garbage when <see cref="CollectionInterval"/> has passed.
    /// Not awaited: nothing depends on it, and a poll must not wait for it.
    /// </summary>
    private void CollectGarbageIfDue(CoreWebView2 webView)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - collectedAt < CollectionInterval) return;
        collectedAt = now;
        _ = CollectAsync();

        async Task CollectAsync()
        {
            try
            {
                await webView.CallDevToolsProtocolMethodAsync("HeapProfiler.collectGarbage", "{}");
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
            {
                // The page went away meanwhile; whatever replaces it starts clean.
            }
        }
    }

    /// <summary>
    /// Development aid (<c>--page-heap</c>): what the hidden page holds, in KB — its JavaScript
    /// heap (used, of reserved), the browser engine's own objects, and buffers. Logged once a
    /// minute it shows the garbage building up and each collection taking it back; a floor
    /// that rises from one collection to the next would be memory that is really held.
    /// </summary>
    public async Task<string?> PageHeapAsync()
    {
        if (!isPageReady || controller is null) return null;
        try
        {
            var webView = await WebViewAsync();
            using var reply = JsonDocument.Parse(await webView.CallDevToolsProtocolMethodAsync("Runtime.getHeapUsage", "{}"));
            long Kilobytes(string name) =>
                reply.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (long)(value.GetDouble() / 1024) : -1;
            return $"js {Kilobytes("usedSize")} of {Kilobytes("totalSize")} KB, engine objects {Kilobytes("embedderHeapUsedSize")} KB, buffers {Kilobytes("backingStorageSize")} KB";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    // MARK: - Page readiness

    /// <summary>
    /// Ensures the hidden web view has a claude.ai page loaded, so <c>fetch()</c> runs with the
    /// right origin and cookies. Concurrent callers share one load, and none waits more than
    /// 30 s — a page that never becomes ready (a stuck Cloudflare challenge) must fail its
    /// callers, or polling would stop silently.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellation)
    {
        if (isPageReady && !needsReload) return;
        var load = loading ??= LoadAsync();
        try
        {
            await load.WaitAsync(ReadinessTimeout, cancellation);
        }
        catch (TimeoutException)
        {
            AppLogger.Shared.Error("page readiness timed out");
            needsReload = true;
            // A web view still being created after 30 s is not coming: the next call starts another.
            if (creating is { IsCompleted: false }) creating = null;
            throw new ApiException(ApiErrorKind.NetworkError, L.T("Page load timed out"));
        }
        finally
        {
            if (ReferenceEquals(loading, load) && (load.IsCompleted || needsReload)) loading = null;
        }
    }

    private async Task LoadAsync()
    {
        isPageReady = false;
        var webView = await WebViewAsync();
        var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only the navigation this load starts counts. Starting it cancels one still in flight
        // (a load that stalled past its timeout); that one then completes too, as a failure,
        // and would otherwise be taken for the result of this load.
        ulong? own = null;
        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) => own ??= e.NavigationId;
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == own) navigated.TrySetResult(e);
        }
        webView.NavigationStarting += OnStarting;
        webView.NavigationCompleted += OnCompleted;
        try
        {
            webView.Navigate(useFullPage ? Origin : LightHostPage);
            var result = await navigated.Task;
            // A status of 0 means the request never got an answer. A challenge page does
            // answer (403) and then reloads itself, so it is handled by the title check below.
            if (!result.IsSuccess && result.HttpStatusCode == 0)
            {
                needsReload = true;
                // The status is an enum name ("Unknown", "Disconnected"): for the log, not the popover.
                AppLogger.Shared.Error($"page load failed: {result.WebErrorStatus}");
                throw new ApiException(ApiErrorKind.NetworkError, FetchFailures.Unreachable);
            }
        }
        finally
        {
            webView.NavigationStarting -= OnStarting;
            webView.NavigationCompleted -= OnCompleted;
        }

        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        while (true)
        {
            var title = await webView.ExecuteScriptAsync("document.title");
            // Known limit: recognizes the interstitial only by Cloudflare's English title.
            if (!title.Contains("just a moment", StringComparison.OrdinalIgnoreCase)) break;
            if (DateTimeOffset.UtcNow > deadline)
            {
                needsReload = true;
                throw new ApiException(ApiErrorKind.NetworkError, L.T("Page load timed out"));
            }
            await Task.Delay(500);
        }
        isPageReady = true;
        needsReload = false;
    }

    // MARK: - API calls

    /// <summary>Fetches the signed-in user's account profile.</summary>
    public async Task<AccountInfo> FetchAccountInfoAsync(CancellationToken cancellation)
    {
        await EnsureReadyAsync(cancellation);
        return AccountInfo.Parse(await FetchJsonStringAsync("/api/account"));
    }

    /// <summary>Fetches current usage windows for the user's organisation.</summary>
    public async Task<UsageResponse> FetchUsageAsync(CancellationToken cancellation)
    {
        await EnsureReadyAsync(cancellation);
        var orgId = await ResolveOrgIdAsync();
        var json = await FetchJsonStringAsync($"/api/organizations/{orgId}/usage");
        try
        {
            return UsageResponse.Parse(json);
        }
        catch (ApiDecodeException e)
        {
            // Log the payload head so an API format change is diagnosable from the log file.
            AppLogger.Shared.Error($"usage decode failed: {e.Message} — payload: {json[..Math.Min(500, json.Length)]}");
            throw;
        }
    }

    private async Task<string> ResolveOrgIdAsync()
    {
        if (cachedOrgId is { } cached) return cached;
        var organizations = Organization.ParseList(await FetchJsonStringAsync("/api/organizations"));
        if (organizations.Count == 0) throw new ApiException(ApiErrorKind.NoOrganization);
        cachedOrgId = organizations[0].Uuid;
        CachedOrgName = organizations[0].Name;
        return organizations[0].Uuid;
    }

    /// <summary>
    /// Runs <c>fetch(path)</c> inside the page and returns the response body as a JSON string.
    ///
    /// A Cloudflare challenge (<c>cf-mitigated: challenge</c>, checked before the status) throws
    /// <c>CF_CHALLENGE</c>; any other non-2xx throws <c>HTTP_&lt;status&gt;</c>. The script runs
    /// through the DevTools <c>Runtime.evaluate</c> call because it awaits the promise and
    /// reports the thrown error; WebView2's own script calls return <c>{}</c> for a promise.
    /// The in-page 30 s abort is the timeout that matters: nothing here can cancel a fetch.
    /// </summary>
    private async Task<string> FetchJsonStringAsync(string path)
    {
        var webView = await WebViewAsync();
        if (SimulateChallengeOnce)
        {
            SimulateChallengeOnce = false;
            throw MapFailure("Error: CF_CHALLENGE");
        }
        // The path travels as a JSON string literal, never spliced in raw.
        var expression =
            "(async () => {"
            + $" const r = await fetch({JsonSerializer.Serialize(path)}, {{ credentials: 'include', signal: AbortSignal.timeout({FetchTimeoutMilliseconds}) }});"
            + " if (r.headers.get('cf-mitigated') === 'challenge') throw new Error('CF_CHALLENGE');"
            + " if (!r.ok) throw new Error('HTTP_' + r.status);"
            + " return JSON.stringify(await r.json());"
            + " })()";
        var parameters = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });

        string reply;
        try
        {
            reply = await webView.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters)
                .WaitAsync(TimeSpan.FromMilliseconds(FetchTimeoutMilliseconds + 15000));
        }
        catch (Exception e) when (e is COMException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            isPageReady = false;
            needsReload = true;
            throw new ApiException(ApiErrorKind.NetworkError, e.Message);
        }

        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var details))
        {
            throw MapFailure(ThrownMessage(details));
        }
        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            CollectGarbageIfDue(webView);
            return value.GetString()!;
        }
        throw new ApiException(ApiErrorKind.InvalidResponse);
    }

    /// <summary>The first line of the error a script threw, e.g. <c>"Error: HTTP_401"</c>.</summary>
    internal static string ThrownMessage(JsonElement exceptionDetails)
    {
        string? message = null;
        if (exceptionDetails.TryGetProperty("exception", out var exception)
            && exception.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
        {
            message = description.GetString();
        }
        else if (exceptionDetails.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            message = text.GetString();
        }
        message ??= "script failed";
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }

    /// <summary>Turns a thrown fetch token into an <see cref="ApiException"/>, applying each failure's side effects.</summary>
    private ApiException MapFailure(string message)
    {
        switch (FetchFailures.Classify(message))
        {
            case FetchFailure.Challenge:
                // Not a session problem — a network error, so it never counts toward expiry.
                // Only a top-level load of a real page passes the challenge.
                isPageReady = false;
                needsReload = true;
                if (!useFullPage)
                {
                    useFullPage = true;
                    AppLogger.Shared.Info("Cloudflare challenged a fetch from the light host page — switching to the full claude.ai page");
                }
                return new ApiException(ApiErrorKind.NetworkError, L.T("Cloudflare check — retrying"));
            case FetchFailure.Unauthorized:
                // The retry must come from a freshly loaded page, not the document that just failed.
                isPageReady = false;
                needsReload = true;
                cachedOrgId = null;
                CachedOrgName = null;
                return new ApiException(ApiErrorKind.Unauthorized);
            case FetchFailure.RateLimited:
                return new ApiException(ApiErrorKind.RateLimited);
            case FetchFailure.NotFound:
                // The cached org may no longer exist for this user — resolve the list again
                // instead of failing on the stale id until restart.
                cachedOrgId = null;
                CachedOrgName = null;
                return new ApiException(ApiErrorKind.HttpError, message);
            case FetchFailure.Http:
                return new ApiException(ApiErrorKind.HttpError, message);
            default:
                return new ApiException(ApiErrorKind.NetworkError, FetchFailures.NetworkDetail(message));
        }
    }
}
