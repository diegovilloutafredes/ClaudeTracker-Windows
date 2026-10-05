using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeTracker.Core;
using WinForms = System.Windows.Forms;

namespace ClaudeTracker.App;

/// <summary>
/// The popover shown when the tray icon is clicked: header, one row per usage window, footer.
/// It hides when it loses focus, and redraws from the view model whenever that changes.
/// </summary>
public partial class PopoverWindow : Window
{
    private readonly UsageViewModel viewModel;
    /// <summary>The screen corner the popover is pinned to, in pixels: its bottom-right, beside the tray.</summary>
    private (int Right, int Bottom)? anchor;
    private DateTimeOffset hiddenAt = DateTimeOffset.MinValue;
    private bool trayPressFoundItOpen;

    /// <summary>
    /// Development aid (<c>--no-focus</c>): show the popover without asking for the keyboard
    /// focus, which is what it looks like when Windows refuses it. That refusal cannot be
    /// provoked, and <see cref="HideIfNeverFocused"/> must still be seen working.
    /// </summary>
    internal bool NeverTakesFocus { get; set; }

    /// <summary>Where the popover is on screen, in pixels; null while it is hidden.</summary>
    internal System.Drawing.Rectangle? ScreenBounds
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!IsVisible || handle == IntPtr.Zero || !Native.GetWindowRect(handle, out var rect)) return null;
            return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
    }

    /// <summary>"Settings" was pressed in the footer.</summary>
    internal event Action? SettingsRequested;

    /// <summary>The Charts tab: one element, kept and brought up to date at each render.</summary>
    private readonly ChartsTab charts;
    /// <summary>"Usage" | "Charts". Kept too, so the tab holding the keyboard keeps it through a poll.</summary>
    private readonly Segmented tabPicker;
    private ContextMenu? accountMenu;
    /// <summary>The body's own elements, kept from render to render (<see cref="ViewCache"/>).</summary>
    private readonly ViewCache views = new();
    /// <summary>What each of the body's buttons does, by the button's name.</summary>
    private readonly Dictionary<string, Action> actions = [];

    /// <summary>The popover's width at 100%, in WPF units.</summary>
    private const double NaturalWidth = 340;
    private double appliedScale = 1;
    /// <summary>Hides a popover that never got the focus; see <see cref="HideIfNeverFocused"/>.</summary>
    private readonly DispatcherTimer unfocused = new() { Interval = TimeSpan.FromSeconds(8) };

    internal PopoverWindow(UsageViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        charts = new ChartsTab(viewModel);
        tabPicker = new Segmented("popover-tab", index => viewModel.SelectedTab = index);
        TabBar.Content = tabPicker;
        AutomationProperties.SetHeadingLevel(HeaderTitle, AutomationHeadingLevel.Level1);
        viewModel.Changed += () => { if (IsVisible) Render(); };
        Deactivated += (_, _) => HidePopover();
        unfocused.Tick += (_, _) => HideIfNeverFocused();
        // The height follows the content; keep the pinned corner still as it changes.
        SourceInitialized += (_, _) =>
        {
            RoundCorners();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepPinned);
        };
        QuitLink.Click += (_, _) => Application.Current.Shutdown();
        SettingsLink.Click += (_, _) => SettingsRequested?.Invoke();
        AccountMenuButton.Click += (_, _) => OpenAccountMenu();
        UpdateBannerAction.Click += (_, _) => viewModel.Updater.Act();
        foreach (var link in new[] { QuitLink, SettingsLink, AccountMenuButton })
        {
            link.MouseEnter += (_, _) => link.Foreground = Primary;
            link.MouseLeave += (_, _) => link.Foreground = Secondary;
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) HidePopover();
        };
    }

    /// <summary>
    /// Called when the tray icon is pressed, ahead of the click. Pressing the icon takes the
    /// focus away, which hides an open popover at once — so whether it was open has to be
    /// noted now. Judged at the click instead, by how recently it hid, a button held for a
    /// moment reopened the popover it had just closed.
    /// </summary>
    internal void NoteTrayPress() =>
        trayPressFoundItOpen = IsVisible || (DateTimeOffset.UtcNow - hiddenAt).TotalMilliseconds < 250;

    /// <summary>The tray icon was clicked: closes the popover that was open, or shows it above the click.</summary>
    internal void Toggle()
    {
        // The second test is the fallback for a click that arrives without its press having
        // been reported first: it is right for a quick click, which is nearly all of them.
        if (trayPressFoundItOpen || IsVisible || (DateTimeOffset.UtcNow - hiddenAt).TotalMilliseconds < 250)
        {
            trayPressFoundItOpen = false;
            HidePopover();
            return;
        }
        ShowNearCursor();
    }

    /// <summary>Shows the popover above the click that asked for it (the tray icon).</summary>
    internal void ShowNearCursor()
    {
        Native.GetCursorPos(out var cursor);
        ShowAbove(WinForms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea, cursor.X);
    }

    /// <summary>
    /// Shows the popover in the corner where the tray is — for a launch, which has no click to
    /// place it by.
    /// </summary>
    internal void ShowBesideTray()
    {
        var area = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).WorkingArea;
        ShowAbove(area, area.Right);
    }

    private void ShowAbove(System.Drawing.Rectangle area, int centerX)
    {
        const int margin = 12;
        // Centered on the given point horizontally, kept inside the work area; resting on its
        // bottom edge, which is the top of the taskbar.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        ApplyPopupSize(); // before the width is measured
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var widthPixels = (int)Math.Round(Width * scale);
        var right = Math.Clamp(centerX + widthPixels / 2, area.Left + widthPixels + margin, area.Right - margin);
        anchor = (right, area.Bottom - margin);
        Render();
        ShowActivated = !NeverTakesFocus;
        Show();
        PinToAnchor();
        if (!NeverTakesFocus)
        {
            Activate();
            Native.SetForegroundWindow(handle);
        }
        unfocused.Stop();
        unfocused.Start();
    }

    /// <summary>
    /// Applies the "Popup size" setting: everything inside is scaled as one, and the window is
    /// made as much wider. It has to be a layout transform — a render transform would draw the
    /// content larger without the window growing around it — and the width has to follow,
    /// because it is fixed on the window, outside what the transform reaches.
    /// </summary>
    private void ApplyPopupSize()
    {
        var scale = viewModel.PopupScale;
        if (scale == appliedScale) return;
        appliedScale = scale;
        Root.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        Width = NaturalWidth * scale;
    }

    /// <summary>The list behind the account's name in the header: every account, then "Add account".</summary>
    private void OpenAccountMenu()
    {
        var menu = new ContextMenu { PlacementTarget = AccountMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var account in viewModel.Accounts)
        {
            // A menu reads "_" as the mark of a shortcut letter; doubled, it is an underscore.
            // Checkable, or the tick is not drawn at all — and a screen reader is told nothing.
            var item = new MenuItem { Header = account.Label.Replace("_", "__"), IsCheckable = true, IsChecked = account.Id == viewModel.ActiveAccountId };
            item.Click += (_, _) => viewModel.SwitchAccount(account.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = L.T("Add account") };
        add.Click += (_, _) => viewModel.OpenLoginForNewAccount();
        menu.Items.Add(add);
        accountMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The popover hides when it loses the focus — but Windows does not always let a window
    /// take it (the user was typing elsewhere as it opened), and a window that never had the
    /// focus never loses it: it would sit on top of everything until clicked. Such a popover
    /// behaves like a notification instead and leaves by itself, unless the pointer is on it.
    /// </summary>
    private void HideIfNeverFocused()
    {
        // Asked of Windows, not of IsActive: a window that was refused the foreground still
        // counts as the active one of its own thread, and IsActive says true.
        if (Native.GetForegroundWindow() == new WindowInteropHelper(this).Handle)
        {
            unfocused.Stop(); // it has the focus: from here on, losing it hides it
            return;
        }
        if (IsMouseOver) return; // being read; look again at the next tick
        HidePopover();
    }

    private void HidePopover()
    {
        unfocused.Stop();
        if (!IsVisible) return;
        hiddenAt = DateTimeOffset.UtcNow;
        // Forget which button had the keyboard: a window gives focus back to it when it is
        // shown again, and a stray Space would then press "Quit".
        System.Windows.Input.FocusManager.SetFocusedElement(this, null);
        Hide();
        // A menu opened without a click — by a screen reader, on a popover that never had the
        // focus — closes with nothing but this: it would stay on screen over an empty corner.
        if (accountMenu is { } open) open.IsOpen = false;
        accountMenu = null;
        charts.Leave();
    }

    private void PinToAnchor()
    {
        if (anchor is not { } pinned || !IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        Native.SetWindowPos(handle, IntPtr.Zero, pinned.Right - (rect.Right - rect.Left), pinned.Bottom - (rect.Bottom - rect.Top), 0, 0,
                            Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate);
    }

    /// <summary>
    /// Keeps the popover's bottom-right corner on its anchor through every move and resize, by
    /// rewriting the position Windows is about to apply (WM_WINDOWPOSCHANGING).
    ///
    /// The height follows the content, and a window grows from its top-left corner: without
    /// this, the rows arriving after "Loading…" pushed the popover's lower half off the bottom
    /// of the screen. Correcting it afterwards from <c>SizeChanged</c> does not work — that
    /// event fires before the window itself has been resized, so there is nothing to measure
    /// yet — and would show the popover in the wrong place for a frame even if it did.
    /// </summary>
    private IntPtr KeepPinned(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int windowPosChanging = 0x0046;
        const uint noSize = 0x0001, noMove = 0x0002;
        if (message != windowPosChanging || anchor is not { } pinned) return IntPtr.Zero;

        var position = Marshal.PtrToStructure<WindowPos>(lParam);
        int width, height;
        if ((position.Flags & noSize) == 0)
        {
            (width, height) = (position.Width, position.Height);
        }
        else if ((position.Flags & noMove) == 0 && GetWindowRect(window, out var rect))
        {
            (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        else
        {
            return IntPtr.Zero; // neither moving nor resizing
        }
        position.X = pinned.Right - width;
        position.Y = pinned.Bottom - height;
        position.Flags &= ~noMove;
        Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private void RoundCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var preference = Native.DwmCornerRound;
        // Windows 11 only; on Windows 10 the call fails and the popover stays square.
        _ = Native.DwmSetWindowAttribute(handle, Native.DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    // MARK: - Rendering

    private bool isDark;

    private Brush Primary => Gray(isDark ? (byte)0xFF : (byte)0x1B);

    private Brush Secondary => Gray(isDark ? (byte)0xC5 : (byte)0x5C);

    private Brush Tertiary => Gray(isDark ? (byte)0x8F : (byte)0x8A);

    private static Brush Gray(byte value) => new SolidColorBrush(Color.FromRgb(value, value, value));

    private static Brush From(Rgb color) => new SolidColorBrush(Color.FromRgb(color.R8, color.G8, color.B8));

    /// <summary>The popover's own background, which urgency-coloured text must stay legible against.</summary>
    private Rgb BackgroundRgb => Rgb.Gray(isDark ? (byte)0x2C : (byte)0xF9);

    /// <summary>
    /// Brings the popover up to date with the view model. Its fixed parts are in the XAML and
    /// only change what they say; the body's parts are kept by name, so they do too.
    /// </summary>
    internal void Render()
    {
        isDark = SystemTheme.AppsAreDark;
        Background = From(BackgroundRgb);
        Root.BorderBrush = Gray(isDark ? (byte)0x45 : (byte)0xD6);
        Divider.Background = Gray(isDark ? (byte)0x45 : (byte)0xDD);
        HeaderTitle.Text = L.T("Claude Tracker");
        HeaderTitle.Foreground = Primary;
        ApplyPopupSize();
        QuitLink.Content = L.T("Quit");
        SettingsLink.Content = L.T("Settings");
        foreach (var link in new[] { QuitLink, SettingsLink, AccountMenuButton })
        {
            link.Foreground = link.IsMouseOver ? Primary : Secondary;
        }

        // With one account the header is the plan badge alone, as it always was. With more,
        // the active account's name stands beside it and opens the list.
        if (viewModel.Accounts.Count >= 2 && viewModel.ActiveAccount is { } active)
        {
            AccountMenuButton.Content = active.Label + " ▾";
            // Read as the account's name: the triangle is for the eye.
            AutomationProperties.SetName(AccountMenuButton, active.Label);
            AccountMenuButton.Visibility = Visibility.Visible;
        }
        else
        {
            AccountMenuButton.Visibility = Visibility.Collapsed;
        }

        if (viewModel.ActiveSubscriptionLabel is { } plan)
        {
            // The label is the canonical English value; it is translated here, where it is shown.
            BadgeText.Text = L.T(plan);
            BadgeText.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(0xC9, 0xA7, 0xF5) : Color.FromRgb(0x7A, 0x3E, 0xC8));
            Badge.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x9B, 0x59, 0xD0));
            Badge.Visibility = Visibility.Visible;
        }
        else
        {
            Badge.Visibility = Visibility.Collapsed;
        }

        var updater = viewModel.Updater;
        if (updater.AvailableUpdate is { } update)
        {
            var green = Color.FromRgb(0x2E, 0xA0, 0x43);
            UpdateBanner.Background = new SolidColorBrush(Palette.With(green, 0.14));
            UpdateBannerSymbol.Text = "\uE896"; // an arrow into a tray
            UpdateBannerSymbol.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(0x4C, 0xC2, 0x62) : green);
            UpdateBannerText.Text = L.F("v%@ available", update.Version);
            UpdateBannerText.Foreground = Primary;
            UpdateBannerProgress.Text = updater.ProgressLabel ?? "";
            UpdateBannerProgress.Foreground = Secondary;
            UpdateBannerProgress.Visibility = updater.ProgressLabel is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateBannerAction.Content = updater.ActionLabel;
            UpdateBannerAction.Visibility = updater.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        NoticeText.Text = viewModel.Notice ?? "";
        NoticeText.Foreground = Secondary;
        NoticeText.Visibility = viewModel.Notice is null ? Visibility.Collapsed : Visibility.Visible;

        var palette = new Palette(isDark, Primary, Secondary, Tertiary, BackgroundRgb);
        var tabs = viewModel.IsAuthenticated && viewModel.ShowChartsTab;
        if (tabs) tabPicker.Show([L.T("Usage"), L.T("Charts")], viewModel.SelectedTab, palette);
        TabBar.Visibility = tabs ? Visibility.Visible : Visibility.Collapsed;

        var body = new List<UIElement>();
        if (tabs && viewModel.SelectedTab == 1)
        {
            // The charts draw from the saved history, so they show before the first fetch too.
            body.Add(charts.Refresh(palette, MaxChartListHeight()));
        }
        else if (!viewModel.IsAuthenticated)
        {
            if (viewModel.SessionNeedsSignIn) RenderExpired(body);
            else RenderSignedOut(body);
        }
        else if (viewModel.Usage is not null)
        {
            RenderUsage(body);
        }
        else if (viewModel.Error is { } error)
        {
            RenderError(body, error);
        }
        else
        {
            body.Add(Centered(Text("loading", L.T("Loading…"), 13, Secondary)));
        }
        // The body is handed the elements it should hold, and left alone when it holds them
        // already. An element taken out and put back is a new element to a screen reader, to
        // the keyboard focus, to an open menu and to a scrolled list — and this runs at every
        // poll, every few seconds.
        ViewCache.SetChildren(Body, body);
        views.Sweep();
    }

    /// <summary>
    /// The tallest the charts' scrolling list may be, so the popover never outgrows the screen
    /// (<see cref="ChartLayout.ListHeightLimit"/>). In the popover's own units, which the
    /// popup size setting scales.
    /// </summary>
    private double MaxChartListHeight()
    {
        var area = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).WorkingArea;
        var available = area.Height / VisualTreeHelper.GetDpi(this).DpiScaleY / appliedScale;
        // The banner and the notice come and go, and both already hold this render's text.
        return ChartLayout.ListHeightLimit(available, HeightOf(UpdateBanner) + HeightOf(NoticeText));

        double HeightOf(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible) return 0;
            // Asked for now: one that has just appeared has not been laid out yet.
            element.Measure(new Size(NaturalWidth - Root.Padding.Left - Root.Padding.Right - 2, double.PositiveInfinity));
            return element.DesiredSize.Height;
        }
    }

    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));

    private void RenderSignedOut(List<UIElement> body)
    {
        if (viewModel.StorageProblem is { } problem)
        {
            body.Add(Centered(Text("problem", problem, 13, Red)));
            return;
        }
        body.Add(Centered(Text("signed-out", L.T("Not signed in"), 13, Secondary)));
        body.Add(Centered(ActionButton("add-account", L.T("Add a Claude account"), viewModel.OpenLoginForNewAccount), top: 11));
        body.Add(Centered(Text("signed-out/how", L.T("Opens Claude in a sign-in window."), 12, Tertiary), top: 11));
    }

    /// <summary>
    /// The active account's session was rejected twice. Signing in again reuses this account —
    /// "Add a Claude account" here would create a duplicate and strand its history.
    /// </summary>
    private void RenderExpired(List<UIElement> body)
    {
        body.Add(Centered(Text("expired", L.T("Session expired"), 13, Secondary)));
        body.Add(Centered(ActionButton("sign-in-again", L.T("Sign in again"), viewModel.SignInAgain), top: 11));
    }

    private void RenderError(List<UIElement> body, string error)
    {
        body.Add(Centered(Text("failure", error, 13, Red)));
        // Only an authentication failure is fixed by signing in; network and format errors
        // retry by themselves, and the button would suggest otherwise.
        if (viewModel.SessionNeedsSignIn)
        {
            body.Add(Centered(ActionButton("sign-in-again", L.T("Sign in again"), viewModel.SignInAgain), top: 8));
        }
    }

    private void RenderUsage(List<UIElement> body)
    {
        if (viewModel.IsDataStale)
        {
            body.Add(Text("stale", L.T("Window reset — refreshing…"), 12, Secondary, bottom: 12));
        }
        else if (viewModel.Error is { } error)
        {
            body.Add(Text("error", error, 12, new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x1E)), bottom: 12));
        }

        var first = true;
        foreach (var tracked in viewModel.ShownWindows)
        {
            body.Add(WindowRow(tracked, top: first ? 0 : 17));
            first = false;
        }

        if (viewModel.Usage?.ExtraUsage is { IsEnabled: true } extra)
        {
            body.Add(Text("extra", L.T("Extra Usage"), 13, Primary, weight: FontWeights.Bold, top: 17));
            if (extra is { UsedCredits: { } used, MonthlyLimit: { } limit })
            {
                body.Add(Text("extra/amount", Money.SpentOfLimit(used, limit, CultureInfo.CurrentCulture), 12, Secondary, top: 4));
            }
        }
    }

    /// <summary>
    /// One usage window: title, percentage, bar, when it resets, and its pace. What is drawn
    /// inside is made again at every render; the row itself is kept, and it is the row that a
    /// screen reader is shown (<see cref="UsageRow"/>).
    /// </summary>
    private UIElement WindowRow(TrackedWindow tracked, double top)
    {
        var window = tracked.Window;
        // After a reset that passed since the last fetch the old number is wrong: show an
        // empty, neutral row until fresh data arrives.
        var isStale = viewModel.IsWindowStale(window);
        var percent = isStale ? "0%" : ((int)window.Utilization).ToString(CultureInfo.InvariantCulture) + "%";
        var urgency = window.Utilization / 100.0;
        var spoken = new List<string> { tracked.Title, percent };

        var row = views.Keep<UsageRow>("row/" + tracked.Key);
        row.Margin = new Thickness(0, top, 0, 0);
        row.Children.Clear();
        var header = new DockPanel { LastChildFill = false };
        var percentText = Plain(percent, 13, isStale ? Secondary : From(Urgency.TextColor(urgency, isDark, BackgroundRgb)), weight: FontWeights.Bold);
        DockPanel.SetDock(percentText, Dock.Right);
        header.Children.Add(percentText);
        header.Children.Add(Plain(tracked.Title, 13, Primary, weight: FontWeights.Bold));
        row.Children.Add(header);

        var fraction = isStale ? 0 : window.UtilizationFraction;
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 7, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(3), Background = Gray(isDark ? (byte)0x4A : (byte)0xDC) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        if (fraction > 0)
        {
            // The bar keeps the raw gradient; only text needs the legible variant.
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = isStale ? Secondary : From(Urgency.Color(urgency)) });
        }
        row.Children.Add(bar);

        // Hidden when stale: the reset has passed, and "Resets in" would be wrong.
        if (window.ResetsAtDate is { } reset && !isStale)
        {
            var now = DateTimeOffset.UtcNow;
            var absolute = TimeText.ResetTimeText(reset, now, viewModel.Use24HourTime, includeDate: tracked.IsSevenDay, culture: TimeText.DisplayCulture());
            var line = L.F("Resets in %@ · %@", RelativeTime.Until(reset - now), absolute);
            row.Children.Add(Plain(line, 12, Secondary, top: 6));
            spoken.Add(line);
        }

        // No pace on a full window (there is nothing left to project) or a stale one.
        if (viewModel.ShowPace && !isStale && window.Utilization < 100 && viewModel.Pace(tracked.Key) is { } pace)
        {
            var now = DateTimeOffset.UtcNow;
            // One band for both lines, so the rate can never be amber over a red outlook.
            var accent = PaceBrush(PaceMath.AccentUrgency(pace.ProjectedHours, window.ResetsAtDate, isStale, now));
            var line = PaceText.Line(pace.Rate, pace.ProjectedHours, viewModel.PaceRateUnit);
            row.Children.Add(Plain(line, 12, accent, top: 5));
            spoken.Add(line);
            if (PaceText.Outlook(pace.ProjectedHours, window.ResetsAtDate, now) is { } outlook)
            {
                row.Children.Add(Plain(outlook.Message, 12, accent, top: 3));
                spoken.Add(outlook.Message);
            }
        }
        row.Label = string.Join(", ", spoken);
        return row;
    }

    /// <summary>Pace text: neutral while the pace is safe, else the urgency colour's legible variant.</summary>
    private Brush PaceBrush(double urgency) => urgency > 0 ? From(Urgency.TextColor(urgency, isDark, BackgroundRgb)) : Secondary;

    /// <summary>A text of the body, kept from render to render under its name.</summary>
    private TextBlock Text(string name, string text, double size, Brush brush, FontWeight? weight = null, double top = 0, double bottom = 0)
    {
        var block = views.Keep<TextBlock>(name);
        block.Text = text;
        block.FontSize = size;
        block.Foreground = brush;
        block.FontWeight = weight ?? FontWeights.Normal;
        block.TextWrapping = TextWrapping.Wrap;
        block.Margin = new Thickness(0, top, 0, bottom);
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.TextAlignment = TextAlignment.Left;
        return block;
    }

    /// <summary>A text inside a usage row: made anew each time, since only its row is anyone's to keep track of.</summary>
    private static TextBlock Plain(string text, double size, Brush brush, FontWeight? weight = null, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static FrameworkElement Centered(FrameworkElement element, double top = 0)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.Margin = new Thickness(0, top, 0, 0);
        if (element is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return element;
    }

    /// <summary>
    /// A button of the body, kept under its name. Its handler is attached once and looks up
    /// what to do: attached at every render, a press would run as many times as it was drawn.
    /// </summary>
    private Button ActionButton(string name, string label, Action action)
    {
        var button = views.Keep<Button>(name);
        if (button.Tag is null)
        {
            button.Tag = name;
            button.Click += (_, _) => actions[name]();
        }
        actions[name] = action;
        button.Content = label;
        button.Padding = new Thickness(16, 7, 16, 7);
        button.FontSize = 13;
        return button;
    }
}

/// <summary>
/// One usage window in the popover. To the eye it is a title, a number, a bar and a line or
/// three; to a screen reader it is one thing, read in the order that makes sense: the
/// window, its percentage, when it resets, its pace. (Left to its parts, the percentage was
/// read before the window it belongs to.) The Mac app's row is one combined element too.
/// </summary>
internal sealed class UsageRow : StackPanel
{
    private string label = "";

    /// <summary>What a screen reader reads for the whole row.</summary>
    public string Label
    {
        get => label;
        set
        {
            if (label == value) return;
            var before = label;
            label = value;
            // Told to whoever is on the row, so it reads what the row now says; nothing is announced.
            UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, before, value);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(UsageRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(UsageRow);
        protected override string GetNameCore() => owner.Label;
        // Its texts are the row's own words already: read once, as one.
        protected override List<AutomationPeer> GetChildrenCore() => [];
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>How long until something, in the two largest units: "3 hr, 12 min".</summary>
internal static class RelativeTime
{
    public static string Until(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1)) return L.T("less than a minute");
        if (remaining < TimeSpan.FromHours(1)) return L.F("%d min", remaining.Minutes);
        if (remaining < TimeSpan.FromDays(1)) return L.F("%d hr, %d min", remaining.Hours, remaining.Minutes);
        var days = (int)remaining.TotalDays;
        var dayText = days == 1 ? L.T("1 day") : L.F("%d days", days);
        return remaining.Hours == 0 ? dayText : dayText + ", " + L.F("%d hr", remaining.Hours);
    }
}
