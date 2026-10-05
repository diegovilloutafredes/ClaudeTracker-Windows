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

    private Mutex? instanceMutex;
    private EventWaitHandle? showPopoverSignal;
    private RegisteredWaitHandle? showPopoverWait;
    private UsageViewModel? viewModel;
    private TrayIcon? tray;
    private PopoverWindow? popover;
    private DispatcherTimer? clock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per user session: a second copy would poll the same accounts and fight
        // over the same browser profiles. It asks the running copy to show itself instead.
        instanceMutex = new Mutex(initiallyOwned: true, @"Local\ClaudeTracker.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            instanceMutex = null;
            AskRunningCopyToShowPopover();
            Shutdown();
            return;
        }
        showPopoverSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowPopoverSignalName);
        showPopoverWait = ThreadPool.RegisterWaitForSingleObject(
            showPopoverSignal, (_, _) => Dispatcher.BeginInvoke(() => popover?.ShowBesideTray()), null, Timeout.Infinite, executeOnlyOnce: false);

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
        viewModel.Start();
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
        if (!EventWaitHandle.TryOpenExisting(ShowPopoverSignalName, out var signal)) return;
        using (signal) signal.Set();
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
        clock?.Stop();
        LoginWindow.CloseCurrent();
        SettingsWindow.CloseCurrent();
        ToastHost.Shared.DismissAll();
        viewModel?.Shutdown();
        tray?.Dispose();
        showPopoverWait?.Unregister(null);
        showPopoverSignal?.Dispose();
        if (instanceMutex is not null)
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
