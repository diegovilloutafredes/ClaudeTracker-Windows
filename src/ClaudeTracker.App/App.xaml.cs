using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeTracker.Core;
using Microsoft.Win32;

namespace ClaudeTracker.App;

/// <summary>
/// Application entry point. The app has no main window: it lives in the system tray, and the
/// popover is the UI. It keeps running until "Quit".
/// </summary>
public partial class App : Application
{
    private const string ShowPopoverSignalName = @"Local\ClaudeTracker.ShowPopover";
    private const string QuitSignalName = @"Local\ClaudeTracker.Quit";

    private Mutex? instanceMutex;
    private EventWaitHandle? showPopoverSignal;
    private RegisteredWaitHandle? showPopoverWait;
    private EventWaitHandle? quitSignal;
    private RegisteredWaitHandle? quitWait;
    private UsageViewModel? viewModel;
    private TrayIcon? tray;
    private PopoverWindow? popover;
    private DispatcherTimer? clock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Development aid: run this copy in another language without changing Windows'.
        // "es" changes the words; "es-CL" also the regional format (dates, numbers).
        if (ArgumentAfter(e.Args, "--language") is { } language)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = culture;
                if (!culture.IsNeutralCulture) CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = culture;
            }
            catch (CultureNotFoundException)
            {
                // Not a language Windows knows: the copy runs as if the switch were not there.
            }
        }

        // One instance per user session: a second copy would poll the same accounts and fight
        // over the same browser profiles. It asks the running copy to show itself instead.
        instanceMutex = new Mutex(initiallyOwned: true, @"Local\ClaudeTracker.SingleInstance", out var isFirstInstance);
        // "--quit" closes the running app: the copy started with it raises the running one's
        // quit signal, and starts nothing. The setup and the uninstaller raise that signal
        // themselves before they touch the app's files, and fall back on this.
        var quit = e.Args.Contains("--quit");
        if (!isFirstInstance || quit)
        {
            if (isFirstInstance) instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            instanceMutex = null;
            if (quit) Signal(QuitSignalName);
            else AskRunningCopyToShowPopover();
            Shutdown();
            return;
        }
        showPopoverSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowPopoverSignalName);
        showPopoverWait = ThreadPool.RegisterWaitForSingleObject(
            showPopoverSignal, (_, _) => Dispatcher.BeginInvoke(() => popover?.ShowBesideTray()), null, Timeout.Infinite, executeOnlyOnce: false);
        quitSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, QuitSignalName);
        quitWait = ThreadPool.RegisterWaitForSingleObject(
            quitSignal, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                AppLogger.Shared.Info("asked to quit through the quit signal: a setup or the uninstaller is about to replace this copy");
                Shutdown();
            }), null, Timeout.Infinite, executeOnlyOnce: true);

        // A failure in one handler must not take the tray app down; it is logged instead.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Shared.Error($"unhandled exception: {args.Exception}");
            args.Handled = true;
        };

        // Work is started without being awaited all over the app. Nothing should escape from
        // it, and if something does it must at least leave a trace. API errors are expected
        // here: a load that was given up on fails later, with nobody left waiting for it.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (args.Exception.InnerExceptions.Any(inner => inner is not ApiException))
            {
                AppLogger.Shared.Error($"unobserved task exception: {args.Exception}");
            }
            args.SetObserved();
        };

        // The Windows 11 look for standard controls, following the system's light or dark mode.
        ThemeMode = ThemeMode.System;

        Directory.CreateDirectory(AppPaths.Data);
        var settings = new SettingsStore(AppPaths.Settings, AppLogger.Shared.Error);
        var accounts = new AccountStore(AppPaths.Data, AppLogger.Shared.Error);
        viewModel = new UsageViewModel(settings, accounts);
        popover = new PopoverWindow(viewModel) { NeverTakesFocus = e.Args.Contains("--no-focus") };
        tray = new TrayIcon();
        tray.Pressed += popover.NoteTrayPress;
        tray.Clicked += popover.Toggle;
        tray.OpenRequested += popover.Open;
        tray.SettingsRequested += () => SettingsWindow.Open(viewModel);
        popover.SettingsRequested += () => SettingsWindow.Open(viewModel);
        // Toasts share the popover's corner: while it shows, they stack past it.
        ToastHost.Shared.Obstacle = () => popover.ScreenBounds;
        popover.IsVisibleChanged += (_, _) => ToastHost.Shared.Arrange();
        popover.SizeChanged += (_, _) => Dispatcher.BeginInvoke(ToastHost.Shared.Arrange);
        tray.QuitRequested += Shutdown;
        viewModel.Changed += RefreshTray;

        // Light/dark or taskbar colour changed: both the icon and the popover depend on it.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Back from sleep: a release may have come out meanwhile.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // The clock was set, or the time zone changed: every time on screen is read from it.
        SystemEvents.TimeChanged += OnTimeChanged;

        // Staleness and "Resets in …" depend on the clock, not only on new data.
        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        clock.Tick += (_, _) =>
        {
            RefreshTray();
            if (popover.IsVisible) popover.Render();
        };
        clock.Start();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        AppLogger.Shared.Info($"ClaudeTracker {version} started");
        // Development aid: host the hidden browser on the full claude.ai page from the start.
        if (e.Args.Contains("--full-host-page"))
        {
            ClaudeApiClient.StartOnFullPage = true;
            AppLogger.Shared.Info("host page: the full claude.ai page (--full-host-page)");
        }
        // Development aid: place the popover and the toasts as if the taskbar were on another
        // edge of the screen, on a PC whose taskbar cannot or should not be moved to look.
        if (ArgumentAfter(e.Args, "--taskbar-edge") is { } side
            && Enum.TryParse<ScreenEdge>(side, ignoreCase: true, out var pretended) && Enum.IsDefined(pretended))
        {
            Taskbar.Pretend = pretended;
            AppLogger.Shared.Info($"the taskbar is taken to be on the {pretended.ToString().ToLowerInvariant()} edge (--taskbar-edge)");
        }
        // Development aids: a reset on the next poll; any pace counts as worth a warning.
        UsageViewModel.SimulateResetOnce = e.Args.Contains("--reset-once");
        UsageViewModel.PaceAlertAlways = e.Args.Contains("--pace-alert-always");
        // Development aid: the first fetch fails as a Cloudflare challenge would.
        if (e.Args.Contains("--challenge-once"))
        {
            ClaudeApiClient.SimulateChallengeOnce = true;
            AppLogger.Shared.Info("the first fetch will be treated as challenged (--challenge-once)");
        }
        // Development aid: read the releases from somewhere else. A setup from there is run
        // no more readily than one from GitHub: only with a signature the embedded key verifies.
        if (ArgumentAfter(e.Args, "--update-feed") is { } feed)
        {
            viewModel.Updater.FeedUrl = feed;
            AppLogger.Shared.Info($"updates are read from {feed} (--update-feed)");
        }
        viewModel.JustInstalled = e.Args.Contains("--just-installed");
        viewModel.Updater.MustWait = () => LoginWindow.IsOpen;
        viewModel.Start();
        viewModel.Updater.Start();
        RefreshTray();
        if (e.Args.Contains("--page-heap")) StartPageHeapLog();

        // Opening the app by hand shows it: Windows 11 puts a new tray icon in the overflow
        // menu, so the icon alone would leave a first launch with nothing on screen. A start
        // that nobody asked for (at sign-in to Windows) passes --background and stays quiet.
        if (!e.Args.Contains("--background")) popover.ShowBesideTray();
    }

    /// <summary>
    /// Development aid (<c>--page-heap</c>): logs what the hidden page holds, once a minute.
    /// </summary>
    private void StartPageHeapLog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            if (viewModel?.Client is { } client && await client.PageHeapAsync() is { } found) AppLogger.Shared.Info($"page heap: {found}");
        };
        timer.Start();
    }

    /// <summary>
    /// Run by a second copy before it exits. Launching the app again is how someone who cannot
    /// find the tray icon asks to see it.
    /// </summary>
    private static void AskRunningCopyToShowPopover()
    {
        // This process was just started by the user, so it may take the foreground; the
        // running copy may not, unless this one lets it.
        Native.AllowSetForegroundWindow(Native.AnyProcess);
        Signal(ShowPopoverSignalName);
    }

    /// <summary>Raises a signal of the running copy, if there is one.</summary>
    private static void Signal(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return;
            using (signal) signal.Set();
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy was started as an administrator and this one was not: Windows
            // lets this one find the signal and not raise it. This runs before anything is
            // there to catch a failure, so uncaught it ended the second copy as a crash.
        }
    }

    /// <summary>The argument that follows a switch on the command line, if both are there.</summary>
    private static string? ArgumentAfter(string[] arguments, string name)
    {
        var at = Array.IndexOf(arguments, name);
        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(() =>
        {
            AppLogger.Shared.Info("back from sleep: checking for updates");
            // A laptop that slept may have travelled.
            ReadTimeZoneAgain();
            viewModel?.Updater.CheckAfterWake();
        });
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ReadTimeZoneAgain);

    /// <summary>
    /// .NET reads the time zone once and keeps it for as long as the process lives. Without
    /// this a PC that changed zone showed every reset time, chart axis and pointer time on
    /// the old zone's clock until the app was restarted.
    /// </summary>
    private void ReadTimeZoneAgain()
    {
        var before = TimeZoneInfo.Local;
        TimeZoneInfo.ClearCachedData();
        var now = TimeZoneInfo.Local;
        if (before.Id != now.Id || before.BaseUtcOffset != now.BaseUtcOffset)
        {
            AppLogger.Shared.Info($"time zone changed from {before.Id} to {now.Id}: times are shown on the new one");
        }
        Redraw();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(Redraw);

    /// <summary>Everything that is on screen, drawn again: its colours or its clock changed under it.</summary>
    private void Redraw()
    {
        RefreshTray();
        if (popover?.IsVisible == true) popover.Render();
        // Settings redraws when the app has news or when it is clicked; with nobody signed
        // in there is no news, and it kept the old theme's colours under the new one's controls.
        SettingsWindow.RefreshCurrent();
    }

    private void RefreshTray()
    {
        if (viewModel is null || tray is null) return;
        tray.Update(viewModel.StatusText, viewModel.StatusUrgency, viewModel.StatusDescription);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        clock?.Stop();
        LoginWindow.CloseCurrent();
        SettingsWindow.CloseCurrent();
        ToastHost.Shared.DismissAll();
        viewModel?.Shutdown();
        tray?.Dispose();
        showPopoverWait?.Unregister(null);
        showPopoverSignal?.Dispose();
        quitWait?.Unregister(null);
        quitSignal?.Dispose();
        // The mutex is not given up here: Windows takes it back when the process ends. The
        // setup and the uninstaller read "no mutex" as "nothing of the app is in use any
        // more", and released here it said so while the process still had its files open.
        base.OnExit(e);
    }
}
