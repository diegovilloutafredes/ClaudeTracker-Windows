param(
    [int]$WatchSeconds = 34,
    [string]$AppArgs = "--background --update-feed http://127.0.0.1:38920/releases.json",
    [switch]$NoRestart
)
# Starts the installed copy against the stand-in feed (feed.ps1) and reports what it does
# about an update: each toast as it appears, the lines it logs, the popover's banner, the
# Settings row, and what it saved. Only an installed copy downloads an update: one run from
# a build folder is offered the release page and nothing else.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
$installed = Join-Path $env:LOCALAPPDATA "Programs\ClaudeTracker\ClaudeTracker.exe"
$log = Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log"
$settingsFile = Join-Path $env:APPDATA "ClaudeTracker\settings.json"
if (-not (Test-Path $installed)) { "no installed copy at $installed"; exit 1 }

function App-Windows {
    $p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { return @() }
    $mine = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $p.Id
    @($AE::RootElement.FindAll($Tree::Children, $mine) | Where-Object { $_.Current.BoundingRectangle.Width -gt 1 })
}
function Elements($Window, [string]$Kind) {
    $out = @()
    foreach ($e in $Window.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if (($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') -eq $Kind -and $e.Current.Name) { $out += $e }
    }
    $out
}

$from = @(Get-Content $log -ErrorAction SilentlyContinue).Count
if (-not $NoRestart) {
    $p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { Start-Process $installed -ArgumentList "--quit"; [void]$p.WaitForExit(10000); Start-Sleep -Seconds 2 }
    Start-Process $installed -ArgumentList $AppArgs
    "started the installed copy with: $AppArgs"
}
$toasts = @{}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -lt $WatchSeconds) {
    Start-Sleep -Milliseconds 500
    foreach ($w in App-Windows) {
        $name = $w.Current.Name
        if ($name -eq 'Claude Tracker' -or $name -like 'Settings*' -or $toasts.ContainsKey($name)) { continue }
        $toasts[$name] = $true
        "  at $([int]$clock.Elapsed.TotalSeconds) s, toast: $((Elements $w 'Text' | ForEach-Object { $_.Current.Name }) -join ' | ')"
    }
}
"--- logged"
Get-Content $log | Select-Object -Skip $from | Where-Object { $_ -match 'update|started|quit' } | ForEach-Object { "  " + ($_ -replace '^\[\S+\] ', '') }

Start-Process $installed; Start-Sleep -Milliseconds 1200          # shows the popover
$popover = App-Windows | Where-Object { $_.Current.Name -eq 'Claude Tracker' } | Select-Object -First 1
if ($popover) {
    $banner = Elements $popover 'Text' | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'available|Downloading|Installing' }
    $action = Elements $popover 'Button' | ForEach-Object { $_.Current.Name } | Where-Object { $_ -in 'Install', 'Download' }
    "--- popover banner: $($banner -join ' | ')   button: $($action -join ', ')"
    (Elements $popover 'Button' | Where-Object { $_.Current.Name -eq 'Settings' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1300
}
$settings = App-Windows | Where-Object { $_.Current.Name -like 'Settings*' } | Select-Object -First 1
if ($settings) {
    $row = @()
    foreach ($e in $settings.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $name = $e.Current.Name
        if ($name -eq 'Open Logs') { break }
        if (($kind -eq 'Text' -and $name -match 'available|signature|mismatch|Downloading|Installing|Checks every|Setup stopped|download the update') -or
            ($kind -eq 'Button' -and $name -in 'Install', 'Download', 'Check for Updates', 'Checking…')) { $row += "[$kind] $name" }
    }
    "--- Settings: $($row -join '   ')"
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}
$saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
"--- saved: lastNotifiedUpdateVersion='$($saved.lastNotifiedUpdateVersion)' failedInstallVersion='$($saved.failedInstallVersion)' failedInstallCount=$($saved.failedInstallCount) updateCheckInterval=$($saved.updateCheckInterval) autoUpdate=$($saved.autoUpdate)"
"--- running version: $((Get-Process ClaudeTracker | Select-Object -First 1).MainModule.FileVersionInfo.ProductVersion); a download kept: $(Test-Path (Join-Path $env:LOCALAPPDATA 'ClaudeTracker\Updates\ClaudeTracker-Setup.exe'))"
