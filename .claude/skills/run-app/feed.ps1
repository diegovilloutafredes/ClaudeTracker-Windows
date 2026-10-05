param(
    [ValidateSet("signed-badly", "unsigned", "missing-file", "none", "stop")] [string]$Kind = "signed-badly",
    [string]$Setup = "",
    [string]$Signature = "",
    [string]$Version = "0.2.0",
    [int]$Port = 38920
)
# A stand-in for this repo's releases on GitHub, served from this PC: a releases list in
# GitHub's shape and the files it points at. Start an installed copy against it with
#   ClaudeTracker.exe --background --update-feed http://127.0.0.1:38920/releases.json
#
#   signed-badly  v<Version> with the setup and a signature that is 64 bytes of nothing
#   unsigned      v<Version> with the setup and no signature
#   missing-file  v<Version> whose setup is not there (the download fails)
#   none          nothing newer than what runs
#   stop          stops the server
#
# -Setup is the file to offer (build one with scripts\build-installer.ps1 -Version 0.2.0
# -OutputDir <somewhere>). Nothing on this PC can be signed with the release key, so the app
# must refuse every kind here. To see it accept one, sign a setup on the Mac
# (xcrun --sdk macosx swift scripts/update-signing.swift sign ClaudeTracker-Setup.exe) and
# pass the result as -Signature with -Kind signed-badly.
$feed = Join-Path $env:TEMP "claudetracker-feed"
$pidFile = Join-Path $feed "server.pid"
if (Test-Path $pidFile) {
    Stop-Process -Id ([int](Get-Content $pidFile)) -Force -ErrorAction SilentlyContinue
    [System.IO.File]::Delete($pidFile)
}
if ($Kind -eq "stop") {
    # The folder goes too: it holds a copy of a setup, some fifty megabytes of it.
    if (Test-Path $feed) { [System.IO.Directory]::Delete($feed, $true) }
    "feed stopped and removed"
    return
}

New-Item -ItemType Directory -Force $feed | Out-Null
$base = "http://127.0.0.1:$Port"
$served = Join-Path $feed "ClaudeTracker-Setup.exe"
if ($Kind -eq "missing-file") { if (Test-Path $served) { [System.IO.File]::Delete($served) } }
elseif ($Kind -ne "none") {
    if (-not $Setup -or -not (Test-Path $Setup)) { throw "-Setup <the setup file to offer> is needed for '$Kind'" }
    Copy-Item $Setup $served -Force
}
if ($Signature) { Copy-Item $Signature (Join-Path $feed "ClaudeTracker-Setup.exe.sig") -Force }
else { [System.IO.File]::WriteAllBytes((Join-Path $feed "ClaudeTracker-Setup.exe.sig"), (New-Object byte[] 64)) }
Set-Content (Join-Path $feed "release.html") "<h1>ClaudeTracker v$Version</h1>" -Encoding utf8

$assets = @()
if ($Kind -ne "none") {
    $assets += @{ name = "ClaudeTracker-Setup.exe"; browser_download_url = "$base/ClaudeTracker-Setup.exe" }
    if ($Kind -ne "unsigned") { $assets += @{ name = "ClaudeTracker-Setup.exe.sig"; browser_download_url = "$base/ClaudeTracker-Setup.exe.sig" } }
}
$releases = @()
if ($Kind -ne "none") {
    $releases += @{ tag_name = "v$Version"; html_url = "$base/release.html"; prerelease = $false; draft = $false; published_at = "2026-10-05T12:00:00Z"; assets = $assets }
}
# A draft and a pre-release that are newer still: the app must pass both over.
$releases += @{ tag_name = "v99.0.0"; html_url = "$base/release.html"; prerelease = $false; draft = $true; published_at = "2026-10-05T13:00:00Z"; assets = @() }
$releases += @{ tag_name = "v98.0.0-beta"; html_url = "$base/release.html"; prerelease = $true; draft = $false; published_at = "2026-10-05T12:30:00Z"; assets = @() }
$releases += @{ tag_name = "v0.0.1"; html_url = "$base/release.html"; prerelease = $false; draft = $false; published_at = "2026-10-03T12:00:00Z"; assets = @() }
[System.IO.File]::WriteAllText((Join-Path $feed "releases.json"), (ConvertTo-Json -InputObject $releases -Depth 5), (New-Object System.Text.UTF8Encoding $false))

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python -or $python -like "*WindowsApps*") { $python = Join-Path $env:LOCALAPPDATA "Programs\python-embed\python.exe" }
$server = Start-Process $python -ArgumentList "-m", "http.server", "$Port", "--bind", "127.0.0.1", "--directory", "`"$feed`"" -WindowStyle Hidden -PassThru
Set-Content $pidFile $server.Id
Start-Sleep -Milliseconds 900
$check = (Invoke-WebRequest -UseBasicParsing "$base/releases.json").Content | ConvertFrom-Json
"feed '$Kind' at $base/releases.json: $(@($check).Count) releases; v$Version carries $(@(($check | Where-Object { $_.tag_name -eq "v$Version" }).assets).Count) file(s)"
