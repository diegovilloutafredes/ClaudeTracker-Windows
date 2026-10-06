using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeTracker.Core;

namespace ClaudeTracker.App;

internal enum ToastKind
{
    /// <summary>A window reset: good news, green.</summary>
    Reset,
    /// <summary>A pace warning, or an update that could not be installed: amber.</summary>
    Pace,
    /// <summary>An update was found: green.</summary>
    Update,
}

/// <summary>
/// Small notifications stacked from the tray's corner, away from the taskbar: a window reset,
/// a pace warning. They never take the keyboard focus — showing one must not hide the popover
/// or interrupt typing — so their text is announced to screen readers instead. The Windows
/// twin of the Mac app's <c>ToastWindowController</c>, which stacks its own below the menu bar.
/// </summary>
internal sealed class ToastHost
{
    public static ToastHost Shared { get; } = new();

    private const int Margin = 12;
    private const int Gap = 8;

    private readonly List<(Guid Id, ToastWindow Window)> entries = [];

    /// <summary>
    /// The popover's rectangle in pixels while it shows. Toasts stack past it: both live in
    /// the corner beside the tray.
    /// </summary>
    public Func<System.Drawing.Rectangle?>? Obstacle { get; set; }

    /// <summary>Shows a toast past any already showing, and returns its id for <see cref="Dismiss"/>.</summary>
    /// <param name="seconds">How long it stays. Ignored when <paramref name="permanent"/>.</param>
    /// <param name="permanent">Stays until clicked or dismissed.</param>
    public Guid Show(string title, string message, ToastKind kind, double seconds, bool permanent)
    {
        var id = Guid.NewGuid();
        var window = new ToastWindow(title, message, kind, () => Dismiss(id));
        entries.Add((id, window));
        window.Show();
        Arrange();
        window.Announce();
        if (!permanent) window.CloseAfter(TimeSpan.FromSeconds(seconds));
        return id;
    }

    public void Dismiss(Guid id)
    {
        var index = entries.FindIndex(entry => entry.Id == id);
        if (index < 0) return;
        var window = entries[index].Window;
        entries.RemoveAt(index);
        window.Close();
        Arrange();
    }

    public void DismissAll()
    {
        foreach (var (id, _) in entries.ToList()) Dismiss(id);
    }

    /// <summary>
    /// Puts every toast in its place: the first beside the taskbar in the tray's corner, or
    /// past the popover while that shows, and each later one past the one before — upwards
    /// from a taskbar at the bottom, downwards from one at the top
    /// (<see cref="PopoverPlacement.Stack"/>).
    /// </summary>
    public void Arrange()
    {
        if (entries.Count == 0) return;
        var sizes = entries.Select(entry => entry.Window.PixelSize).ToList();
        // Where a popover opened at the tray would be held, on whichever edge the taskbar is.
        var (edge, room, along) = Taskbar.Find(null);
        var from = PopoverPlacement.Place(edge, room, sizes[0].Width, along, Margin);
        ScreenRect? popover = Obstacle?.Invoke() is { } taken ? new ScreenRect(taken.Left, taken.Top, taken.Right, taken.Bottom) : null;
        var places = PopoverPlacement.Stack(from, sizes, Gap, popover);
        for (var i = 0; i < entries.Count; i++) entries[i].Window.MoveTo(places[i].X, places[i].Y);
    }
}

/// <summary>One toast. Built in code: it is a title, a line of text, a symbol and a close button.</summary>
internal sealed class ToastWindow : Window
{
    private readonly TextBlock messageText;
    private readonly string announcement;
    private readonly Action dismiss;
    private DispatcherTimer? timer;

    public ToastWindow(string title, string message, ToastKind kind, Action dismiss)
    {
        this.dismiss = dismiss;
        var isDark = SystemTheme.AppsAreDark;
        Brush Gray(byte value) => new SolidColorBrush(Color.FromRgb(value, value, value));
        var primary = Gray(isDark ? (byte)0xFF : (byte)0x1B);
        var secondary = Gray(isDark ? (byte)0xC5 : (byte)0x5C);

        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        UseLayoutRounding = true;
        Background = Gray(isDark ? (byte)0x2C : (byte)0xF9);
        announcement = title + ". " + message;

        // A tick, a warning triangle, an arrow into a tray.
        var symbol = Symbols.Text(kind switch { ToastKind.Reset => "\uE73E", ToastKind.Update => "\uE896", _ => "\uE7BA" }, 20);
        symbol.Foreground = new SolidColorBrush(kind == ToastKind.Pace ? Color.FromRgb(0xD9, 0x82, 0x1E) : Color.FromRgb(0x2E, 0xA0, 0x43));
        symbol.Margin = new Thickness(0, 2, 12, 0);
        symbol.VerticalAlignment = VerticalAlignment.Top;

        var cross = Symbols.Text("\uE711", 10);
        cross.Foreground = secondary;
        var close = new Button
        {
            Content = cross,
            Template = PlainButton(),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Margin = new Thickness(8, -2, -4, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(close, L.T("Dismiss"));
        close.Click += (_, _) => dismiss();

        messageText = new TextBlock { Text = message, FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = primary, TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(messageText);

        var row = new DockPanel();
        DockPanel.SetDock(symbol, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(symbol);
        row.Children.Add(close);
        row.Children.Add(texts);

        Content = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = Gray(isDark ? (byte)0x45 : (byte)0xD6),
            Child = row,
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Without these a click on the toast would make it the active window, and the
            // popover — which hides when it loses the focus — would vanish under it.
            var style = Native.GetWindowLongPtr(handle, Native.ExtendedStyle).ToInt64();
            Native.SetWindowLongPtr(handle, Native.ExtendedStyle, new IntPtr(style | Native.NoActivate | Native.ToolWindow));
            var preference = Native.DwmCornerRound;
            _ = Native.DwmSetWindowAttribute(handle, Native.DwmWindowCornerPreference, ref preference, sizeof(int));
        };
        Closed += (_, _) => timer?.Stop();
    }

    /// <summary>A button that is only its content: no chrome to draw, nothing to take the focus.</summary>
    private static ControlTemplate PlainButton()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(6));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>
    /// The toast's width and height on the screen. In pixels, as for the popover: WPF's own
    /// units change meaning between monitors.
    /// </summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !Native.GetWindowRect(handle, out var rect)) return (0, 0);
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    /// <summary>Moves the toast so its top-left corner is at the given pixel.</summary>
    public void MoveTo(int left, int top)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Native.SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate);
    }

    /// <summary>
    /// Has screen readers say the toast. It cannot take the focus, so a reader would never
    /// come across it by itself.
    /// </summary>
    public void Announce()
    {
        var peer = UIElementAutomationPeer.FromElement(messageText) ?? UIElementAutomationPeer.CreatePeerForElement(messageText);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
                                     announcement, "ClaudeTracker.Toast");
    }

    public void CloseAfter(TimeSpan delay)
    {
        timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            dismiss(); // through the host, so the toasts above this one move down
        };
        timer.Start();
    }
}
