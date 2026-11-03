# WinKit

A native Windows tray manager for Comienzo, Voolime (two o characters),
Trapezoid, Captura, and Altabi. Built with C#/.NET 10, WPF's Fluent theme, and the Windows notification
area. No third-party application dependencies or separate runtime installation.

## Use

Run the portable `WinKit.exe`, choose any combination of utilities, and click
**Apply selection**. The published manager copies itself to
`%LOCALAPPDATA%\WinKit\WinKit.exe` so Windows startup never points to a temporary
download folder. Closing the window keeps the manager in the tray.

Right-click the tray icon to toggle Comienzo, Voolime, Trapezoid, Captura, or Altabi. Disabling a
utility persists the selection, cancels any pending download, stops that utility's
managed processes, and removes its independent startup entries. Settings and
downloaded versions are preserved. Enabling installs/updates and starts it again.

**Start with Windows** registers the manager with `--background` for the current
user. At each launch, it checks GitHub's latest stable release for enabled utilities,
installs missing/new versions, and launches them with `--background`. Unchanged
packages are reused. The only fixed sources are the catalog's repository URLs in
`Models.cs`; release versions and package URLs are resolved dynamically.

Exiting WinKit leaves enabled utilities running and preserves startup preferences.
Disable utilities first if you also want to stop them.

WinKit only installs releases that declare support for managed background startup, so an
older release never opens a settings window or runs its own updater. The requirements for
utilities are in [AGENTS.md](AGENTS.md#making-a-utility-winkit-compatible).

The manager targets x64 Windows 10/11. It uses per-user storage and needs no administrator
access. Elevated applications may require elevated utility copies; WinKit does not silently
elevate or create privileged scheduled tasks.

## Update behavior

Downloads and extraction use isolated staging directories. Hash, size, executable,
and archive-path checks run before activation. Each utility's enable/disable and
activation operations are serialized; obsolete downloads cannot restart a disabled
utility. The previous package remains available for rollback. An offline startup
uses the installed copy and records the update error without stopping other apps.

Existing instances outside the managed directory are reported rather than terminated. Close
such copies once before adopting them into WinKit.

State: `%LOCALAPPDATA%\WinKit\settings.json`.
Packages: `%LOCALAPPDATA%\WinKit\apps\<id>\versions`.
Diagnostics: `%LOCALAPPDATA%\WinKit\winkit.log`.

## Build and verify

```powershell
dotnet build WinKit.csproj -c Release
dotnet run --project Tests/WinKit.Tests.csproj -c Release
dotnet run --project Tests/WinKit.Tests.csproj -c Release -- --verify-live <isolated-download-directory>
./scripts/publish-portable.ps1 -AppName WinKit -ProjectPath WinKit.csproj -Version v0.2.0
```

The tests use isolated directories and fake release/startup services. They cover
partial/full selections, preservation of existing selections, updates, unchanged-release reuse, offline startup,
failed downloads, rollback, cancellation, rapid toggles, state persistence,
unsafe archives, and real path-scoped process termination with disposable test
processes. They do not edit real startup entries or install application hooks.
The optional `--verify-live` check uses the production GitHub resolver, downloader,
checksum verifier, and extractor against all current releases without
starting the utilities or changing Windows startup.

`WinKit.exe --preview <path.png>` renders an off-screen preview without installing
the manager, touching startup, downloading utilities, or creating tray icons.

## Adding new utilities to an existing installation

Exit the previous WinKit tray instance and run the updated executable. Existing
utility selections are preserved. Newly added utilities (Captura, Altabi) start disabled for
existing settings; enable them in the selection window or tray menu to download and run the
latest compatible GitHub release. First-time installations offer all five utilities.
