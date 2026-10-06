# ClaudeTracker for Windows

A system tray app that shows your [Claude](https://claude.ai) usage limits in real time.
It is the Windows port of [ClaudeTracker for macOS](https://github.com/diegovilloutafredes/ClaudeTracker).

![Windows](https://img.shields.io/badge/Windows-11-blue)
![.NET](https://img.shields.io/badge/.NET-10-purple)
![License](https://img.shields.io/badge/license-MIT-green)

> **Not released yet.** The app is built and runs — tray icon, popover with usage rows and
> charts, sign-in, several accounts, alerts, settings, a setup with updates — and its first
> release has not been published. Until it is, build it from source (below).

## What it does

The same things the macOS app does, shown the Windows way:

- 5-Hour and 7-Day usage with reset countdowns, plus per-model weekly limits
- The current percentage drawn into the tray icon, coloured from green to red
- Pace: how fast a window is filling and whether it will last until its reset
- Charts of the last 30 days, alerts, several accounts, English and Spanish

No API key: you sign in to claude.ai once in a window the app opens, and it reads your
usage with that session.

## Install

Once there is a release, one line in PowerShell installs it for your user, with no
administrator prompt:

```powershell
irm https://raw.githubusercontent.com/diegovilloutafredes/ClaudeTracker-Windows/main/scripts/install.ps1 | iex
```

Or download `ClaudeTracker-Setup.exe` from the latest release and run it. The setup is not
code-signed, so Windows shows "Windows protected your PC" for a copy saved by a browser:
choose **More info**, then **Run anyway**. The one line above is not stopped.

The app starts when you sign in to Windows and keeps itself up to date; both can be
switched off in its Settings. Uninstalling it from Windows' Settings keeps your accounts
and history, and tells you where they are.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/diegovilloutafredes/ClaudeTracker-Windows.git
cd ClaudeTracker-Windows
dotnet test
dotnet run --project src/ClaudeTracker.App    # the popover opens beside the tray
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1   # the setup, in artifacts\ (needs Inno Setup 6)
```

Windows 11 puts a new tray icon in the overflow menu (the `^` beside the clock); drag it
out to keep the percentage in view. Opening the app again shows the popover either way.

## How it relates to the macOS app

The two apps are separate native codebases that implement one product. A small private
workspace keeps them in step: shared specs, a feature parity matrix, shared test vectors,
and checks that the constants in both codebases agree.

## Disclaimer

This app uses **unofficial, undocumented** internal claude.ai endpoints. It is not
affiliated with, endorsed by, or supported by Anthropic. The API may change at any time
without notice. Use at your own risk.

## License

MIT — see [LICENSE](LICENSE).
