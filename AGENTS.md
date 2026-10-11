# AGENTS.md: Gmail Watcher

A Windows desktop app that makes a verified, local, read-only backup of Gmail accounts (SQLite + files + DuckDB analytics), styled and updated like its sibling `repo-watch`. Read `docs/PROGRESS.md` first; read `docs/architecture.md` before changing the engine.

## Commands

```bash
dotnet restore GmailWatcher.slnx --locked-mode
dotnet build GmailWatcher.slnx -c Release
dotnet test --solution GmailWatcher.slnx -c Release        # or: dotnet test --project tests/GmailWatcher.Archive.Tests
dotnet run --project src/GmailWatcher.Desktop -- --demo     # made-up mail, own data folder, no Google
pwsh scripts/publish-windows.ps1 -SkipTests                 # release zip + SHA-256 in artifacts/release (needs a git commit)
pwsh scripts/make-icon.ps1                                  # regenerate Assets/GmailWatcher.ico
```

Tests use xunit v3 on Microsoft.Testing.Platform: no `-nologo`, and the analyzer requires `TestContext.Current.CancellationToken` wherever a token is accepted.

## Rules that must stay true

- **Read-only toward Gmail.** Only the `gmail.readonly` scope is requested. Nothing may modify or delete mail in Gmail.
- **The archive is append-only.** A message Gmail no longer has is *marked* (`remote_deleted_at`), never removed. Only `ArchiveMaintenance.DeleteAccountData` (typed confirmation) and `Repair` (messages Gmail still has) remove archived data.
- **Files before rows.** Write to a temp name, flush to disk, rename; commit the database row only afterwards.
- **Never claim more than was checked.** `Verified` requires Gmail to have been listed *and* every message file (.eml) re-hashed. An unreadable extracted attachment copy (missing, changed, or blocked by an antivirus: Windows error 225/226) is a *note* only while its .eml is intact, because the .eml holds the attachment; if the .eml is damaged too it is a problem. UI wording lives in `AccountPresenter` and `AccountDetailsViewModel` and is tested.
- Tokens only in `ICredentialStore`; never in SQLite, settings, logs, URLs or the clipboard. Never log message content, subjects, addresses or tokens.
- Mail content is untrusted: attachment names go through `ArchiveLayout.SafeFileName`; HTML is converted to text, never rendered; risky attachment types are revealed, not opened.
- SQLite is authoritative; DuckDB is derived and must be rebuildable from it.
- Don't present anything as verified that wasn't: real Google sign-in, the real Gmail API and real throttling cannot be tested without the user's OAuth client.

## Conventions

C# with nullable and warnings-as-errors; file-scoped namespaces; central package versions in `Directory.Packages.props` with lock files (`--locked-mode` in CI); comments explain *why*. Avalonia compiled bindings: a control that sets `DataContext` must not also bind other properties to the parent's context (wrap it). Python isn't installed on the maintainer's machine: script with PowerShell or sed, and use heredocs without apostrophes.

- **Settings records and JSON:** the settings are read by a source-generated System.Text.Json context, which fills a *missing* `init`-only property with the type's zero value, overriding a `= true` initializer. A property whose default is not the zero value must be `{ get; set; }` (see `BackgroundSettings.KeepRunningInTray`; test: `SettingsCodecTests`).

## Layout

`src/GmailWatcher.{Core,Google,Archive,Desktop}`, `tests/*Tests`, `scripts/`, `docs/`. See `docs/architecture.md`.
