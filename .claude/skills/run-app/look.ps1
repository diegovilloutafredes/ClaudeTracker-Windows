param([string]$Tag = "state", [int]$LogLines = 6, [string]$OutDir = $env:TEMP)
# Prints what the app's own windows say (through UI Automation), saves a picture of each
# (only the window's own rectangle, never the screen), and tails the app's log.
# A hidden popover is not listed: launch ClaudeTracker.exe again first, which shows it.
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices;
public static class AppScreen {
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
  // SRCCOPY | CAPTUREBLT: an open menu or a tooltip is a layered window, and a plain copy of
  // the screen leaves those out, so the picture would show the window without them.
  public static void Grab(System.Drawing.Graphics g, int x, int y, int w, int h) { var s = GetDC(IntPtr.Zero); var d = g.GetHdc(); BitBlt(d, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000); g.ReleaseHdc(d); ReleaseDC(IntPtr.Zero, s); }
}
"@
"=== $Tag @ $((Get-Date).ToUniversalTime().ToString('HH:mm:ss'))Z ==="
$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "app not running" }
else {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
        $r = $w.Current.BoundingRectangle
        if ($r.Width -le 1 -or $r.Height -le 1) { continue }
        "window '$($w.Current.Name)' w=$([int]$r.Width) h=$([int]$r.Height) bottom=$([int]($r.Y + $r.Height))"
        foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            if ($e.Current.Name -and $kind -ne 'Window') { "  [$kind] $($e.Current.Name)" }
        }
        $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        [AppScreen]::Grab($g, [int]$r.X, [int]$r.Y, $bmp.Width, $bmp.Height)
        # One picture per window: the tag, then the window's name without its punctuation.
        $picture = Join-Path $OutDir ("claudetracker-$Tag-" + (($w.Current.Name -replace '[^\p{L}\p{Nd}]+', '-').Trim('-')) + ".png")
        $bmp.Save($picture)
        $g.Dispose(); $bmp.Dispose()
        "  picture: $picture"
    }
}
"--- log ---"
Get-Content (Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log") -Tail $LogLines
