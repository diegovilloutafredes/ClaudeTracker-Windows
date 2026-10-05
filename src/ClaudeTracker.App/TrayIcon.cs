using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ClaudeTracker.Core;
using WinForms = System.Windows.Forms;

namespace ClaudeTracker.App;

/// <summary>
/// The app's presence beside the clock. A tray icon is a small square, so the tracked window's
/// percentage is drawn into the icon itself and coloured by urgency; the window's name and
/// anything else that needs words goes in the tooltip.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon notifyIcon = new();
    private readonly WinForms.ToolStripMenuItem openItem = new();
    private readonly WinForms.ToolStripMenuItem settingsItem = new();
    private readonly WinForms.ToolStripMenuItem quitItem = new();
    private const string CondensedFamily = "Bahnschrift SemiBold Condensed";

    private IntPtr iconHandle;
    private string renderedKey = "";

    /// <summary>The primary button went down on the icon; <see cref="Clicked"/> follows if it is released there.</summary>
    public event Action? Pressed;

    /// <summary>The icon was clicked with the primary button.</summary>
    public event Action? Clicked;

    /// <summary>"Open" was chosen from the icon's menu.</summary>
    public event Action? OpenRequested;

    /// <summary>"Settings" was chosen from the icon's menu.</summary>
    public event Action? SettingsRequested;

    public event Action? QuitRequested;

    public TrayIcon()
    {
        notifyIcon.MouseDown += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) Pressed?.Invoke();
        };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) Clicked?.Invoke();
        };
        // Enter on the icon, from the keyboard or a screen reader, arrives as a double click.
        // It shows the popover rather than toggling it: the click that began a real double
        // click has already opened it, and a toggle would close it again.
        notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        quitItem.Click += (_, _) => QuitRequested?.Invoke();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(quitItem);
        notifyIcon.ContextMenuStrip = menu;

        // The icon first appears under the app's name alone. Windows keeps the first tooltip
        // an icon ever had: its Settings list the icon under it for good ("Other system tray
        // icons"), and a screen reader is read the one it was added with before the current
        // one. Added while still starting up, the icon was "Claude Tracker, signed out" there.
        Update("…", null, L.T("Claude Tracker"));
    }

    /// <summary>Redraws the icon if what it shows changed, and updates the tooltip and menu texts.</summary>
    /// <param name="text">At most three characters: a percentage, or "–", "…", "!".</param>
    /// <param name="urgency">0…1 to colour a live number; null for the neutral colour.</param>
    public void Update(string text, double? urgency, string tooltip)
    {
        var taskbarIsDark = SystemTheme.TaskbarIsDark;
        var size = WinForms.SystemInformation.SmallIconSize.Width;
        var key = FormattableString.Invariant($"{text}|{urgency:0.00}|{taskbarIsDark}|{size}");
        if (key != renderedKey)
        {
            var handle = Render(text, urgency, taskbarIsDark, size);
            notifyIcon.Icon = Icon.FromHandle(handle);
            // Icon.FromHandle does not own the handle; release the one being replaced.
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = handle;
            renderedKey = key;
        }
        // The shell rejects tooltips longer than 127 characters.
        notifyIcon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        openItem.Text = L.T("Open");
        settingsItem.Text = L.T("Settings");
        quitItem.Text = L.T("Quit");
        notifyIcon.Visible = true;
    }

    private static IntPtr Render(string text, double? urgency, bool taskbarIsDark, int size)
    {
        // The taskbar is the icon's background. A live number takes the urgency colour's
        // legible variant against it; everything else is the plain foreground.
        var background = Rgb.Gray(taskbarIsDark ? (byte)0x20 : (byte)0xF3);
        var rgb = urgency is { } u ? Urgency.TextColor(u, taskbarIsDark, background) : Rgb.Gray(taskbarIsDark ? (byte)0xFF : (byte)0x1A);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale antialiasing: ClearType needs an opaque background.
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.FromArgb(rgb.R8, rgb.G8, rgb.B8));
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

            // The largest size at which the text fits the square. A condensed face lets "100"
            // keep digits tall enough to read at 16 pixels; Segoe UI is the fallback where
            // Bahnschrift is missing (GDI+ substitutes silently, so the name is checked).
            var pixels = size * 1.1f;
            Font font;
            while (true)
            {
                font = new Font(CondensedFamily, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
                if (font.Name != CondensedFamily)
                {
                    font.Dispose();
                    font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
                }
                if (graphics.MeasureString(text, font, PointF.Empty, format).Width <= size - 1 || pixels <= 6) break;
                font.Dispose();
                pixels -= 0.5f;
            }
            using (font)
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
            }
        }
        return bitmap.GetHicon();
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
        iconHandle = IntPtr.Zero;
    }
}
