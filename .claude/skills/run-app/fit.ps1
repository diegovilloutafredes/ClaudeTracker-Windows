param([double]$Scale = 1.0)
# Measures the popover against the taskbar it stands beside and the screen it is on: which
# edge the taskbar is on and whether it hides itself, how far the popover is from the strip
# the taskbar shows in, where the tab bar and the Charts tab's two controls are, how tall the
# list of charts is, and how much stands around the list. -Scale is the "Popup size" in use
# (1.5 for 150 %), to give the list's figures in the popover's own units -- the units of
# ChartLayout.ListSurroundings (250), which must not be less than "around it" here.
# Show the popover first. Two things to compare between two runs:
#   - before and after pressing "Charts", or switching a chart off in the content menu: the
#     lines for the tab bar and the two controls must not change (they are on the held side);
#   - with an update banner or a notice showing: the list that much shorter, the popover no taller.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Shell {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
  // 5 = ABM_GETTASKBARPOS (edge and rectangle), 4 = ABM_GETSTATE (bit 1: it hides itself).
  [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
  public static APPBARDATA Ask(uint msg, out bool answered) { var d = new APPBARDATA(); d.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)); var r = SHAppBarMessage(msg, ref d); answered = r != UIntPtr.Zero; d.lParam = (IntPtr)(long)r.ToUInt64(); return d; }
}
"@
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "the app is not running"; exit 1 }
$mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $p.Id
$popover = @($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.Name -eq 'Claude Tracker' -and $_.Current.BoundingRectangle.Width -gt 1 }) | Select-Object -First 1
if (-not $popover) { "no popover on screen"; exit 1 }
$window = $popover.Current.BoundingRectangle
$screen = [System.Windows.Forms.Screen]::FromRectangle((New-Object System.Drawing.Rectangle ([int]$window.X), ([int]$window.Y), ([int]$window.Width), ([int]$window.Height)))
$bounds = $screen.Bounds; $area = $screen.WorkingArea

# The taskbar as the shell gives it, and the strip it shows in: on its edge of the screen,
# as thick as it is. The rectangle the shell gives may be the hidden one.
$answered = $false; $bar = [Shell]::Ask(5, [ref]$answered); $edge = $null; $clear = $null
if ($answered -and [System.Windows.Forms.Screen]::FromRectangle([System.Drawing.Rectangle]::FromLTRB($bar.rc.Left, $bar.rc.Top, $bar.rc.Right, $bar.rc.Bottom)).DeviceName -eq $screen.DeviceName) {
    $edge = @('left', 'top', 'right', 'bottom')[[int]$bar.uEdge]
    $thick = if ($edge -in 'top', 'bottom') { $bar.rc.Bottom - $bar.rc.Top } else { $bar.rc.Right - $bar.rc.Left }
    $hides = ([Shell]::Ask(4, [ref]$answered).lParam.ToInt64() -band 1) -eq 1
    switch ($edge) {
        'bottom' { $strip = "$($bounds.Bottom - $thick)..$($bounds.Bottom) down the screen"; $clear = ($bounds.Bottom - $thick) - $window.Bottom; $spare = $window.Top - $bounds.Top }
        'top'    { $strip = "$($bounds.Top)..$($bounds.Top + $thick) down the screen"; $clear = $window.Top - ($bounds.Top + $thick); $spare = $bounds.Bottom - $window.Bottom }
        'left'   { $strip = "$($bounds.Left)..$($bounds.Left + $thick) across the screen"; $clear = $window.Left - ($bounds.Left + $thick); $spare = $bounds.Right - $window.Right }
        'right'  { $strip = "$($bounds.Right - $thick)..$($bounds.Right) across the screen"; $clear = ($bounds.Right - $thick) - $window.Right; $spare = $window.Left - $bounds.Left }
    }
    "taskbar:        on the $edge edge, $(if ($hides) { 'hides itself' } else { 'always showing' }); shows in $strip ($thick px)"
} else { "taskbar:        not on the popover's screen, or the shell would not say" }
"screen:         $($bounds.Left),$($bounds.Top) to $($bounds.Right),$($bounds.Bottom); work area $($area.Left),$($area.Top) to $($area.Right),$($area.Bottom)"
"popover:        left $([int]$window.Left), top $([int]$window.Top), right $([int]$window.Right), bottom $([int]$window.Bottom) ($([int]$window.Width) x $([int]$window.Height))"
if ($null -ne $clear) { if ($clear -ge 0) { "                CLEAR of the taskbar's strip by $([int]$clear) px" } else { "                IN THE TASKBAR'S STRIP by $([int](-$clear)) px" } }

function Where-Is([string]$id) {
    $e = $popover.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), $id))
    if (-not $e -or $e.Current.BoundingRectangle.Width -le 1) { return "(not showing)" }
    $r = $e.Current.BoundingRectangle
    "left $([int]$r.Left), top $([int]$r.Top), bottom $([int]$r.Bottom)"
}
"tab bar:        $(Where-Is 'popover-tab-0')"
"range picker:   $(Where-Is 'chart-range-0')"
"content button: $(Where-Is 'ChartContent')"

$list = $null; $above = @()
foreach ($e in $popover.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.ClassName -eq 'ScrollViewer' -and -not $list) { $list = $e.Current.BoundingRectangle }
    $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($kind -eq 'Text' -and $e.Current.Name -match 'available|already here|disponible|ya estaba') { $above += $e.Current.Name }
}
"banner, notice: $(if ($above) { $above -join ' | ' } else { '(neither showing)' })"
if ($list) {
    "list of charts: $([int]$list.Height) px = $([int]($list.Height / $Scale)) units; around it: $([int](($window.Height - $list.Height) / $Scale)) units"
} else { "list of charts: none (is the Charts tab showing?)" }
$off = @()
if ($window.Top -lt $bounds.Top) { $off += "OFF THE TOP by $([int]($bounds.Top - $window.Top)) px" }
if ($window.Bottom -gt $bounds.Bottom) { $off += "OFF THE BOTTOM by $([int]($window.Bottom - $bounds.Bottom)) px" }
if ($window.Left -lt $bounds.Left) { $off += "OFF THE LEFT by $([int]($bounds.Left - $window.Left)) px" }
if ($window.Right -gt $bounds.Right) { $off += "OFF THE RIGHT by $([int]($window.Right - $bounds.Right)) px" }
if ($off) { $off } elseif ($null -ne $clear) { "INSIDE the screen: $([int]$spare) px to spare on the side away from the taskbar" } else { "INSIDE the screen" }
