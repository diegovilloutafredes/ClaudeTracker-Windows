# Shows that the uninstaller stops, and removes nothing, when the app will not quit.
# No app is left running for it to close: this quits the app, then holds the app's
# single-instance mutex itself, which is all the uninstaller can see of "a copy is running".
# Expect about 20 seconds, exit code 1, and the same files, apps-list entry and sign-in
# entry after as before. The installed copy is started again at the end, quietly.
#
# If the uninstaller ever goes through here, the app is gone and its data is not: run the
# setup again (the accounts, settings and history are kept by an uninstall).
$folder = Join-Path $env:LOCALAPPDATA "Programs\ClaudeTracker"
$exe = Join-Path $folder "ClaudeTracker.exe"
$uninstaller = Join-Path $folder "unins000.exe"
$log = Join-Path $env:TEMP "claudetracker-uninstall-refused.log"
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E0B5C2D-7B0A-4F0E-9C39-6A1D2B7E4C11}_is1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not (Test-Path $uninstaller)) { "no installed copy at $folder"; exit 1 }

function State {
    $entry = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).ClaudeTracker
    "files=$(@(Get-ChildItem $folder -Recurse -File -ErrorAction SilentlyContinue).Count) listed-among-the-apps=$(Test-Path $key) sign-in-entry=$([bool]$entry)"
}

$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
    foreach ($w in @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $mine))) {
        if ($w.Current.Name -like "Sign in*" -or $w.Current.Name -like "Iniciar*") { "A SIGN-IN WINDOW IS OPEN - stopping here"; exit 2 }
    }
    Start-Process $exe -ArgumentList "--quit"
    if (-not $p.WaitForExit(15000)) { "the app did not quit - stopping here"; exit 3 }
    Start-Sleep -Seconds 2
}
"before: $(State)"
$created = $false
$mutex = New-Object System.Threading.Mutex($true, "Local\ClaudeTracker.SingleInstance", [ref]$created)
try {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $run = Start-Process $uninstaller -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$log`"" -PassThru
    $null = $run.Handle
    if (-not $run.WaitForExit(120000)) { "THE UNINSTALLER DID NOT END IN 2 MINUTES" }
    else { "uninstaller: exit code $($run.ExitCode) after $([int]$clock.Elapsed.TotalSeconds) s" }
    # Its second half runs from a copy in the temp folder; wait for that too.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process | Where-Object { $_.ProcessName -like "_unins*" }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
"after:  $(State)"
"--- uninstaller log ($log)"
if (Test-Path $log) {
    Get-Content $log | Where-Object { $_ -match 'ClaudeTracker is running|did not quit|Defaulting|still running|sigue abierto|exception|Deleting file' } |
        ForEach-Object { "  " + ($_ -replace '^\S+ \S+\s+', '') } | Select-Object -First 12
}
if (Test-Path $exe) {
    Start-Process $exe -ArgumentList "--background"
    Start-Sleep -Seconds 3
    "started again: $(@(Get-Process ClaudeTracker -ErrorAction SilentlyContinue).Count) copy running"
} else { "THE APP IS GONE: run the setup again" }
