param(
    [int]$WatchSeconds = 0,
    [string]$Window = "*",
    [switch]$Popover,
    [switch]$Brief
)
# What a screen reader is handed: every element of the app's windows in the order it would be
# read, with its kind, its name and what else it says about itself - then whatever is wrong
# with that. With -WatchSeconds it reads twice, that many seconds apart, and says which
# elements were replaced meanwhile: a screen reader that was on one of those loses its place.
#
#   reader.ps1 -Popover             the windows that are showing, after showing the popover
#   reader.ps1 -Popover -WatchSeconds 25   ...and whether they are still the same elements after a few polls
#   reader.ps1 -Brief               only the problems
#
# It reads what Narrator reads (UI Automation). It does not speak, and a clean report here is
# not Narrator having been listened to.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$app = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { "app not running"; exit 1 }
$mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $app.Id
$pressable = 'Button', 'CheckBox', 'RadioButton', 'ComboBox', 'Slider', 'MenuItem', 'Edit', 'Hyperlink', 'ListItem', 'TabItem'
# Symbol-font glyphs (the private-use block), shapes and arrows: decoration that cannot be read aloud.
# Built from numbers so this file stays plain ASCII, which Windows PowerShell reads the same everywhere.
$symbols = "[{0}-{1}{2}-{3}{4}-{5}]" -f [char]0xE000, [char]0xF8FF, [char]0x25A0, [char]0x25FF, [char]0x2190, [char]0x21FF

function Kind($e) { $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '' }
function Id($e) { try { ($e.GetRuntimeId() -join '.') } catch { "?" } }
function Read-Windows {
    # The popover leaves by itself after a few seconds: asked for again before each look.
    if ($Popover) { Start-Process $app.Path; Start-Sleep -Milliseconds 1200 }
    $out = @()
    foreach ($w in $AE::RootElement.FindAll($Tree::Children, $mine)) {
        if ($w.Current.BoundingRectangle.Width -le 1 -or $w.Current.Name -notlike $Window) { continue }
        foreach ($e in $w.FindAll($Tree::Subtree, [System.Windows.Automation.Automation]::ControlViewCondition)) {
            try {
                # A slider's own track and thumb: parts of the slider, which is read as one control.
                if ($e.Current.ClassName -in 'RepeatButton', 'Thumb') { continue }
                $kind = Kind $e
                $state = ""
                $p = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $state = $p.Current.ToggleState.ToString().ToLower() }
                elseif ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { if ($p.Current.IsSelected) { $state = "selected" } }
                elseif ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$p)) { $state = "at " + [math]::Round($p.Current.Value, 2) }
                $out += [pscustomobject]@{
                    Window = $w.Current.Name; Id = Id $e; Kind = $kind; Name = $e.Current.Name; Help = $e.Current.HelpText
                    Keyboard = $e.Current.IsKeyboardFocusable; Enabled = $e.Current.IsEnabled; Offscreen = $e.Current.IsOffscreen; State = $state
                }
            } catch {}
        }
    }
    $out
}

$first = @(Read-Windows)
if ($first.Count -eq 0) { "no window of the app is showing"; exit 1 }
if (-not $Brief) {
    $window = ""; $previous = $null
    foreach ($e in $first) {
        if ($e.Window -ne $window) { $window = $e.Window; ""; "=== $window, as it would be read ===" }
        if ($e.Kind -eq 'Window') { continue }
        # A button's caption is listed under it as a text of its own; it is the button, not a second thing to read.
        $caption = $e.Kind -eq 'Text' -and $previous -and $previous.Kind -in $pressable -and $previous.Name -eq $e.Name
        $previous = $e
        if ($caption) { continue }
        $line = "  [$($e.Kind)] $($e.Name)"
        if ($e.State) { $line += "  ($($e.State))" }
        if ($e.Help) { $line += "  - says: $($e.Help)" }
        if ($e.Kind -in $pressable -and -not $e.Keyboard -and $e.Enabled) { $line += "  [no keyboard]" }
        $line
    }
}

""; "=== problems ==="
$problems = @()
foreach ($e in $first) {
    if ($e.Kind -eq 'Window') { continue }
    $where = "$($e.Window): [$($e.Kind)] '$($e.Name)'"
    if ($e.Kind -in $pressable -and -not $e.Name) { $problems += "no name - $where" }
    if ($e.Kind -eq 'Image' -and -not $e.Name) { $problems += "a picture without a name - $where" }
    if ($e.Kind -eq 'Text' -and -not $e.Name.Trim()) { $problems += "an empty text - $where" }
    if ($e.Name -match $symbols) { $problems += "a symbol in a name - $where" }
    if ($e.Kind -eq 'Slider' -and -not $e.Help) { $problems += "a slider that does not say its value in words - $where" }
    if ($e.Kind -in $pressable -and $e.Kind -ne 'MenuItem' -and $e.Enabled -and -not $e.Keyboard) { $problems += "cannot be reached by keyboard - $where" }
}
if ($problems.Count) { $problems | ForEach-Object { "  $_" } } else { "  none in $($first.Count) elements" }

if ($WatchSeconds -gt 0) {
    Start-Sleep -Seconds $WatchSeconds
    $second = @(Read-Windows)
    ""; "=== after $WatchSeconds s ==="
    $before = @{}; foreach ($e in $first) { $before[$e.Id] = $e }
    $after = @{}; foreach ($e in $second) { $after[$e.Id] = $e }
    $gone = @($first | Where-Object { -not $after.ContainsKey($_.Id) -and $_.Kind -ne 'Window' })
    $new = @($second | Where-Object { -not $before.ContainsKey($_.Id) -and $_.Kind -ne 'Window' })
    $changed = @($second | Where-Object { $before.ContainsKey($_.Id) -and $before[$_.Id].Name -ne $_.Name })
    "  $($second.Count) elements; $($gone.Count) gone, $($new.Count) new, $($changed.Count) saying something else"
    foreach ($e in $gone) { "  gone: [$($e.Kind)] $($e.Name)" }
    foreach ($e in $new) { "  new:  [$($e.Kind)] $($e.Name)" }
    foreach ($e in $changed) { "  same element, now: [$($e.Kind)] $($e.Name)" }
    if ($gone.Count -eq 0 -and $new.Count -eq 0) { "  every element is the one it was: a screen reader keeps its place" }
}
