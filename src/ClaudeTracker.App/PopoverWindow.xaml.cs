using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
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
    /// <summary>Hides a popover that never got the focus; see <see cref="HideIfNeverFocused"/>.</summary>
    private readonly DispatcherTimer unfocused = new() { Interval = TimeSpan.FromSeconds(8) };

    internal PopoverWindow(UsageViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
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
        QuitLink.MouseEnter += (_, _) => QuitLink.Foreground = Primary;
        QuitLink.MouseLeave += (_, _) => QuitLink.Foreground = Secondary;
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

    /// <summary>Rebuilds the popover from the view model.</summary>
    internal void Render()
    {
        isDark = SystemTheme.AppsAreDark;
        Background = From(BackgroundRgb);
        Root.BorderBrush = Gray(isDark ? (byte)0x45 : (byte)0xD6);
        Divider.Background = Gray(isDark ? (byte)0x45 : (byte)0xDD);
        HeaderTitle.Text = L.T("Claude Tracker");
        HeaderTitle.Foreground = Primary;
        QuitLink.Content = L.T("Quit");
        QuitLink.Foreground = QuitLink.IsMouseOver ? Primary : Secondary;

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

        Body.Children.Clear();
        if (!viewModel.IsAuthenticated)
        {
            if (viewModel.SessionNeedsSignIn) RenderExpired();
            else RenderSignedOut();
        }
        else if (viewModel.Usage is not null)
        {
            RenderUsage();
        }
        else if (viewModel.Error is { } error)
        {
            RenderError(error);
        }
        else
        {
            Body.Children.Add(Centered(Text(L.T("Loading…"), 13, Secondary)));
        }
    }

    private void RenderSignedOut()
    {
        if (viewModel.StorageProblem is { } problem)
        {
            Body.Children.Add(Centered(Text(problem, 13, new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B)))));
            return;
        }
        Body.Children.Add(Centered(Text(L.T("Not signed in"), 13, Secondary)));
        Body.Children.Add(Centered(ActionButton(L.T("Add a Claude account"), viewModel.OpenLoginForNewAccount), top: 11));
        Body.Children.Add(Centered(Text(L.T("Opens Claude in a sign-in window."), 12, Tertiary), top: 11));
    }

    /// <summary>
    /// The active account's session was rejected twice. Signing in again reuses this account —
    /// "Add a Claude account" here would create a duplicate and strand its history.
    /// </summary>
    private void RenderExpired()
    {
        Body.Children.Add(Centered(Text(L.T("Session expired"), 13, Secondary)));
        Body.Children.Add(Centered(ActionButton(L.T("Sign in again"), viewModel.SignInAgain), top: 11));
    }

    private void RenderError(string error)
    {
        Body.Children.Add(Centered(Text(error, 13, new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B)))));
        // Only an authentication failure is fixed by signing in; network and format errors
        // retry by themselves, and the button would suggest otherwise.
        if (viewModel.SessionNeedsSignIn)
        {
            Body.Children.Add(Centered(ActionButton(L.T("Sign in again"), viewModel.SignInAgain), top: 8));
        }
    }

    private void RenderUsage()
    {
        if (viewModel.IsDataStale)
        {
            Body.Children.Add(Text(L.T("Window reset — refreshing…"), 12, Secondary, bottom: 12));
        }
        else if (viewModel.Error is { } error)
        {
            Body.Children.Add(Text(error, 12, new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x1E)), bottom: 12));
        }

        var first = true;
        foreach (var tracked in viewModel.ShownWindows)
        {
            Body.Children.Add(WindowRow(tracked, top: first ? 0 : 17));
            first = false;
        }
    }

    /// <summary>One usage window: title, percentage, bar, and when it resets.</summary>
    private UIElement WindowRow(TrackedWindow tracked, double top)
    {
        var window = tracked.Window;
        // After a reset that passed since the last fetch the old number is wrong: show an
        // empty, neutral row until fresh data arrives.
        var isStale = viewModel.IsWindowStale(window);
        var percent = isStale ? "0%" : ((int)window.Utilization).ToString(CultureInfo.InvariantCulture) + "%";
        var urgency = window.Utilization / 100.0;

        var panel = new StackPanel { Margin = new Thickness(0, top, 0, 0) };
        var header = new DockPanel { LastChildFill = false };
        var percentText = Text(percent, 13, isStale ? Secondary : From(Urgency.TextColor(urgency, isDark, BackgroundRgb)), weight: FontWeights.Bold);
        DockPanel.SetDock(percentText, Dock.Right);
        header.Children.Add(percentText);
        header.Children.Add(Text(tracked.Title, 13, Primary, weight: FontWeights.Bold));
        panel.Children.Add(header);

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
        panel.Children.Add(bar);

        // Hidden when stale: the reset has passed, and "Resets in" would be wrong.
        if (window.ResetsAtDate is { } reset && !isStale)
        {
            var now = DateTimeOffset.UtcNow;
            var absolute = TimeText.ResetTimeText(reset, now, viewModel.Use24HourTime, includeDate: tracked.IsSevenDay);
            panel.Children.Add(Text(L.F("Resets in %@ · %@", RelativeTime.Until(reset - now), absolute), 12, Secondary, top: 6));
        }
        return panel;
    }

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, double top = 0, double bottom = 0) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, bottom),
    };

    private static FrameworkElement Centered(FrameworkElement element, double top = 0)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.Margin = new Thickness(0, top, 0, 0);
        if (element is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return element;
    }

    private static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 7, 16, 7), FontSize = 13 };
        button.Click += (_, _) => action();
        return button;
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
