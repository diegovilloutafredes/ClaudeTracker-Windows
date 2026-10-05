using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using ClaudeTracker.Core;

namespace ClaudeTracker.App;

/// <summary>Where a download-and-install stands.</summary>
internal enum UpdatePhase
{
    Idle,
    Downloading,
    Installing,
    Failed,
}

/// <summary>
/// The in-app update flow: finding a newer release of this app on GitHub, the adaptive check
/// schedule, the optional auto-install, and the download and install themselves. The Windows
/// twin of the Mac app's <c>UpdateService</c>; what it decides is in <see cref="Updates"/>.
///
/// An update is a setup file. It is run only if its signature verifies against the key
/// compiled into the app, and only if the file itself is a newer version than this copy.
/// Lives on the UI thread, like everything that raises a change.
/// </summary>
internal sealed class UpdateService(SettingsStore settings)
{
    private static readonly HttpClient Http = MakeClient();

    private readonly string currentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0";
    private CancellationTokenSource? periodic;
    private string lastNotifiedVersion = "";
    /// <summary>
    /// The release whose automatic install was last set off, and how many times. Counted when
    /// an install starts, not when it fails: one that ends without this copy living to see
    /// how — the PC shut down, the setup closed the app and then stopped — is still one of
    /// the <see cref="Updates.MaxAutoInstallAttempts"/>.
    /// </summary>
    private string attemptedVersion = "";
    private int attemptCount;
    /// <summary>True from "installing in ~10s" until that install starts or is called off.</summary>
    private bool countingDown;
    /// <summary>The release whose signature failed this session (<see cref="Updates.InstallableUpdate"/>).</summary>
    private string? signatureRejectedVersion;
    /// <summary>Seconds between checks, from how often releases come out. Clamped to 4–24 hours.</summary>
    private double nextCheckInterval = 12 * 3600;

    /// <summary>Raised after anything a view shows here has changed.</summary>
    public event Action? Changed;

    public UpdateInfo? AvailableUpdate { get; private set; }

    public bool IsChecking { get; private set; }

    public UpdatePhase Phase { get; private set; }

    /// <summary>Why the last install failed, while <see cref="Phase"/> is <see cref="UpdatePhase.Failed"/>.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>
    /// True while the app must not be closed under the user: a sign-in window is open. A setup
    /// that is ready to run waits for it — it would close the window, and with it a sign-in
    /// that is half done.
    /// </summary>
    public Func<bool> MustWait { get; set; } = () => false;

    /// <summary>
    /// Development aid (<c>--update-feed</c>): another address to read the releases from. What
    /// it serves is trusted no more than GitHub is: a setup is still run only with a signature
    /// the embedded key verifies.
    /// </summary>
    public string FeedUrl { get; set; } = Updates.ReleasesUrl;

    /// <summary>On by default: installs stay current with nothing to do. Settings opts out.</summary>
    public bool AutoUpdate
    {
        get => settings.GetBool(PrefKey.AutoUpdate) ?? true;
        set
        {
            if (value == AutoUpdate) return;
            settings.Set(PrefKey.AutoUpdate, value);
            SchedulePeriodicCheck();
            Raise();
        }
    }

    public string CheckIntervalLabel => Updates.CheckIntervalLabel(nextCheckInterval);

    /// <summary>
    /// What the button beside an available update says: "Install" while the app can install
    /// it itself — after an install that failed too, which is how to try again — and
    /// "Download" (the release page) when it cannot. Null while an install is under way, and
    /// with nothing available.
    /// </summary>
    public string? ActionLabel => (Phase, AvailableUpdate) switch
    {
        (_, null) or (UpdatePhase.Downloading or UpdatePhase.Installing, _) => null,
        (_, { DownloadUrl: not null }) => L.T("Install"),
        _ => L.T("Download"),
    };

    /// <summary>What is being done, while it is.</summary>
    public string? ProgressLabel => Phase switch
    {
        UpdatePhase.Downloading => L.T("Downloading…"),
        UpdatePhase.Installing => L.T("Installing…"),
        _ => null,
    };

    /// <summary>What that button does.</summary>
    public void Act()
    {
        if (AvailableUpdate?.DownloadUrl is null)
        {
            OpenReleasePage();
            return;
        }
        // A check clears a failure too, but with auto-install off none may come for days.
        if (Phase == UpdatePhase.Failed) (Phase, FailureMessage) = (UpdatePhase.Idle, null);
        DownloadAndInstall();
    }

    /// <summary>
    /// Loads what earlier runs saved, arms the periodic check, and checks once ten seconds
    /// after launch. Not done in the constructor, so the object can exist without the network.
    /// </summary>
    public void Start()
    {
        lastNotifiedVersion = settings.GetString(PrefKey.LastNotifiedUpdateVersion) ?? "";
        attemptedVersion = settings.GetString(PrefKey.FailedInstallVersion) ?? "";
        attemptCount = settings.GetInt(PrefKey.FailedInstallCount) ?? 0;
        if (settings.GetDouble(PrefKey.UpdateCheckInterval) is { } saved && saved >= 4 * 3600) nextCheckInterval = saved;
        SchedulePeriodicCheck();
        _ = FirstCheckAsync();

        async Task FirstCheckAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            // The setup that installed this copy is still on disk, and so is one whose install
            // a quit cut short. Left for now rather than for the start: the setup that started
            // this copy was still running then, with its own file open.
            if (Phase == UpdatePhase.Idle) DiscardDownload();
            CheckForUpdates();
        }
    }

    /// <summary>
    /// Reads the last ten releases, offers a newer one, and sets the check interval from how
    /// often they come out. Safe to call at any time: a check already running is the check.
    /// </summary>
    public void CheckForUpdates()
    {
        if (IsChecking) return;
        _ = CheckAsync();
    }

    /// <summary>
    /// The check made on waking. Windows says the PC is back before the network is: a check
    /// that could not reach the list is made once more a minute later, instead of leaving the
    /// release for the next one, hours away.
    /// </summary>
    public void CheckAfterWake()
    {
        if (IsChecking) return;
        _ = RunAsync();

        async Task RunAsync()
        {
            if (await CheckAsync()) return;
            await Task.Delay(TimeSpan.FromSeconds(Updates.WakeRetrySeconds));
            if (!IsChecking) await CheckAsync();
        }
    }

    /// <returns>True when the list of releases was read, whatever it held.</returns>
    private async Task<bool> CheckAsync()
    {
        if (Phase == UpdatePhase.Failed) (Phase, FailureMessage) = (UpdatePhase.Idle, null);
        IsChecking = true;
        Raise();
        try
        {
            string json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await Http.SendAsync(request, limit.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    AppLogger.Shared.Error($"update check failed: HTTP {(int)response.StatusCode}");
                    return false;
                }
                json = await response.Content.ReadAsStringAsync(limit.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException)
            {
                AppLogger.Shared.Error($"update check failed: {e.Message}");
                return false;
            }

            var (found, dates) = Updates.ParseGitHubReleases(json, currentVersion);
            if (found is not null)
            {
                var update = Installable(found);
                AvailableUpdate = update;
                if (ShouldAutoInstall(update))
                {
                    if (Phase == UpdatePhase.Idle && !countingDown) TriggerAutoInstall(update);
                }
                else if (Phase is not (UpdatePhase.Downloading or UpdatePhase.Installing))
                {
                    Announce(update);
                }
            }

            var computed = Updates.AdaptiveCheckInterval(dates);
            if (computed != nextCheckInterval)
            {
                nextCheckInterval = computed;
                settings.Set(PrefKey.UpdateCheckInterval, computed);
                AppLogger.Shared.Info($"update check interval adjusted to {(int)(computed / 3600)}h based on release cadence");
                if (AutoUpdate) SchedulePeriodicCheck();
            }
            return true;
        }
        finally
        {
            IsChecking = false;
            Raise();
        }
    }

    /// <summary>
    /// The update as this copy can take it. Only a copy the setup installed replaces itself:
    /// one run from a build folder is offered the release page.
    /// </summary>
    private UpdateInfo Installable(UpdateInfo update) => AppPaths.IsInstalled
        ? Updates.InstallableUpdate(update, signatureRejectedVersion)
        : new UpdateInfo(update.Version, update.ReleaseUrl, null);

    /// <summary>
    /// Whether a check sets the update's install off by itself: auto-install is on, this copy
    /// can install the release, and has not already started to
    /// <see cref="Updates.MaxAutoInstallAttempts"/> times.
    /// </summary>
    private bool ShouldAutoInstall(UpdateInfo update) =>
        AutoUpdate && update.DownloadUrl is not null
        && Updates.ShouldRetryAutoInstall(Updates.InstallAttempts(update.Version, attemptedVersion, attemptCount));

    /// <summary>Says, once per version and across restarts, that an update is there to be installed by hand.</summary>
    private void Announce(UpdateInfo update)
    {
        if (lastNotifiedVersion == update.Version) return;
        lastNotifiedVersion = update.Version;
        settings.Set(PrefKey.LastNotifiedUpdateVersion, update.Version);
        AppLogger.Shared.Info($"update: v{update.Version} is available" + (update.DownloadUrl is null ? " (as a download only)" : ""));
        ToastHost.Shared.Show(L.T("Update available"), L.F("v%@ is ready — open Settings to install", update.Version), ToastKind.Update, 12, permanent: false);
    }

    private void SchedulePeriodicCheck()
    {
        periodic?.Cancel();
        periodic = null;
        if (!AutoUpdate) return;
        var cancellation = periodic = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(nextCheckInterval);
        _ = LoopAsync();

        async Task LoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try { await Task.Delay(interval, cancellation.Token); }
                catch (OperationCanceledException) { return; }
                CheckForUpdates();
            }
        }
    }

    private void TriggerAutoInstall(UpdateInfo update)
    {
        // Counted before it starts, and saved before it is counted on. Counted only when it
        // failed, an install that never got to fail — the setup closed the app and then
        // stopped — was not counted at all: with the version marked as announced it was never
        // tried again, and without the mark it would be tried at every start, for ever.
        var attempt = Updates.InstallFailureCount(update.Version, attemptedVersion, attemptCount);
        if (!(settings.Set(PrefKey.FailedInstallVersion, update.Version) & settings.Set(PrefKey.FailedInstallCount, attempt)))
        {
            // Settings that cannot be read also read as "auto-install is on" when it may be off.
            AppLogger.Shared.Error($"update: v{update.Version} is left to be installed by hand: the attempt could not be saved, and uncounted attempts have no end");
            Announce(update);
            return;
        }
        (attemptedVersion, attemptCount) = (update.Version, attempt);
        AppLogger.Shared.Info($"update: v{update.Version} is available; installing it (automatic attempt {attempt} of {Updates.MaxAutoInstallAttempts})");
        ToastHost.Shared.Show(L.T("Update available"), L.F("v%@ found — installing in ~10s", update.Version), ToastKind.Update, 12, permanent: false);
        countingDown = true;
        _ = CountDownAsync();

        async Task CountDownAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(Updates.AutoInstallCountdownSeconds));
            countingDown = false;
            // Asked again: auto-install may have been switched off during the countdown.
            if (AutoUpdate) DownloadAndInstall(automatic: true);
        }
    }

    /// <summary>
    /// Downloads the available update's setup, checks it, and runs it. The setup asks this
    /// copy to quit when it is ready to replace it, and starts the new one when it is done.
    /// </summary>
    /// <param name="automatic">Set off by a check rather than by the button: one of the attempts that are counted.</param>
    public void DownloadAndInstall(bool automatic = false)
    {
        if (AvailableUpdate is not { DownloadUrl: { } download, SignatureUrl: { } signatureUrl } update || Phase != UpdatePhase.Idle) return;
        (Phase, FailureMessage) = (UpdatePhase.Downloading, null);
        Raise();
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                // A setup that runs out of room stops after it has closed the app.
                if (FreeSpace(AppPaths.Executable) < Updates.FreeSpaceToInstallBytes)
                {
                    throw new UpdateException(L.T("Not enough free disk space to install the update"));
                }

                // A folder of its own, emptied first: nothing is carried from one attempt to the next.
                if (Directory.Exists(AppPaths.Updates)) Directory.Delete(AppPaths.Updates, recursive: true);
                Directory.CreateDirectory(AppPaths.Updates);
                var setup = Path.Combine(AppPaths.Updates, Updates.InstallerAssetName);
                await DownloadAsync(download, setup);
                byte[] signature;
                using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(Updates.DownloadStallSeconds)))
                {
                    signature = await Http.GetByteArrayAsync(signatureUrl, patience.Token);
                }

                // One handle on the file, from reading it to running it: while it is held the
                // file can be read and run, and not written, replaced or removed. What is run
                // is then what was judged, however long a sign-in window keeps it waiting.
                await using var held = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[held.Length];
                await held.ReadExactlyAsync(bytes);

                // Nothing is run until the file verifies against the embedded key: HTTPS only
                // proves the bytes came from GitHub, not from the maintainer. And it must be
                // the newer version it was offered as: the release was found up to a day ago,
                // and its file could have been replaced since.
                var version = update.Version;
                var running = currentVersion;
                var verdict = await Task.Run(() => Updates.JudgeSetup(bytes, signature, Updates.SigningPublicKey, () => version = VersionOf(setup), running));
                if (verdict == SetupVerdict.SignatureInvalid) throw new UpdateException(L.T("Update signature is invalid"), signatureInvalid: true);
                if (verdict == SetupVerdict.NotNewer) throw new UpdateException(L.T("Update package version mismatch"));

                Phase = UpdatePhase.Installing;
                Raise();

                if (MustWait())
                {
                    AppLogger.Shared.Info($"update: v{version} is ready; waiting for the sign-in window to close before running its setup");
                    while (MustWait()) await Task.Delay(TimeSpan.FromSeconds(2));
                }
                AppLogger.Shared.Info($"update: running the setup for v{version}; it closes this copy when it is ready to replace it");
                using var process = Process.Start(new ProcessStartInfo(setup, Updates.SilentInstallArguments) { UseShellExecute = false })
                    ?? throw new UpdateException(L.F("Setup stopped before installing (code %d)", -1));
                await process.WaitForExitAsync();
                // Still here: the setup ended without replacing this copy. It asks the app to
                // quit before it touches a file, so a setup that fails early leaves it running.
                throw new UpdateException(L.F("Setup stopped before installing (code %d)", process.ExitCode));
            }
            catch (Exception e)
            {
                // Everything, not only what is expected: a failure nobody thought of must still
                // end the attempt. Left at "Downloading…", the app offers no button and installs
                // no later release until it is restarted.
                // The app's own words for its own verdicts; for anything else — the network,
                // the disk — one plain sentence, and what was thrown goes to the log.
                (Phase, FailureMessage) = (UpdatePhase.Failed, e is UpdateException ? e.Message : L.T("Couldn't download the update"));
                if (e is UpdateException { SignatureInvalid: true })
                {
                    signatureRejectedVersion = update.Version;
                    AvailableUpdate = Updates.InstallableUpdate(update, update.Version);
                }
                // First: nothing below may keep a refused setup on disk.
                DiscardDownload();
                RecordFailure(update.Version, e, automatic);
                Raise();
            }
        }
    }

    /// <summary>
    /// Fetches the setup into <paramref name="path"/>. Given up when nothing arrives for
    /// <see cref="Updates.DownloadStallSeconds"/>, or when more arrives than any setup of this
    /// app would hold: a list that is not GitHub's can name any address.
    /// </summary>
    private static async Task DownloadAsync(Uri address, string path)
    {
        var stall = TimeSpan.FromSeconds(Updates.DownloadStallSeconds);
        using var patience = new CancellationTokenSource(stall);
        using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, patience.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > Updates.LargestSetupBytes) throw TooLarge();
        await using var source = await response.Content.ReadAsStreamAsync(patience.Token);
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            patience.CancelAfter(stall);
            var read = await source.ReadAsync(buffer, patience.Token);
            if (read == 0) return;
            if ((total += read) > Updates.LargestSetupBytes) throw TooLarge();
            await file.WriteAsync(buffer.AsMemory(0, read), patience.Token);
        }

        static IOException TooLarge() => new($"the setup offered is larger than {Updates.LargestSetupBytes / (1024 * 1024)} MB");
    }

    /// <summary>The version a setup file says it is. Asked only of a file whose signature has verified.</summary>
    private static string VersionOf(string setup)
    {
        var details = FileVersionInfo.GetVersionInfo(setup);
        return Updates.SetupVersion(details.FileMajorPart, details.FileMinorPart, details.FileBuildPart);
    }

    /// <summary>Free bytes on the drive a file is on. Null when Windows will not say (a network folder): then nothing is concluded.</summary>
    private static long? FreeSpace(string file)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(file)) ?? "").AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logs an install that failed. The attempt was counted when it was set off; when it was
    /// the last one this release is allowed, one toast points at the manual way.
    /// </summary>
    private void RecordFailure(string version, Exception error, bool automatic)
    {
        var attempts = Updates.InstallAttempts(version, attemptedVersion, attemptCount);
        var which = automatic ? $"automatic attempt {attempts} of {Updates.MaxAutoInstallAttempts}" : "asked for by hand";
        // What nobody expected is logged whole, to be found.
        var what = error is UpdateException or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception
            ? error.Message
            : error.ToString();
        AppLogger.Shared.Error($"update failed (v{version}, {which}): {what}");
        if (!automatic || Updates.ShouldRetryAutoInstall(attempts)) return;
        // This toast is the announcement: the next check has nothing to add to it.
        lastNotifiedVersion = version;
        settings.Set(PrefKey.LastNotifiedUpdateVersion, version);
        ToastHost.Shared.Show(L.T("Update failed"), L.F("Couldn't install v%@ automatically — open Settings to install it", version), ToastKind.Pace, 12, permanent: false);
    }

    /// <summary>
    /// A setup that was refused, or only half arrived, or has done its work, is not kept: it
    /// is no use and it is a program.
    /// </summary>
    private static void DiscardDownload()
    {
        try
        {
            if (Directory.Exists(AppPaths.Updates)) Directory.Delete(AppPaths.Updates, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLogger.Shared.Error($"couldn't remove the downloaded update: {e.Message}");
        }
    }

    /// <summary>Opens the release's page in the browser: the way to an update the app will not install itself.</summary>
    public void OpenReleasePage()
    {
        if (AvailableUpdate is not { } update || !Updates.IsWebLink(update.ReleaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(update.ReleaseUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLogger.Shared.Error($"couldn't open the release page: {e.Message}");
        }
    }

    public void Stop()
    {
        periodic?.Cancel();
        periodic = null;
    }

    private void Raise() => Changed?.Invoke();

    private static HttpClient MakeClient()
    {
        // Each call sets its own limit: ten seconds suit a check, not a download. What is read
        // whole into memory — the list of releases, a signature — is small, and held to that.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        // GitHub's API refuses a request that does not say who is asking.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeTracker-Windows");
        return client;
    }

    private sealed class UpdateException(string message, bool signatureInvalid = false) : Exception(message)
    {
        public bool SignatureInvalid { get; } = signatureInvalid;
    }
}
