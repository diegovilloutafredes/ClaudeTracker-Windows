# ClaudeTracker for Windows, installed with one line in PowerShell:
#
#   irm https://raw.githubusercontent.com/diegovilloutafredes/ClaudeTracker-Windows/main/scripts/install.ps1 | iex
#
# Downloads the latest release's setup and runs it for this user: no administrator prompt,
# no questions. A file fetched this way is not marked as having come from the Internet, so
# Windows SmartScreen does not stop it the way it stops an unsigned setup saved by a browser.
# The path of this file on main is part of that line: moving or renaming it breaks it for
# everyone who has it written down.
& {
    $ErrorActionPreference = "Stop"
    # CLAUDETRACKER_SETUP_URL is for trying this script against a setup that is not released yet.
    $url = if ($env:CLAUDETRACKER_SETUP_URL) { $env:CLAUDETRACKER_SETUP_URL }
           else { "https://github.com/diegovilloutafredes/ClaudeTracker-Windows/releases/latest/download/ClaudeTracker-Setup.exe" }
    $setup = Join-Path ([System.IO.Path]::GetTempPath()) "ClaudeTracker-Setup.exe"

    Write-Host "==> Downloading ClaudeTracker..."
    $ProgressPreference = "SilentlyContinue"   # the progress bar slows a download many times over
    Invoke-WebRequest -UseBasicParsing $url -OutFile $setup

    Write-Host "==> Installing..."
    try {
        # The setup closes a copy that is running, replaces it, and starts the new one.
        # Waited for by itself, not with -Wait: that waits for everything the setup starts
        # too, and the app it starts keeps running.
        $process = Start-Process $setup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -PassThru
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "The setup ended with code $($process.ExitCode)." }
    }
    finally {
        Remove-Item -Force $setup -ErrorAction SilentlyContinue
    }
    Write-Host "ClaudeTracker is installed and running. Its icon is beside the clock; Windows may keep it under the ^ arrow."
}
