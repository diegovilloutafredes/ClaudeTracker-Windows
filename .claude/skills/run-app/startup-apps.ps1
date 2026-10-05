param([ValidateSet("off", "on", "read")] [string]$Set = "read")
# Switches ClaudeTracker in Windows' own list (Settings > Apps > Startup), the way a person
# does it, and closes Settings again. This is the "switched off somewhere else" the app must follow.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $Tree = [System.Windows.Automation.TreeScope]
Start-Process "ms-settings:startupapps"
$toggle = $null; $window = $null
foreach ($try in 1..20) {
    Start-Sleep -Milliseconds 700
    foreach ($w in $AE::RootElement.FindAll($Tree::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            if ($w.Current.ClassName -ne 'ApplicationFrameWindow') { continue }
            foreach ($e in $w.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                $p = $null
                if ($e.Current.Name -like '*ClaudeTracker*' -and $e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $toggle = $e; $window = $w }
            }
        } catch {}
    }
    if ($toggle) { break }
}
if (-not $toggle) { "ClaudeTracker was not found in Windows' startup list"; if ($window) { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }; exit 1 }
$pattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
"Windows' startup list: '$($toggle.Current.Name)' is $($pattern.Current.ToggleState)"
$want = if ($Set -eq "off") { "Off" } elseif ($Set -eq "on") { "On" } else { $null }
if ($want -and $pattern.Current.ToggleState.ToString() -ne $want) {
    $pattern.Toggle(); Start-Sleep -Milliseconds 900
    "switched: now $($pattern.Current.ToggleState)"
}
$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
$note = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -ErrorAction SilentlyContinue).ClaudeTracker
"what Windows wrote beside the entry: $(if ($note) { ($note | ForEach-Object { $_.ToString('X2') }) -join ' ' } else { '(nothing)' })"
