# ClaudeTracker for Windows

A system tray app that shows your [Claude](https://claude.ai) usage limits in real time.
It is the Windows port of [ClaudeTracker for macOS](https://github.com/diegovilloutafredes/ClaudeTracker).

![Windows](https://img.shields.io/badge/Windows-11-blue)
![.NET](https://img.shields.io/badge/.NET-10-purple)
![License](https://img.shields.io/badge/license-MIT-green)

> **Work in progress — there is nothing to install yet.** This repository holds the app's
> platform-neutral core (the usage logic, the claude.ai payload decoders and their tests),
> ported from the macOS app, and a first, unreleased build of the tray app on top of it:
> tray icon, popover with the usage rows, sign-in, polling.

## What it will do

The same things the macOS app does, shown the Windows way:

- 5-Hour and 7-Day usage with reset countdowns, plus per-model weekly limits
- The current percentage drawn into the tray icon, coloured from green to red
- Pace: how fast a window is filling and whether it will last until its reset
- Charts of the last 30 days, alerts, several accounts, English and Spanish

No API key: you sign in to claude.ai once in a window the app opens, and it reads your
usage with that session.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/diegovilloutafredes/ClaudeTracker-Windows.git
cd ClaudeTracker-Windows
dotnet test
dotnet run --project src/ClaudeTracker.App    # the popover opens beside the tray
```

Windows 11 puts a new tray icon in the overflow menu (the `^` beside the clock); drag it
out to keep the percentage in view. Opening the app again shows the popover either way.

## How it relates to the macOS app

The two apps are separate native codebases that implement one product. A small workspace
repo ([claudetracker-workspace](https://github.com/diegovilloutafredes/claudetracker-workspace))
keeps them in step: shared specs, a feature parity matrix, shared test vectors, and
checks that the constants in both codebases agree.

## Disclaimer

This app uses **unofficial, undocumented** internal claude.ai endpoints. It is not
affiliated with, endorsed by, or supported by Anthropic. The API may change at any time
without notice. Use at your own risk.

## License

MIT — see [LICENSE](LICENSE).
