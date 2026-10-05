---
name: run-app
description: Build, launch and check the ClaudeTracker Windows tray app from a terminal — show its popover, read and capture only the app's own windows, press its buttons through UI Automation, cut it off from the network to test recovery, measure its memory, and find its logs and data. Use when asked to run the app, see a UI change, or confirm something works in the real app rather than in tests.
---

# Running and checking the Windows app

The app is a tray app with no main window, so "run it and look" needs a few tricks. All of
them below were used to verify the first build (spec CT-001). Paths are relative to the
repo root.

## Build and launch

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"   # or plain `dotnet` if it is on PATH
Get-Process ClaudeTracker -ErrorAction SilentlyContinue | Stop-Process -Force   # the build fails while the exe is locked
& $dotnet build
$exe = "src\ClaudeTracker.App\bin\Debug\net10.0-windows\ClaudeTracker.exe"
Start-Process $exe
```

- **Launching shows the popover**, in the corner beside the tray. Launching again while it
  runs starts no second copy: the running one shows its popover. That is the way to bring
  the popover back from a terminal — the tray icon sits in Windows 11's overflow menu.
- The popover hides when it loses focus, and after 8 seconds if it never had it — which
  is always the case for a copy started from a terminal tool. Launch again right before
  looking at it.
- `--background` starts the app without showing anything (what the launch-at-login entry
  will pass).
- `--full-host-page` hosts the hidden browser on the full claude.ai page instead of the
  light one, for comparing the two (`shared/DIVERGENCES.md` row 4). The log says so at start.
- `--no-focus` shows the popover without asking for the keyboard focus, as when Windows
  refuses it. Expect it to leave by itself 8 seconds later unless the pointer is on it.
- `--page-heap` logs, once a minute, what the hidden page holds ("page heap: js 700 of
  1024 KB, engine objects … KB, buffers … KB"). Expect the numbers to climb and, every 30
  minutes, to fall back when the app has the page collect its garbage. A floor that rises
  from one collection to the next would be memory that is really held.
- `--challenge-once` treats the first fetch as challenged by Cloudflare, which a test cannot
  provoke for real. Expect in the log: "Cloudflare challenged a fetch from the light host
  page — switching to the full claude.ai page", then usage within seconds, and a browser
  about 190 MB heavier (`soak.ps1 -Samples 1`).

## Look at it without capturing the user's screen

`look.ps1` prints every text and button in the app's visible windows, saves a picture of
each window's own rectangle (never the whole screen), and tails the log:

```powershell
Start-Process $exe; Start-Sleep -Milliseconds 900     # show the popover
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-app\look.ps1 -Tag signed-in
```

Reading the printed text is usually enough; read the PNG (`%TEMP%\claudetracker-<tag>.png`)
when layout or colour matters. Window names: the popover is `Claude Tracker`, the sign-in
window `Sign in to Claude` (translated when Windows' display language is Spanish).

## Press a button

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process ClaudeTracker | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
$buttons = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
    $b = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttons) | Where-Object { $_.Current.Name -eq "Quit" }
    if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
# close a window the way the user would:
# $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
```

"Quit" this way is the clean exit (it closes the browser processes too). The popover must
be visible for its buttons to be found.

## Cut the app off from the network

Do not switch the PC's network off — that also cuts whatever session is running the check.
Cut only the app's browser: WebView2 reads extra browser arguments from an environment
variable, and `tunnel.py` is a loopback tunnel that passes traffic through untouched (TLS
stays end to end) until it is killed.

```powershell
$py = "$env:LOCALAPPDATA\Programs\python-embed\python.exe"   # or any Python 3
$tunnel = Start-Process $py -ArgumentList ".claude\skills\run-app\tunnel.py", "38917" -WindowStyle Hidden -PassThru
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--proxy-server=http://127.0.0.1:38917"
Start-Process $exe                                    # this copy's browser now goes through the tunnel
Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
Stop-Process -Id $tunnel.Id -Force                    # network off: open connections drop, new ones are refused
# ... look: the last rows under "Network error: Couldn't reach claude.ai (retry in 20s)"
$tunnel = Start-Process $py -ArgumentList ".claude\skills\run-app\tunnel.py", "38917" -WindowStyle Hidden -PassThru   # network back
# ... look again after the retry delay: the rows alone, and "poll: next in …" in the log
```

Launch with the tunnel stopped to test starting offline. `%TEMP%\claudetracker-tunnel.log`
lists the hosts that went through, which proves the browser used it. Quit the app and start
it normally afterwards: the variable applies to the whole life of that copy's browser.

What this does not exercise: Windows' own offline detection, DNS failures, and a changed
address on reconnect (`shared/TESTING.md`, known debt).

## Take the browser engine away

To see what the app does on a PC without WebView2, point one launch at an empty folder:

```powershell
$env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = (New-Item -ItemType Directory -Force "$env:TEMP\without-webview2").FullName
Start-Process $exe
Remove-Item Env:\WEBVIEW2_BROWSER_EXECUTABLE_FOLDER
```

Expect "Microsoft Edge WebView2 is missing on this PC. Install it from Microsoft and try
again. (retry in 20s)" in the popover, and retries in the log — not an endless "Loading…".
Nothing is written to the browser profiles in this state.

## Leave a browser profile behind

Make a folder `WV2Profile_acct-<any 32 hex digits>` beside the real profiles (see the
table below) with the app stopped, then launch. The log must say "deleting orphaned
browser profile acct-…", the folder must go, and the signed-in profile must stay.

## Measure memory

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-app\soak.ps1                                # 9 minutes
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-app\soak.ps1 -Samples 13 -EverySeconds 300  # an hour
```

One line per sample: the app's private memory, and its browser's, split by process kind;
then everything the app logged meanwhile (errors, Cloudflare challenges, poll changes).
Compare like with like: the minutes after a sign-in are not the steady state. With the
sign-in window's page just drawn, the browser measured about 110 MB more than it did after
the next launch.

## The tray icon

The icon is drawn by `TrayIcon.Render` (private). To judge it by eye, call it through
reflection from a throwaway console project that references the built `ClaudeTracker.dll`,
and paint the results enlarged on taskbar-coloured tiles (dark `#202020`, light `#F3F3F3`)
at 16, 20, 24 and 32 pixels. At 16 pixels three digits ("100") are the hard case.

## Where its state lives

| What | Where |
|---|---|
| Log | `%LocalAppData%\ClaudeTracker\Logs\claudetracker.log` |
| Settings, roster, chart history | `%AppData%\ClaudeTracker\settings.json`, `accounts.json`, `history-<id>.json` |
| Browser profiles (one per account) | `%LocalAppData%\ClaudeTracker\WebView2\EBWebView\WV2Profile_acct-<id>` |

Its browser processes are the `msedgewebview2.exe` whose command line contains
`ClaudeTracker\WebView2`; signed out there are none.

## Limits

- **Never sign in for the user or type credentials.** Anything past the sign-in page needs
  the user at the keyboard; ask them to sign in, then read the log and look at the popover.
- These files are the user's real app data once they have signed in. Don't delete them to
  "start clean" without asking.
- Showing the popover takes the keyboard focus for a moment. Don't do it in a loop while
  the user is working.
