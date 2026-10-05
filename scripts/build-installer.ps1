param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$WithoutWebView2
)
# Builds ClaudeTracker-Setup.exe: publishes the app with .NET inside it, fetches Microsoft's
# small WebView2 installer to carry along, and compiles installer\ClaudeTracker.iss.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1                  # the version in Directory.Build.props
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Version 0.1.1   # another, for trying an upgrade
#
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup). The release
# workflow runs this same script, so what it builds is what is tested by hand.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Find-Tool([string]$Name, [string[]]$Places) {
    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($place in $Places) { if ($place -and (Test-Path $place)) { return $place } }
    throw "$Name was not found. Looked on PATH and in: $($Places -join ', ')"
}
# The first dotnet that has an SDK: the one on PATH may be a runtime alone, with the SDK
# installed for this user only.
$dotnet = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") |
    Where-Object { $_ -and (Test-Path $_) -and (& $_ --list-sdks) } | Select-Object -First 1
if (-not $dotnet) { throw "No .NET SDK was found, on PATH or in the usual folders" }
$iscc = Find-Tool "ISCC" @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")

if (-not $Version) {
    $Version = (Select-Xml -Path (Join-Path $repo "Directory.Build.props") -XPath "//Version").Node.InnerText
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "'$Version' is not a version like 1.2.3" }
if (-not $OutputDir) { $OutputDir = Join-Path $repo "artifacts" }
$publish = Join-Path $OutputDir "publish"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"

"publishing ClaudeTracker $Version ($Configuration, win-x64, .NET included)"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
& $dotnet publish (Join-Path $repo "src\ClaudeTracker.App") -c $Configuration -r win-x64 --self-contained true "-p:Version=$Version" -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publish "ClaudeTracker.exe"))) { throw "the published folder has no ClaudeTracker.exe" }

$defines = @("/DAppVersion=$Version", "/DSourceDir=$publish", "/DOutputDir=$OutputDir")
if (-not $WithoutWebView2) {
    # Microsoft's "Evergreen Bootstrapper": 2 MB that fetch the runtime on a PC without it.
    $redist = Join-Path $OutputDir "redist"
    $bootstrapper = Join-Path $redist "MicrosoftEdgeWebview2Setup.exe"
    if (-not (Test-Path $bootstrapper)) {
        New-Item -ItemType Directory -Force $redist | Out-Null
        "fetching Microsoft's WebView2 installer"
        Invoke-WebRequest -UseBasicParsing "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bootstrapper
    }
    # It will be run on other people's PCs: it must be Microsoft's file, whatever the link returned.
    $signature = Get-AuthenticodeSignature $bootstrapper
    if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
        Remove-Item -Force $bootstrapper
        throw "the WebView2 installer that was downloaded is not signed by Microsoft ($($signature.Status))"
    }
    $defines += "/DWebView2Bootstrapper=$bootstrapper"
}

"compiling the setup"
& $iscc /Qp @defines (Join-Path $repo "installer\ClaudeTracker.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$setup = Get-Item (Join-Path $OutputDir "ClaudeTracker-Setup.exe")
"built $($setup.FullName)"
"  version $($setup.VersionInfo.FileVersion), $([math]::Round($setup.Length / 1MB, 1)) MB, sha256 $((Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLower())"
