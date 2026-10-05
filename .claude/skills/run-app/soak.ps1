param([int]$Samples = 7, [int]$EverySeconds = 90)
# Samples the private memory of the app and of its browser processes, then lists what the
# app logged meanwhile. The defaults give a 9-minute run; -Samples 13 -EverySeconds 300 an hour.
$log = Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log"
$t0 = Get-Date
function Snap {
    $app = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $app) { return "app not running" }
    $wv = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*ClaudeTracker\WebView2*' })
    $priv = 0; $kinds = @()
    foreach ($w in $wv) {
        $pr = Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue
        if ($pr) {
            $mb = [int]($pr.PrivateMemorySize64 / 1MB); $priv += $mb
            $kind = if ($w.CommandLine -match '--type=([a-z\-]+)') { $Matches[1] } else { 'browser' }
            $kinds += "$kind=$mb"
        }
    }
    "t+{0,4}s app={1} MB (cpu {2:0.0}s)  webview2={3} MB in {4} processes [{5}]" -f [int]((Get-Date) - $t0).TotalSeconds, [int]($app.PrivateMemorySize64 / 1MB), $app.TotalProcessorTime.TotalSeconds, $priv, $wv.Count, ($kinds -join ' ')
}
for ($i = 0; $i -lt $Samples; $i++) {
    Snap
    if ($i -lt $Samples - 1) { Start-Sleep -Seconds $EverySeconds }
}
"--- what the app logged meanwhile ---"
$from = "[" + $t0.ToUniversalTime().AddSeconds(-30).ToString("yyyy-MM-ddTHH:mm:ss")
Get-Content $log | Where-Object { $_ -ge $from }
