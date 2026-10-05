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
alerts for resets and pace (CT-004, done) and the Charts tab (CT-006, done) — specs in the
workspace's `shared/features/`. That build has been run signed in (2026-10-05, through
Google with a passkey): sign-in is detected, usage rows appear, the session survives a
relaunch, and polling recovers by itself after the network drops. What is still untried is
listed under known debt in the workspace's `shared/TESTING.md`; `shared/PARITY_MATRIX.md`
is the honest list of what works.

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
  Storage.cs                          ← preference keys, settings file, account roster and history files
  Localization/L.cs                   ← string lookup by English key
  Localization/Localizable.xcstrings  ← COPY of the shared string catalog (do not edit here)
  Localization/Windows.strings.json   ← strings only Windows shows
src/ClaudeTracker.App/                ← the app: WPF + WebView2, everything that touches Windows. Output: ClaudeTracker.exe
  App.xaml.cs                         ← entry point: single instance, theme, wires the pieces, 30 s clock
  UsageViewModel.cs                   ← state, accounts, the adaptive poll loop; raises Changed
  ClaudeApiClient.cs                  ← hidden WebView2 per account; runs fetch() in a claude.ai page
  TrayIcon.cs                         ← the tray icon, with the percentage drawn into it
  PopoverWindow.xaml(.cs)             ← the popover; rebuilt from the view model on every change
  Charts.cs                           ← the Charts tab, the chart element it draws, the segmented picker
  ViewCache.cs                        ← keeps a view's elements from one render to the next
  LoginWindow.xaml(.cs)               ← claude.ai's login page; detects the new session cookie
  SettingsWindow.xaml(.cs)            ← accounts, display and alert settings; its two questions (rename, remove)
  Toasts.cs                           ← the toasts above the tray: a window reset, a pace warning
  Infrastructure.cs                   ← file locations, the log, light/dark detection, the symbol font, Win32 calls
tests/ClaudeTracker.Core.Tests/       ← xUnit; Fixtures/ holds COPIES of the shared test vectors
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
- **The Settings window is not rebuilt on a change, unlike the popover.** A poll lands
  every few seconds, and rebuilding would replace a slider under the hand dragging it and
  take the keyboard focus from a switch. Its Display controls are made once and only their
  values refreshed, under a flag (`refreshing`) that keeps the change handlers from writing
  the same value back; the account list is rebuilt only when what it shows has changed.
- **The Charts tab is not rebuilt either: it is one element, brought up to date**
  (`ChartsTab.Refresh`). Rebuilt like the usage rows, it closed its own content menu,
  scrolled its list back to the top and took the keyboard from its range picker at every
  poll. Its fixed parts are made once; what comes and goes with the data — sections, charts,
  figures — is asked of a `ViewCache` by name, so the same name gives the same element and
  a render only changes what it says. Whatever is set on a kept element must be set at
  every render: it remembers the last one. `PopoverWindow.Render` leaves the tab where it
  is when it is already what the body shows.
- **The charts are drawn by the app** (`MiniChart.OnRender`): no charting library. What
  can be wrong without looking wrong is in `ChartLayout`, pure and tested — the forecast's
  frame, "expected" at a moment, the pace scale, and the time axis's marks, which are
  counted on the wall clock so they stay on round times when the clocks change.
- **A symbol is an element of its own inside its button** (`Symbols.Text`), never the
  button's font. A menu takes the font of the button it hangs from, and the symbol font
  draws every letter as an empty box: the chart content menu showed seven boxes with ticks
  while UI Automation read its labels correctly. Look at a picture of anything new.
- **A menu of the popover closes with the popover** (`HidePopover`). One opened without a
  click, as a screen reader opens it, on a popover that never had the focus, has nothing
  else to close it.
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
| `--background` | Starts without showing the popover. Not only for development: the launch-at-login entry will pass it |
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

## Releases

Not set up yet. The rules that are already fixed:

- This repo releases on its own, with its own version: the `<Version>` line in
  `Directory.Build.props`, tagged `v<Version>`.
- The updater will read this repo's GitHub releases and install the asset named exactly
  `ClaudeTracker-Setup.exe`, only when `ClaudeTracker-Setup.exe.sig` verifies against the
  embedded key (`Updates.ParseGitHubReleases`, `Updates.VerifyUpdateSignature`).
- Never publish a Windows file or tag in the Mac repo: installed Mac copies update
  themselves from that repo's newest release and would try to install it.

## Commits

Reference the spec ID from the workspace's `shared/features/` in the subject, for example
`feat(tray): draw the percentage into the icon (CT-001)`. Never add AI attribution.
