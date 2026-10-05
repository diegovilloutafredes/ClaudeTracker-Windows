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
    private string failedInstallVersion = "";
    private int failedInstallCount;
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
    /// it itself, "Download" (the release page) when it cannot or when its install failed.
    /// Null while an install is under way, and with nothing available.
    /// </summary>
    public string? ActionLabel => (Phase, AvailableUpdate) switch
    {
        (_, null) or (UpdatePhase.Downloading or UpdatePhase.Installing, _) => null,
        (UpdatePhase.Idle, { DownloadUrl: not null }) => L.T("Install"),
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
        if (Phase == UpdatePhase.Idle && AvailableUpdate?.DownloadUrl is not null) DownloadAndInstall();
        else OpenReleasePage();
    }

    /// <summary>
    /// Loads what earlier runs saved, arms the periodic check, and checks once ten seconds
    /// after launch. Not done in the constructor, so the object can exist without the network.
    /// </summary>
    public void Start()
    {
        lastNotifiedVersion = settings.GetString(PrefKey.LastNotifiedUpdateVersion) ?? "";
        failedInstallVersion = settings.GetString(PrefKey.FailedInstallVersion) ?? "";
        failedInstallCount = settings.GetInt(PrefKey.FailedInstallCount) ?? 0;
        if (settings.GetDouble(PrefKey.UpdateCheckInterval) is { } saved && saved >= 4 * 3600) nextCheckInterval = saved;
        SchedulePeriodicCheck();
        _ = FirstCheckAsync();

        async Task FirstCheckAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
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

    private async Task CheckAsync()
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
                    return;
                }
                json = await response.Content.ReadAsStringAsync(limit.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException)
            {
                AppLogger.Shared.Error($"update check failed: {e.Message}");
                return;
            }

            var (found, dates) = Updates.ParseGitHubReleases(json, currentVersion);
            if (found is not null)
            {
                var update = Installable(found);
                AvailableUpdate = update;
                // Told once per version, across restarts.
                if (lastNotifiedVersion != update.Version)
                {
                    lastNotifiedVersion = update.Version;
                    settings.Set(PrefKey.LastNotifiedUpdateVersion, update.Version);
                    AppLogger.Shared.Info($"update: v{update.Version} is available" + (update.DownloadUrl is null ? " (as a download only)" : ""));
                    if (AutoUpdate && update.DownloadUrl is not null)
                    {
                        if (Phase == UpdatePhase.Idle) TriggerAutoInstall(update);
                    }
                    else
                    {
                        ToastHost.Shared.Show(L.T("Update available"), L.F("v%@ is ready — open Settings to install", update.Version), ToastKind.Update, 12, permanent: false);
                    }
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
        ToastHost.Shared.Show(L.T("Update available"), L.F("v%@ found — installing in ~10s", update.Version), ToastKind.Update, 12, permanent: false);
        _ = CountDownAsync();

        async Task CountDownAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(Updates.AutoInstallCountdownSeconds));
            // Asked again: auto-install may have been switched off during the countdown.
            if (AutoUpdate) DownloadAndInstall();
        }
    }

    /// <summary>
    /// Downloads the available update's setup, checks it, and runs it. The setup asks this
    /// copy to quit when it is ready to replace it, and starts the new one when it is done.
    /// </summary>
    public void DownloadAndInstall()
    {
        if (AvailableUpdate is not { DownloadUrl: { } download, SignatureUrl: { } signatureUrl } update || Phase != UpdatePhase.Idle) return;
        (Phase, FailureMessage) = (UpdatePhase.Downloading, null);
        Raise();
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                // A folder of its own, emptied first: nothing is carried from one attempt to the next.
                if (Directory.Exists(AppPaths.Updates)) Directory.Delete(AppPaths.Updates, recursive: true);
                Directory.CreateDirectory(AppPaths.Updates);
                var setup = Path.Combine(AppPaths.Updates, Updates.InstallerAssetName);
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using (var response = await Http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, limit.Token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(setup);
                    await response.Content.CopyToAsync(file, limit.Token);
                }
                var signature = await Http.GetByteArrayAsync(signatureUrl, limit.Token);

                // Nothing is run until the file verifies against the embedded key: HTTPS only
                // proves the bytes came from GitHub, not from the maintainer. And it must be
                // the newer version it was offered as: the release was found up to a day ago,
                // and its file could have been replaced since.
                var details = FileVersionInfo.GetVersionInfo(setup);
                var version = Updates.SetupVersion(details.FileMajorPart, details.FileMinorPart, details.FileBuildPart);
                var running = currentVersion;
                var verdict = await Task.Run(() => Updates.JudgeSetup(File.ReadAllBytes(setup), signature, Updates.SigningPublicKey, version, running));
                if (verdict == SetupVerdict.SignatureInvalid) throw new UpdateException(L.T("Update signature is invalid"), signatureInvalid: true);
                if (verdict == SetupVerdict.NotNewer) throw new UpdateException(L.T("Update package version mismatch"));

                Phase = UpdatePhase.Installing;
                Raise();

                AppLogger.Shared.Info($"update: running the setup for v{version}; it closes this copy when it is ready to replace it");
                using var process = Process.Start(new ProcessStartInfo(setup, Updates.SilentInstallArguments) { UseShellExecute = false })
                    ?? throw new UpdateException(L.F("Setup stopped before installing (code %d)", -1));
                await process.WaitForExitAsync();
                // Still here: the setup ended without replacing this copy. It asks the app to
                // quit before it touches a file, so a setup that fails early leaves it running.
                throw new UpdateException(L.F("Setup stopped before installing (code %d)", process.ExitCode));
            }
            catch (Exception e) when (e is UpdateException or HttpRequestException or OperationCanceledException or IOException
                                          or UnauthorizedAccessException or Win32Exception)
            {
                // The app's own words for its own verdicts; for anything else — the network,
                // the disk — one plain sentence, and what was thrown goes to the log.
                (Phase, FailureMessage) = (UpdatePhase.Failed, e is UpdateException ? e.Message : L.T("Couldn't download the update"));
                if (e is UpdateException { SignatureInvalid: true })
                {
                    signatureRejectedVersion = update.Version;
                    AvailableUpdate = Updates.InstallableUpdate(update, update.Version);
                }
                RecordInstallFailure(update.Version, e);
                DiscardDownload();
                Raise();
            }
        }
    }

    /// <summary>
    /// Forgets that the version was announced, so the next check tries its install again: one
    /// failure in passing must not leave the release to be found by hand in Settings. After
    /// <see cref="Updates.MaxAutoInstallAttempts"/> failures of one release it stays announced,
    /// retries stop, and one toast points at the manual way.
    /// </summary>
    private void RecordInstallFailure(string version, Exception error)
    {
        var failures = Updates.InstallFailureCount(version, failedInstallVersion, failedInstallCount);
        failedInstallVersion = version;
        failedInstallCount = failures;
        settings.Set(PrefKey.FailedInstallVersion, version);
        settings.Set(PrefKey.FailedInstallCount, failures);
        AppLogger.Shared.Error($"auto-update failed (v{version}, #{failures}): {error.Message}");

        if (Updates.ShouldRetryAutoInstall(failures))
        {
            lastNotifiedVersion = "";
            settings.Remove(PrefKey.LastNotifiedUpdateVersion);
        }
        else if (failures == Updates.MaxAutoInstallAttempts)
        {
            ToastHost.Shared.Show(L.T("Update failed"), L.F("Couldn't install v%@ automatically — open Settings to install it", version), ToastKind.Pace, 12, permanent: false);
        }
    }

    /// <summary>A setup that was refused, or only half arrived, is not kept: it is no use and it is a program.</summary>
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
        // Each call sets its own limit: ten seconds suit a check, not a download.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API refuses a request that does not say who is asking.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeTracker-Windows");
        return client;
    }

    private sealed class UpdateException(string message, bool signatureInvalid = false) : Exception(message)
    {
        public bool SignatureInvalid { get; } = signatureInvalid;
    }
}
