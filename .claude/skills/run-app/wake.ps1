param([int]$WatchSeconds = 75, [switch]$NoRestart)
# Tells the installed app, and only it, that the PC has come back from sleep and that the
# clock changed: the two messages Windows would broadcast to everyone, sent to the one hidden
# window through which .NET hears them. Nothing else on the PC is told anything.
#
# The app is first started against an update feed nobody serves, so every check fails: the
# check on waking must then be made a second time a minute later. Expect "back from sleep:
# checking for updates" and two "update check failed" lines about a minute apart. The clock
# message logs nothing unless the time zone really changed (then: "time zone changed from").
#
# Afterwards quit the app and start it again without the feed (--background).
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class BroadcastWindow {
    delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    public static List<IntPtr> Of(int process) {
        var found = new List<IntPtr>();
        EnumWindows((window, _) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == (uint)process) {
                var name = new StringBuilder(256); GetClassName(window, name, 256);
                if (name.ToString().StartsWith(".NET-BroadcastEventWindow")) found.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Send(IntPtr window, uint message, int wParam) {
        IntPtr result;
        return SendMessageTimeout(window, message, (IntPtr)wParam, IntPtr.Zero, 2, 3000, out result) != IntPtr.Zero;
    }
}
"@
$installed = Join-Path $env:LOCALAPPDATA "Programs\ClaudeTracker\ClaudeTracker.exe"
$log = Join-Path $env:LOCALAPPDATA "ClaudeTracker\Logs\claudetracker.log"
if (-not $NoRestart) {
    $p = Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { Start-Process $installed -ArgumentList "--quit"; [void]$p.WaitForExit(10000); Start-Sleep -Seconds 2 }
    Start-Process $installed -ArgumentList "--background", "--update-feed", "http://127.0.0.1:38920/releases.json"
    "started against a feed nobody serves; waiting for the first check (10 s after launch)"
    Start-Sleep -Seconds 16
}
$p = Get-Process ClaudeTracker | Select-Object -First 1
$from = @(Get-Content $log).Count
$windows = [BroadcastWindow]::Of($p.Id)
"the app's broadcast windows: $($windows.Count)"
foreach ($w in $windows) {
    "  clock changed (WM_TIMECHANGE) delivered: $([BroadcastWindow]::Send($w, 0x001E, 0))"
    "  back from sleep (WM_POWERBROADCAST, resumed) delivered: $([BroadcastWindow]::Send($w, 0x0218, 7))"
}
Start-Sleep -Seconds $WatchSeconds
"--- logged in the $WatchSeconds s since"
Get-Content $log | Select-Object -Skip $from | Where-Object { $_ -match 'update|sleep|zone|ERROR|unhandled' } | ForEach-Object { "  $_" }
"still running: $([bool](Get-Process ClaudeTracker -ErrorAction SilentlyContinue))"
