# WinKit development

Apply the `windows-rust-utilities` skill to this project. Its installed location
is the user's Codex skills directory; the deliverable copy sits beside this source.

WinKit uses C#/.NET 10, WPF Fluent, and a native Windows tray menu. Keep background
startup silent and enforce one instance before creating UI or tray resources.
The catalog's repository URLs are fixed; release versions and portable packages are
resolved from each latest stable GitHub release and its `winkit.json` manifest.

Maintain per-utility cancellation and activation serialization. Never restart a
disabled utility. Keep process termination scoped to managed paths and preserve
one previous working version for offline fallback and rollback. All I/O belongs
off the UI thread. User-facing text and committed repository text are English.

Validate changes with a Release build and `dotnet run --project Tests/WinKit.Tests.csproj -c Release`.
For publishing changes, generate portable assets with `scripts/publish-portable.ps1`.
Use `--preview <path.png>` for off-screen visual inspection. Do not exercise the
utilities' global hooks or edit real startup entries merely to run tests.

Do not commit generated binaries, package folders, or user state.
