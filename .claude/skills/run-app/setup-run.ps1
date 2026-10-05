param(
    [string]$Setup = (Join-Path $PSScriptRoot "..\..\..\artifacts\ClaudeTracker-Setup.exe"),
    [string]$Name = "run",
    [string]$HoldFile = "",
    [string]$Extra = ""
)
# Runs a setup silently, the way the app's updater runs one, over whatever copy is there, and
# reports: its exit code, what its log says about closing and restarting the app, whether
# the app is running afterwards, and whether the installed folder is whole.
#
#   setup-run.ps1                          a normal upgrade: exit code 0, the app back with --background
#   setup-run.ps1 -HoldFile <a file of the installed app, e.g. ...\ClaudeTracker.Core.dll>
#                                          the setup cannot replace that file and stops half way:
#                                          exit code 5, "starting it again", and the app is back
#   setup-run.ps1 -HoldFile <the setup itself>
#                                          what the updater does: it keeps the setup open for
#                                          reading from the moment it checked it until it has run
#
# The file is held as the app holds it: others may read it, nobody may write, replace or
# remove it. Never run this while a sign-in window is open (it says so and stops).
$Setup = (Resolve-Path $Setup).Path
$log = Join-Path $env:TEMP "claudetracker-setup-$Name.log"
$appLog = Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log"
$folder = Join-Path $env:LOCALAPPDATA "Programs\ClaudeTracker"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Running {
    foreach ($p in @(Get-Process ClaudeTracker -ErrorAction SilentlyContinue)) {
        "pid $($p.Id): $((Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)").CommandLine)"
    }
}

$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
    foreach ($w in @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $mine))) {
        if ($w.Current.Name -like "Sign in*" -or $w.Current.Name -like "Iniciar*") { "A SIGN-IN WINDOW IS OPEN - not running the setup"; exit 2 }
    }
}
"before: $((Running) -join ' | ')"
$from = @(Get-Content $appLog -ErrorAction SilentlyContinue).Count
$held = $null
if ($HoldFile) {
    $held = New-Object System.IO.FileStream $HoldFile, ([System.IO.FileMode]::Open), ([System.IO.FileAccess]::Read), ([System.IO.FileShare]::Read)
    "holding $HoldFile open (version read while held: $([System.Diagnostics.FileVersionInfo]::GetVersionInfo($HoldFile).FileVersion))"
}
try {
    $arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/AppArgs=--background", "/LOG=`"$log`"")
    if ($Extra) { $arguments += @($Extra -split '\s+' | Where-Object { $_ }) }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $run = Start-Process $Setup -ArgumentList $arguments -PassThru
    $null = $run.Handle
    if (-not $run.WaitForExit(240000)) { "THE SETUP DID NOT END IN 4 MINUTES"; exit 3 }
    "setup: exit code $($run.ExitCode) after $([int]$clock.Elapsed.TotalSeconds) s"
}
finally {
    if ($held) { $held.Dispose() }
}
Start-Sleep -Seconds 5
"after: $((Running) -join ' | ')"
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E0B5C2D-7B0A-4F0E-9C39-6A1D2B7E4C11}_is1"
$entry = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).ClaudeTracker
"installed folder: $(@(Get-ChildItem $folder -Recurse -File -ErrorAction SilentlyContinue).Count) files, listed among the apps: $(Test-Path $key), sign-in entry: $([bool]$entry)"
"--- setup log ($log)"
Get-Content $log | Where-Object { $_ -notmatch 'Dest filename' -and $_ -match 'ClaudeTracker is running|did not quit|starting it again|Exception|[Aa]bort|rror|-- Run entry|^\S+ \S+\s+Filename: |Parameters:|succeeded|Rolling back|still running|sigue abierto|Retrying|Defaulting|canceled' } |
    ForEach-Object { "  " + ($_ -replace '^\S+ \S+\s+', '') } | Select-Object -First 30
"--- app log"
Get-Content $appLog | Select-Object -Skip $from | Where-Object { $_ -match 'started|quit|launch at|update|ERROR' } | ForEach-Object { "  " + ($_ -replace '^\[\S+\] ', '') } | Select-Object -First 20
