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
        // "--quit" is how the setup and the uninstaller close the app before they touch its
        // files: the copy started with it tells the running one to quit, and starts nothing.
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
                AppLogger.Shared.Info("asked to quit by another copy (--quit): a setup or the uninstaller is about to replace this one");
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
        tray.OpenRequested += popover.ShowNearCursor;
        tray.SettingsRequested += () => SettingsWindow.Open(viewModel);
        popover.SettingsRequested += () => SettingsWindow.Open(viewModel);
        // Toasts share the popover's corner: while it shows, they stack above it.
        ToastHost.Shared.Obstacle = () => popover.ScreenBounds;
        popover.IsVisibleChanged += (_, _) => ToastHost.Shared.Arrange();
        popover.SizeChanged += (_, _) => Dispatcher.BeginInvoke(ToastHost.Shared.Arrange);
        tray.QuitRequested += Shutdown;
        viewModel.Changed += RefreshTray;

        // Light/dark or taskbar colour changed: both the icon and the popover depend on it.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Back from sleep: a release may have come out meanwhile.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

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
        if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return;
        using (signal) signal.Set();
    }

    /// <summary>The argument that follows a switch on the command line, if both are there.</summary>
    private static string? ArgumentAfter(string[] arguments, string name)
    {
        var at = Array.IndexOf(arguments, name);
        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => viewModel?.Updater.CheckForUpdates());
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshTray();
            if (popover?.IsVisible == true) popover.Render();
        });

    private void RefreshTray()
    {
        if (viewModel is null || tray is null) return;
        tray.Update(viewModel.StatusText, viewModel.StatusUrgency, viewModel.StatusDescription);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
        if (instanceMutex is not null)
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
