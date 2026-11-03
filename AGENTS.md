# WinKit development

Apply the `windows-rust-utilities` skill to this project. Its installed location
is the user's Codex skills directory; the deliverable copy sits beside this source.

WinKit uses C#/.NET 10, WPF Fluent, and a native Windows tray menu. Keep background
startup silent and enforce one instance before creating UI or tray resources.
The list of utilities is data: `catalog.json` on the default branch, downloaded at every refresh
and cached in `%LOCALAPPDATA%\WinKit\catalog.json`. Never hardcode utilities in code. Release
versions and portable packages are resolved from each latest stable GitHub release and its
`winkit.json` manifest.

Maintain per-utility cancellation and activation serialization. Never restart a
disabled utility. Keep process termination scoped to managed paths and preserve
one previous working version for offline fallback and rollback. All I/O belongs
off the UI thread. User-facing text and committed repository text are English.

Validate changes with a Release build and `dotnet run --project Tests/WinKit.Tests.csproj -c Release`.
For publishing changes, generate portable assets with `scripts/publish-portable.ps1`.
Use `--preview <path.png>` for off-screen visual inspection. Do not exercise the
utilities' global hooks or edit real startup entries merely to run tests.

Do not commit generated binaries, package folders, or user state.

## Making a utility WinKit-compatible

These rules are enforced by `Releases.cs` (`GitHubReleases`, `SelectPackage`, `PackageExtractor`)
and `WindowsIntegration.cs`; keep this list in sync with them.

The utility must:

- Start silently when launched as `<App>.exe --background` (no window; tray only). WinKit launches
  it with that argument, `UseShellExecute = false`, the version directory as working directory
  and `WINKIT_MANAGED=1` in the environment.
- Keep running: a process that exits within 500 ms of launch is reported as a failed start.
- Enforce a single instance (for example a named `Local\` mutex). A second launch may exit.
- Run per-user, `asInvoker`. WinKit never elevates.
- When `WINKIT_MANAGED=1`, disable its own "Start with Windows" control, leave its own startup
  entry untouched and skip any self-update check. WinKit owns startup and updates.
- Name any independent startup entry after the catalog `Name`: an `HKCU\...\CurrentVersion\Run`
  value `<Name>` or a Startup-folder shortcut `<Name>.lnk`. WinKit removes exactly those when the
  utility is disabled.
- Keep its process image named after the catalog `Executable`. WinKit stops managed instances by
  process name and only when their path is inside its managed directory.

Each latest GitHub release must:

- Be a published stable release (not a draft or prerelease) on the catalog's repository.
- Attach `winkit.json` (at most 64 KB) with `schema: 1`, `background`, `singleInstance` and
  `selfContained` all `true`, and one `packages` entry per architecture. WinKit currently
  requires `"architecture": "x64"`; `executable` must equal the catalog `Executable`
  (case-insensitive) and `sha256` must be the ZIP's 64-character hex digest.
- Attach the named `.zip` (at most 512 MB, at most 1 GB expanded, no absolute or `..` paths or
  links) with a self-contained Windows executable at the archive root.

`scripts/publish-portable.ps1 -AppName <App> -ProjectPath <csproj> -Version v<x.y.z>` produces
compliant `.NET` assets (`<App>-<version>-win-x64-self-contained.zip`, `winkit.json`,
`SHA256SUMS.txt`). Copy it into the utility's repository and run it from a tag-triggered
GitHub Actions workflow that uploads `artifacts/release/*` with `gh release create`. Native
utilities (C++) must produce the same files themselves.

```json
{
  "schema": 1,
  "background": true,
  "singleInstance": true,
  "selfContained": true,
  "packages": [
    {
      "architecture": "x64",
      "asset": "App-v1.0.0-win-x64-self-contained.zip",
      "executable": "App.exe",
      "sha256": "<64 hexadecimal characters>"
    }
  ]
}
```

To add the utility to WinKit, append an entry to `catalog.json` and push it to the default
branch. No WinKit release is needed: every WinKit picks it up the next time its window opens or
its refresh button is pressed, shows it as new and preselected, and installs it on
**Apply selection**. Background startup never installs or touches a utility the user has not
been offered.

```json
{
  "id": "app",
  "name": "App",
  "description": "Up to 80 characters",
  "repository": "https://github.com/<owner>/<repository>",
  "executable": "App.exe"
}
```

`Catalog.Parse` rejects the whole catalog if any entry is invalid (lowercase `id` of letters,
digits and dashes; unique `id` and `executable`; `https://github.com/<owner>/<repository>`), so
check it before pushing:

```powershell
$env:WINKIT_CATALOG = "$PWD\catalog.json"   # local file instead of the published catalog
dotnet run --project Tests/WinKit.Tests.csproj -c Release -- --verify-live
dotnet run --project Tests/WinKit.Tests.csproj -c Release
```

`--verify-live` downloads and verifies every catalog release without starting anything. Also add
the utility to the table in `README.md`.
