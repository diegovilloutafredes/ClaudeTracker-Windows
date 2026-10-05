using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClaudeTracker.App;

/// <summary>Where the app keeps its files. Everything is per user; nothing needs admin.</summary>
internal static class AppPaths
{
    /// <summary>Settings, the account roster and chart history: small files worth roaming.</summary>
    public static string Data { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeTracker");

    private static string Local { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTracker");

    /// <summary>
    /// The WebView2 user-data folder (one profile per account inside it). Set explicitly:
    /// the default is next to the executable, which is not writable under Program Files.
    /// </summary>
    public static string WebView { get; } = Path.Combine(Local, "WebView2");

    public static string Logs { get; } = Path.Combine(Local, "Logs");

    public static string Settings => Path.Combine(Data, "settings.json");
}

/// <summary>
/// Rolling file logger: <c>%LocalAppData%\ClaudeTracker\Logs\claudetracker.log</c>, 512 KB max,
/// one rotation kept as <c>claudetracker.1.log</c>. A failed write drops the line — a logger
/// that runs on every poll must never take the app down.
/// </summary>
internal sealed class AppLogger
{
    public static AppLogger Shared { get; } = new();

    private const long MaxFileBytes = 512 * 1024;
    private readonly Lock gate = new();

    public string LogFile { get; } = Path.Combine(AppPaths.Logs, "claudetracker.log");

    public void Info(string message) => Write(message, "INFO");

    public void Error(string message) => Write(message, "ERROR");

    private void Write(string message, string level)
    {
        var line = $"[{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}] [{level}] {message}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                // Re-created on every write that finds it missing: a cleanup tool can delete
                // the folder under a running app.
                Directory.CreateDirectory(AppPaths.Logs);
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Move(LogFile, Path.Combine(AppPaths.Logs, "claudetracker.1.log"), overwrite: true);
                }
                File.AppendAllText(LogFile, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Dropped on purpose.
            }
        }
    }
}

/// <summary>The Windows light/dark choices the app follows.</summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Apps (the popover and windows) are dark.</summary>
    public static bool AppsAreDark => ReadFlag("AppsUseLightTheme") == 0;

    /// <summary>The taskbar is dark — it has its own setting, and it is the tray icon's background.</summary>
    public static bool TaskbarIsDark => ReadFlag("SystemUsesLightTheme") == 0;

    private static int ReadFlag(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : 1;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>Lets another process take the foreground; only the process that has it can grant this.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>ASFW_ANY.</summary>
    public const uint AnyProcess = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    /// <summary>GWL_EXSTYLE.</summary>
    public const int ExtendedStyle = -20;
    /// <summary>WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab.</summary>
    public const long ToolWindow = 0x00000080;
    /// <summary>WS_EX_NOACTIVATE: clicking the window does not make it the active one.</summary>
    public const long NoActivate = 0x08000000;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11): asks for rounded corners on a borderless window.</summary>
    public const int DwmWindowCornerPreference = 33;
    public const int DwmCornerRound = 2;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar.</summary>
    public const int DwmUseImmersiveDarkMode = 20;
}
