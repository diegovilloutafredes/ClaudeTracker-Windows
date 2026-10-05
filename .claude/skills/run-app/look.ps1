param([string]$Tag = "state", [int]$LogLines = 6, [string]$OutDir = $env:TEMP)
# Prints what the app's own windows say (through UI Automation), saves a picture of each
# (only the window's own rectangle, never the screen), and tails the app's log.
# A hidden popover is not listed: launch ClaudeTracker.exe again first, which shows it.
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
"=== $Tag @ $((Get-Date).ToUniversalTime().ToString('HH:mm:ss'))Z ==="
$p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "app not running" }
else {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
        $r = $w.Current.BoundingRectangle
        if ($r.Width -le 1 -or $r.Height -le 1) { continue }
        "window '$($w.Current.Name)' w=$([int]$r.Width) h=$([int]$r.Height) bottom=$([int]($r.Y + $r.Height))"
        foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $kind = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            if ($e.Current.Name -and $kind -ne 'Window') { "  [$kind] $($e.Current.Name)" }
        }
        $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        $picture = Join-Path $OutDir "claudetracker-$Tag.png"
        $bmp.Save($picture)
        $g.Dispose(); $bmp.Dispose()
        "  picture: $picture"
    }
}
"--- log ---"
Get-Content (Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log") -Tail $LogLines
