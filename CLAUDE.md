# CLAUDE.md

Guidance for working in this repository. Cross-repo rules (specs, parity, shared data)
live in the workspace's `CLAUDE.md`, one directory up when this repo is cloned as
`windows/` inside `claudetracker-workspace`; on conflict about this repo's specifics, this
file wins.

## Project

ClaudeTracker for Windows — a system tray app that shows Claude usage limits in real time.
It is a native port of the macOS menu bar app (repo `ClaudeTracker`), which is the
reference implementation. It reads the unofficial claude.ai web API from inside a WebView2
session. Open source, MIT licensed.

**Status:** the platform-neutral library and its tests exist, and so does the app: tray
icon, popover with usage rows and pace, sign-in window, polling (spec CT-001, done) and a
Settings window with several accounts (CT-002, done), one row per account (CT-003),
alerts for resets and pace (CT-004, done), a setup with signed updates and launch at
sign-in (CT-005, done), the Charts tab (CT-006, done), and a pass in Spanish and as a
screen reader is handed it (CT-007, done) — specs in the workspace's `shared/features/`.
Nothing is released yet: the repo is not on GitHub. The app has been run signed in
(2026-10-05, through Google with a passkey), from a build folder and installed by its
setup: sign-in is detected, usage rows appear, the session survives a relaunch, an upgrade
and an uninstall, and polling recovers by itself after the network drops. What is still
untried is listed under known debt in the workspace's `shared/TESTING.md`;
`shared/PARITY_MATRIX.md` is the honest list of what works.

## Build & test

Requires the .NET 10 SDK. Nothing else.

```powershell
dotnet build          # warnings are errors
dotnet test           # xUnit; offline, no network
```

`dotnet` may not be on PATH. On the maintainer's PC the SDK is user-local:
`& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`.

## Layout

```
ClaudeTracker.slnx
Directory.Build.props                 ← the version (one line), nullable, warnings as errors
src/ClaudeTracker.Core/               ← logic, API decoders, storage. net10.0, no UI, no Windows APIs
  ApiModels.cs                        ← claude.ai payload types and their fail-soft decoding; log signatures
  UsageMath.cs                        ← urgency colours, pace, polling tiers, reset detection, time text
  RowText.cs                          ← the words of the pace lines and of money; the popup size range
  Alerts.cs                           ← the words of the two alerts, which windows raise them, the sliders' ranges
  History.cs                          ← chart history, chart series, downsampling
  ChartLayout.cs                      ← the charts' arithmetic: the forecast's frame, the axis marks, the figures
  Updates.cs                          ← release parsing, version compare, update signature verification
  FetchFailure.cs                     ← classification of in-page fetch failures; API errors
  LoginItem.cs                        ← launch at sign-in: what Windows' two records mean, and what to do about them
  Storage.cs                          ← preference keys, settings file, account roster and history files
  Localization/L.cs                   ← string lookup by English key
  Localization/Localizable.xcstrings  ← COPY of the shared string catalog (do not edit here)
  Localization/Windows.strings.json   ← strings only Windows shows
src/ClaudeTracker.App/                ← the app: WPF + WebView2, everything that touches Windows. Output: ClaudeTracker.exe
  App.xaml.cs                         ← entry point: single instance, theme, wires the pieces, 30 s clock
  UsageViewModel.cs                   ← state, accounts, the adaptive poll loop; raises Changed
  ClaudeApiClient.cs                  ← hidden WebView2 per account; runs fetch() in a claude.ai page
  UpdateService.cs                    ← finds a newer release, downloads its setup, has it judged, runs it
  TrayIcon.cs                         ← the tray icon, with the percentage drawn into it
  PopoverWindow.xaml(.cs)             ← the popover; brought up to date from the view model on every change
  Charts.cs                           ← the Charts tab, the chart element it draws, the segmented picker
  ViewCache.cs                        ← keeps a view's elements from one render to the next
  LoginWindow.xaml(.cs)               ← claude.ai's login page; detects the new session cookie
  SettingsWindow.xaml(.cs)            ← accounts, display and alert settings; its two questions (rename, remove)
  Toasts.cs                           ← the toasts above the tray: a window reset, a pace warning
  Infrastructure.cs                   ← file locations, the log, light/dark detection, the sign-in entry, the symbol font, Win32 calls
  Assets/ClaudeTracker.ico            ← the app icon, written by scripts/generate-appicon.ps1
tests/ClaudeTracker.Core.Tests/       ← xUnit; Fixtures/ holds COPIES of the shared test vectors
installer/ClaudeTracker.iss           ← the setup and the uninstaller (Inno Setup 6)
scripts/build-installer.ps1           ← publishes the app and compiles the setup; by hand and in the release workflow
scripts/install.ps1                   ← the one-line install; its path on main is in every README that quotes it
scripts/publish-release.sh            ← run on the Mac: signs a draft release's setup and publishes it
scripts/generate-appicon.ps1          ← redraws the icon; not a build step
.claude/skills/run-app/               ← how to launch the app and look at it from a terminal
```

## How the app works

- **No main window.** `App` owns a `TrayIcon` and a `PopoverWindow`; the process lives
  until "Quit".
- **Opening the app shows the popover**, beside the tray. Windows 11 keeps a new tray icon
  in its overflow menu, so the icon alone would leave a launch with nothing on screen. A
  second copy starts nothing (named mutex): it signals the first through a named event to
  show its popover, and exits. `--background` starts silently; the launch-at-login entry
  must pass it.
- **One change event.** `UsageViewModel.Changed` fires after any state change; the tray
  icon and the popover redraw from the view model's current values. There are no bindings.
  Everything runs on the UI thread, where WebView2 has to live, so async code needs no locks.
- **The Settings window is not rebuilt on a change.** A poll lands
  every few seconds, and rebuilding would replace a slider under the hand dragging it and
  take the keyboard focus from a switch. Its Display controls are made once and only their
  values refreshed, under a flag (`refreshing`) that keeps the change handlers from writing
  the same value back; the account list is rebuilt only when what it shows has changed.
- **The popover's elements are kept from one render to the next** (`ViewCache`). It is
  brought up to date at every poll, every few seconds. Made of new elements each time, a
  screen reader that was reading a row found itself at the top of the window again, the
  Charts tab closed its own content menu and scrolled its list back, and whatever held the
  keyboard lost it. Fixed parts are in the XAML or made once (`ChartsTab`, `Segmented`);
  what comes and goes with the data — rows, sections, charts — is asked of a `ViewCache`
  by name, so the same name gives the same element and a render only changes what it says.
  Whatever is set on a kept element must be set at every render: it remembers the last one.
  `ViewCache.SetChildren` leaves a panel alone when it already holds what it should.
- **A screen reader is given things in the order they are put into the tree, one element
  per thing.** A usage row is one element (`UsageRow`) that reads "5-Hour Window, 31%,
  Resets in 22 min · 15:50", as the Mac's combined row does; a chart is one picture that
  says its figures in whole words. Texts that only repeat that for the eye — a chart's
  heading and its row of "pk" and "avg", the "30m" beside a slider, a symbol — are
  `QuietText`, which a screen reader is not shown. Never put a row together from the right
  edge inwards (a `DockPanel` with the right-hand part added first): it looks the same and
  reads backwards — the percentage before its window, "Sign out & remove" before the
  account's name — and the Tab key follows the same order. Section titles carry a heading
  level; a slider says its value in words in its help text.
  `.claude/skills/run-app/reader.ps1` shows what is handed over; run it after any change
  to a window.
- **The charts are drawn by the app** (`MiniChart.OnRender`): no charting library. What
  can be wrong without looking wrong is in `ChartLayout`, pure and tested — the forecast's
  frame, "expected" at a moment, the pace scale, and the time axis's marks, which are
  counted on the wall clock so they stay on round times when the clocks change.
- **The tray icon is first shown under the app's name alone** (`TrayIcon`'s constructor).
  Windows keeps the first tooltip an icon is ever given: its Settings list the icon under
  it for good, and a screen reader is read the one it was added with in front of the
  current one. Enter on the icon, from the keyboard, arrives as a double click, which
  shows the popover. So does a double click of the mouse, whose first click has opened
  it and whose second press has closed it again: it ends open (meant, and not yet seen —
  `shared/TESTING.md`).
- **A date is written in the language of the sentence around it** (`TimeText.DisplayCulture`):
  Windows' regional format while that speaks the display language, else the display
  language's own.
- **Settings moves up when it grows past the bottom of the screen** (`KeepOnScreen`): it is
  as tall as what it shows, and switching pace alerts on adds six rows underneath.
- **A symbol is an element of its own inside its button** (`Symbols.Text`), never the
  button's font. A menu takes the font of the button it hangs from, and the symbol font
  draws every letter as an empty box: the chart content menu showed seven boxes with ticks
  while UI Automation read its labels correctly. Look at a picture of anything new.
- **A menu of the popover closes with the popover** (`HidePopover`). One opened without a
  click, as a screen reader opens it, on a popover that never had the focus, has nothing
  else to close it.
- **A copy the setup installed is told apart from one in a build folder**
  (`AppPaths.IsInstalled`: the setup's uninstaller is beside it). Only an installed copy
  enters the user's sign-in or replaces itself with an update; the other shows neither
  switch in Settings and is offered an update as a link. A build folder is rebuilt under
  your hands, and registered at sign-in it would be what starts instead of the real app.
- **"--quit" closes the running copy.** The copy started with it raises a named event and
  starts nothing. The setup and the uninstaller use it before they touch the app's files,
  and then wait for the single-instance mutex to go.
- **Launch at sign-in is two registry values, and the app follows what was done to them
  elsewhere** (`LoginItem.AtStart`, applied by `SyncLaunchAtLogin` at start and whenever
  Settings opens). The entry is a value under the user's Run key, run with
  `--background`. Windows does not remove an entry the user switches off in Task Manager
  or Settings: it keeps it and notes the fact beside it (`StartupApproved\Run`), first byte
  odd for off. Windows 11's Settings wrote 01 and, for on again, twelve zeros — not the 03
  and 02 written down for Task Manager — so the rule reads odd and even. The app opts in
  once, on the first run of an installed copy; after that an entry switched off or removed
  turns the app's switch off and is never written back, and one switched on again turns it
  on. The uninstaller removes the entry, so the setup says `--just-installed` to the app it
  starts after a first install, and the app puts the entry back if the kept settings said on.
- **An update is a setup file, run only on a verdict** (`UpdateService`, `Updates.JudgeSetup`):
  signed by the key compiled into the app, and a newer version than this copy — the
  signature first, because nothing a file says about itself counts until the file is known
  to be the maintainer's. A setup that is refused, or only half arrived, is deleted.
- **The app does not quit to be updated.** It starts the setup with
  `Updates.SilentInstallArguments` and waits; the setup asks it to quit when it is ready to
  replace it, and starts it again with what `/AppArgs=` carried (`--background`). A setup
  that stops early therefore leaves the app running, which counts it as a failed install:
  three of one release and it stops trying (`RecordInstallFailure`, the Mac's rule).
- **"Download" opens a release's page only if it is a web address** (`Updates.IsWebLink`).
  The address comes out of a list the app fetched, and Windows opens or runs whatever it
  is handed.
- **Switches apply on Checked and Unchecked, never on Click.** A screen reader toggles a
  switch without clicking it. The same goes for anything a test drives through UI
  Automation — which also presses buttons of a window that a question is blocking, hence
  the `asking` guard in `SettingsWindow`.
- **Fetching.** `ClaudeApiClient` keeps one hidden WebView2 on a claude.ai page and runs
  each API call as `fetch()` inside it, through the DevTools `Runtime.evaluate` call with
  `awaitPromise` — WebView2's own `ExecuteScriptAsync` returns `{}` for a promise. A failed
  fetch throws a token (`HTTP_401`, `CF_CHALLENGE`) that `FetchFailures.Classify` reads,
  exactly as on the Mac. A failure with no status gets plain words for the popover
  (`FetchFailures.NetworkDetail`): Chromium's own are "TypeError: Failed to fetch" and, for
  a page that would not load, an enum name such as "Unknown". The raw value goes to the log.
- **The hidden page is a light one** (`/robots.txt`, same origin): the browser takes about
  140 MB on it against about 330 MB on the web app. The first fetch Cloudflare challenges
  switches that client to the full page for good. No real challenge has been seen yet, so
  that switch is exercised with `--challenge-once`; `--full-host-page` starts on the full
  page. The measurements and what is still unknown are in `shared/DIVERGENCES.md` row 4.
- **The hidden page is asked to collect its garbage every 30 minutes**
  (`CollectGarbageIfDue`, after a fetch that worked). Each poll leaves a few kilobytes
  behind, and Chromium does not collect a page this quiet by itself: its process grew by
  11 MB an hour until a collection was forced, which took back everything the page held.
  `--page-heap` logs the page's heap once a minute to watch it.
- **One WebView2 profile per account** (`Account.ProfileName`), all in one user-data
  folder. Removing an account deletes its profile, which also closes any view still on it.
- **An account is never in the list twice** (CT-003). Who signed in is only known when the
  account profile arrives, in `RefreshAccountInfoAsync`; if another row already has that
  email, `Accounts.MergeDuplicate` says what the roster becomes and `AdoptSession` applies
  it: the row that was already there keeps its id, name and history and takes over the new
  profile, the row made for the sign-in goes, and the old profile is deleted. Asked only
  when a row first learns its email, so signing a row back in never merges it.
- **Sign-in** uses its own visible web view on the account's profile. The window polls the
  profile's cookies once a second and reports a `sessionKey` that differs from the one
  present when it opened. Fetches for that account wait while it is open.
- **A poll must always schedule the next one.** `FetchUsageAsync` catches everything for
  that reason; an exception escaping it would end polling without a trace. The same goes
  for what leads up to the first poll: `RefreshAccountInfoAsync` is the first call to
  create the web view, and catches everything too; `Notify` swallows (and logs) a view's
  failure to redraw, because the loop notifies before it arms the next poll.
- **A failed web view creation is never kept.** `WebViewAsync` and
  `WebViewHost.EnvironmentAsync` drop a faulted task, and after a failure the next attempt
  starts from a new environment (WebView2's own advice: an update may have replaced the
  runtime the old one was bound to). A creation that is still pending when readiness
  times out is given up on; `creation` numbers the attempts so a late one closes itself.
- **A load counts only its own navigation** (`LoadAsync` matches the navigation id): the
  navigation it replaces completes too, as a failure. Outside a load, a navigation of the
  page's own that fails or leaves claude.ai marks the page for reloading.
- **Profiles nobody owns are deleted at launch** (`SweepOrphanedProfiles`), once the
  roster is final and never while a corrupt roster is set aside. That is what cleans up
  after a quit or a failed delete; deleting a profile takes a live browser process.
- **A toast never takes the focus** (`ToastWindow`: `WS_EX_NOACTIVATE`, not focusable, shown
  without activation). If it did, clicking it would make it the active window and the
  popover, which hides when it loses the focus, would vanish under it. Since nobody can tab
  to it, its text is announced to screen readers when it appears (`Announce`). `ToastHost`
  stacks toasts upwards from the tray's corner, above the popover while that shows, in
  pixels like the popover.
- **Alerts are for the active account only, and a pace alert warns once per episode.** The
  rule for one window is `PaceMath.AlertStep`; `CheckPaceNotifications` applies it to
  every window and keeps the toast ids so a warning can be taken down when its reason is
  gone (the pace eased, the window reset, alerts switched off, another account active).
- **The popover hides when it loses the focus — and by itself if it never had it**
  (`HideIfNeverFocused`, 8 s, unless the pointer is on it). Windows does not always let a
  window take the focus, and one that never had it never gets `Deactivated`. A process
  started from a terminal tool is always in that case, so there the popover is a
  notification: look at it within those seconds.
- **The tray icon's press is noted before its click** (`NoteTrayPress`): pressing the icon
  hides an open popover by taking the focus, so the click that follows must already know
  it was open, or it would open it again.
- **The popover is pinned by its bottom-right corner, in pixels, inside
  `WM_WINDOWPOSCHANGING`** (`PopoverWindow.KeepPinned`). Its height follows its content, and
  a window grows from its top-left corner, so without the pin the rows arriving after
  "Loading…" push its lower half off the bottom of the screen (seen on the first signed-in
  run). Do not move this to `SizeChanged`: that event fires before the window itself has
  been resized, so `GetWindowRect` still returns the old size and the correction does
  nothing. Pixels, not WPF units, because WPF's coordinates change meaning between monitors
  of different scale.

### Development switches

Command-line arguments that exist to make a rare state appear on demand. None changes
what a user gets; `.claude/skills/run-app` says what to expect from each.

| Argument | What it does |
|---|---|
| `--background` | Starts without showing the popover. Not for development: the sign-in entry passes it, and so does an update |
| `--quit` | Closes the copy that is running and starts nothing. Not for development either: the setup and the uninstaller use it |
| `--just-installed` | Said by the setup to the app it starts after a first install (not an upgrade): a sign-in entry the uninstaller took away is put back if the settings say on |
| `--language <tag>` | Runs this copy in another language without changing Windows: `es` for the words, `es-CL` for the regional format too. Windows' own words inside the app (a text field's menu) stay as Windows has them |
| `--update-feed <address>` | Reads the releases from that address instead of GitHub. What it serves is trusted no more than GitHub: a setup still needs a signature the embedded key verifies |
| `--full-host-page` | Hosts the hidden browser on the full claude.ai page from the start |
| `--challenge-once` | Treats the first fetch as challenged by Cloudflare |
| `--no-focus` | Shows the popover without asking for the keyboard focus |
| `--page-heap` | Logs what the hidden page holds in memory, once a minute |
| `--reset-once` | Treats the second poll as a reset of the 5-Hour window, through the real detection path |
| `--pace-alert-always` | Counts any pace as inside the warning threshold, so a pace alert fires as soon as there is a pace |

## How this code relates to the Mac app

- **Every pure function has a Swift twin of the same name** in the Mac repo's
  `Models.swift`, `APIModels.swift` or `UpdateService.swift`; the workspace's
  `shared/NAMING_MAP.md` lists the pairs. Change them together, and change both test
  suites: each C# test is the port of a Swift test of the same name, asserting the same
  values.
- **Shared data, never shared code.** `Localization/Localizable.xcstrings` and everything
  in `tests/ClaudeTracker.Core.Tests/Fixtures/` are byte-identical copies of files in the
  workspace's `shared/` repo. Edit the canonical file there, re-copy it to both app repos,
  and run the workspace gate (`python3 shared/tools/check_all.py`). `.gitattributes`
  marks these copies `-text` so Git never converts their line endings.
- **Constants are registered.** A product constant (a poll tier, a threshold, the signing
  key) has a row and a regex anchor in the workspace's `shared/BUSINESS_RULES.md`. The
  anchors match the source text, so reformatting such a line can fail the gate: fix the
  anchor in the same change.
- **Deliberate differences are written down** in `shared/DIVERGENCES.md`. If you make the
  Windows app behave differently from the Mac app on purpose, add a row.

## Key constraints

**`ClaudeTracker.Core` stays free of UI and Windows APIs.** It targets plain `net10.0` so
its tests run anywhere. Anything that needs `System.Windows`, the registry or WebView2
belongs in the app project.

**Decoding is fail-soft in specific ways** (`ApiModels.cs`, and the table in
`shared/API_CONTRACT.md`): a malformed sub-window becomes null, a malformed `limits`
element is dropped, a wrong-typed optional field reads as null — and only a payload that
is not a JSON object fails. The API has changed shape before; keep each rule when adding a
field, and add a fixture test for it.

**Persisted files must keep loading.** New fields on `Account` or `UsageDataPoint` must be
optional. A settings, roster or history file that fails to parse is copied to a `.corrupt`
sibling before anything can overwrite it (`Storage.cs`).

**Strings are looked up by their English text** (`L.T`, `L.F`). Format keys keep the Mac
catalog's printf specifiers (`%@`, `%d`, `%lld`), so a key is the same string in both
apps. A string the Mac app also shows goes in the shared catalog; a Windows-only string
goes in `Windows.strings.json` with its Spanish translation.

**Numbers in logs and labels match the Mac app's formatting**: "." as the decimal point
regardless of the system language, and round-half-even where Swift's `%.1f` does it
(`PaceRateUnits.Format`).

**The update-signing public key in `Updates.cs` is the Mac app's key.** One key signs both
apps' releases; its private half exists only in the maintainer's Mac Keychain. The test
`EmbeddedPublicKeyMatchesTheReleaseSigningKey` verifies a real signature against it.

## The setup

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1                  # artifacts\ClaudeTracker-Setup.exe, the version in Directory.Build.props
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Version 0.1.1   # another version, to try an upgrade or an update
```

Needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`; the script finds it in the
user's or the machine's folder). It publishes the app for win-x64 with .NET inside it,
fetches Microsoft's small WebView2 installer (and refuses it unless Microsoft signed it),
and compiles `installer/ClaudeTracker.iss`.

The setup installs for the current user (`%LocalAppData%\Programs\ClaudeTracker`), asks for
no rights, and leaves `%AppData%\ClaudeTracker` and `%LocalAppData%\ClaudeTracker` — the
accounts, the settings, the sessions — alone, as does the uninstaller. What the app and
the script agree on by name is listed at the top of the script: change one side and the
other breaks silently.

In an Inno Setup script no line of `[Code]` may begin with a square bracket (it reads as a
section), and `Start-Process -Wait` on a setup waits for the app the setup starts too.

## Releases

Nothing has been released yet, and the two steps below that need GitHub or the Mac have
never run. The script they share with a build by hand has.

```bash
# 1. Bump <Version> in Directory.Build.props, commit, tag the same number, push both.
git tag v1.2.0 && git push origin main v1.2.0
# 2. .github/workflows/release.yml builds the setup and makes a DRAFT release with it.
# 3. On the Mac, where the signing key is (the Mac repo is looked for beside this one):
scripts/publish-release.sh 1.2.0      # waits for the workflow, signs the setup, uploads its .sig, publishes
```

- This repo releases on its own, with its own version: the `<Version>` line in
  `Directory.Build.props`, tagged `v<Version>`. The workflow refuses a tag that is not that
  version.
- The updater reads this repo's GitHub releases and installs the asset named exactly
  `ClaudeTracker-Setup.exe`, only when `ClaudeTracker-Setup.exe.sig` verifies against the
  embedded key (`Updates.ParseGitHubReleases`, `Updates.JudgeSetup`). A draft is passed
  over, so nothing reaches anyone until the last step.
- Never publish a Windows file or tag in the Mac repo: installed Mac copies update
  themselves from that repo's newest release and would try to install it.
- The setup is not code-signed. One saved by a browser is stopped by Windows SmartScreen
  ("Windows protected your PC") until a certificate signs it; the one-line install
  (`scripts/install.ps1`) is not, because a file PowerShell fetches is not marked as having
  come from the Internet.

## Commits

Reference the spec ID from the workspace's `shared/features/` in the subject, for example
`feat(tray): draw the percentage into the icon (CT-001)`. Never add AI attribution.
