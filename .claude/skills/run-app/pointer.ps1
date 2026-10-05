param(
    [Parameter(Mandatory)] [string]$Control,
    [string]$Type = "",
    [string]$Window = "Claude Tracker",
    [ValidateSet("hover", "click")] [string]$Action = "hover",
    [double]$At = 0.5,
    [int]$HoldMilliseconds = 900,
    [string]$Picture = "",
    [string]$OutDir = $env:TEMP,
    [int]$MinIdleSeconds = 20,
    [switch]$Stay
)
# Hovers or clicks one element of the app with the real pointer, for what UI Automation cannot
# do: a chart's pointer mark, a tooltip, a menu that stays open while its ticks are changed.
#
#   -Control "Utilization" -Type Image -At 0.7            rest on the first utilization chart, 70 % across
#   -Control "Chart content" -Action click -Picture menu  open the charts' menu and picture it
#   -Control "Pace" -Type MenuItem -Action click          tick an item of the menu that is open
#   -Control "Claude Tracker" -Type Text -Action click    click the popover's title: closes a menu
#
# It borrows the user's pointer, so it refuses to run while they are at the PC (-MinIdleSeconds;
# its own moves count as input, so a second call needs a lower number) and puts the pointer
# back where it was unless -Stay. Prints what the window says afterwards.
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices;
public static class AppPointer {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
  public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)Environment.TickCount - i.dwTime; }
  // A move the window is told about: placing the pointer alone does not always send one.
  public static void MoveTo(int x, int y) { SetCursorPos(x, y); mouse_event(1, 1, 0, 0, UIntPtr.Zero); mouse_event(1, -1, 0, 0, UIntPtr.Zero); }
  public static void Click(int x, int y) { MoveTo(x, y); System.Threading.Thread.Sleep(120); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
  // SRCCOPY | CAPTUREBLT: menus and tooltips are layered windows, and a plain copy of the screen leaves them out.
  public static void Grab(System.Drawing.Graphics g, int x, int y, int w, int h) { var s = GetDC(IntPtr.Zero); var d = g.GetHdc(); BitBlt(d, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000); g.ReleaseHdc(d); ReleaseDC(IntPtr.Zero, s); }
}
"@
$idle = [AppPointer]::IdleMs()
if ($idle -lt $MinIdleSeconds * 1000) { "THE USER IS AT THE PC (last input $idle ms ago): leaving the pointer alone"; exit 3 }

$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$app = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { "app not running"; exit 1 }
if ($Window -eq "Claude Tracker") { Start-Process $app.Path; Start-Sleep -Milliseconds 1100 }   # shows the popover
$mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $app.Id
function Tops { @($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.BoundingRectangle.Width -gt 1 }) }
function Kind($Element) { $Element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }

$top = Tops | Where-Object { $_.Current.Name -like $Window } | Select-Object -First 1
if (-not $top) { "no window like '$Window'"; exit 1 }
$target = $null
foreach ($e in $top.FindAll($Tree::Subtree, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.Name -eq $Control -and (-not $Type -or (Kind $e) -eq $Type)) { $target = $e; break }
}
if (-not $target) { "no '$Control' $Type in '$($top.Current.Name)'"; exit 1 }

$old = New-Object AppPointer+POINT
[void][AppPointer]::GetCursorPos([ref]$old)
try {
    $r = $target.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width * $At); $y = [int]($r.Y + $r.Height / 2)
    if ($Action -eq "click") { [AppPointer]::Click($x, $y) } else { [AppPointer]::MoveTo($x, $y) }
    Start-Sleep -Milliseconds $HoldMilliseconds
    "$Action on '$Control' at $x,$y"
    $shown = @($top)
    foreach ($t in (Tops)) {
        foreach ($e in $t.FindAll($Tree::Subtree, [System.Windows.Automation.Condition]::TrueCondition)) {
            $kind = Kind $e
            if ($kind -in 'Menu', 'ToolTip') { $shown += $e; "  [$kind] $($e.Current.Name)" }
            elseif ($kind -eq 'MenuItem') {
                $toggle = $null; $mark = "   "
                if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) { $mark = if ($toggle.Current.ToggleState -eq 'On') { "[x]" } else { "[ ]" } }
                "  [MenuItem] $mark $($e.Current.Name)"
            }
            elseif ($kind -eq 'Text' -and $e.Current.Name -and $t.Current.Name -eq $top.Current.Name) { "  [Text] $($e.Current.Name)" }
        }
    }
    if ($Picture) {
        $left = ($shown | ForEach-Object { $_.Current.BoundingRectangle.Left } | Measure-Object -Minimum).Minimum
        $topEdge = ($shown | ForEach-Object { $_.Current.BoundingRectangle.Top } | Measure-Object -Minimum).Minimum
        $right = ($shown | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
        $bottom = ($shown | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum
        $bmp = New-Object System.Drawing.Bitmap ([int]($right - $left)), ([int]($bottom - $topEdge))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        [AppPointer]::Grab($g, [int]$left, [int]$topEdge, $bmp.Width, $bmp.Height)
        $file = Join-Path $OutDir "claudetracker-$Picture-pointer.png"
        $bmp.Save($file); $g.Dispose(); $bmp.Dispose()
        "  picture: $file"
    }
}
finally {
    if (-not $Stay) { [void][AppPointer]::SetCursorPos($old.X, $old.Y); "pointer back at $($old.X),$($old.Y)" }
}
