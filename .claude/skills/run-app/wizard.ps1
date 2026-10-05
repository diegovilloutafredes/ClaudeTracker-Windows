param([string]$Program = "", [string]$Arguments = "", [string]$Tag = "wizard", [int]$TimeoutSeconds = 240, [switch]$Attach, [string]$OutDir = $env:TEMP)
# Runs a setup or an uninstaller the way a person would: starts it without switches (or, with
# -Attach, takes the one already on screen) and presses the wizard's own buttons, printing
# each page's text as it goes. Inno Setup's controls are plain Windows controls that UI
# Automation lists by class but cannot "invoke", so a button is pressed with the message a
# click sends it (BM_CLICK): the pointer is never moved.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Wiz {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

$process = $null
if (-not $Attach) {
    $process = if ($Arguments) { Start-Process $Program -ArgumentList $Arguments -PassThru } else { Start-Process $Program -PassThru }
    "started $([System.IO.Path]::GetFileName($Program)) (pid $($process.Id))"
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$seen = @{}
$shot = 0
# The setup and the uninstaller both hand over to a temporary copy of themselves: follow any
# of their processes' windows, and the message boxes they raise.
function Windows {
    $all = @()
    foreach ($w in $AE::RootElement.FindAll($Tree::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $owner = Get-Process -Id $w.Current.ProcessId -ErrorAction SilentlyContinue
            if ($owner -and $owner.ProcessName -match '^(ClaudeTracker-Setup|unins\d+|_unins|_iu14D2N|is-[A-Z0-9]+)' -and $w.Current.BoundingRectangle.Width -gt 10) { $all += $w }
        } catch {}
    }
    $all
}
$quiet = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $windows = @(Windows)
    if ($windows.Count -eq 0) {
        $quiet++
        # Gone for a few seconds after something was shown: the wizard is over.
        if ($seen.Count -gt 0 -and $quiet -ge 6) { break }
        if ($process -and $process.HasExited -and $quiet -ge 3) { break }
        continue
    }
    $quiet = 0
    foreach ($w in $windows) {
        $texts = @(); $buttons = @()
        # A message box is a window of its own under the wizard: its buttons come with the rest.
        foreach ($e in $w.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            try {
                $name = $e.Current.Name; $class = $e.Current.ClassName
                if (-not $name -or $e.Current.IsOffscreen) { continue }
                if ($class -in 'TNewButton', 'Button', 'TButton') { if ($e.Current.IsEnabled) { $buttons += $e } }
                elseif ($class -in 'TNewStaticText', 'TNewPathEdit', 'Static', 'TNewMemo', 'TRichEditViewer', 'TNewCheckListBox') { $texts += $name }
            } catch {}
        }
        $page = "$($w.Current.Name) | " + (($texts | Select-Object -Unique) -join " / ")
        $names = $buttons | ForEach-Object { $_.Current.Name -replace '&', '' }
        if (-not $seen.ContainsKey($page)) {
            $seen[$page] = $true
            "--- page: $page"
            "    buttons: $($names -join ', ')"
            $r = $w.Current.BoundingRectangle
            $shot++
            $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
            $file = Join-Path $OutDir "claudetracker-$Tag-$shot.png"; $bmp.Save($file); $g.Dispose(); $bmp.Dispose()
            "    picture: $file"
        }
        # The button that moves the wizard on; never Cancel, Back, Browse or No.
        $next = $buttons | Where-Object { ($_.Current.Name -replace '&', '') -match '^(Next( >)?|Install|Finish|OK|Yes|Uninstall|Siguiente( >)?|Instalar|Finalizar|Aceptar|Sí|Desinstalar)$' } | Select-Object -First 1
        if ($next) {
            $handle = [IntPtr]$next.Current.NativeWindowHandle
            if ($handle -ne [IntPtr]::Zero) {
                [void][Wiz]::PostMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                "    pressed: $($next.Current.Name -replace '&', '')"
                Start-Sleep -Milliseconds 900
            }
        }
    }
}
"wizard windows left: $(@(Windows).Count)" + $(if ($process) { "; process exited: $($process.HasExited)" + $(if ($process.HasExited) { " (code $($process.ExitCode))" } else { "" }) } else { "" })
