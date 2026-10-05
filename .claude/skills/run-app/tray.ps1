param([switch]$Activate, [string]$OutDir = $env:TEMP, [string]$Tag = "tray", [int]$MinIdleSeconds = 60)
# Reaches the app's icon beside the clock the way someone without a mouse does: Win+B puts
# the keyboard in the notification area, the arrows walk its icons, Enter on the arrow for
# hidden icons opens those. On the way it asks UI Automation what has the keyboard, which is
# what a screen reader says aloud. It reports what the app's icon is called, pictures the
# icon alone, and with -Activate presses Enter on it: the popover must come up.
#
# It types into the user's session, so:
#   - it refuses to run while they are at the PC;
#   - it presses Enter on two things only, each checked by name first: the arrow for hidden
#     icons, and the app's own icon. Win+B does not always land on the arrow - it once landed
#     on another program's icon, and an Enter sent on trust opened that program's window;
#   - it backs out with Esc when it cannot find its way.
# The taskbar's icons cannot be listed from outside (and not at all while the taskbar hides
# itself), which is why this walks instead of looking.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class TrayKeys {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static uint IdleMs() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)Environment.TickCount - i.dwTime; }
  public static void Press(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void Chord(byte modifier, byte vk) { keybd_event(modifier, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); keybd_event(modifier, 0, 2, UIntPtr.Zero); }
}
"@
$idle = [TrayKeys]::IdleMs()
if ($idle -lt $MinIdleSeconds * 1000) { "THE USER IS AT THE PC (last input $idle ms ago): not typing into their session"; exit 3 }
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
$app = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { "app not running"; exit 1 }
$shell = @(Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$VK_LWIN = 0x5B; $VK_B = 0x42; $VK_RETURN = 0x0D; $VK_ESCAPE = 0x1B; $VK_LEFT = 0x25; $VK_RIGHT = 0x27; $VK_DOWN = 0x28

function Focused { try { $AE::FocusedElement } catch { $null } }
function In-Shell($e) { $e -and ($shell -contains $e.Current.ProcessId) }
function Is-Ours($e) { (In-Shell $e) -and $e.Current.Name -like "Claude Tracker*" }
function Is-Arrow($e) { (In-Shell $e) -and $e.Current.Name -match 'Hidden Icons|iconos ocultos' }
function Key($e) { if ($e) { try { ($e.GetRuntimeId() -join '.') } catch { "?" } } else { "none" } }
# What has the keyboard, without naming other programs' icons.
function Say($e) {
    if (-not $e) { return "(nothing)" }
    $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ((Is-Ours $e) -or (Is-Arrow $e)) { "[$kind] $($e.Current.Name)" } else { "[$kind] (another program's)" }
}
function Back-Out([string]$Why) {
    [TrayKeys]::Press($VK_ESCAPE); Start-Sleep -Milliseconds 200; [TrayKeys]::Press($VK_ESCAPE)
    $Why
    exit 1
}
# Presses a key until the keyboard is on what is wanted, has left the shell, or has stopped moving.
function Walk([byte]$Key, [scriptblock]$Wanted, [int]$Most = 30) {
    $seen = @{}
    foreach ($step in 1..$Most) {
        $at = Focused
        if (-not (In-Shell $at)) { return $null }
        if (& $Wanted $at) { return $at }
        if ($seen.ContainsKey((Key $at))) { return $null }
        $seen[(Key $at)] = $true
        [TrayKeys]::Press($Key); Start-Sleep -Milliseconds 220
    }
    $null
}

[TrayKeys]::Chord($VK_LWIN, $VK_B); Start-Sleep -Milliseconds 700
$focus = Focused
"Win+B: the keyboard is on $(Say $focus)"
if (-not (In-Shell $focus)) { Back-Out "Win+B did not put the keyboard in the notification area; nothing was pressed" }

$wanted = { param($e) (Is-Ours $e) -or (Is-Arrow $e) }
$stop = Walk $VK_LEFT $wanted
if (-not $stop) { $stop = Walk $VK_RIGHT $wanted 60 }
if (-not $stop) { Back-Out "neither the app's icon nor the arrow for hidden icons was reached; backed out with Esc" }
$found = $null
if (Is-Ours $stop) { $found = $stop; "reached without opening the hidden icons: it is on the taskbar, or they were already showing" }
else {
    # The arrow's name ends in "Hide" while the hidden icons are showing: Enter would then
    # close them. Closed first, so the next Enter opens them and carries the keyboard in.
    if ($stop.Current.Name -match 'Hide$|Ocultar$') {
        "on $(Say $stop): the hidden icons are already showing; closing them first"
        [TrayKeys]::Press($VK_RETURN); Start-Sleep -Milliseconds 700
        $stop = Focused
        if (-not (Is-Arrow $stop)) { Back-Out "the keyboard left the arrow for hidden icons; nothing more was pressed" }
    }
    "on $(Say $stop): Enter opens the hidden icons"
    [TrayKeys]::Press($VK_RETURN); Start-Sleep -Milliseconds 900
    $ours = { param($e) Is-Ours $e }
    $found = Walk $VK_RIGHT $ours
    if (-not $found) { $found = Walk $VK_DOWN $ours }
    if (-not $found) { Back-Out "the app's icon was not among the hidden icons; backed out with Esc" }
    "the icon is among the hidden icons"
}

"a screen reader calls it: $($found.Current.Name)"
$r = $found.Current.BoundingRectangle
if ($r.Width -gt 2) {
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $big = New-Object System.Drawing.Bitmap ($bmp.Width * 6), ($bmp.Height * 6)
    $g2 = [System.Drawing.Graphics]::FromImage($big)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g2.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
    $file = Join-Path $OutDir "claudetracker-$Tag-icon.png"
    $big.Save($file); $g.Dispose(); $g2.Dispose(); $bmp.Dispose(); $big.Dispose()
    "picture of the icon alone, six times its size: $file"
}

if (-not $Activate) {
    [TrayKeys]::Press($VK_ESCAPE); Start-Sleep -Milliseconds 200; [TrayKeys]::Press($VK_ESCAPE)
    "left without pressing it"
    exit 0
}
$mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $app.Id
function Popover-Showing { [bool](@($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.Name -eq "Claude Tracker" -and $_.Current.BoundingRectangle.Width -gt 1 }).Count) }
"popover showing before: $(Popover-Showing)"
# Checked once more, at the moment of pressing.
if (-not (Is-Ours (Focused))) { Back-Out "the keyboard moved off the app's icon; nothing was pressed" }
[TrayKeys]::Press($VK_RETURN); Start-Sleep -Milliseconds 1300
$now = Focused
"Enter on the icon: popover showing = $(Popover-Showing); the keyboard is in $(if ($now -and $now.Current.ProcessId -eq $app.Id) { "the app's '" + $now.Current.Name + "'" } else { 'another program' })"
# Esc closes the popover and hands the keyboard back - sent only if the app has it.
if ($now -and $now.Current.ProcessId -eq $app.Id) { [TrayKeys]::Press($VK_ESCAPE); Start-Sleep -Milliseconds 500 }
"Esc: popover showing = $(Popover-Showing)"
