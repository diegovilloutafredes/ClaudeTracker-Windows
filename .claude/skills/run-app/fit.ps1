param([double]$Scale = 1.0)
# Measures the popover against the screen it is on: where its top is, how tall its list of
# charts is, and how much stands around the list. -Scale is the "Popup size" in use (1.5 for
# 150 %), to give the figures in the popover's own units -- the units of
# ChartLayout.ListSurroundings (250), which must not be less than "around it" here.
# Show the popover on the Charts tab first. With an update banner or a notice showing, the
# list must be that much shorter and the popover no taller.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "the app is not running"; exit 1 }
$mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $p.Id
$popover = @($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.Name -eq 'Claude Tracker' -and $_.Current.BoundingRectangle.Width -gt 1 }) | Select-Object -First 1
if (-not $popover) { "no popover on screen"; exit 1 }
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$window = $popover.Current.BoundingRectangle
$list = $null; $above = @()
foreach ($e in $popover.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.ClassName -eq 'ScrollViewer' -and -not $list) { $list = $e.Current.BoundingRectangle }
    $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($kind -eq 'Text' -and $e.Current.Name -match 'available|already here|disponible|ya estaba') { $above += $e.Current.Name }
}
"work area: top $($area.Top), height $($area.Height)"
"popover:   top $([int]$window.Top), height $([int]$window.Height), bottom $([int]$window.Bottom)"
"showing above the list: $(if ($above) { $above -join ' | ' } else { '(no banner, no notice)' })"
if ($list) {
    "list of charts: $([int]$list.Height) px = $([int]($list.Height / $Scale)) units; around it: $([int](($window.Height - $list.Height) / $Scale)) units"
} else { "no list of charts found (is the Charts tab showing?)" }
if ($window.Top -ge $area.Top) { "INSIDE the screen: $([int]($window.Top - $area.Top)) px to spare above" } else { "OFF THE TOP by $([int]($area.Top - $window.Top)) px" }
