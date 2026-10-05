param(
    [string]$Language = "",
    [string]$Tag = "walk",
    [string]$OutDir = $env:TEMP,
    [string]$Exe = "",
    [switch]$SignInWindow
)
# Walks every surface of the app and pictures each: the popover's two tabs, Settings from top
# to bottom, the two test toasts, the rename question, and (with -SignInWindow) the sign-in
# window's frame. With -Language es (or es-CL, which also sets the regional format) the app
# is restarted in that language first, without changing Windows.
#
# Controls are found by the names they have in the code (their automation ids), never by
# what they say, so the walk is the same in every language. Look at the pictures: a text that
# is cut off or runs under its neighbour shows nowhere else.
#
# It never presses "Sign out & remove": that question is one press from deleting a session.
# The switches it flips to show everything (pace lines, pace alerts) are put back as they
# were. Leaves the app running in -Language.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices;
public static class WalkScreen {
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
  public static void Grab(System.Drawing.Graphics g, int x, int y, int w, int h) { var s = GetDC(IntPtr.Zero); var d = g.GetHdc(); BitBlt(d, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000); g.ReleaseHdc(d); ReleaseDC(IntPtr.Zero, s); }
}
"@
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
$running = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Exe) { $Exe = if ($running) { $running.Path } else { Join-Path $env:LOCALAPPDATA "Programs\ClaudeTracker\ClaudeTracker.exe" } }

function App-Windows {
    $p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { return @() }
    $mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $p.Id
    @($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.BoundingRectangle.Width -gt 1 })
}
function By-Id($Root, [string]$Id) {
    $Root.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::AutomationIdProperty), $Id))
}
function Of-Kind($Root, $Type) {
    @($Root.FindAll($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $Type)))
}
function Press($Element) { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Popover { Start-Process $Exe; Start-Sleep -Milliseconds 1200; App-Windows | Where-Object { (By-Id $_ "QuitLink") } | Select-Object -First 1 }
function Settings { App-Windows | Where-Object { (By-Id $_ "AddAccountButton") } | Select-Object -First 1 }
function Picture($Element, [string]$Name) {
    $r = $Element.Current.BoundingRectangle
    if ($r.Width -lt 2) { return }
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    [WalkScreen]::Grab($g, [int]$r.X, [int]$r.Y, $bmp.Width, $bmp.Height)
    $file = Join-Path $OutDir "claudetracker-$Tag-$Name.png"
    $bmp.Save($file); $g.Dispose(); $bmp.Dispose()
    "  picture: $file"
}
function Toggle-State($Element) { $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() }
function Set-Switch($Element, [string]$State) {
    if ((Toggle-State $Element) -ne $State) { $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 400 }
}
# A toast lasts seconds: looked for every quarter second, pictured the moment it is there.
function Catch-Toast([string]$Name, [scriptblock]$Raise) {
    $known = @(App-Windows | ForEach-Object { $_.Current.NativeWindowHandle })
    & $Raise
    foreach ($i in 1..24) {
        Start-Sleep -Milliseconds 250
        $toast = App-Windows | Where-Object { $known -notcontains $_.Current.NativeWindowHandle } | Select-Object -First 1
        if ($toast) {
            "  toast: $((Of-Kind $toast ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name }) -join ' | ')"
            Picture $toast $Name
            return
        }
    }
    "  no toast appeared for $Name"
}

if ($Language) {
    if ($running) { Start-Process $Exe -ArgumentList "--quit"; [void]$running.WaitForExit(10000); Start-Sleep -Seconds 2 }
    Start-Process $Exe -ArgumentList "--no-focus", "--language", $Language
    "started in '$Language': $Exe"
    Start-Sleep -Seconds 5
}

"=== Settings first: the pace lines are switched on for the walk"
$popover = Popover
Press (By-Id $popover "SettingsLink"); Start-Sleep -Milliseconds 1400
$settings = Settings
$showPace = By-Id $settings "ShowPaceSwitch"
$showPaceWas = Toggle-State $showPace
Set-Switch $showPace "On"

"=== popover, first tab"
$popover = Popover
$tabs = Of-Kind $popover ([System.Windows.Automation.ControlType]::RadioButton)
if ($tabs.Count -ge 2) { $tabs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500; $popover = Popover }
Of-Kind $popover ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { "  $($_.Current.Name)" }
Picture $popover "popover-usage"

if ($tabs.Count -ge 2) {
    "=== popover, second tab"
    $tabs = Of-Kind $popover ([System.Windows.Automation.ControlType]::RadioButton)
    $tabs[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 600
    $popover = Popover
    Of-Kind $popover ([System.Windows.Automation.ControlType]::Text) | Select-Object -First 14 | ForEach-Object { "  $($_.Current.Name)" }
    Of-Kind $popover ([System.Windows.Automation.ControlType]::Image) | Select-Object -First 3 | ForEach-Object { "  [picture] $($_.Current.Name) - says: $($_.Current.HelpText)" }
    Picture $popover "popover-charts"
    $tabs = Of-Kind $popover ([System.Windows.Automation.ControlType]::RadioButton)
    $tabs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400
    $popover = Popover
}

"=== Settings"
$settings = Settings
"  title: $($settings.Current.Name)"
Picture $settings "settings-top"
$scroll = $null
foreach ($e in Of-Kind $settings ([System.Windows.Automation.ControlType]::Pane)) {
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$p) -and $p.Current.VerticallyScrollable) { $scroll = $p }
}
if ($scroll) { $scroll.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 500; Picture $settings "settings-bottom"; $scroll.SetScrollPercent(-1, 0); Start-Sleep -Milliseconds 300 }

"=== the two toasts"
Catch-Toast "toast-reset" { Press (By-Id $settings "ResetTestButton") }
Start-Sleep -Seconds 4
$notifyPace = By-Id $settings "NotifyPaceSwitch"
$paceWas = Toggle-State $notifyPace
Set-Switch $notifyPace "On"
$paceToast = By-Id $settings "PaceToastSwitch"
$toastWas = Toggle-State $paceToast
Set-Switch $paceToast "On"
if ($scroll) { $scroll.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 400 }
Start-Sleep -Milliseconds 300
Picture (Settings) "settings-pace-open"
Catch-Toast "toast-pace" { Press (By-Id $settings "PaceTestButton") }
Set-Switch (By-Id $settings "PaceToastSwitch") $toastWas
Set-Switch (By-Id $settings "NotifyPaceSwitch") $paceWas
"  pace switches back to: notify=$(Toggle-State (By-Id $settings 'NotifyPaceSwitch'))"

"=== the rename question"
if ($scroll) { $scroll.SetScrollPercent(-1, 0); Start-Sleep -Milliseconds 300 }
# The pencil is the last button but one of the first account row; found by its help text's position, not its words.
$rowButtons = @(Of-Kind $settings ([System.Windows.Automation.ControlType]::Button) | Where-Object { $_.Current.HelpText -and -not $_.Current.AutomationId })
if ($rowButtons.Count -ge 2) {
    Press $rowButtons[0]; Start-Sleep -Milliseconds 900
    $dialog = Of-Kind $settings ([System.Windows.Automation.ControlType]::Window) | Select-Object -First 1
    if ($dialog) {
        "  '$($dialog.Current.Name)': $((Of-Kind $dialog ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name }) -join ' | ') / buttons: $((Of-Kind $dialog ([System.Windows.Automation.ControlType]::Button) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ', ')"
        Picture $dialog "rename"
        # Its own close box: the same as Cancel. Nothing is renamed.
        $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep -Milliseconds 500
    } else { "  the question did not open" }
}

if ($SignInWindow) {
    "=== the sign-in window (closed again at once: its placeholder account is taken back)"
    Press (By-Id $settings "AddAccountButton"); Start-Sleep -Seconds 3
    $signIn = App-Windows | Where-Object { -not (By-Id $_ "AddAccountButton") -and -not (By-Id $_ "QuitLink") -and $_.Current.BoundingRectangle.Height -gt 300 } | Select-Object -First 1
    if ($signIn) {
        "  title: $($signIn.Current.Name)"
        $signIn.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep -Seconds 2
    } else { "  no sign-in window found" }
    "  accounts on file afterwards: $(@((Get-Content (Join-Path $env:APPDATA 'ClaudeTracker\accounts.json') -Raw | ConvertFrom-Json)).Count)"
}
$settings = Settings
if ($settings) {
    Set-Switch (By-Id $settings "ShowPaceSwitch") $showPaceWas
    "  pace lines back to: $(Toggle-State (By-Id $settings 'ShowPaceSwitch'))"
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}
"done. Look at the pictures: $OutDir\claudetracker-$Tag-*.png"
