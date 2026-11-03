# WinKit

WinKit is a small manager for a family of native Windows utilities. Pick the ones you want and
WinKit downloads them from their GitHub releases, keeps them up to date, starts them with
Windows and runs them in the background. It lives in the notification area and needs no
administrator access or separate runtime.

## Managed utilities

| Utility | What it does | Project |
| --- | --- | --- |
| Comienzo | A fast, keyboard-first Start menu replacement | [victor141516/comienzo](https://github.com/victor141516/comienzo) |
| Voolime | Per-app volume control with your volume keys | [victor141516/voolime](https://github.com/victor141516/voolime) |
| Trapezoid | Move and resize windows with keyboard shortcuts | [victor141516/trapezoid](https://github.com/victor141516/trapezoid) |
| Captura | Capture, annotate and share screenshots | [victor141516/Captura](https://github.com/victor141516/Captura) |
| Altabi | Mac-style Alt+Tab grouped by app, with window previews | [victor141516/altabi](https://github.com/victor141516/altabi) |

## Use

Download `WinKit.exe` from the [latest release](https://github.com/victor141516/WinKit/releases/latest)
and run it. It copies itself to `%LOCALAPPDATA%\WinKit\WinKit.exe`, so Windows startup never
points to a temporary download folder. Choose the utilities you want and click
**Apply selection**. Closing the window keeps WinKit in the tray, where you can also toggle
utilities, check for updates or exit.

Every time the window opens, and whenever you press the refresh button in its top-right
corner, WinKit downloads the list of utilities ([`catalog.json`](catalog.json)) and checks the
latest release of each one. New utilities appear in the list already selected, and available
updates are shown next to each utility; **Apply selection** installs and starts everything.
Background starts with Windows update the enabled utilities only; unchanged packages are reused
and an offline start runs the installed copy.

Disabling a utility stops it and removes its startup entry; its downloaded versions are kept.
Exiting WinKit leaves enabled utilities running.

WinKit targets x64 Windows 10/11. Elevated applications may require elevated utility copies;
WinKit never elevates.

State, packages and diagnostics live in `%LOCALAPPDATA%\WinKit` (`settings.json`,
`catalog.json`, `apps\<id>\versions`, `winkit.log`).

## Build and verify

```powershell
dotnet build WinKit.csproj -c Release
dotnet run --project Tests/WinKit.Tests.csproj -c Release
dotnet run --project Tests/WinKit.Tests.csproj -c Release -- --verify-live
./scripts/publish-portable.ps1 -AppName WinKit -ProjectPath WinKit.csproj -Version v0.4.0
```

`--verify-live` downloads and verifies every utility's latest release without starting it.
`WinKit.exe --preview <path.png>` renders the window off-screen without installing anything.
Pushing a `v*` tag publishes a release through GitHub Actions.

To add a utility, follow [AGENTS.md](AGENTS.md#making-a-utility-winkit-compatible): a new entry
in `catalog.json` reaches every WinKit without a new release.
