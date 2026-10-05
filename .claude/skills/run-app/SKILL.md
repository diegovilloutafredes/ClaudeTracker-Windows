---
name: run-app
description: Build, launch and check the ClaudeTracker Windows tray app from a terminal — show its popover, read and capture only the app's own windows, press its buttons through UI Automation, hover and click with the real pointer where that is not enough, run its setup and uninstaller as a person would, test launch at sign-in and updates without a release, cut it off from the network to test recovery, measure its memory, and find its logs and data. Use when asked to run the app, see a UI change, or confirm something works in the real app rather than in tests.
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
- `--reset-once` treats the second poll as a reset of the 5-Hour window: about ten seconds
  after launch the log says "window reset detected: five_hour" and, with the default
  settings, a toast shows for three seconds.
- `--pace-alert-always` counts any pace as worth a warning. With "Notify when approaching
  limit" and its toast on, one pace toast appears once the usage number has moved (a pace
  needs two different readings), and only one however long it runs.
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

Reading the printed text is usually enough; read the PNG when layout or colour matters
(one per window: `%TEMP%\claudetracker-<tag>-<window name>.png`). Window names: the popover is `Claude Tracker`, the sign-in
window `Sign in to Claude` (translated when Windows' display language is Spanish).

Toasts are windows of the app too, named by their title ("Claude Usage Reset",
"Approaching usage limit"). They last seconds: to catch one, poll for it every half second
rather than looking once. "Test" in Settings raises one on demand.

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

Two things about windows that ask a question (Settings' rename and remove):

- **They sit under their owner in the automation tree**, not beside it. Look for them among
  the Settings window's descendants of type Window, not among the app's top-level windows.
- **Close one before pressing anything else, and close it by its own close box**
  (`WindowPattern.Close()`), which is Cancel. Never press "Remove" on the user's own
  account: it deletes the session. Removal is tested on a second, throwaway sign-in.

## What only the real pointer can do

UI Automation cannot hover, and its "press" does not close a menu that stays open while its
ticks are changed. `pointer.ps1` moves the real pointer onto one element of the app, hovers
or clicks, prints what the window (and any open menu or tooltip) says, and puts the pointer
back:

```powershell
$pointer = ".claude\skills\run-app\pointer.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $pointer -Control "Utilization" -Type Image -At 0.7                # rest on the first chart, 70 % across
powershell -NoProfile -ExecutionPolicy Bypass -File $pointer -Control "Chart content" -Action click -Picture menu      # open the charts' menu, picture it
powershell -NoProfile -ExecutionPolicy Bypass -File $pointer -Control "Pace" -Type MenuItem -Action click -MinIdleSeconds 0   # tick an item; the menu stays
powershell -NoProfile -ExecutionPolicy Bypass -File $pointer -Control "Claude Tracker" -Type Text -Action click -MinIdleSeconds 0   # a click on the title closes it
```

- **It borrows the user's pointer, so it refuses to run while they are at the PC**: nothing
  happens unless the last key or mouse input is at least `-MinIdleSeconds` old (20 by
  default). Its own moves count as input, so the calls that follow the first pass
  `-MinIdleSeconds 0` — only after the first one has run. Tell the user afterwards.
- **Never cut its output short with `Select-Object -First`**: that ends the script before
  it has put the pointer back. Filter with `Where-Object` instead.
- **Always close a menu you opened**, with a click on the popover's title: an error in the
  middle of a script otherwise leaves it open on the user's screen.
- Hovering a chart sets every chart's figures to the reading there ("@ 8%  12:26  pk …");
  hold still for a poll or two and they must not change back.

**Read what UI Automation says, then look at the picture too.** They can disagree: the
charts' menu once read "5-Hour, 7-Day, Utilization…" to UI Automation while every label was
drawn as an empty box (it had taken the symbol font of the button it hangs from).
`look.ps1` and `pointer.ps1` both picture menus and tooltips, which a plain copy of the
screen leaves out.

## The installed copy, the setup, the uninstaller

The user may have the app installed (`%LocalAppData%\Programs\ClaudeTracker`). That copy and
one in a build folder share one single-instance lock and one set of data, so only one runs:
`ClaudeTracker.exe --quit` (either copy's exe) closes whichever is running. **When you are
done, leave the installed copy running** (`ClaudeTracker.exe --background`), or the user's
tray icon is simply gone. Launch at sign-in and installing updates exist only in an
installed copy: test those there, not in `bin\Debug`.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1                       # artifacts\ClaudeTracker-Setup.exe
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Version 0.2.0 -OutputDir $env:TEMP\ct-020   # a "newer release" to upgrade to

$wizard = ".claude\skills\run-app\wizard.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $wizard -Program artifacts\ClaudeTracker-Setup.exe -Tag setup        # as a person would: every page, every button
powershell -NoProfile -ExecutionPolicy Bypass -File $wizard -Program "$env:LOCALAPPDATA\Programs\ClaudeTracker\unins000.exe" -Tag uninstall
Start-Process artifacts\ClaudeTracker-Setup.exe -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /AppArgs=--background /LOG=$env:TEMP\setup.log"   # as an update runs it
```

- `wizard.ps1` prints each page's text and pictures it, and presses Next, Install, Finish,
  Yes and OK — never Cancel, Back or No. Inno Setup's controls show to UI Automation as
  panes that cannot be "invoked", so it sends them the message a click sends.
- **Back the user's data up first** (`%AppData%\ClaudeTracker`, and
  `%LocalAppData%\ClaudeTracker\WebView2` with the app closed): neither the setup nor the
  uninstaller touches it, and a mistake in the script is how that would stop being true.
  Afterwards compare: the three JSON files byte for byte, and the browser profile's
  `Cookies` still there.
- After an uninstall, check that these are gone: the folder, the Start menu shortcut
  (`%AppData%\Microsoft\Windows\Start Menu\Programs\ClaudeTracker.lnk`), the apps-list key
  (`HKCU\…\Uninstall\{5E0B5C2D-…}_is1`), and both sign-in values (next section).
- `/WebView2=force` makes the setup run Microsoft's WebView2 installer even though the
  runtime is there: it shows the step is wired, not that the install works.
- **`Start-Process -Wait` on a setup never returns**: it waits for the app the setup
  starts as well. Use `-PassThru` and `.WaitForExit()`.

## Launch at sign-in

Two values under `HKCU\Software\Microsoft\Windows\CurrentVersion`: `Run\ClaudeTracker`
(the command) and `Explorer\StartupApproved\Run\ClaudeTracker` (Windows' note that the user
switched the entry off: first byte odd). `startup-apps.ps1` flips the switch in Windows' own
Settings > Apps > Startup, as a person would, and closes Settings again:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-app\startup-apps.ps1 -Set off   # or on, or read
```

Then open the app's Settings (it looks again each time it opens) or restart the app: its
switch must follow, and it must not write the entry back. To see what a sign-in does
without signing out, run the entry's own command line: the app must start with no window.

## Updates without a release

`feed.ps1` serves a stand-in for the repo's releases from this PC, and `watch-update.ps1`
restarts the **installed** copy against it and reports the toasts, the log, the popover's
banner, the Settings row and what was saved:

```powershell
$skill = ".claude\skills\run-app"
powershell -NoProfile -ExecutionPolicy Bypass -File $skill\feed.ps1 -Kind signed-badly -Setup $env:TEMP\ct-020\ClaudeTracker-Setup.exe
powershell -NoProfile -ExecutionPolicy Bypass -File $skill\watch-update.ps1          # ~35 s: found, counted down, downloaded, refused
# ... -Kind unsigned (a link only, nothing downloaded), missing-file ("Couldn't download the update"), none
& "$env:LOCALAPPDATA\Programs\ClaudeTracker\ClaudeTracker.exe" --quit
python $skill\update-reset.py                                                      # ALWAYS, with the app closed
powershell -NoProfile -ExecutionPolicy Bypass -File $skill\feed.ps1 -Kind stop
```

- Expect, for a badly signed release: "Update available — v0.2.0 found — installing in
  ~10s" eleven seconds after start, then "auto-update failed (v0.2.0, #1): Update signature
  is invalid" in the log, "Download" in the banner and in Settings, and the running version
  unchanged. Each restart tries again; the third failure shows one "Update failed" toast and
  the fourth start tries nothing, with "Install" there to press.
- **Run `update-reset.py` when done.** A `lastNotifiedUpdateVersion` of 0.2.0 left in the
  user's settings would keep the real 0.2.0 from ever being announced or installed by
  itself. Run it with `autoUpdate` after its name if you switched that off for a test.
- Nothing here can be signed with the release key, so the app accepting an update cannot be
  seen on this PC. `feed.ps1` says how to serve one signed on the Mac.
- Do not press "Download": it opens the user's browser.

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
