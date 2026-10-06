# Says whether showing the popover now would land on something the user is in the middle of:
# a game or a video in full screen, or a presentation. Run it before anything here that puts
# a window on the screen -- the popover is always on top, and one shown over a full-screen
# game is in the way and can throw the game out of full screen. Exit code 1 when busy.
# It also gives the screen's size: a game that takes the screen over at another resolution
# changes it, so two measurements taken minutes apart may not be of the same screen.
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class Busy {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  public static string Title() { var t = new StringBuilder(200); GetWindowText(GetForegroundWindow(), t, 200); return t.ToString(); }
  public static double Idle() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (Environment.TickCount - (int)i.dwTime) / 1000.0; }
}
"@
$state = 0; [void][Busy]::SHQueryUserNotificationState([ref]$state)
# 2 a full-screen window, 3 Direct3D full screen, 4 presentation mode.
$what = @{ 2 = 'a full-screen app is in front'; 3 = 'a full-screen game or video is in front'; 4 = 'a presentation is running' }[$state]
"in front:    '$([Busy]::Title())'"
"last input:  $([Busy]::Idle().ToString('0')) s ago"
"main screen: $([Busy]::GetSystemMetrics(0)) x $([Busy]::GetSystemMetrics(1))"
if ($what) { "BUSY: $what -- do not show the popover now"; exit 1 }
"free: nothing in full screen"
